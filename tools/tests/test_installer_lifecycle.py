"""Exercise extracted lifecycle helpers with native launches replaced by fakes."""
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
        'Write-InstallerLogStages', 'Wait-InstallerProcessExit', 'Invoke-CheckedProcess',
        'New-InstallerProcess', 'Assert-FirstRunSetupLaunch', 'Invoke-IsolatedCanaryApplication')
    $functions = @($ast.FindAll({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -cin $names
    }, $false))
    if ($functions.Count -ne $names.Count) { throw 'Diagnostic helpers are missing.' }
    foreach ($function in $functions) {
        . ([scriptblock]::Create($function.Extent.Text))
    }
    # The guarded lifecycle entrypoint is never evaluated; native launch is stubbed.
    function Start-Process { throw 'A diagnostic fixture cannot launch a process.' }
    $env:WISP_INSTALLER_STAGE_LOG = $null
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

    def isolation(self, scenario, application_name="Wisp.exe"):
        output = self.evaluate(r"""
            $scenario = '__SCENARIO__'
            $application = Join-Path $FixtureDirectory '__APPLICATION_NAME__'
            $expectedApplication = [IO.Path]::GetFullPath($application)
            [IO.File]::WriteAllText($application, 'fixture; never executed')
            $script:rules = @{}
            $script:removed = @()
            $script:launched = $false
            $script:settings = @{}
            function Get-Service { param($Name, $ErrorAction)
                if ($Name -cne 'MpsSvc') { throw 'Wrong service.' }
                @{ Status = if ($scenario -eq 'stopped') { 'Stopped' } else { 'Running' } }
            }
            function Get-NetFirewallProfile { param($PolicyStore, $ErrorAction)
                if ($PolicyStore -cne 'ActiveStore') { throw 'Wrong profile store.' }
                foreach ($name in @('Domain', 'Private', 'Public')) {
                    @{ Name = $name; Enabled = $scenario -ne 'disabled' }
                }
            }
            function New-NetFirewallRule {
                param($Name, $DisplayName, $Direction, $Action, $Enabled, $Profile,
                    $Program, $PolicyStore, $ErrorAction)
                $script:settings = @{} + $PSBoundParameters
                $script:rules[$Name] = [pscustomobject]@{
                    Name = $Name; Enabled = $Enabled; Direction = $Direction;
                    Action = $Action; Profile = $Profile; Program = $Program;
                    CimInstanceProperties = @{ EnforcementStatus = @{ Value = @(5, 1, 1, 1) } }
                }
                if ($scenario -eq 'creation-failed') { throw 'fixture creation failed' }
                $script:rules[$Name]
            }
            function Get-NetFirewallRule { param($Name, $PolicyStore, $ErrorAction)
                if ($script:rules.ContainsKey($Name)) {
                    if ($PolicyStore -eq 'ActiveStore' -and $scenario -eq 'inactive') { return }
                    $rule = $script:rules[$Name]
                    if ($PolicyStore -eq 'ActiveStore' -and $scenario -eq 'allow') { $rule.Action = 'Allow' }
                    if ($scenario -eq 'not-enforced') { $rule.CimInstanceProperties['EnforcementStatus'].Value = @(1, 16) }
                    if ($scenario -eq 'all-inactive') { $rule.CimInstanceProperties['EnforcementStatus'].Value = @(5, 5) }
                    if ($scenario -eq 'empty-status') { $rule.CimInstanceProperties['EnforcementStatus'].Value = @() }
                    $rule
                }
            }
            function Get-NetFirewallApplicationFilter {
                param([Parameter(ValueFromPipeline)]$Rule)
                process {
                    @{ Program = if ($scenario -eq 'wrong-program') { 'elsewhere.exe' } else { $Rule.Program } }
                }
            }
            function Remove-NetFirewallRule {
                param([Parameter(ValueFromPipeline)]$Rule)
                process { $script:removed += $Rule.Name; $script:rules.Remove($Rule.Name) }
            }
            $failure = ''
            try {
                Invoke-IsolatedCanaryApplication $application {
                    if ($script:rules.Count -ne 1) { throw 'Launch was not isolated.' }
                    $script:launched = $true
                    if ($scenario -eq 'launch-failed') { throw 'fixture launch failed' }
                }
            } catch { $failure = $_.Exception.Message }
            @{ launched = $script:launched; remaining = $script:rules.Count;
               removed = $script:removed.Count; failure = $failure;
               settings = $script:settings; pathMatches = $script:settings.ContainsKey('Program') -and
                   [string]::Equals($script:settings.Program, $expectedApplication, [StringComparison]::OrdinalIgnoreCase)
            } | ConvertTo-Json -Depth 4 -Compress
        """.replace("__SCENARIO__", scenario).replace("__APPLICATION_NAME__", application_name))
        return json.loads(output.splitlines()[-1])

    def test_canary_launch_is_scoped_offline_and_rule_is_removed(self):
        result = self.isolation("success")
        self.assertTrue(result["launched"])
        self.assertTrue(result["pathMatches"])
        self.assertEqual((result["remaining"], result["removed"], result["failure"]), (0, 1, ""))
        rule = result["settings"]
        self.assertEqual((rule["Direction"], rule["Action"], rule["Enabled"], rule["Profile"]),
                         ("Outbound", "Block", "True", "Any"))
        self.assertEqual(rule["PolicyStore"], "PersistentStore")
        self.assertRegex(rule["Name"], r"^Wisp-Installer-Canary-[0-9a-f]{32}$")
        self.assertEqual(rule["Name"], rule["DisplayName"])

    def test_missing_firewall_refuses_to_launch(self):
        for scenario in ("stopped", "disabled"):
            with self.subTest(scenario=scenario):
                result = self.isolation(scenario)
                self.assertFalse(result["launched"])
                self.assertEqual(result["removed"], 0)
                self.assertIn("active firewall", result["failure"])

    def test_equivalent_executable_path_is_scoped_to_the_canonical_file(self):
        result = self.isolation("success", r".\Wisp.exe")
        self.assertTrue(result["launched"])
        self.assertTrue(result["pathMatches"])
        self.assertEqual((result["remaining"], result["removed"], result["failure"]), (0, 1, ""))

    def test_missing_or_misdirected_active_block_refuses_launch_and_cleans_up(self):
        for scenario in ("creation-failed", "inactive", "allow", "not-enforced",
                         "all-inactive", "empty-status", "wrong-program"):
            with self.subTest(scenario=scenario):
                result = self.isolation(scenario)
                self.assertFalse(result["launched"])
                self.assertEqual((result["remaining"], result["removed"]), (0, 1))
                self.assertTrue(result["failure"])

    def test_failed_setup_still_removes_its_own_network_rule(self):
        result = self.isolation("launch-failed")
        self.assertTrue(result["launched"])
        self.assertEqual((result["remaining"], result["removed"]), (0, 1))
        self.assertEqual(result["failure"], "fixture launch failed")

    def test_first_run_entrypoint_uses_network_isolation(self):
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("Invoke-IsolatedCanaryApplication $canaryApplication {\n"
                      "        Assert-FirstRunSetupLaunch $canaryApplication\n    }", source)

    def test_network_probe_refuses_a_non_ci_machine(self):
        environment = dict(os.environ, GITHUB_ACTIONS="false", CI="false")
        result = subprocess.run(
            [self.shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
             str(SCRIPT.with_name("Test-InstallerNetworkIsolation.ps1"))],
            env=environment, capture_output=True, text=True, timeout=15)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("requires an ephemeral Windows CI runner", result.stderr)

    def test_network_probe_runs_before_the_expensive_build(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        probe = workflow.index("run: ./.github/scripts/Test-InstallerNetworkIsolation.ps1")
        build = workflow.index("run: ./installer/Build-Installer.ps1")
        self.assertLess(probe, build)

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
            "Write-InstallerLogStages $log 'In-place update canary' $timer $seen; "
            "Write-InstallerLogStages $log 'In-place update canary' $timer $seen")
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
            $exited = Wait-InstallerProcessExit $fake $log 'In-place update canary' $timer 180
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
            $exited = Wait-InstallerProcessExit $fake $log 'In-place update canary' $timer 2
            @{ exited = $exited; waits = $fake.Waits } | ConvertTo-Json -Compress
        """)
        result = json.loads(output)
        self.assertFalse(result["exited"])
        self.assertGreaterEqual(len(result["waits"]), 1)
        self.assertLessEqual(sum(result["waits"]), 800)
        self.assertEqual(result["waits"][-1], 0)

    def test_launch_time_cannot_reset_the_operation_deadline(self):
        output = self.evaluate(r"""
            $script:fake = [pscustomobject]@{ Killed = $false; Disposed = $false; Waits = @(); ExitCode = 0 }
            $script:fake | Add-Member ScriptMethod WaitForExit {
                param([int]$Milliseconds)
                $this.Waits += $Milliseconds
                return $this.Killed -or $Milliseconds -gt 0
            }
            $script:fake | Add-Member ScriptMethod Kill { param([bool]$Tree) $this.Killed = $Tree }
            $script:fake | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
            $script:fake | Add-Member ScriptMethod Start {
                Start-Sleep -Milliseconds 1200
                return $true
            }
            function New-InstallerProcess { return $script:fake }
            $timedOut = $false
            try { Invoke-CheckedProcess 'unused' @('unused') 'In-place update canary' $log 1 }
            catch { $timedOut = $_.Exception.Message -ceq 'In-place update canary exceeded its 1-second timeout.' }
            @{ timedOut = $timedOut; killed = $script:fake.Killed;
               disposed = $script:fake.Disposed; waits = $script:fake.Waits } |
                ConvertTo-Json -Compress
        """)
        result = json.loads(output.splitlines()[-1])
        self.assertTrue(result["timedOut"])
        self.assertTrue(result["killed"])
        self.assertTrue(result["disposed"])
        self.assertEqual(result["waits"], [0, 10000])

    def test_direct_launcher_preserves_quoted_arguments_without_starting(self):
        output = self.evaluate(r"""
            $path = Join-Path $FixtureDirectory 'unused.exe'
            $arguments = @('/WISPUPDATE', '/DIR="C:\private fixture\app"',
                '/LOG="C:\private fixture\native.log"', '/LOGCLOSEAPPLICATIONS')
            $process = New-InstallerProcess $path $arguments
            $setup = New-InstallerProcess $path @()
            try {
                $unstarted = $false
                try { $null = $process.WaitForExit(0) }
                catch { $unstarted = $true }
                @{ unstarted = $unstarted; shell = $process.StartInfo.UseShellExecute;
                   pathMatches = $process.StartInfo.FileName -ceq $path;
                   argumentsMatch = $process.StartInfo.Arguments -ceq ($arguments -join ' ');
                   noArguments = $setup.StartInfo.Arguments -ceq '';
                   directoryMatches = $process.StartInfo.WorkingDirectory -ceq (Get-Location).ProviderPath } |
                    ConvertTo-Json -Compress
            }
            finally { $process.Dispose(); $setup.Dispose() }
        """)
        self.assertEqual(json.loads(output), {
            "unstarted": True, "shell": False, "pathMatches": True,
            "argumentsMatch": True, "noArguments": True, "directoryMatches": True})

    def test_failed_native_launch_is_disposed_without_private_exception_text(self):
        output = self.evaluate(r"""
            $script:fake = [pscustomobject]@{ Disposed = $false }
            $script:fake | Add-Member ScriptMethod Start { throw 'PRIVATE_FIXTURE_VALUE' }
            $script:fake | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
            function New-InstallerProcess { return $script:fake }
            $failure = ''
            try { Invoke-CheckedProcess 'unused' @('unused') 'In-place update canary' $log 1 }
            catch { $failure = $_.Exception.Message }
            @{ failure = $failure; disposed = $script:fake.Disposed } | ConvertTo-Json -Compress
        """)
        self.assertNotIn("PRIVATE_FIXTURE_VALUE", output)
        self.assertEqual(json.loads(output.splitlines()[-1]), {
            "failure": "In-place update canary launch failed.", "disposed": True})

    def test_nonzero_exit_still_fails_and_disposes(self):
        output = self.evaluate(r"""
            $script:fake = [pscustomobject]@{ Disposed = $false; ExitCode = 42 }
            $script:fake | Add-Member ScriptMethod Start { return $true }
            $script:fake | Add-Member ScriptMethod WaitForExit { param([int]$Milliseconds) return $true }
            $script:fake | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
            function New-InstallerProcess { return $script:fake }
            $failure = ''
            try { Invoke-CheckedProcess 'unused' @('unused') 'In-place update canary' $log 1 }
            catch { $failure = $_.Exception.Message }
            @{ failure = $failure; disposed = $script:fake.Disposed } | ConvertTo-Json -Compress
        """)
        self.assertEqual(json.loads(output.splitlines()[-1]), {
            "failure": "In-place update canary failed with exit code 42.", "disposed": True})

    def test_first_run_launch_failure_is_disposed_and_sanitized(self):
        output = self.evaluate(r"""
            $script:fake = [pscustomobject]@{ Disposed = $false }
            $script:fake | Add-Member ScriptMethod Start { throw 'PRIVATE_FIXTURE_VALUE' }
            $script:fake | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
            function New-InstallerProcess { return $script:fake }
            $failure = ''
            try { Assert-FirstRunSetupLaunch 'unused' }
            catch { $failure = $_.Exception.Message }
            @{ failure = $failure; disposed = $script:fake.Disposed } | ConvertTo-Json -Compress
        """)
        self.assertNotIn("PRIVATE_FIXTURE_VALUE", output)
        self.assertEqual(json.loads(output.splitlines()[-1]), {
            "failure": "The installed application could not be started.", "disposed": True})

    def test_first_run_still_requires_setup_window_and_clean_close(self):
        output = self.evaluate(r"""
            $script:fake = [pscustomobject]@{ Disposed = $false; HasExited = $false;
                ExitCode = 0; MainWindowHandle = [IntPtr]1; MainWindowTitle = 'Wisp Setup';
                Closed = $false; Waits = @() }
            $script:fake | Add-Member ScriptMethod Start { return $true }
            $script:fake | Add-Member ScriptMethod Refresh { }
            $script:fake | Add-Member ScriptMethod CloseMainWindow { $this.Closed = $true; return $true }
            $script:fake | Add-Member ScriptMethod WaitForExit {
                param([int]$Milliseconds)
                $this.Waits += $Milliseconds
                $this.HasExited = $true
                return $true
            }
            $script:fake | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
            function New-InstallerProcess { return $script:fake }
            Assert-FirstRunSetupLaunch 'unused'
            @{ disposed = $script:fake.Disposed; closed = $script:fake.Closed;
               waits = $script:fake.Waits } | ConvertTo-Json -Compress
        """)
        self.assertIn("First-run Wisp Setup window detected.", output)
        self.assertIn("First-run Wisp Setup completed.", output)
        self.assertEqual(json.loads(output.splitlines()[-1]), {
            "disposed": True, "closed": True, "waits": [10000]})

    def test_stage_file_contains_only_fixed_markers(self):
        output = self.evaluate(r"""
            $env:WISP_INSTALLER_STAGE_LOG = Join-Path $FixtureDirectory 'stages.log'
            Write-InstallerLifecycleMarker 'In-place update canary' 'started' $timer
            Write-InstallerLifecycleMarker 'PRIVATE_FIXTURE_VALUE' 'started' $timer
            Write-InstallerLifecycleMarker 'In-place update canary' 'PRIVATE_FIXTURE_VALUE' $timer
            Write-InstallerLifecycleMarker 'In-place update canary' 'native stage observed: restart-manager-query-started' $timer
            $lines = [System.IO.File]::ReadAllLines($env:WISP_INSTALLER_STAGE_LOG)
            @{ lines = $lines } | ConvertTo-Json -Compress
        """)
        self.assertNotIn("PRIVATE_FIXTURE_VALUE", output)
        lines = json.loads(output.splitlines()[-1])["lines"]
        self.assertEqual(len(lines), 2)
        self.assertTrue(lines[0].startswith("In-place update canary started. utc="))
        self.assertTrue(lines[1].startswith(
            "In-place update canary native stage observed: restart-manager-query-started. utc="))

    def test_unwritable_stage_file_does_not_change_process_result(self):
        output = self.evaluate(r"""
            $env:WISP_INSTALLER_STAGE_LOG = Join-Path $FixtureDirectory 'PRIVATE_FIXTURE_VALUE/missing/stages.log'
            $script:fake = [pscustomobject]@{ Disposed = $false; ExitCode = 0 }
            $script:fake | Add-Member ScriptMethod Start { return $true }
            $script:fake | Add-Member ScriptMethod WaitForExit { param([int]$Milliseconds) return $true }
            $script:fake | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
            function New-InstallerProcess { return $script:fake }
            Invoke-CheckedProcess 'unused' @('unused') 'In-place update canary' $log 1
            @{ disposed = $script:fake.Disposed } | ConvertTo-Json -Compress
        """)
        self.assertNotIn("PRIVATE_FIXTURE_VALUE", output)
        self.assertIn("Could not persist installer lifecycle stage evidence.", output)
        self.assertIn("In-place update canary completed.", output)
        self.assertTrue(json.loads(output.splitlines()[-1])["disposed"])


if __name__ == "__main__":
    unittest.main()
