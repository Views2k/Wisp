[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$shaderDirectory = Join-Path $repository 'src\Wisp.App\Shaders'
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if ($outputPath.Equals($shaderDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $outputPath.StartsWith($shaderDirectory + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Compile into a separate output directory before reviewing the packaged shaders.'
}
if (Test-Path -LiteralPath $outputPath) {
    if (-not (Test-Path -LiteralPath $outputPath -PathType Container) -or
        @(Get-ChildItem -LiteralPath $outputPath -Force).Count -ne 0) {
        throw 'The shader output directory must be new or empty.'
    }
}

if (-not ('Wisp.ShaderTools.Compiler' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Wisp.ShaderTools
{
    public static class Compiler
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct Macro
        {
            [MarshalAs(UnmanagedType.LPStr)] public string Name;
            [MarshalAs(UnmanagedType.LPStr)] public string Definition;
        }

        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int D3DCompile(
            byte[] source, UIntPtr sourceSize,
            [MarshalAs(UnmanagedType.LPStr)] string sourceName,
            [In] Macro[] defines, IntPtr include,
            [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
            [MarshalAs(UnmanagedType.LPStr)] string target,
            uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr GetBufferPointer(IntPtr blob);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate UIntPtr GetBufferSize(IntPtr blob);

        private static byte[] ReadBlob(IntPtr blob)
        {
            IntPtr table = Marshal.ReadIntPtr(blob);
            var getPointer = (GetBufferPointer)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(table, 3 * IntPtr.Size), typeof(GetBufferPointer));
            var getSize = (GetBufferSize)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(table, 4 * IntPtr.Size), typeof(GetBufferSize));
            byte[] bytes = new byte[checked((int)getSize(blob).ToUInt64())];
            Marshal.Copy(getPointer(blob), bytes, 0, bytes.Length);
            return bytes;
        }

        public static byte[] Compile(byte[] source, string sourceName, bool electric, uint flags)
        {
            Macro[] defines = electric
                ? new[] { new Macro { Name = "ELECTRIC_NEEDLE", Definition = "1" }, new Macro() }
                : null;
            IntPtr code = IntPtr.Zero;
            IntPtr errors = IntPtr.Zero;
            try
            {
                int result = D3DCompile(source, new UIntPtr((uint)source.Length), sourceName,
                    defines, IntPtr.Zero, "main", "ps_3_0", flags, 0, out code, out errors);
                if (result < 0)
                {
                    string detail = errors == IntPtr.Zero ? "No compiler diagnostic was returned."
                        : Encoding.UTF8.GetString(ReadBlob(errors)).TrimEnd('\0', '\r', '\n');
                    throw new InvalidOperationException("Shader compilation failed: " + detail);
                }
                byte[] bytes = ReadBlob(code);
                if (bytes.Length < 8 || BitConverter.ToUInt32(bytes, 0) != 0xFFFF0300)
                    throw new InvalidOperationException("The compiler did not produce ps_3_0 bytecode.");
                return bytes;
            }
            finally
            {
                if (code != IntPtr.Zero) Marshal.Release(code);
                if (errors != IntPtr.Zero) Marshal.Release(errors);
            }
        }
    }
}
'@
}

# The gauge shaders use optimization level 3; the needles use the compiler default.
$variants = @(
    @{ Source = 'AnalogGauge.hlsl'; Output = 'AnalogGauge.ps'; Electric = $false; Flags = 0x8000 },
    @{ Source = 'DigitalGauge.hlsl'; Output = 'DigitalGauge.ps'; Electric = $false; Flags = 0x8000 },
    @{ Source = 'AnalogNeedle.hlsl'; Output = 'AnalogNeedle.ps'; Electric = $false; Flags = 0 },
    @{ Source = 'AnalogNeedle.hlsl'; Output = 'ElectricAnalogNeedle.ps'; Electric = $true; Flags = 0 }
)
$compiled = foreach ($variant in $variants) {
    $source = [System.IO.File]::ReadAllBytes((Join-Path $shaderDirectory $variant.Source))
    [pscustomobject]@{
        Name = $variant.Output
        Bytes = [Wisp.ShaderTools.Compiler]::Compile($source, $variant.Source, $variant.Electric, $variant.Flags)
    }
}

[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null
foreach ($shader in $compiled) {
    $destination = Join-Path $outputPath $shader.Name
    $stream = [System.IO.File]::Open($destination, [System.IO.FileMode]::CreateNew)
    try {
        $stream.Write($shader.Bytes, 0, $shader.Bytes.Length)
    }
    finally {
        $stream.Dispose()
    }
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    Write-Output "$($shader.Name): $hash"
}
$compilerPath = Join-Path $env:WINDIR 'System32\d3dcompiler_47.dll'
$compilerVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($compilerPath).FileVersion
Write-Output "D3DCompiler_47.dll: $compilerVersion; main; ps_3_0"
