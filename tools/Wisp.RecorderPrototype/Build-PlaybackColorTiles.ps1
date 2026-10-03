param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin\playback-color'))
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$buildDirectory = Join-Path $PSScriptRoot 'obj\playback-color'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio C++ tools were not found.' }
$install = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($LASTEXITCODE -ne 0 -or $install.Count -ne 1) { throw 'A single C++ toolset could not be resolved.' }
$developerCommand = Join-Path $install[0] 'Common7\Tools\VsDevCmd.bat'
foreach ($argumentPath in @($developerCommand, $PSScriptRoot, $buildDirectory, $OutputDirectory)) {
    if ($argumentPath -match '["\r\n%!]') { throw 'Unsupported characters in the build path.' }
}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
[void][IO.Directory]::CreateDirectory($buildDirectory)
$library = Join-Path $OutputDirectory 'Wisp.PlaybackColorTiles.dll'
$batch = @"
@echo off
call "$developerCommand" -no_logo -arch=x64 -host_arch=x64 -winsdk=10.0.26100.0
if errorlevel 1 exit /b 1
cl.exe /nologo /std:c++17 /permissive- /EHsc /O2 /W4 /WX /MT /Z7 /guard:cf /DWINVER=0x0A00 /D_WIN32_WINNT=0x0A00 /DUNICODE /D_UNICODE /DNOMINMAX /c /Fo"$buildDirectory\PlaybackColorTiles.obj" "$PSScriptRoot\PlaybackColorTiles.cpp"
if errorlevel 1 exit /b 1
link.exe /nologo /DLL /OUT:"$library" "$buildDirectory\PlaybackColorTiles.obj" /DEBUG:FULL /PDB:"$buildDirectory\PlaybackColorTiles.pdb" /PDBALTPATH:PlaybackColorTiles.pdb /DYNAMICBASE /NXCOMPAT /GUARD:CF /OPT:REF /OPT:ICF /INCREMENTAL:NO d3d11.lib dxgi.lib user32.lib
exit /b %errorlevel%
"@
$compilerBatch = Join-Path $buildDirectory 'build.cmd'
[IO.File]::WriteAllText($compilerBatch, $batch.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $compilerBatch
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $library -PathType Leaf)) { throw 'Playback color tile helper compilation failed.' }
[ordered]@{ built = $true; sha256 = (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant(); testsRun = $false; captureActivated = $false } | ConvertTo-Json -Compress
