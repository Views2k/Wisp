param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin\Release\x64'),
    [switch]$RunContractTests
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$buildDirectory = Join-Path $PSScriptRoot 'obj\Release\x64'
[void][IO.Directory]::CreateDirectory($OutputDirectory)
[void][IO.Directory]::CreateDirectory($buildDirectory)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio C++ build tools were not found.' }
$install = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($LASTEXITCODE -ne 0 -or $install.Count -ne 1 -or [string]::IsNullOrWhiteSpace($install[0])) { throw 'A single current Visual Studio C++ toolset could not be resolved.' }
$developerCommand = Join-Path $install[0] 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $developerCommand -PathType Leaf)) { throw 'Visual Studio environment setup is missing.' }
$encoding = [Text.UTF8Encoding]::new($false)
$common = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Quad.hlsl'))
foreach ($material in @('AnalogGauge','AnalogNeedle')) {
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "..\Wisp.App\Shaders\$material.hlsl"))
    if (($source | Select-String -Pattern 'sampler2D ImplicitInput : register\(s0\);' -AllMatches).Matches.Count -ne 1 -or
        ($source | Select-String -Pattern 'float4 main\(float2 uv : TEXCOORD0\) : COLOR0' -AllMatches).Matches.Count -ne 1) {
        throw 'The original material interface changed; review the D3D11 port before building.'
    }
    # The original material body stays the source of truth. Only the API bindings and output tint change.
    $ported = $source.Replace('sampler2D ImplicitInput : register(s0);', '')
    $ported = $ported.Replace('float2 GaugeParameters : register(c0);', '#define GaugeParameters MaterialParameters.xy')
    $ported = $ported.Replace('float BlurAmount : register(c0);', '#define BlurAmount MaterialParameters.x')
    $ported = $ported.Replace('tex2D(ImplicitInput, uv)', 'InputTexture.Sample(InputSampler, uv)')
    $ported = $ported.Replace('float4 main(float2 uv : TEXCOORD0) : COLOR0', 'float4 MaterialMain(float2 uv : TEXCOORD0) : COLOR0')
    $ported += "`nfloat4 main(VertexOutput input) : SV_TARGET { return TintPremultiplied(MaterialMain(input.uv)); }`n"
    [IO.File]::WriteAllText((Join-Path $buildDirectory "$material.hlsl"), $common + "`n" + $ported, $encoding)
}
$compilerBatch = Join-Path $buildDirectory 'build-native.cmd'
$dll = Join-Path $buildDirectory 'Wisp.NativeRenderer.dll'
$pdb = Join-Path $buildDirectory 'Wisp.NativeRenderer.pdb'
$object = Join-Path $buildDirectory 'Wisp.NativeRenderer.obj'
foreach ($argumentPath in @($developerCommand,$PSScriptRoot,$buildDirectory,$dll,$pdb,$object)) {
    if ($argumentPath -match '["\r\n%!]') { throw 'Unsupported characters in the native build path.' }
}
$contractBuild = if ($RunContractTests) { @"
if errorlevel 1 exit /b 1
cl.exe /nologo /std:c++17 /permissive- /EHsc /O2 /W4 /WX /MT /DWINVER=0x0A00 /D_WIN32_WINNT=0x0A00 /DNOMINMAX /Fo"$buildDirectory\NativeContractTests.obj" /Fe"$buildDirectory\NativeContractTests.exe" "$PSScriptRoot\NativeContractTests.cpp" /link "$buildDirectory\Wisp.NativeRenderer.lib" user32.lib
if errorlevel 1 exit /b 1
"$buildDirectory\NativeContractTests.exe"
"@ } else { '' }
$batch = @"
@echo off
call "$developerCommand" -no_logo -arch=x64 -host_arch=x64
if errorlevel 1 exit /b 1
set "FXC=%WindowsSdkDir%bin\%WindowsSDKVersion%x64\fxc.exe"
if not exist "%FXC%" exit /b 2
"%FXC%" /nologo /O3 /T vs_5_0 /E vs_main /Vn QuadVertex /Fh "$buildDirectory\QuadVertex.h" "$PSScriptRoot\Quad.hlsl"
if errorlevel 1 exit /b 1
"%FXC%" /nologo /O3 /T ps_5_0 /E image_main /Vn ImagePixel /Fh "$buildDirectory\ImagePixel.h" "$PSScriptRoot\Quad.hlsl"
if errorlevel 1 exit /b 1
"%FXC%" /nologo /O3 /T ps_5_0 /E main /Vn DialPixel /Fh "$buildDirectory\DialPixel.h" "$buildDirectory\AnalogGauge.hlsl"
if errorlevel 1 exit /b 1
"%FXC%" /nologo /O3 /T ps_5_0 /E main /Vn NeedlePixel /Fh "$buildDirectory\NeedlePixel.h" "$buildDirectory\AnalogNeedle.hlsl"
if errorlevel 1 exit /b 1
cl.exe /nologo /std:c++17 /permissive- /EHsc /O2 /W4 /WX /MT /LD /Z7 /guard:cf /DWINVER=0x0A00 /D_WIN32_WINNT=0x0A00 /DNOMINMAX /I"$buildDirectory" /Fo"$object" /Fe"$dll" "$PSScriptRoot\Wisp.NativeRenderer.cpp" /link /PDB:"$pdb" /PDBALTPATH:Wisp.NativeRenderer.pdb /DEBUG:FULL /OPT:REF /OPT:ICF /INCREMENTAL:NO /DYNAMICBASE /NXCOMPAT d3d11.lib dxgi.lib dcomp.lib user32.lib ole32.lib
$contractBuild
exit /b %errorlevel%
"@
[IO.File]::WriteAllText($compilerBatch, $batch.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $compilerBatch
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Native renderer compilation failed.' }
Copy-Item -LiteralPath $dll -Destination (Join-Path $OutputDirectory 'Wisp.NativeRenderer.dll') -Force
Copy-Item -LiteralPath $pdb -Destination (Join-Path $OutputDirectory 'Wisp.NativeRenderer.pdb') -Force
$sha256 = [Security.Cryptography.SHA256]::Create()
$dllStream = [IO.File]::OpenRead($dll)
try { $dllHash = [BitConverter]::ToString($sha256.ComputeHash($dllStream)).Replace('-', '').ToLowerInvariant() }
finally { $dllStream.Dispose(); $sha256.Dispose() }
[ordered]@{ built=$true; architecture='x64'; hardwareOnly=$true; dllSha256=$dllHash } | ConvertTo-Json -Compress
