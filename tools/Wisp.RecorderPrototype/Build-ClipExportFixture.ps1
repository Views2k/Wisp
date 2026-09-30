param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin\export')
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$buildDirectory = Join-Path $PSScriptRoot 'obj\export'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio C++ tools were not found.' }
$install = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($LASTEXITCODE -ne 0 -or $install.Count -ne 1 -or [string]::IsNullOrWhiteSpace($install[0])) { throw 'A single C++ toolset could not be resolved.' }
$developerCommand = Join-Path $install[0] 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $developerCommand -PathType Leaf)) { throw 'Visual Studio environment setup is missing.' }
foreach ($argumentPath in @($developerCommand, $PSScriptRoot, $buildDirectory, $OutputDirectory)) {
    if ($argumentPath -match '["\r\n%!]') { throw 'Unsupported characters in the build path.' }
}
foreach ($sourceName in @('HardwareEncoder.h', 'HardwareEncoder.cpp', 'HardwareEncoderInternal.h', 'HardwareVideoSession.h', 'HardwareVideoSession.cpp', 'EncodedClipBuffer.h', 'EncodedClipBuffer.cpp', 'Mp4ClipWriter.h', 'Mp4ClipWriter.cpp', 'ClipExportFixture.cpp', 'HdrFrameConverter.h', 'HdrFrameConverter.cpp', 'GpuFrameConverter.h', 'GpuFrameConverter.cpp', 'ConversionOutput.h', 'ConversionOutputContracts.cpp', 'SyntheticHdrFrameProvider.h', 'SyntheticHdrFrameProvider.cpp', 'AacEncoder.h', 'AacEncoder.cpp')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $sourceName) -PathType Leaf)) { throw 'Clip export fixture source is incomplete.' }
}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
[void][IO.Directory]::CreateDirectory($buildDirectory)
$compilerBatch = Join-Path $buildDirectory 'build.cmd'
$executable = Join-Path $OutputDirectory 'Wisp.ClipExportFixture.exe'
$compilerFlags = '/nologo /std:c++17 /permissive- /EHsc /O2 /W4 /WX /MT /Z7 /guard:cf /DWINVER=0x0A00 /D_WIN32_WINNT=0x0A00 /DNTDDI_VERSION=0x0A000010 /DUNICODE /D_UNICODE /DNOMINMAX'
$streamSources = @('EncodedSpool', 'OwnedFileStream', 'SpoolMp4Writer')
$streamCompile = foreach ($source in $streamSources) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot "$source.cpp") -PathType Leaf)) { throw 'Streaming export source is incomplete.' }
    "cl.exe $compilerFlags /c /Fo`"$buildDirectory\$source.obj`" `"$PSScriptRoot\$source.cpp`"`r`nif errorlevel 1 exit /b 1"
}
$streamObjects = ($streamSources | ForEach-Object { "`"$buildDirectory\$_.obj`"" }) -join ' '
$batch = @"
@echo off
call "$developerCommand" -no_logo -arch=x64 -host_arch=x64 -winsdk=10.0.26100.0
if errorlevel 1 exit /b 1
$($streamCompile -join "`r`n")
cl.exe $compilerFlags /c /Fo"$buildDirectory\HardwareEncoder.obj" "$PSScriptRoot\HardwareEncoder.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\ClipExportFixture.obj" "$PSScriptRoot\ClipExportFixture.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\EncodedClipBuffer.obj" "$PSScriptRoot\EncodedClipBuffer.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\Mp4ClipWriter.obj" "$PSScriptRoot\Mp4ClipWriter.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\HdrFrameConverter.obj" "$PSScriptRoot\HdrFrameConverter.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\SyntheticHdrFrameProvider.obj" "$PSScriptRoot\SyntheticHdrFrameProvider.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\AacEncoder.obj" "$PSScriptRoot\AacEncoder.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\HardwareVideoSession.obj" "$PSScriptRoot\HardwareVideoSession.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\GpuFrameConverter.obj" "$PSScriptRoot\GpuFrameConverter.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\ConversionOutputContracts.obj" "$PSScriptRoot\ConversionOutputContracts.cpp"
if errorlevel 1 exit /b 1
link.exe /nologo /OUT:"$executable" "$buildDirectory\HardwareEncoder.obj" "$buildDirectory\ClipExportFixture.obj" "$buildDirectory\EncodedClipBuffer.obj" "$buildDirectory\Mp4ClipWriter.obj" "$buildDirectory\HdrFrameConverter.obj" "$buildDirectory\SyntheticHdrFrameProvider.obj" "$buildDirectory\AacEncoder.obj" "$buildDirectory\HardwareVideoSession.obj" "$buildDirectory\GpuFrameConverter.obj" "$buildDirectory\ConversionOutputContracts.obj" $streamObjects /DEBUG:FULL /PDB:"$buildDirectory\Wisp.ClipExportFixture.pdb" /PDBALTPATH:Wisp.ClipExportFixture.pdb /DYNAMICBASE /NXCOMPAT /GUARD:CF /OPT:REF /OPT:ICF /INCREMENTAL:NO windowsapp.lib d3d11.lib dxgi.lib mfplat.lib mf.lib mfuuid.lib uuid.lib ole32.lib oleaut32.lib user32.lib evr.lib mfreadwrite.lib d3dcompiler.lib wmcodecdspuuid.lib ntdll.lib
exit /b %errorlevel%
"@
[IO.File]::WriteAllText($compilerBatch, $batch.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $compilerBatch
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Clip export fixture compilation failed.' }
[ordered]@{ built = $true; architecture = 'x64'; sdk = '10.0.26100.0'; sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant(); testsRun = $false; encoderActivated = $false } | ConvertTo-Json -Compress
