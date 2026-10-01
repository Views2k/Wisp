param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin')
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$sourceDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildDirectory = Join-Path $PSScriptRoot 'obj\mp4'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio C++ tools were not found.' }
$install = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($LASTEXITCODE -ne 0 -or $install.Count -ne 1 -or [string]::IsNullOrWhiteSpace($install[0])) { throw 'A single C++ toolset could not be resolved.' }
$developerCommand = Join-Path $install[0] 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $developerCommand -PathType Leaf)) { throw 'Visual Studio environment setup is missing.' }
foreach ($argumentPath in @($developerCommand, $PSScriptRoot, $sourceDirectory, $buildDirectory, $OutputDirectory)) {
    if ($argumentPath -match '["\r\n%!]') { throw 'Unsupported characters in the build path.' }
}
foreach ($sourceName in @('Mp4ClipWriter.h', 'Mp4ClipWriter.cpp', 'AacEncoder.h', 'AacEncoder.cpp', 'EncodedClipBuffer.h')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $sourceName) -PathType Leaf)) { throw 'MP4 fixture source is incomplete.' }
}
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'LosslessMp4Fixture.cpp') -PathType Leaf)) { throw 'MP4 fixture source is missing.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
[void][IO.Directory]::CreateDirectory($buildDirectory)
$compilerBatch = Join-Path $buildDirectory 'build.cmd'
$executable = Join-Path $OutputDirectory 'Wisp.LosslessMp4Fixture.exe'
$compilerFlags = '/nologo /std:c++17 /permissive- /EHsc /O2 /W4 /WX /MT /Z7 /guard:cf /DWINVER=0x0A00 /D_WIN32_WINNT=0x0A00 /DNTDDI_VERSION=0x0A000010 /DUNICODE /D_UNICODE /DNOMINMAX'
$batch = @"
@echo off
call "$developerCommand" -no_logo -arch=x64 -host_arch=x64 -winsdk=10.0.26100.0
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\LosslessMp4Fixture.obj" "$PSScriptRoot\LosslessMp4Fixture.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\Mp4ClipWriter.obj" "$sourceDirectory\Mp4ClipWriter.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\AacEncoder.obj" "$sourceDirectory\AacEncoder.cpp"
if errorlevel 1 exit /b 1
link.exe /nologo /OUT:"$executable" "$buildDirectory\LosslessMp4Fixture.obj" "$buildDirectory\Mp4ClipWriter.obj" "$buildDirectory\AacEncoder.obj" /DEBUG:FULL /PDB:"$buildDirectory\Wisp.LosslessMp4Fixture.pdb" /PDBALTPATH:Wisp.LosslessMp4Fixture.pdb /DYNAMICBASE /NXCOMPAT /GUARD:CF /OPT:REF /OPT:ICF /INCREMENTAL:NO mfplat.lib mf.lib mfuuid.lib mfreadwrite.lib wmcodecdspuuid.lib ole32.lib oleaut32.lib uuid.lib bcrypt.lib
exit /b %errorlevel%
"@
[IO.File]::WriteAllText($compilerBatch, $batch.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $compilerBatch
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Lossless MP4 fixture compilation failed.' }
[ordered]@{ built = $true; architecture = 'x64'; sdk = '10.0.26100.0'; sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant(); testsRun = $false; graphicsActivated = $false } | ConvertTo-Json -Compress
