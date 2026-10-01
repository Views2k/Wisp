param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateSet('baseline', 'current')][string]$Source = 'baseline',
    [switch]$Timing
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$workPrefix = (Join-Path $repo 'work') + [IO.Path]::DirectorySeparatorChar
if (-not [IO.Path]::IsPathRooted($OutputDirectory) -or -not $output.StartsWith($workPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $output -match '["\r\n%!]') { throw 'A fresh absolute checkout work output is required.' }
if (Test-Path -LiteralPath $output) { throw 'Build output must not exist.' }
$parent = [IO.DirectoryInfo][IO.Path]::GetDirectoryName($output)
for ($part = $parent; $null -ne $part; $part = $part.Parent) {
    if (-not $part.Exists -or ($part.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Existing non-reparse output parents are required.' }
}
$baseline = 'eb975e9d1565a7ec954464a3e6b8683500d7b770'
$revision = @(& git -C $repo rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $revision.Count -ne 1) { throw 'Source revision could not be read.' }
[void][IO.Directory]::CreateDirectory($output)
$snapshot = Join-Path $output 'source'
[void][IO.Directory]::CreateDirectory($snapshot)
if ($Source -eq 'baseline') {
    $archive = Join-Path $output 'baseline-source.zip'
    & git -C $repo archive --format=zip "--output=$archive" $baseline -- tools/Wisp.RecorderPrototype
    if ($LASTEXITCODE -ne 0) { throw 'Exact baseline source archive failed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            if (-not $entry.FullName.StartsWith('tools/Wisp.RecorderPrototype/', [StringComparison]::Ordinal) -and
                $entry.FullName -notin @('tools/', 'tools/Wisp.RecorderPrototype/')) { throw 'Unexpected baseline archive entry.' }
            if ($entry.FullName.Split('/') -contains '..' -or $entry.FullName.Contains('\') -or $entry.FullName.Contains(':')) { throw 'Invalid archive path.' }
            $destination = [IO.Path]::GetFullPath((Join-Path $snapshot $entry.FullName))
            if (-not $destination.StartsWith($snapshot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive containment failed.' }
            if ($entry.FullName.EndsWith('/')) { [void][IO.Directory]::CreateDirectory($destination); continue }
            if (($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000 -or $entry.Length -gt 64MB) { throw 'Archive entry is not bounded regular source.' }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            $inputStream = $entry.Open()
            $outputStream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    } finally { $zip.Dispose() }
    $native = Join-Path $snapshot 'tools\Wisp.RecorderPrototype'
    $sourceRevision = $baseline
} else {
    $native = Join-Path $snapshot 'tools\Wisp.RecorderPrototype'
    [void][IO.Directory]::CreateDirectory($native)
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repo 'tools\Wisp.RecorderPrototype') -File) {
        if ($file.Extension -notin @('.cpp', '.h')) { continue }
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse source refused.' }
        [IO.File]::Copy($file.FullName, (Join-Path $native $file.Name), $false)
    }
    [void][IO.Directory]::CreateDirectory((Join-Path $native 'LosslessProbe'))
    [IO.File]::Copy((Join-Path $repo 'tools\Wisp.RecorderPrototype\LosslessProbe\nvEncodeAPI.h'), (Join-Path $native 'LosslessProbe\nvEncodeAPI.h'), $false)
    $sourceRevision = $revision[0]
}
$fixtureSource = Join-Path $output 'fixture'
[void][IO.Directory]::CreateDirectory($fixtureSource)
foreach ($name in @('SyntheticHostFixture.cpp', 'SyntheticCapture.cpp', 'SyntheticTiming.cpp', 'SyntheticTiming.h', 'Apply-SyntheticTiming.ps1')) {
    [IO.File]::Copy((Join-Path $PSScriptRoot $name), (Join-Path $fixtureSource $name), $false)
}
$sources = @('RecorderHost', 'RecorderProtocol', 'HardwareEncoder', 'HardwareVideoSession', 'NvencLosslessVideoSession',
    'HdrFrameConverter', 'GpuFrameConverter', 'ProcessAudioCapture', 'AudioTimeline', 'AacEncoder', 'EncodedSpool',
    'OwnedFileStream', 'SpoolMp4Writer', 'Mp4ClipWriter', 'EncodedClipBuffer')
$units = @($sources | ForEach-Object { Join-Path $native "${_}.cpp" }) + @(
    (Join-Path $fixtureSource 'SyntheticHostFixture.cpp'), (Join-Path $fixtureSource 'SyntheticCapture.cpp'), (Join-Path $fixtureSource 'SyntheticTiming.cpp'))
foreach ($unit in $units) { if (-not (Test-Path -LiteralPath $unit -PathType Leaf)) { throw 'Fixture source is incomplete.' } }
function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path); $hasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose(); $stream.Dispose() }
}
if ((Get-Sha256 (Join-Path $native 'LosslessProbe\nvEncodeAPI.h')) -ne '4fe4094541ef0f8a13249d97a8692dc5f835a6e9dd42eeadb3e2f7321d54dc7e') { throw 'Pinned NVIDIA header mismatch.' }
$beforeInventory = @(Get-ChildItem -LiteralPath $native -Recurse -File | Where-Object { $_.Extension -in @('.h', '.cpp') } | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($native.Length + 1).Replace('\', '/'); sha256 = Get-Sha256 $_.FullName }
})
if ($Timing) { & (Join-Path $fixtureSource 'Apply-SyntheticTiming.ps1') -NativeDirectory $native -EvidenceDirectory (Join-Path $output 'instrumentation') }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$install = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($LASTEXITCODE -ne 0 -or $install.Count -ne 1) { throw 'A single C++ toolset is required.' }
$developerCommand = Join-Path $install[0] 'Common7\Tools\VsDevCmd.bat'
foreach ($path in @($developerCommand, $native, $fixtureSource, $output)) { if ($path -match '["\r\n%!]') { throw 'Unsupported build path.' } }
$objects = Join-Path $output 'objects'
[void][IO.Directory]::CreateDirectory($objects)
$compilerFlags = '/nologo /std:c++17 /permissive- /EHsc /O2 /W4 /WX /MT /Z7 /guard:cf /DWINVER=0x0A00 /D_WIN32_WINNT=0x0A00 /DNTDDI_VERSION=0x0A000010 /DUNICODE /D_UNICODE /DNOMINMAX /DWISP_SYNTHETIC_HOST_FIXTURE'
if ($Timing) { $compilerFlags += ' /DWISP_SYNTHETIC_HOST_TIMING' }
$compile = foreach ($unit in $units) {
    $object = Join-Path $objects ([IO.Path]::GetFileNameWithoutExtension($unit) + '.obj')
    "cl.exe $compilerFlags /I`"$native`" /I`"$fixtureSource`" /c /Fo`"$object`" `"$unit`"`r`nif errorlevel 1 exit /b 1"
}
$objectArguments = ($units | ForEach-Object { '"' + (Join-Path $objects ([IO.Path]::GetFileNameWithoutExtension($_) + '.obj')) + '"' }) -join ' '
$exe = Join-Path $output 'Wisp.SyntheticHostFixture.exe'
$batch = @"
@echo off
call "$developerCommand" -no_logo -arch=x64 -host_arch=x64 -winsdk=10.0.26100.0
if errorlevel 1 exit /b 1
$($compile -join "`r`n")
link.exe /nologo /OUT:"$exe" $objectArguments /DEBUG:FULL /PDB:"$output\Wisp.SyntheticHostFixture.pdb" /PDBALTPATH:Wisp.SyntheticHostFixture.pdb /DYNAMICBASE /NXCOMPAT /GUARD:CF /OPT:REF /OPT:ICF /INCREMENTAL:NO /MANIFEST:EMBED /MANIFESTUAC:"level='asInvoker' uiAccess='false'" windowsapp.lib runtimeobject.lib d3d11.lib dxgi.lib mfplat.lib mf.lib mfuuid.lib uuid.lib ole32.lib oleaut32.lib user32.lib evr.lib mfreadwrite.lib d3dcompiler.lib wmcodecdspuuid.lib ntdll.lib
exit /b %errorlevel%
"@
$batchPath = Join-Path $output 'build.cmd'
[IO.File]::WriteAllText($batchPath, $batch.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $batchPath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Synthetic host fixture build failed.' }
$inventory = @(Get-ChildItem -LiteralPath $native -Recurse -File | Where-Object { $_.Extension -in @('.h', '.cpp') } | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($native.Length + 1).Replace('\', '/'); sha256 = Get-Sha256 $_.FullName }
})
$provenance = [ordered]@{ source = $Source; revision = $sourceRevision; baselineSourceVerified = ($Source -eq 'baseline');
    baselineUnmodified = ($Source -eq 'baseline' -and -not $Timing); timingInstrumented = [bool]$Timing;
    nativeSourcesBeforeInstrumentation = $beforeInventory; nativeSources = $inventory;
    fixtureSources = @(Get-ChildItem -LiteralPath $fixtureSource -File | ForEach-Object { [ordered]@{ path = $_.Name; sha256 = Get-Sha256 $_.FullName } });
    executableSha256 = Get-Sha256 $exe; diagnosticVariants = @('baseline_save_at_thirty_five_seconds', 'high_output_eight_seconds_no_save', 'varying_8x8_eight_seconds_no_save');
    selectedVariantRecordedIn = 'fixture.json'; desktopCaptureLinked = $false; windowsCreated = $false; executionPerformed = $false }
[IO.File]::WriteAllText((Join-Path $output 'build-provenance.json'), ($provenance | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
[ordered]@{ built = $true; source = $Source; sha256 = $provenance.executableSha256; testsRun = $false; captureActivated = $false } | ConvertTo-Json -Compress
