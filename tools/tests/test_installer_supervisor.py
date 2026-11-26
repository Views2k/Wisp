"""Test the CI supervisor with headless fixtures; never run the Wisp installer."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / ".github/scripts/Invoke-InstallerLifecycleCanary.ps1"
FIXTURE_TIMEOUT_SECONDS = 25
HARNESS = r"""
param([string]$Source, [string]$FixtureDirectory, [string]$Shell)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Join-Path (Split-Path $Source) 'InstallerLifecycleJob.cs')
. (Join-Path (Split-Path $Source) 'InstallerLifecycleStages.ps1')
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $Source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Supervisor source does not parse.' }
$names = @('New-InstallerLifecycleWorker', 'Invoke-InstallerLifecycleWorker', 'Write-InstallerLifecycleProgress')
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

START_CHILD = r"""
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = (Get-Process -Id $PID).Path
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 18')) {
    $info.ArgumentList.Add($argument)
}
$child = [Diagnostics.Process]::Start($info)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'child.pid'), [string]$child.Id)
"""


class InstallerSupervisorTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(
            prefix="wisp-supervisor-", dir=os.environ.get("WISP_CI_PLAN_TEST_TEMP"))
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.shell = shutil.which("pwsh")
        self.assertIsNotNone(self.shell, "PowerShell 7 is required.")

    def command(self, body, worker):
        worker_header = "param([string]$InstallerPath, [string]$ExpectedVersion)\n" \
                        "if ([Console]::ReadLine() -cne 'run') { exit 99 }\n"
        (self.root / "benign worker.ps1").write_text(worker_header + worker, encoding="utf-8")
        harness = self.root / "helpers.ps1"
        harness.write_text(HARNESS.replace("__BODY__", body), encoding="utf-8")
        return [self.shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(harness),
                "-Source", str(SCRIPT), "-FixtureDirectory", str(self.root), "-Shell", self.shell]

    def evaluate(self, body, worker=""):
        result = subprocess.run(
            self.command(body, worker),
            capture_output=True, text=True, timeout=FIXTURE_TIMEOUT_SECONDS)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stderr, "")
        self.assertNotIn("PRIVATE_FIXTURE_VALUE", result.stdout)
        lines = result.stdout.splitlines()
        self.diagnostics = [line for line in lines if line.startswith("Supervisor diagnostic: ")]
        return json.loads("\n".join(line for line in lines if not line.startswith("Supervisor diagnostic: ")))

    def test_direct_hidden_worker_preserves_argument_boundaries(self):
        result = self.evaluate(r"""
            $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'C:\fixture folder\setup.exe' '2.6.1' $stageLog
            try {
                $s = $p.StartInfo
                @{ shell = $s.UseShellExecute; noConsole = $s.CreateNoWindow;
                   hidden = ($s.WindowStyle -eq [Diagnostics.ProcessWindowStyle]::Hidden);
                   stdin = $s.RedirectStandardInput; stdout = $s.RedirectStandardOutput; stderr = $s.RedirectStandardError;
                   arguments = @($s.ArgumentList); log = $s.Environment['WISP_INSTALLER_STAGE_LOG']
                } | ConvertTo-Json -Compress
            } finally { $p.Dispose() }
        """)
        self.assertFalse(result["shell"])
        self.assertFalse(result["noConsole"])
        self.assertTrue(result["hidden"])
        self.assertTrue(result["stdin"])
        self.assertTrue(result["stdout"])
        self.assertTrue(result["stderr"])
        self.assertEqual(result["arguments"], ["-NoLogo", "-NoProfile", "-NonInteractive", "-File",
                         str(self.root / "benign worker.ps1"), "-InstallerPath",
                         "C:\\fixture folder\\setup.exe", "-ExpectedVersion", "2.6.1"])
        self.assertEqual(result["log"], str(self.root / "stages.log"))

    def test_real_canary_refuses_to_start_without_supervisor_permission(self):
        # This path does not exist, so even a broken guard cannot run an installer.
        missing = self.root / "missing-installer.exe"
        run = subprocess.run(
            [self.shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
             str(SCRIPT.with_name("Test-InstallerLifecycle.ps1")), "-InstallerPath", str(missing),
             "-ExpectedVersion", "2.7.6"],
            env={**os.environ, "GITHUB_ACTIONS": "true", "CI": "true"},
            input="", text=True, capture_output=True, timeout=8)
        self.assertNotEqual(run.returncode, 0)
        self.assertIn("contain the worker before setup starts", run.stderr)

    def test_successful_worker_completes(self):
        result = self.evaluate(r"""
            $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'fixture' '2.6.1' $stageLog
            Invoke-InstallerLifecycleWorker $p -TimeoutSeconds 5
            @{ completed = $true } | ConvertTo-Json -Compress
        """, "exit 0")
        self.assertTrue(result["completed"])
        self.assertIn("Supervisor diagnostic: worker start returned.", self.diagnostics)
        self.assertIn("Supervisor diagnostic: process wait completed.", self.diagnostics)
        self.assertEqual(self.diagnostics[-1], "Supervisor diagnostic: worker handles disposed.")

    def test_noisy_worker_drains_both_streams_without_exposing_output(self):
        result = self.evaluate(r"""
            $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'fixture' '2.7.5' $stageLog
            Invoke-InstallerLifecycleWorker $p -TimeoutSeconds 10
            @{ completed = $true } | ConvertTo-Json -Compress
        """, r"""
$chunk = 'PRIVATE_FIXTURE_VALUE' * 4096
for ($index = 0; $index -lt 32; $index++) {
    [Console]::Out.Write($chunk)
    [Console]::Error.Write($chunk)
}
exit 0
""")
        self.assertTrue(result["completed"])

    def test_upgrade_worker_preserves_baseline_arguments(self):
        result = self.evaluate(r"""
            $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'candidate.exe' '2.7.5' $stageLog `
                'C:\baseline folder\setup.exe' '2.7.4'
            try { @($p.StartInfo.ArgumentList) | ConvertTo-Json -Compress } finally { $p.Dispose() }
        """)
        self.assertEqual(result[-4:], ["-BaselineInstallerPath", "C:\\baseline folder\\setup.exe",
                                      "-BaselineVersion", "2.7.4"])

    def test_wait_reports_completed_allowlisted_stages_before_worker_exits(self):
        result = self.evaluate(r"""
            function Write-Host([string]$Message) {
                Microsoft.PowerShell.Utility\Write-Host $Message
                if ($Message.StartsWith('Supervisor diagnostic: Published upgrade canary handoff started.')) {
                    [IO.File]::WriteAllText((Join-Path $FixtureDirectory 'marker-seen'), '')
                }
            }
            $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'fixture' '2.7.6' $stageLog
            Invoke-InstallerLifecycleWorker $p -TimeoutSeconds 10 -StageLogPath $stageLog
            @{ completed = $true } | ConvertTo-Json -Compress
        """, r"""
$marker = 'Published upgrade canary handoff started. utc=2026-11-19T00:00:00.0000000Z elapsed_ms=71'
$failure = 'Installer lifecycle failure: type=IOException; line=8'
[IO.File]::WriteAllText($env:WISP_INSTALLER_STAGE_LOG, "$marker`nPRIVATE_FIXTURE_VALUE`n$($failure.Substring(0, 6))")
$ack = Join-Path $PSScriptRoot 'marker-seen'
$timer = [Diagnostics.Stopwatch]::StartNew()
while (-not [IO.File]::Exists($ack)) {
    if ($timer.Elapsed.TotalSeconds -gt 7) { exit 9 }
    Start-Sleep -Milliseconds 50
}
[IO.File]::AppendAllText($env:WISP_INSTALLER_STAGE_LOG, $failure.Substring(6) + "`n")
exit 0
""")
        self.assertTrue(result["completed"])
        marker = "Supervisor diagnostic: Published upgrade canary handoff started."
        matches = [line for line in self.diagnostics if line.startswith(marker)]
        self.assertEqual(len(matches), 1)
        self.assertLess(self.diagnostics.index(matches[0]),
                        self.diagnostics.index("Supervisor diagnostic: process wait completed."))
        self.assertIn("Supervisor diagnostic: Installer lifecycle failure: type=IOException; line=8",
                      self.diagnostics)

    def test_incomplete_baseline_arguments_are_rejected(self):
        result = self.evaluate(r"""
            @(foreach ($pair in @(@('baseline.exe', ''), @('', '2.7.4'))) {
                try {
                    $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'candidate.exe' '2.7.5' $stageLog `
                        $pair[0] $pair[1]
                    $p.Dispose()
                    $false
                } catch { $true }
            }) | ConvertTo-Json -Compress
        """)
        self.assertEqual(result, [True, True])

    def test_failed_worker_cannot_report_success(self):
        result = self.evaluate(r"""
            $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'fixture' '2.6.1' $stageLog
            $message = ''
            try { Invoke-InstallerLifecycleWorker $p -TimeoutSeconds 5 }
            catch { $message = $_.Exception.Message }
            @{ message = $message } | ConvertTo-Json -Compress
        """, "exit 7")
        self.assertIn("failed with exit code 7", result["message"])
        self.assertIn("Supervisor diagnostic: process wait completed.", self.diagnostics)
        self.assertEqual(self.diagnostics[-1], "Supervisor diagnostic: worker handles disposed.")

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
        """, START_CHILD + "Start-Sleep -Seconds 18")
        self.assertIn("exceeded its 3-second deadline", result["message"])
        self.assertFalse(result["childRunning"])
        self.assertLess(result["elapsed"], 13)
        deadline = self.diagnostics.index("Supervisor diagnostic: process wait reached its deadline.")
        termination = self.diagnostics.index("Supervisor diagnostic: closing the owned worker job.")
        disposed = self.diagnostics.index("Supervisor diagnostic: worker handles disposed.")
        self.assertLess(deadline, termination)
        self.assertLess(termination, disposed)

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
        self.assertNotIn("Supervisor diagnostic: worker start returned.", self.diagnostics)
        self.assertEqual(self.diagnostics[-1], "Supervisor diagnostic: worker handles disposed.")

    def test_failed_worker_leaving_a_child_preserves_its_error_and_reaps_the_child(self):
        result = self.evaluate(r"""
            $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'fixture' '2.7.6' $stageLog
            $message = ''
            try { Invoke-InstallerLifecycleWorker $p -TimeoutSeconds 5 }
            catch { $message = $_.Exception.Message }
            $childId = [int][IO.File]::ReadAllText((Join-Path $FixtureDirectory 'child.pid'))
            $child = Get-Process -Id $childId -ErrorAction SilentlyContinue
            $running = $null -ne $child -and -not $child.HasExited
            if ($running) { $child.Kill(); [void]$child.WaitForExit(3000) }
            @{ message = $message; orphan = $running } | ConvertTo-Json -Compress
        """, START_CHILD + "exit 7")
        self.assertIn("failed with exit code 7", result["message"])
        self.assertFalse(result["orphan"])

    def test_abrupt_supervisor_exit_also_stops_its_worker(self):
        import _winapi

        command = self.command(r"""
            $p = New-InstallerLifecycleWorker $Shell $fixtureScript 'fixture' '2.7.6' $stageLog
            [IO.File]::WriteAllText((Join-Path $FixtureDirectory 'supervisor.ready'), '')
            Invoke-InstallerLifecycleWorker $p -TimeoutSeconds 20
        """, r"""
$pending = Join-Path $PSScriptRoot 'worker.tmp'
[IO.File]::WriteAllText($pending, [string]$PID)
[IO.File]::Move($pending, (Join-Path $PSScriptRoot 'worker.pid'))
Start-Sleep -Seconds 60
""")
        with subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                              stderr=subprocess.DEVNULL) as supervisor:
            def wait_for_marker(filename, stage):
                deadline = time.monotonic() + FIXTURE_TIMEOUT_SECONDS
                while not (self.root / filename).exists():
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        self.fail(f"Timed out waiting for {stage}.")
                    try:
                        code = supervisor.wait(timeout=min(remaining, 0.1))
                    except subprocess.TimeoutExpired:
                        continue
                    self.fail(f"Supervisor exited with code {code} while waiting for {stage}.")

            try:
                # Cold PowerShell and Add-Type initialization precede the worker's own deadline.
                wait_for_marker("supervisor.ready", "PowerShell fixture initialization")
                wait_for_marker("worker.pid", "the contained worker's startup handshake")
                worker_pid = int((self.root / "worker.pid").read_text())
                process_terminate = 0x0001
                worker = _winapi.OpenProcess(_winapi.SYNCHRONIZE | process_terminate, False, worker_pid)

                def cleanup_worker():
                    try:
                        if _winapi.WaitForSingleObject(worker, 0) == _winapi.WAIT_TIMEOUT:
                            _winapi.TerminateProcess(worker, 1)
                            _winapi.WaitForSingleObject(worker, 3000)
                    finally:
                        _winapi.CloseHandle(worker)

                self.addCleanup(cleanup_worker)
                self.assertEqual(_winapi.WaitForSingleObject(worker, 0), _winapi.WAIT_TIMEOUT,
                                 "The contained worker exited before its supervisor was killed.")
                self.assertIsNone(supervisor.poll(), "The supervisor exited before the termination check.")
            finally:
                supervisor.kill()
                supervisor.wait(timeout=3)
        self.assertEqual(_winapi.WaitForSingleObject(worker, 3000), _winapi.WAIT_OBJECT_0,
                         "The worker survived the abrupt loss of its supervisor.")


if __name__ == "__main__":
    unittest.main()
