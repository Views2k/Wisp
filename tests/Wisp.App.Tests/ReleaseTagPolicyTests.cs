using System.Diagnostics;
using System.Text;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ReleaseTagPolicyTests
{
    [Fact]
    public async Task NumericTagGateNormalizesOnlyStableBoundedVersions()
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(shell));
        var startInfo = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(Harness)));
        startInfo.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(shell)!, "Modules");
        startInfo.Environment["WISP_RELEASE_TAG_SCRIPT"] = Path.Combine(
            RepositoryRoot(), ".github", "scripts", "Test-ReleaseTag.ps1");
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The release-tag parser check exceeded its deadline.");
        }
        var result = await output;
        var diagnostics = await errors;
        Assert.True(process.ExitCode == 0, $"Release-tag parser check failed: {result} {diagnostics}");
        Assert.Contains("release-tag-parser-ok", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseAutomationKeepsCanonicalArtifactsAndBothTagCasesProtected()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        var policy = File.ReadAllText(Path.Combine(root, ".github", "scripts", "Test-ReleaseTag.ps1"));
        var rules = File.ReadAllText(Path.Combine(root, ".github", "rulesets", "protect-release-tags.json"));
        Assert.Contains("tags: [\"v*\", \"V*\"]", workflow, StringComparison.Ordinal);
        Assert.Contains("src/Wisp.App/Wisp.App.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("display_version=\"${version%.0}\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("${GITHUB_REF_NAME#v}", workflow, StringComparison.Ordinal);
        Assert.Contains("Wisp-Setup-${version}.exe", workflow, StringComparison.Ordinal);
        Assert.Contains("--title \"Wisp ${display_version}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("refs/tags/v*", rules, StringComparison.Ordinal);
        Assert.Contains("refs/tags/V*", rules, StringComparison.Ordinal);
        Assert.Contains("$tagCommit[0] -cne $head[0]", policy, StringComparison.Ordinal);
        Assert.Contains("$head[0] -cne $main[0]", policy, StringComparison.Ordinal);
        Assert.Contains("$projectVersion -cne $version -or $installerVersion -cne $version", policy,
            StringComparison.Ordinal);
        Assert.Contains("$releaseNotesHeading -cne \"# Wisp $version\"", policy, StringComparison.Ordinal);
        Assert.Contains("$releaseNotesHeading -cne \"# Wisp $displayVersion\"", policy, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Wisp.sln from the test output directory.");
    }

    private const string Harness = """
        $ErrorActionPreference = 'Stop'
        try {
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $env:WISP_RELEASE_TAG_SCRIPT, [ref]$tokens, [ref]$errors)
            if ($errors.Count -ne 0) { throw 'The release-tag script has syntax errors.' }
            $function = $ast.Find({ param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -ceq 'ConvertTo-CanonicalReleaseVersion'
            }, $true)
            if ($null -eq $function) { throw 'The numeric tag parser is missing.' }
            . ([scriptblock]::Create($function.Extent.Text))
            $accepted = [ordered]@{
                'v1' = '1.0.0'; 'v1.0' = '1.0.0'; 'v1.0.0' = '1.0.0'
                'V1.1' = '1.1.0'; '1.1' = '1.1.0'; '0' = '0.0.0'
                'v1-stable' = '1.0.0'; 'V1.1-STABLE' = '1.1.0'
                'v1.2.3-stable' = '1.2.3'; 'v65535.65535.65535' = '65535.65535.65535'
            }
            foreach ($entry in $accepted.GetEnumerator()) {
                $actual = ConvertTo-CanonicalReleaseVersion $entry.Key
                if ($actual -cne $entry.Value) { throw "Incorrect normalization for $($entry.Key)." }
            }
            foreach ($value in @(
                'v01', 'v1.02', 'v1.0.00', 'v1.0.0.0', 'v1.', 'v1..0', 'v1 stable',
                'v1.1-rc.1', 'v1.1-stable.1', 'v65536', 'v1.65536', 'v1.0.65536',
                'v99999999999999999999999', ' v1', "v1`n", '-1', 'v-1', 'v1-stable ',
                'v1+build', 'stable', 'version1', ('v' + ('1' * 128)))) {
                $rejected = $false
                try { $null = ConvertTo-CanonicalReleaseVersion $value }
                catch { $rejected = $true }
                if (-not $rejected) { throw 'An invalid numeric release tag was accepted.' }
            }
            Write-Output 'release-tag-parser-ok'
        }
        catch {
            Write-Output $_.Exception.Message
            exit 1
        }
        """;
}
