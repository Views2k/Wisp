"""Test the CI supervisor with headless fixtures; never run the Wisp installer."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / ".github/scripts/Invoke-InstallerLifecycleCanary.ps1"
HARNESS = r"""
param([string]$Source, [string]$FixtureDirectory, [string]$Shell)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $Source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Supervisor source does not parse.' }
$names = @('New-InstallerLifecycleWorker', 'Invoke-InstallerLifecycleWorker')
$functions = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -cin $names
}, $false))
if ($functions.Count -ne $names.Count) { throw 'Supervisor helpers are missing.' }
foreach ($function in $functions) {
    . ([scriptblock]::Create($function.Extent.Text))
}
$fixtureScript = Join-Path $FixtureDirectory 'benign worker.ps1'
$stageLog = Join-Path $FixtureDirectory 'stages.log'
__BODY__
"""


class InstallerSupervisorTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(
            prefix="wisp-supervisor-", dir=os.environ.get("WISP_CI_PLAN_TEST_TEMP"))
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.shell = shutil.which("pwsh")
        self.assertIsNotNone(self.shell, "PowerShell 7 is required.")

    def evaluate(self, body, worker=""):
        (self.root / "benign worker.ps1").write_text(worker, encoding="utf-8")
        harness = self.root / "helpers.ps1"
        harness.write_text(HARNESS.replace("__BODY__", body), encoding="utf-8")
        result = subprocess.run(
            [self.shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(harness),
             "-Source", str(SCRIPT), "-FixtureDirectory", str(self.root), "-Shell", self.shell],
            capture_output=True, text=True, timeout=25)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stderr, "")
        return json.loads(result.stdout)

    def test_direct_hidden_worker_preserves_argument_boundaries(self):
        result = self.evaluate(r"""
            $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'C:\fixture folder\setup.exe' '2.6.1' $stageLog
            try {
                $s = $p.StartInfo
                @{ shell = $s.UseShellExecute; hidden = $s.CreateNoWindow;
                   arguments = @($s.ArgumentList); log = $s.Environment['WISP_INSTALLER_STAGE_LOG']
                } | ConvertTo-Json -Compress
            } finally { $p.Dispose() }
        """)
        self.assertFalse(result["shell"])
        self.assertTrue(result["hidden"])
        self.assertEqual(result["arguments"], ["-NoLogo", "-NoProfile", "-NonInteractive", "-File",
                         str(self.root / "benign worker.ps1"), "-InstallerPath",
                         "C:\\fixture folder\\setup.exe", "-ExpectedVersion", "2.6.1"])
        self.assertEqual(result["log"], str(self.root / "stages.log"))

    def test_successful_worker_completes(self):
        result = self.evaluate(r"""
            $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'fixture' '2.6.1' $stageLog
            Invoke-InstallerLifecycleWorker $p -TimeoutSeconds 5
            @{ completed = $true } | ConvertTo-Json -Compress
        """, "param([string]$InstallerPath, [string]$ExpectedVersion)\nexit 0\n")
        self.assertTrue(result["completed"])

    def test_failed_worker_cannot_report_success(self):
        result = self.evaluate(r"""
            $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'fixture' '2.6.1' $stageLog
            $message = ''
            try { Invoke-InstallerLifecycleWorker $p -TimeoutSeconds 5 }
            catch { $message = $_.Exception.Message }
            @{ message = $message } | ConvertTo-Json -Compress
        """, "param([string]$InstallerPath, [string]$ExpectedVersion)\nexit 7\n")
        self.assertIn("failed with exit code 7", result["message"])

    def test_timeout_terminates_only_the_owned_worker_tree(self):
        result = self.evaluate(r"""
            $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'fixture' '2.6.1' $stageLog
            $timer = [Diagnostics.Stopwatch]::StartNew()
            $message = ''
            try { Invoke-InstallerLifecycleWorker $p -TimeoutSeconds 3 }
            catch { $message = $_.Exception.Message }
            $childId = [int][IO.File]::ReadAllText((Join-Path $FixtureDirectory 'child.pid'))
            $child = Get-Process -Id $childId -ErrorAction SilentlyContinue
            @{ message = $message; childRunning = ($null -ne $child -and -not $child.HasExited);
               elapsed = $timer.Elapsed.TotalSeconds } | ConvertTo-Json -Compress
        """, r"""
param([string]$InstallerPath, [string]$ExpectedVersion)
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = (Get-Process -Id $PID).Path
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 60')) {
    $info.ArgumentList.Add($argument)
}
$child = [Diagnostics.Process]::Start($info)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'child.pid'), [string]$child.Id)
Start-Sleep -Seconds 60
""")
        self.assertIn("exceeded its 3-second deadline", result["message"])
        self.assertFalse(result["childRunning"])
        self.assertLess(result["elapsed"], 13)

    def test_failed_launch_is_sanitized_and_disposed(self):
        result = self.evaluate(r"""
            $p = [pscustomobject]@{ disposed = $false }
            $p | Add-Member ScriptMethod Start { throw 'PRIVATE_FIXTURE_VALUE' }
            $p | Add-Member ScriptMethod Dispose { $this.disposed = $true }
            $message = ''
            try { Invoke-InstallerLifecycleWorker $p -TimeoutSeconds 1 }
            catch { $message = $_.Exception.Message }
            @{ message = $message; disposed = $p.disposed } | ConvertTo-Json -Compress
        """)
        self.assertEqual(result["message"], "The installer lifecycle worker could not start.")
        self.assertTrue(result["disposed"])


if __name__ == "__main__":
    unittest.main()
