param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin\presets')
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$buildDirectory = Join-Path $PSScriptRoot 'obj\presets'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio C++ tools were not found.' }
$install = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($LASTEXITCODE -ne 0 -or $install.Count -ne 1 -or [string]::IsNullOrWhiteSpace($install[0])) { throw 'A single C++ toolset could not be resolved.' }
$developerCommand = Join-Path $install[0] 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $developerCommand -PathType Leaf)) { throw 'Visual Studio environment setup is missing.' }
foreach ($argumentPath in @($developerCommand, $PSScriptRoot, $buildDirectory, $OutputDirectory)) {
    if ($argumentPath -match '["\r\n%!]') { throw 'Unsupported characters in the build path.' }
}
$sources = @(
    'PresetEncodeFixture', 'RecorderHost', 'RecorderHostPolicyContracts', 'RecorderProtocol',
    'GameWindowCapture', 'GameScreenCapture', 'HardwareEncoder', 'HardwareVideoSession', 'NvencLosslessVideoSession', 'CudaPlanarInput', 'HdrFrameConverter',
    'GpuFrameConverter', 'ProcessAudioCapture', 'AudioTimeline', 'AacEncoder',
    'EncodedSpool', 'OwnedFileStream', 'SpoolMp4Writer', 'Mp4ClipWriter',
    'EncodedClipBuffer', 'ClipThumbnail', 'ClipThumbnailContracts'
)
foreach ($source in $sources) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot "$source.cpp") -PathType Leaf)) { throw 'Preset fixture source is incomplete.' }
}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
[void][IO.Directory]::CreateDirectory($buildDirectory)
$executable = Join-Path $OutputDirectory 'Wisp.PresetEncodeFixture.exe'
$compilerFlags = '/nologo /std:c++17 /permissive- /EHsc /O2 /W4 /WX /MT /Z7 /guard:cf /DWINVER=0x0A00 /D_WIN32_WINNT=0x0A00 /DNTDDI_VERSION=0x0A000010 /DUNICODE /D_UNICODE /DNOMINMAX /DWISP_THUMBNAIL_NO_MAIN'
$compile = foreach ($source in $sources) {
    "cl.exe $compilerFlags /c /Fo`"$buildDirectory\$source.obj`" `"$PSScriptRoot\$source.cpp`"`r`nif errorlevel 1 exit /b 1"
}
$objects = ($sources | ForEach-Object { "`"$buildDirectory\$_.obj`"" }) -join ' '
$batch = @"
@echo off
call "$developerCommand" -no_logo -arch=x64 -host_arch=x64 -winsdk=10.0.26100.0
if errorlevel 1 exit /b 1
$($compile -join "`r`n")
link.exe /nologo /OUT:"$executable" $objects /DEBUG:FULL /PDB:"$buildDirectory\Wisp.PresetEncodeFixture.pdb" /PDBALTPATH:Wisp.PresetEncodeFixture.pdb /DYNAMICBASE /NXCOMPAT /GUARD:CF /OPT:REF /OPT:ICF /INCREMENTAL:NO /MANIFEST:EMBED /MANIFESTUAC:"level='asInvoker' uiAccess='false'" windowsapp.lib runtimeobject.lib d3d11.lib dxgi.lib mfplat.lib mf.lib mfuuid.lib uuid.lib ole32.lib oleaut32.lib user32.lib dwmapi.lib evr.lib mfreadwrite.lib d3dcompiler.lib wmcodecdspuuid.lib ntdll.lib
exit /b %errorlevel%
"@
$compilerBatch = Join-Path $buildDirectory 'build.cmd'
[IO.File]::WriteAllText($compilerBatch, $batch.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $compilerBatch
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Preset fixture compilation failed.' }
$hashStream = [IO.File]::OpenRead($executable)
$hashAlgorithm = [Security.Cryptography.SHA256]::Create()
try { $sha256 = [BitConverter]::ToString($hashAlgorithm.ComputeHash($hashStream)).Replace('-', '').ToLowerInvariant() }
finally { $hashAlgorithm.Dispose(); $hashStream.Dispose() }
[ordered]@{ built = $true; architecture = 'x64'; sdk = '10.0.26100.0'; sha256 = $sha256; testsRun = $false; captureActivated = $false } | ConvertTo-Json -Compress
