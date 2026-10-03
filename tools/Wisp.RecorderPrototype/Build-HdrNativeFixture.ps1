param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'),
    [switch]$FourK
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$sourceDirectory = $PSScriptRoot
$buildDirectory = Join-Path $PSScriptRoot $(if ($FourK) { 'obj\hdr-native-2160' } else { 'obj\hdr-native' })
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio C++ tools were not found.' }
$install = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($LASTEXITCODE -ne 0 -or $install.Count -ne 1 -or [string]::IsNullOrWhiteSpace($install[0])) { throw 'A single C++ toolset could not be resolved.' }
$developerCommand = Join-Path $install[0] 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $developerCommand -PathType Leaf)) { throw 'Visual Studio environment setup is missing.' }
foreach ($argumentPath in @($developerCommand, $PSScriptRoot, $sourceDirectory, $buildDirectory, $OutputDirectory)) {
    if ($argumentPath -match '["\r\n%!]') { throw 'Unsupported characters in the build path.' }
}
foreach ($sourceName in @('NvencLosslessVideoSession.h', 'NvencLosslessVideoSession.cpp', 'HdrFrameConverter.h', 'HdrFrameConverter.cpp')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $sourceName) -PathType Leaf)) { throw 'Session fixture source is incomplete.' }
}
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'HdrNativeFixture.cpp') -PathType Leaf)) { throw 'Session fixture source is missing.' }
if ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'LosslessProbe/nvEncodeAPI.h') -Algorithm SHA256).Hash.ToLowerInvariant() -ne '4fe4094541ef0f8a13249d97a8692dc5f835a6e9dd42eeadb3e2f7321d54dc7e') { throw 'Pinned NVIDIA header integrity check failed.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
[void][IO.Directory]::CreateDirectory($buildDirectory)
$compilerBatch = Join-Path $buildDirectory 'build.cmd'
$executable = Join-Path $OutputDirectory 'Wisp.HdrNativeFixture.exe'
$compilerFlags = '/nologo /std:c++17 /permissive- /EHsc /O2 /W4 /WX /MT /Z7 /guard:cf /DWISP_HDR_FIXTURE /DWINVER=0x0A00 /D_WIN32_WINNT=0x0A00 /DNTDDI_VERSION=0x0A000010 /DUNICODE /D_UNICODE /DNOMINMAX'
if ($FourK) { $compilerFlags += ' /DWISP_HDR_FIXTURE_4K' }
$batch = @"
@echo off
call "$developerCommand" -no_logo -arch=x64 -host_arch=x64 -winsdk=10.0.26100.0
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\HdrNativeFixture.obj" "$PSScriptRoot\HdrNativeFixture.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\CudaPlanarInput.obj" "$sourceDirectory\CudaPlanarInput.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\NvencLosslessVideoSession.obj" "$sourceDirectory\NvencLosslessVideoSession.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\HdrFrameConverter.obj" "$sourceDirectory\HdrFrameConverter.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\Mp4ClipWriter.obj" "$sourceDirectory\Mp4ClipWriter.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\AacEncoder.obj" "$sourceDirectory\AacEncoder.cpp"
if errorlevel 1 exit /b 1
link.exe /nologo /OUT:"$executable" "$buildDirectory\HdrNativeFixture.obj" "$buildDirectory\NvencLosslessVideoSession.obj" "$buildDirectory\CudaPlanarInput.obj" "$buildDirectory\HdrFrameConverter.obj" "$buildDirectory\Mp4ClipWriter.obj" "$buildDirectory\AacEncoder.obj" /DEBUG:FULL /PDB:"$buildDirectory\Wisp.HdrNativeFixture.pdb" /PDBALTPATH:Wisp.HdrNativeFixture.pdb /DYNAMICBASE /NXCOMPAT /GUARD:CF /OPT:REF /OPT:ICF /INCREMENTAL:NO mfplat.lib mf.lib mfreadwrite.lib wmcodecdspuuid.lib oleaut32.lib mfuuid.lib ole32.lib d3dcompiler.lib d3d11.lib dxgi.lib uuid.lib
exit /b %errorlevel%
"@
[IO.File]::WriteAllText($compilerBatch, $batch.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $compilerBatch
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Lossless session fixture compilation failed.' }
[ordered]@{ built = $true; architecture = 'x64'; sdk = '10.0.26100.0'; height = $(if ($FourK) { 2160 } else { 1080 }); sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant(); testsRun = $false; graphicsActivated = $false } | ConvertTo-Json -Compress
