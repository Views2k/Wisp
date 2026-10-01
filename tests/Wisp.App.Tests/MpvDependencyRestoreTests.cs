using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Wisp.App.Tests;

public sealed class MpvDependencyRestoreTests
{
    [Fact]
    public void NormalBuildRestoresThePinnedRuntimeAsReplaceableContent()
    {
        var root = RepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "Wisp.App", "Wisp.App.csproj"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tools", "mpv-dependency.json")));
        var digest = manifest.RootElement.GetProperty("archive").GetProperty("sha256").GetString()!;
        Assert.EndsWith(digest, project.Descendants("MpvDependencyDirectory").Single().Value, StringComparison.Ordinal);
        var content = project.Descendants("Content").Single(item => (string?)item.Attribute("Link") == @"libmpv\win-x64\libmpv-2.dll");
        Assert.Equal("true", (string?)content.Attribute("ExcludeFromSingleFile"));
        Assert.Equal("PreserveNewest", (string?)content.Attribute("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)content.Attribute("CopyToPublishDirectory"));
        var target = project.Descendants("Target").Single(item => (string?)item.Attribute("Name") == "RestoreWispMpvDependency");
        Assert.Equal("PrepareForBuild", (string?)target.Attribute("BeforeTargets"));
        Assert.Contains("Restore-MpvDependency.ps1", (string)target.Element("Exec")!.Attribute("Command")!, StringComparison.Ordinal);
        Assert.Contains("DesignTimeBuild", (string)target.Attribute("Condition")!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("cached-offline")]
    [InlineData("archive-hash")]
    [InlineData("binary-hash")]
    [InlineData("missing-binary")]
    [InlineData("duplicate-entry")]
    [InlineData("escaping-entry")]
    [InlineData("invalid-cache")]
    [InlineData("no-download")]
    [InlineData("file-defaults")]
    public async Task RestoreUsesOnlyVerifiedBoundedLocalArtifacts(string scenario)
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
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
        start.Environment["WISP_MPV_RESTORE"] = Path.Combine(RepositoryRoot(), "tools", "Restore-MpvDependency.ps1");
        start.Environment["WISP_MPV_CASE"] = scenario;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
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
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
        $fixture = Join-Path $parent ('wisp-mpv-restore-' + [guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($fixture)
        function Digest([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
        try {
            $source = Join-Path $fixture 'payload.bin'
            [IO.File]::WriteAllBytes($source, [byte[]](1, 2, 3, 4, 5, 6, 7))
            $archive = Join-Path $fixture 'fixture.zip'
            $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
            try {
                $name = if ($env:WISP_MPV_CASE -eq 'missing-binary') { 'other.dll' } else { 'libmpv-2.dll' }
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $source, $name)
                if ($env:WISP_MPV_CASE -eq 'duplicate-entry') { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $source, $name) }
                if ($env:WISP_MPV_CASE -eq 'escaping-entry') { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $source, '../outside.dll') }
            } finally { $zip.Dispose() }
            $archiveHash = Digest $archive
            $manifest = @{schemaVersion=1; archive=@{url='https://github.com/mpv-player/mpv/releases/download/test/fixture.zip'; bytes=(Get-Item -LiteralPath $archive).Length; sha256=$archiveHash};
                binary=@{entry='libmpv-2.dll'; bytes=7; sha256=(Digest $source)}}
            if ($env:WISP_MPV_CASE -eq 'archive-hash') { $manifest.archive.sha256 = '0' * 64 }
            if ($env:WISP_MPV_CASE -eq 'binary-hash') { $manifest.binary.sha256 = '0' * 64 }
            $manifestPath = Join-Path $fixture 'manifest.json'
            [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4))
            $cache = Join-Path $fixture 'cache'
            $cachedDll = Join-Path (Join-Path $cache $manifest.archive.sha256) 'libmpv-2.dll'
            if ($env:WISP_MPV_CASE -eq 'file-defaults') {
                $tools = Join-Path $fixture 'tools'
                [void][IO.Directory]::CreateDirectory($tools)
                $scriptCopy = Join-Path $tools 'Restore-MpvDependency.ps1'
                [IO.File]::Copy($env:WISP_MPV_RESTORE, $scriptCopy)
                [IO.File]::Copy($manifestPath, (Join-Path $tools 'mpv-dependency.json'))
                $cache = Join-Path $fixture 'work/dependencies/libmpv'
                $cachedDll = Join-Path (Join-Path $cache $manifest.archive.sha256) 'libmpv-2.dll'
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($cachedDll))
                [IO.File]::Copy($archive, (Join-Path ([IO.Path]::GetDirectoryName($cachedDll)) 'archive.zip'))
            }
            if ($env:WISP_MPV_CASE -eq 'invalid-cache') {
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($cachedDll))
                [IO.File]::WriteAllText($cachedDll, 'changed')
            }
            $refused = $false
            try {
                if ($env:WISP_MPV_CASE -eq 'file-defaults') {
                    & (Join-Path $PSHOME 'powershell.exe') -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $scriptCopy -NoDownload | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell default-path restore failed.' }
                } elseif ($env:WISP_MPV_CASE -eq 'no-download') {
                    & $env:WISP_MPV_RESTORE -ManifestPath $manifestPath -CacheRoot $cache -NoDownload | Out-Null
                } else {
                    & $env:WISP_MPV_RESTORE -ManifestPath $manifestPath -CacheRoot $cache -ArchivePath $archive -NoDownload | Out-Null
                }
                if ($env:WISP_MPV_CASE -eq 'cached-offline') {
                    [IO.File]::Delete($archive)
                    & $env:WISP_MPV_RESTORE -ManifestPath $manifestPath -CacheRoot $cache -NoDownload | Out-Null
                }
            } catch { $refused = $true }
            $expectedRefusal = $env:WISP_MPV_CASE -notin @('valid', 'cached-offline', 'file-defaults')
            if ($refused -ne $expectedRefusal) { throw 'Pinned restore acceptance differed from the case.' }
            if (-not $refused -and (Digest $cachedDll) -cne (Digest $source)) { throw 'Restored runtime differs from the fixture.' }
            if ($env:WISP_MPV_CASE -eq 'invalid-cache' -and [IO.File]::ReadAllText($cachedDll) -cne 'changed') { throw 'Existing cache was overwritten.' }
            if ([IO.File]::Exists((Join-Path $fixture 'outside.dll'))) { throw 'Archive escaped its cache.' }
        } finally {
            $resolved = [IO.Path]::GetFullPath($fixture)
            if (-not $resolved.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($resolved) -notlike 'wisp-mpv-restore-*') { throw 'Unsafe fixture cleanup path.' }
            if ([IO.Directory]::Exists($resolved)) { [IO.Directory]::Delete($resolved, $true) }
        }
        """;
}
