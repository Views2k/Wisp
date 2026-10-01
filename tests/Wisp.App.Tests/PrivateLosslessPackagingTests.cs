using System.Diagnostics;
using System.Text;
using Xunit;

namespace Wisp.App.Tests;

public sealed class PrivateLosslessPackagingTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("missing-plugin")]
    [InlineData("extra-plugin")]
    [InlineData("changed-plugin")]
    [InlineData("missing-notice")]
    [InlineData("missing-managed")]
    [InlineData("sync")]
    [InlineData("wrong-output")]
    public async Task DecoderPackagingUsesTheExactPublishedBundle(string scenario)
    {
        var root = RepositoryRoot();
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell)
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
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(shell)!, "Modules");
        start.Environment["WISP_PRIVATE_SCRIPT"] = Path.Combine(root, "installer", "Build-PrivateDiagnosticInstaller.ps1");
        start.Environment["WISP_DECODER_MANIFEST"] = Path.Combine(root, "LICENSES", "libvlc-3.0.24-source-manifest.json");
        start.Environment["WISP_PRIVATE_CASE"] = scenario;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, (await output) + (await error));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root unavailable.");
    }

    private const string Harness = """
        $ErrorActionPreference = 'Stop'
        Set-StrictMode -Version Latest
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($env:WISP_PRIVATE_SCRIPT, [ref]$tokens, [ref]$errors)
        if ($errors.Count -ne 0) { throw 'Private packaging script has syntax errors.' }
        foreach ($name in @('Get-PrivateRegularFiles', 'Assert-PrivateLosslessPayload', 'Sync-PrivateLosslessTestPayload')) {
            $definition = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name}, $false)
            if ($null -eq $definition) { throw 'Decoder packaging helper is missing.' }
            . ([scriptblock]::Create($definition.Extent.Text))
        }
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
        $fixture = Join-Path $temporaryRoot ('wisp-private-lossless-' + [guid]::NewGuid().ToString('N'))
        function Write-FixtureFile([string]$Path, [string]$Text = 'fixture') {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
            [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
        }
        try {
            $publish = Join-Path $fixture 'published'
            $upstream = Get-Content -LiteralPath $env:WISP_DECODER_MANIFEST -Raw | ConvertFrom-Json
            $native = @()
            foreach ($entry in $upstream.nativeFiles) {
                $path = Join-Path $publish ('libvlc/win-x64/' + $entry.path)
                Write-FixtureFile $path
                $native += @{path=$entry.path; bytes=7; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
            }
            $notice = Join-Path $publish 'Licenses/LibVLCThirdParty/fixture/COPYING'
            Write-FixtureFile $notice
            $manifest = @{nativeFiles=$native; noticeFiles=@(@{path='fixture/COPYING'; bytes=7; sha256=(Get-FileHash -LiteralPath $notice -Algorithm SHA256).Hash.ToLowerInvariant()})}
            $manifestPath = Join-Path $fixture 'reviewed-manifest.json'
            Write-FixtureFile $manifestPath ($manifest | ConvertTo-Json -Depth 6)
            foreach ($relative in @('LibVLCSharp.dll', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'Licenses/LGPL-2.1.txt', 'Licenses/NVIDIA-nvEncodeAPI-MIT.txt')) {
                Write-FixtureFile (Join-Path $publish $relative)
            }
            [IO.File]::Copy($manifestPath, (Join-Path $publish 'Licenses/libvlc-3.0.24-source-manifest.json'))
            $firstPlugin = Join-Path $publish ('libvlc/win-x64/' + @($native | Where-Object path -like 'plugins/*')[0].path)
            switch ($env:WISP_PRIVATE_CASE) {
                'missing-plugin' { Remove-Item -LiteralPath $firstPlugin }
                'extra-plugin' { Write-FixtureFile (Join-Path $publish 'libvlc/win-x64/plugins/extra.dll') }
                'changed-plugin' { Write-FixtureFile $firstPlugin 'changed' }
                'missing-notice' { Remove-Item -LiteralPath $notice }
                'missing-managed' { Remove-Item -LiteralPath (Join-Path $publish 'LibVLCSharp.dll') }
            }
            $refused = $false
            try {
                $hashes = Assert-PrivateLosslessPayload $publish $manifestPath
                if ($env:WISP_PRIVATE_CASE -in @('sync', 'wrong-output')) {
                    $target = Join-Path $fixture 'tools/Wisp.UiReview/bin/Release/net8.0-windows'
                    if ($env:WISP_PRIVATE_CASE -eq 'wrong-output') { $target = Join-Path $fixture 'unrelated' }
                    Write-FixtureFile (Join-Path $target 'unrelated.keep') 'preserve'
                    Write-FixtureFile (Join-Path $target 'libvlc/win-x64/plugins/unselected.dll')
                    Write-FixtureFile (Join-Path $target 'libvlc/win-x86/libvlc.dll')
                    Sync-PrivateLosslessTestPayload $publish $target $fixture $hashes
                    $actual = Assert-PrivateLosslessPayload $target $manifestPath
                    foreach ($relative in $hashes.Keys) {
                        if ($actual[$relative] -cne $hashes[$relative]) { throw 'Decoder synchronization changed a published file.' }
                    }
                    if ([IO.File]::ReadAllText((Join-Path $target 'unrelated.keep')) -cne 'preserve') { throw 'An unrelated output changed.' }
                }
            } catch { $refused = $true }
            $expectedRefusal = $env:WISP_PRIVATE_CASE -notin @('valid', 'sync')
            if ($refused -ne $expectedRefusal) { throw 'Decoder packaging did not enforce its expected file boundaries.' }
        } finally {
            $resolved = [IO.Path]::GetFullPath($fixture)
            if (-not $resolved.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($resolved) -notlike 'wisp-private-lossless-*') { throw 'Unsafe fixture cleanup path.' }
            if ([IO.Directory]::Exists($resolved)) { [IO.Directory]::Delete($resolved, $true) }
        }
        """;
}
