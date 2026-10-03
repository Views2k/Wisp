using System.Diagnostics;
using System.Text;
using Xunit;

namespace Wisp.App.Tests;

public sealed class PrivateDiagnosticInstallerTests
{
    [Fact]
    public void PrivateBuilderKeepsPublicGateAndMandatoryChecks()
    {
        var root = RepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "installer", "Build-PrivateDiagnosticInstaller.ps1"));
        var canonical = File.ReadAllText(Path.Combine(root, "installer", "Build-Installer.ps1"));
        Assert.Contains("Assert-PublicBuildIdentity $projectText $projectVersion", canonical);
        Assert.Contains("Private packaging requires the exact explicit diagnostic ID and label", script);
        Assert.DoesNotContain("SkipTests", script);
        Assert.DoesNotContain("ValidatedBuildReport", script);
        Assert.DoesNotContain("Publish-ReleaseBundle", script);
        Assert.Contains("artifacts/private-candidates", script);
        Assert.Contains("--verify-no-changes", script);
        Assert.Contains("--filter $nonAllocationFilter", script);
        Assert.Contains("Assert-SinglePassedTestResult", script);
        Assert.Contains("DisabledRecordCallsCreateNoHistoryOrPerCallAllocations", script);
        Assert.Contains("DisabledRendererDoesNotAllocateOrCreateCapture", script);
        Assert.Contains("EnabledNeedleRecordingReportsOfflineCostWithoutATimingThreshold", script);
        Assert.Contains("EnabledUncontendedProducerHasNoPerEventAllocations", script);
        Assert.Contains("-p:PublishSingleFile=false", script);
        Assert.Contains("Assert-PrivatePayloadFiles $publishDirectory", script);
        Assert.Contains("Invoke-InstallerRuntimeValidation $dotnetExecutable", script);
        Assert.Contains("New-InstallerArchive $setupPath", script);
        Assert.Contains("test $solution --configuration Release --no-build --no-restore", script);
        Assert.Contains("Invoke-WithPrivateTestIdentity $DiagnosticBuildId $DiagnosticBuildLabel {", script);
        Assert.Contains("$testHostDirectories", script);
        Assert.True(script.IndexOf("publish $project", StringComparison.Ordinal) < script.IndexOf("test $solution", StringComparison.Ordinal));
        Assert.True(script.IndexOf("test $solution", StringComparison.Ordinal) < script.IndexOf("& $innoExecutable", StringComparison.Ordinal));
        Assert.True(script.IndexOf("Assert-PrivatePayloadFiles $publishDirectory", StringComparison.Ordinal) < script.IndexOf("& $innoExecutable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("valid-identity")]
    [InlineData("missing-identity")]
    [InlineData("mismatched-identity")]
    [InlineData("missing-updater")]
    [InlineData("changed-inno")]
    [InlineData("test-identity-restores-existing")]
    [InlineData("test-identity-restores-empty")]
    [InlineData("test-identity-failure-restores-existing")]
    [InlineData("test-identity-failure-restores-empty")]
    [InlineData("test-identity-missing-id")]
    [InlineData("test-identity-missing-label")]
    public async Task PrivatePackagingGuardsFailClosed(string scenario)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(Harness)));
        start.Environment["WISP_PRIVATE_SCRIPT"] = Path.Combine(RepositoryRoot(), "installer", "Build-PrivateDiagnosticInstaller.ps1");
        start.Environment["WISP_PRIVATE_CASE"] = scenario;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, (await output) + (await error));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root unavailable.");
    }

    private const string Harness = """
        $ErrorActionPreference = 'Stop'
        Set-StrictMode -Version Latest
        $tokens = $null
        $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($env:WISP_PRIVATE_SCRIPT, [ref]$tokens, [ref]$errors)
        if ($errors.Count -ne 0) { throw 'Private script has syntax errors.' }
        foreach ($name in @('Assert-PrivateBuildIdentity', 'Invoke-WithPrivateTestIdentity', 'Assert-PrivatePayloadFiles', 'Replace-PrivateDirective')) {
            $definition = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name}, $false)
            if ($null -eq $definition) { throw 'Missing private validation function.' }
            . ([scriptblock]::Create($definition.Extent.Text))
        }
        $scenario = $env:WISP_PRIVATE_CASE
        $id = 'shift-capture-fixture'
        $label = 'Shift Capture Fixture'
        if ($scenario -like 'test-identity-*') {
            $idVariable = 'WISP_TEST_EXPECTED_DIAGNOSTIC_BUILD_ID'
            $labelVariable = 'WISP_TEST_EXPECTED_DIAGNOSTIC_BUILD_LABEL'
            $previousId = $null
            $previousLabel = $null
            if ($scenario -like '*-existing') { $previousId = 'previous-id'; $previousLabel = 'Previous Label' }
            [Environment]::SetEnvironmentVariable($idVariable, $previousId, 'Process')
            [Environment]::SetEnvironmentVariable($labelVariable, $previousLabel, 'Process')
            $invalid = $scenario -like '*-missing-*'
            if ($scenario -eq 'test-identity-missing-id') { $id = '' }
            if ($scenario -eq 'test-identity-missing-label') { $label = ' ' }
            $throwDuringTest = $scenario -like '*-failure-*'
            $state = [pscustomobject]@{ Invoked = $false }
            $failure = $null
            try {
                Invoke-WithPrivateTestIdentity $id $label {
                    $state.Invoked = $true
                    if ([Environment]::GetEnvironmentVariable($idVariable, 'Process') -cne $id -or
                        [Environment]::GetEnvironmentVariable($labelVariable, 'Process') -cne $label) {
                        throw 'Private identity was not applied to the test action.'
                    }
                    if ($throwDuringTest) { throw 'Expected scoped test failure.' }
                }
            } catch { $failure = $_.Exception.Message }
            $expectedFailure = $null
            if ($invalid) { $expectedFailure = 'Private tests require an explicit expected diagnostic ID, label, and test action.' }
            elseif ($throwDuringTest) { $expectedFailure = 'Expected scoped test failure.' }
            if ($failure -cne $expectedFailure -or $state.Invoked -eq $invalid) { throw 'Private test scope changed failure or invocation behavior.' }
            if ([Environment]::GetEnvironmentVariable($idVariable, 'Process') -cne $previousId -or
                [Environment]::GetEnvironmentVariable($labelVariable, 'Process') -cne $previousLabel) {
                throw 'Private test scope did not restore the previous environment.'
            }
            return
        }
        $text = '<Project><ItemGroup><AssemblyMetadata Include="WispDiagnosticBuildId" Value="shift-capture-fixture"/><AssemblyMetadata Include="WispDiagnosticBuildLabel" Value="Shift Capture Fixture"/></ItemGroup></Project>'
        if ($scenario -eq 'missing-identity') { $text = '<Project />' }
        if ($scenario -eq 'mismatched-identity') { $id = 'wrong-id' }
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ('wisp-private-guard-' + [guid]::NewGuid().ToString('N'))
        try {
            $failed = $false
            try {
                switch ($scenario) {
                    'missing-updater' {
                        [IO.Directory]::CreateDirectory($temporary) | Out-Null
                        Assert-PrivatePayloadFiles $temporary
                    }
                    'changed-inno' { Replace-PrivateDirective 'altered canonical source' '#define original' '#define replacement' | Out-Null }
                    default { Assert-PrivateBuildIdentity $text $id $label }
                }
            } catch { $failed = $true }
            if ($failed -ne ($scenario -ne 'valid-identity')) { throw 'Private guard accepted an invalid input or rejected valid identity.' }
        } finally {
            if ([IO.Directory]::Exists($temporary)) { [IO.Directory]::Delete($temporary, $false) }
        }
        """;
}
