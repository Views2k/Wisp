#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ManifestPath,
    [string]$CacheRoot,
    [string]$ArchivePath,
    [switch]$NoDownload
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Windows PowerShell initializes this script root after advanced parameter binding.
if ([string]::IsNullOrWhiteSpace($ManifestPath)) { $ManifestPath = Join-Path $PSScriptRoot 'mpv-dependency.json' }
if ([string]::IsNullOrWhiteSpace($CacheRoot)) { $CacheRoot = Join-Path $PSScriptRoot '..\work\dependencies\libmpv' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Net.Http
if (-not ('WispMpvRestoreDirectory' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class WispMpvRestoreDirectory {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out Tag value, uint size);
    [StructLayout(LayoutKind.Sequential)] public struct Tag { public uint Attributes; public uint ReparseTag; }
}
'@
}
$held = [Collections.Generic.List[IDisposable]]::new()
$temporary = [Collections.Generic.List[string]]::new()
function Hold-Directory([string]$Path, [bool]$Create = $false) {
    $chain = [Collections.Generic.Stack[string]]::new()
    for ($part = [IO.DirectoryInfo]$Path; $null -ne $part; $part = $part.Parent) { $chain.Push($part.FullName) }
    while ($chain.Count -gt 0) {
        $current = $chain.Pop()
        if (-not [IO.Directory]::Exists($current)) {
            if (-not $Create) { throw 'An mpv dependency parent is missing.' }
            [void][IO.Directory]::CreateDirectory($current)
        }
        $handle = [WispMpvRestoreDirectory]::CreateFileW($current, 0x80, 3, [IntPtr]::Zero, 3, 0x02200000, [IntPtr]::Zero)
        $held.Add($handle)
        $tag = [WispMpvRestoreDirectory+Tag]::new()
        if ($handle.IsInvalid -or -not [WispMpvRestoreDirectory]::GetFileInformationByHandleEx($handle, 9, [ref]$tag, 8) -or
            ($tag.Attributes -band 0x10) -eq 0 -or ($tag.Attributes -band 0x400) -ne 0) { throw 'mpv cache parents must be regular directories.' }
    }
}
function Open-VerifiedFile([string]$Path, [long]$Bytes, [string]$Hash) {
    if (([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'mpv dependency reparse file refused.' }
    $file = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($file.Length -ne $Bytes) { throw 'mpv dependency size mismatch.' }
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($hasher.ComputeHash($file)).Replace('-', '').ToLowerInvariant() }
        finally { $hasher.Dispose() }
        if ($actual -cne $Hash) { throw 'mpv dependency hash mismatch.' }
        $file.Position = 0
        return $file
    } catch { $file.Dispose(); throw }
}
function Receive-Archive([string]$Url, [string]$Destination, [long]$ExpectedBytes) {
    $handler = [Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(120))
    $failureReason = 'request-failed'
    try {
        $uri = [Uri]$Url
        for ($redirect = 0; $redirect -le 5; $redirect++) {
            if ($uri.Scheme -cne 'https' -or $uri.UserInfo.Length -ne 0 -or $uri.Port -ne 443 -or
                $uri.Host -notin @('github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com')) { throw 'mpv download destination refused.' }
            $response = $client.GetAsync($uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -in @(301, 302, 303, 307, 308)) {
                    if ($null -eq $response.Headers.Location) { throw 'mpv redirect is missing.' }
                    $uri = [Uri]::new($uri, $response.Headers.Location); continue
                }
                if (-not $response.IsSuccessStatusCode) {
                    $failureReason = 'http-' + [int]$response.StatusCode
                    throw 'mpv archive response is invalid.'
                }
                if ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -ne $ExpectedBytes) {
                    $failureReason = 'response-size-mismatch'
                    throw 'mpv archive response is invalid.'
                }
                $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                $outputStream = $null
                try {
                    $outputStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    $buffer = [byte[]]::new(65536); [long]$total = 0
                    while (($count = $inputStream.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                        $total += $count
                        if ($total -gt $ExpectedBytes) { throw 'mpv archive exceeds its pinned size.' }
                        $outputStream.Write($buffer, 0, $count)
                    }
                    if ($total -ne $ExpectedBytes) { throw 'mpv archive is incomplete.' }
                    $outputStream.Flush($true)
                } finally { if ($null -ne $outputStream) { $outputStream.Dispose() }; $inputStream.Dispose() }
                return
            } finally { $response.Dispose() }
        }
        throw 'mpv redirect limit exceeded.'
    } catch {
        if ($deadline.IsCancellationRequested) { $failureReason = 'timeout' }
        throw ('Pinned mpv download failed ({0}). Use a verified local archive or retry when the pinned artifact is reachable.' -f $failureReason)
    }
    finally { $deadline.Dispose(); $client.Dispose(); $handler.Dispose() }
}
try {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.archive.bytes -le 0 -or $manifest.archive.bytes -gt 128MB -or
        [string]$manifest.archive.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
        ([string]$manifest.archive.url -cnotmatch '^https://github\.com/mpv-player/mpv/releases/download/[^/?#]+/[^/?#]+\.zip$' -and
            [string]$manifest.archive.url -cne 'https://github.com/Views2k/Wisp/releases/download/playback-runtime-a1f50f2c3/libmpv-v0.41.0-dev-ga1f50f2c3-36640285359-x86_64-w64-mingw32-lgpl.zip') -or
        $manifest.binary.entry -cne 'libmpv-2.dll' -or $manifest.binary.bytes -le 0 -or $manifest.binary.bytes -gt 256MB -or
        [string]$manifest.binary.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'mpv dependency manifest is invalid.' }
    $cache = [IO.Path]::GetFullPath($CacheRoot)
    if ($cache.StartsWith('\\') -or $cache.Substring(2).Contains(':')) { throw 'mpv requires a local cache directory.' }
    $directory = Join-Path $cache $manifest.archive.sha256
    Hold-Directory $directory $true
    $lockPath = Join-Path $directory '.restore.lock'
    if ([IO.File]::Exists($lockPath) -and (([IO.File]::GetAttributes($lockPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'mpv restore lock is not regular.' }
    $lock = $null; $waiting = [Diagnostics.Stopwatch]::StartNew()
    while ($null -eq $lock) {
        try { $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] { if ($waiting.Elapsed.TotalSeconds -ge 60) { throw 'mpv dependency restore is already busy.' }; Start-Sleep -Milliseconds 100 }
    }
    $held.Add($lock)
    $dll = Join-Path $directory 'libmpv-2.dll'
    if ([IO.File]::Exists($dll)) {
        $verified = Open-VerifiedFile $dll $manifest.binary.bytes $manifest.binary.sha256
        $verified.Dispose()
    } else {
        $archive = Join-Path $directory 'archive.zip'
        if (-not [string]::IsNullOrWhiteSpace($ArchivePath)) {
            $archive = [IO.Path]::GetFullPath($ArchivePath)
            Hold-Directory ([IO.Path]::GetDirectoryName($archive))
        } elseif (-not [IO.File]::Exists($archive)) {
            if ($NoDownload) { throw 'The pinned mpv archive is not cached; download is disabled.' }
            $download = Join-Path $directory ([guid]::NewGuid().ToString('N') + '.download')
            $temporary.Add($download)
            Receive-Archive $manifest.archive.url $download $manifest.archive.bytes
            $verified = Open-VerifiedFile $download $manifest.archive.bytes $manifest.archive.sha256
            $verified.Dispose(); [IO.File]::Move($download, $archive)
        }
        $inputStream = Open-VerifiedFile $archive $manifest.archive.bytes $manifest.archive.sha256
        try {
            $zip = [IO.Compression.ZipArchive]::new($inputStream, [IO.Compression.ZipArchiveMode]::Read, $true)
            try {
                if ($zip.Entries.Count -gt 256) { throw 'mpv archive entry limit exceeded.' }
                $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                $member = $null; [long]$total = 0
                foreach ($entry in $zip.Entries) {
                    $name = $entry.FullName
                    if ([string]::IsNullOrEmpty($name) -or $name.StartsWith('/') -or $name.Contains('\') -or $name.Contains(':') -or
                        @($name.TrimEnd('/').Split('/') | Where-Object { $_ -eq '.' -or $_ -eq '..' -or $_ -eq '' }).Count -ne 0 -or
                        -not $names.Add($name) -or ($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000) { throw 'mpv archive entry path refused.' }
                    $total += $entry.Length
                    if ($total -gt 512MB -or $entry.Length -lt 0) { throw 'mpv archive expansion limit exceeded.' }
                    if ($name -ceq $manifest.binary.entry) { $member = $entry }
                }
                if ($null -eq $member -or $member.Length -ne $manifest.binary.bytes) { throw 'mpv archive binary is missing or mismatched.' }
                $staged = Join-Path $directory ([guid]::NewGuid().ToString('N') + '.dll.tmp'); $temporary.Add($staged)
                $source = $member.Open()
                $destination = $null
                try {
                    $destination = [IO.File]::Open($staged, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    $buffer = [byte[]]::new(65536); [long]$copied = 0
                    while (($count = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $copied += $count
                        if ($copied -gt $manifest.binary.bytes) { throw 'mpv binary expansion exceeds its pinned size.' }
                        $destination.Write($buffer, 0, $count)
                    }
                    $destination.Flush($true)
                } finally { if ($null -ne $destination) { $destination.Dispose() }; $source.Dispose() }
                $verified = Open-VerifiedFile $staged $manifest.binary.bytes $manifest.binary.sha256
                $verified.Dispose(); [IO.File]::Move($staged, $dll)
            } finally { $zip.Dispose() }
        } finally { $inputStream.Dispose() }
    }
    Write-Output 'Pinned mpv runtime verified.'
} finally {
    foreach ($path in $temporary) { if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) } }
    for ($index = $held.Count - 1; $index -ge 0; $index--) { $held[$index].Dispose() }
}
