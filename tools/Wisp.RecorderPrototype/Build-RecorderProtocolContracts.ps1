param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin\protocol')
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$buildDirectory = Join-Path $PSScriptRoot 'obj\protocol'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio C++ tools were not found.' }
$install = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($LASTEXITCODE -ne 0 -or $install.Count -ne 1 -or [string]::IsNullOrWhiteSpace($install[0])) { throw 'A single C++ toolset could not be resolved.' }
$developerCommand = Join-Path $install[0] 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $developerCommand -PathType Leaf)) { throw 'Visual Studio environment setup is missing.' }
foreach ($argumentPath in @($developerCommand, $PSScriptRoot, $buildDirectory, $OutputDirectory)) {
    if ($argumentPath -match '["\r\n%!]') { throw 'Unsupported characters in the build path.' }
}
foreach ($sourceName in @('RecorderProtocol.h', 'RecorderProtocol.cpp', 'RecorderProtocolContracts.cpp')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $sourceName) -PathType Leaf)) { throw 'Recorder protocol contracts source is incomplete.' }
}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
[void][IO.Directory]::CreateDirectory($buildDirectory)
$compilerBatch = Join-Path $buildDirectory 'build.cmd'
$executable = Join-Path $OutputDirectory 'Wisp.RecorderProtocolContracts.exe'
$compilerFlags = '/nologo /std:c++17 /permissive- /EHsc /O2 /W4 /WX /MT /Z7 /guard:cf /DWINVER=0x0A00 /D_WIN32_WINNT=0x0A00 /DNTDDI_VERSION=0x0A000010 /DUNICODE /D_UNICODE /DNOMINMAX'
$batch = @"
@echo off
call "$developerCommand" -no_logo -arch=x64 -host_arch=x64 -winsdk=10.0.26100.0
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\RecorderProtocol.obj" "$PSScriptRoot\RecorderProtocol.cpp"
if errorlevel 1 exit /b 1
cl.exe $compilerFlags /c /Fo"$buildDirectory\RecorderProtocolContracts.obj" "$PSScriptRoot\RecorderProtocolContracts.cpp"
if errorlevel 1 exit /b 1
link.exe /nologo /OUT:"$executable" "$buildDirectory\RecorderProtocol.obj" "$buildDirectory\RecorderProtocolContracts.obj" /DEBUG:FULL /PDB:"$buildDirectory\Wisp.RecorderProtocolContracts.pdb" /PDBALTPATH:Wisp.RecorderProtocolContracts.pdb /DYNAMICBASE /NXCOMPAT /GUARD:CF /OPT:REF /OPT:ICF /INCREMENTAL:NO
exit /b %errorlevel%
"@
[IO.File]::WriteAllText($compilerBatch, $batch.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $compilerBatch
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Recorder protocol contracts compilation failed.' }
[ordered]@{ built = $true; architecture = 'x64'; sdk = '10.0.26100.0'; sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant(); testsRun = $false; graphicsActivated = $false } | ConvertTo-Json -Compress
