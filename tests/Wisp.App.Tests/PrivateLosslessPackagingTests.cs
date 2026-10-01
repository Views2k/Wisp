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
    [InlineData("mpv-missing")]
    [InlineData("mpv-changed")]
    [InlineData("mpv-extra")]
    [InlineData("mpv-notice-missing")]
    [InlineData("mpv-manifest-missing")]
    [InlineData("mpv-manifest-changed")]
    [InlineData("mpv-source-mismatch")]
    [InlineData("mpv-source-incomplete")]
    [InlineData("mpv-source-not-ready")]
    [InlineData("mpv-source-missing")]
    [InlineData("recorder-missing")]
    [InlineData("recorder-dll")]
    [InlineData("recorder-x86")]
    [InlineData("recorder-truncated")]
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
        . (Join-Path ([IO.Path]::GetDirectoryName($env:WISP_PRIVATE_SCRIPT)) 'ClipDecoderPackaging.ps1')
        foreach ($name in @('Sync-PrivateLosslessTestPayload')) {
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
            [IO.Directory]::CreateDirectory($publish) | Out-Null
            $recorder = Join-Path $publish 'Wisp.Recorder.exe'
            $pe = [byte[]]::new(512)
            [BitConverter]::GetBytes([uint16]0x5A4D).CopyTo($pe, 0)
            [BitConverter]::GetBytes([uint32]0x80).CopyTo($pe, 0x3C)
            [BitConverter]::GetBytes([uint32]0x00004550).CopyTo($pe, 0x80)
            [BitConverter]::GetBytes([uint16]0x8664).CopyTo($pe, 0x84)
            [BitConverter]::GetBytes([uint16]1).CopyTo($pe, 0x86)
            [BitConverter]::GetBytes([uint16]112).CopyTo($pe, 0x94)
            [BitConverter]::GetBytes([uint16]2).CopyTo($pe, 0x96)
            [BitConverter]::GetBytes([uint16]0x020B).CopyTo($pe, 0x98)
            [IO.File]::WriteAllBytes($recorder, $pe)
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
            $mpvDll = Join-Path $publish 'libmpv/win-x64/libmpv-2.dll'
            Write-FixtureFile $mpvDll
            $mpvHash = (Get-FileHash -LiteralPath $mpvDll -Algorithm SHA256).Hash.ToLowerInvariant()
            $mpvNotices = @()
            foreach ($relative in @('LGPL-3.0.txt', 'GPL-3.0.txt', 'libmpv-thirdparty/fixture/COPYING')) {
                $path = Join-Path $publish ('Licenses/' + $relative)
                Write-FixtureFile $path
                $mpvNotices += @{path=$relative; bytes=7; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
            }
            $mpvManifest = @{schemaVersion=1; sourceClosure=@{status='complete'; distributionReady=$true};
                nativeFiles=@(@{path='libmpv-2.dll'; bytes=7; sha256=$mpvHash}); noticeFiles=$mpvNotices}
            $mpvManifestPath = Join-Path $fixture 'reviewed-mpv-manifest.json'
            Write-FixtureFile $mpvManifestPath ($mpvManifest | ConvertTo-Json -Depth 6)
            $mpvDependencyPath = Join-Path $fixture 'reviewed-mpv-dependency.json'
            Write-FixtureFile $mpvDependencyPath (@{schemaVersion=1; binary=@{entry='libmpv-2.dll'; bytes=7; sha256=$mpvHash}} | ConvertTo-Json -Depth 4)
            [IO.File]::Copy($mpvManifestPath, (Join-Path $publish 'Licenses/libmpv-source-manifest.json'))
            [IO.File]::Copy($mpvDependencyPath, (Join-Path $publish 'Licenses/mpv-dependency.json'))
            $firstPlugin = Join-Path $publish ('libvlc/win-x64/' + @($native | Where-Object path -like 'plugins/*')[0].path)
            switch ($env:WISP_PRIVATE_CASE) {
                'missing-plugin' { Remove-Item -LiteralPath $firstPlugin }
                'extra-plugin' { Write-FixtureFile (Join-Path $publish 'libvlc/win-x64/plugins/extra.dll') }
                'changed-plugin' { Write-FixtureFile $firstPlugin 'changed' }
                'missing-notice' { Remove-Item -LiteralPath $notice }
                'missing-managed' { Remove-Item -LiteralPath (Join-Path $publish 'LibVLCSharp.dll') }
                'mpv-missing' { Remove-Item -LiteralPath $mpvDll }
                'mpv-changed' { Write-FixtureFile $mpvDll 'changed' }
                'mpv-extra' { Write-FixtureFile (Join-Path $publish 'libmpv/win-x64/extra.dll') }
                'mpv-notice-missing' { Remove-Item -LiteralPath (Join-Path $publish 'Licenses/LGPL-3.0.txt') }
                'mpv-manifest-missing' { Remove-Item -LiteralPath (Join-Path $publish 'Licenses/libmpv-source-manifest.json') }
                'mpv-manifest-changed' { Write-FixtureFile (Join-Path $publish 'Licenses/mpv-dependency.json') '{}' }
                'mpv-source-mismatch' {
                    $mpvManifest.nativeFiles[0].sha256 = ('0' * 64)
                    Write-FixtureFile $mpvManifestPath ($mpvManifest | ConvertTo-Json -Depth 6)
                }
                'mpv-source-incomplete' { $mpvManifest.sourceClosure.status = 'incomplete' }
                'mpv-source-not-ready' { $mpvManifest.sourceClosure.distributionReady = $false }
                'mpv-source-missing' { $mpvManifest.Remove('sourceClosure') }
                'recorder-missing' { Remove-Item -LiteralPath $recorder }
                'recorder-dll' { $pe[0x97] = 0x20; [IO.File]::WriteAllBytes($recorder, $pe) }
                'recorder-x86' { $pe[0x84] = 0x4C; $pe[0x85] = 1; [IO.File]::WriteAllBytes($recorder, $pe) }
                'recorder-truncated' { [IO.File]::WriteAllBytes($recorder, [byte[]]@(0, 1)) }
            }
            if ($env:WISP_PRIVATE_CASE -in @('mpv-source-incomplete', 'mpv-source-not-ready', 'mpv-source-missing')) {
                Write-FixtureFile $mpvManifestPath ($mpvManifest | ConvertTo-Json -Depth 6)
                [IO.File]::Copy($mpvManifestPath, (Join-Path $publish 'Licenses/libmpv-source-manifest.json'), $true)
            }
            $refused = $false
            try {
                Assert-RecorderExecutable $recorder
                $hashes = Assert-ClipDecoders $publish $manifestPath $mpvManifestPath $mpvDependencyPath
                if ($env:WISP_PRIVATE_CASE -in @('sync', 'wrong-output')) {
                    $target = Join-Path $fixture 'tools/Wisp.UiReview/bin/Release/net8.0-windows'
                    if ($env:WISP_PRIVATE_CASE -eq 'wrong-output') { $target = Join-Path $fixture 'unrelated' }
                    Write-FixtureFile (Join-Path $target 'unrelated.keep') 'preserve'
                    Write-FixtureFile (Join-Path $target 'libvlc/win-x64/plugins/unselected.dll')
                    Write-FixtureFile (Join-Path $target 'libvlc/win-x86/libvlc.dll')
                    Write-FixtureFile (Join-Path $target 'libmpv/win-x86/libmpv-2.dll')
                    Write-FixtureFile (Join-Path $target 'libmpv/win-x64/stale.dll')
                    Sync-PrivateLosslessTestPayload $publish $target $fixture $hashes
                    $actual = Assert-ClipDecoders $target $manifestPath $mpvManifestPath $mpvDependencyPath
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
