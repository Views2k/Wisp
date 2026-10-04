"""Exercise only extracted diagnostic helpers; never launch or install Wisp."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / ".github/scripts/Test-InstallerLifecycle.ps1"
STAMP = "2026-10-03 23:57:01.123   "
HARNESS = r"""
param([string]$Source, [string]$FixtureDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
try {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $Source, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw 'The lifecycle source did not parse.' }
    $names = @('Get-InstallerLogStages', 'Write-InstallerLifecycleMarker',
        'Write-InstallerLogStages', 'Wait-InstallerProcessExit')
    $functions = @($ast.FindAll({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -cin $names
    }, $false))
    if ($functions.Count -ne $names.Count) { throw 'Diagnostic helpers are missing.' }
    foreach ($function in $functions) {
        . ([scriptblock]::Create($function.Extent.Text))
    }
    # The guarded lifecycle entrypoint and process launcher are never evaluated.
    function Start-Process { throw 'A diagnostic fixture cannot launch a process.' }
    $log = Join-Path $FixtureDirectory 'native.log'
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    __BODY__
}
catch {
    Write-Output ('Diagnostic fixture failed: ' + $_.Exception.GetType().Name)
    exit 1
}
"""


class InstallerLifecycleDiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(
            prefix="wisp-lifecycle-helpers-", dir=os.environ.get("WISP_CI_PLAN_TEST_TEMP"))
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.shell = shutil.which("pwsh")
        self.assertIsNotNone(self.shell, "PowerShell 7 is required for diagnostic fixtures.")

    def write_log(self, messages, encoding="utf-8-sig", complete=True):
        text = "\r\n".join(STAMP + message for message in messages)
        if complete:
            text += "\r\n"
        (self.root / "native.log").write_text(text, encoding=encoding, newline="")

    def evaluate(self, body):
        harness = self.root / "helpers.ps1"
        harness.write_text(HARNESS.replace("__BODY__", body), encoding="utf-8")
        result = subprocess.run(
            [self.shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(harness),
             "-Source", str(SCRIPT), "-FixtureDirectory", str(self.root)],
            capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stderr, "")
        return result.stdout

    def stages(self):
        return json.loads(self.evaluate(
            "ConvertTo-Json -InputObject @(Get-InstallerLogStages $log) -Compress"))

    def test_pinned_native_stages_and_known_wisp_names(self):
        self.write_log([
            "Found a file to register with RestartManager: C:\\PRIVATE_FIXTURE_VALUE\\Wisp.exe",
            "Found 7 files to register with RestartManager.",
            "Calling RestartManager's RmGetList.",
            "RmGetList finished successfully.",
            "RestartManager found an application using one of our files: Wisp",
            "RestartManager found an application using one of our files: Wisp Update Helper",
            "Starting the installation process.",
            "Installation process succeeded.",
            "Deinitializing Setup.",
        ])
        self.assertEqual(self.stages(), [
            "restart-manager-registering-files", "restart-manager-resources-ready",
            "restart-manager-query-started", "restart-manager-query-completed",
            "restart-manager-application-wisp", "restart-manager-application-wisp-updater",
            "installation-started", "installation-succeeded", "setup-deinitializing",
        ])

    def test_unknown_names_and_private_lines_cannot_escape(self):
        self.write_log([
            "Command line: PRIVATE_FIXTURE_VALUE",
            "User name: PRIVATE_FIXTURE_VALUE",
            "RestartManager found an application using one of our files: PRIVATE_FIXTURE_VALUE",
            "RestartManager found an application using one of our files: Wisp PRIVATE_FIXTURE_VALUE",
            "RmGetList finished successfully. PRIVATE_FIXTURE_VALUE",
            "PRIVATE_FIXTURE_VALUE RmGetList failed.",
            "RmGetList failed.",
        ])
        output = self.evaluate(
            "$seen = [System.Collections.Generic.HashSet[string]]::new(); "
            "Write-InstallerLogStages $log 'Fixture' $timer $seen; "
            "Write-InstallerLogStages $log 'Fixture' $timer $seen")
        self.assertNotIn("PRIVATE_FIXTURE_VALUE", output)
        self.assertEqual(output.count("restart-manager-application-found"), 1)
        self.assertEqual(output.count("restart-manager-query-failed"), 1)
        self.assertNotIn("restart-manager-application-wisp", output)
        self.assertRegex(output, r"utc=\d{4}-\d{2}-\d{2}T[^ ]+Z elapsed_ms=\d+")

    def test_missing_empty_and_unreadable_logs_are_best_effort(self):
        self.assertEqual(self.stages(), [])
        (self.root / "native.log").touch()
        self.assertEqual(self.stages(), [])
        (self.root / "native.log").unlink()
        (self.root / "native.log").mkdir()
        self.assertEqual(self.stages(), [])

    def test_bounded_tail_ignores_old_data_and_incomplete_final_line(self):
        self.write_log([
            "Calling RestartManager's RmGetList.",
            "PRIVATE_FIXTURE_VALUE" * 5000,
            "RestartManager found no applications using one of our files.",
            "RmGetList failed.",
        ], complete=False)
        self.assertEqual(self.stages(), ["restart-manager-no-applications"])

    def test_utf16_log_is_recognized(self):
        self.write_log(["RmGetList finished successfully."], encoding="utf-16")
        self.assertEqual(self.stages(), ["restart-manager-query-completed"])

    def test_append_after_incomplete_line_and_shared_writer(self):
        self.write_log(["RmGetList finished successfully."], complete=False)
        output = self.evaluate(r"""
            $before = @(Get-InstallerLogStages $log)
            $writer = [System.IO.File]::Open($log, [System.IO.FileMode]::Append,
                [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
            try {
                $bytes = [System.Text.Encoding]::UTF8.GetBytes("`r`n")
                $writer.Write($bytes, 0, $bytes.Length)
                $writer.Flush()
                $after = @(Get-InstallerLogStages $log)
            }
            finally { $writer.Dispose() }
            @{ before = $before; after = $after } | ConvertTo-Json -Compress
        """)
        self.assertEqual(json.loads(output), {
            "before": [], "after": ["restart-manager-query-completed"]})

    def test_wait_reports_live_stages_once_and_returns_on_exit(self):
        self.write_log(["Calling RestartManager's RmGetList."])
        output = self.evaluate(r"""
            $fake = [pscustomobject]@{ Calls = 0; Waits = @() }
            $fake | Add-Member ScriptMethod WaitForExit {
                param([int]$Milliseconds)
                $this.Calls++
                $this.Waits += $Milliseconds
                return $this.Calls -ge 2
            }
            $exited = Wait-InstallerProcessExit $fake $log 'Fixture' $timer 180
            @{ exited = $exited; calls = $fake.Calls; waits = $fake.Waits } |
                ConvertTo-Json -Compress
        """)
        self.assertEqual(output.count("restart-manager-query-started"), 1)
        result = json.loads(output.splitlines()[-1])
        self.assertTrue(result["exited"])
        self.assertEqual(result["calls"], 2)
        self.assertEqual(result["waits"], [1000, 1000])

    def test_diagnostic_time_consumes_one_total_wait_deadline(self):
        output = self.evaluate(r"""
            function Get-InstallerLogStages { Start-Sleep -Milliseconds 1200 }
            $fake = [pscustomobject]@{ Waits = @() }
            $fake | Add-Member ScriptMethod WaitForExit {
                param([int]$Milliseconds)
                $this.Waits += $Milliseconds
                if ($Milliseconds -gt 0) { Start-Sleep -Milliseconds $Milliseconds }
                return $false
            }
            $exited = Wait-InstallerProcessExit $fake $log 'Fixture' $timer 2
            @{ exited = $exited; waits = $fake.Waits } | ConvertTo-Json -Compress
        """)
        result = json.loads(output)
        self.assertFalse(result["exited"])
        self.assertGreaterEqual(len(result["waits"]), 1)
        self.assertLessEqual(sum(result["waits"]), 800)
        self.assertEqual(result["waits"][-1], 0)


if __name__ == "__main__":
    unittest.main()
