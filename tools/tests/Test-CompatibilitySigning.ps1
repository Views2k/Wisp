#Requires -Version 7.4
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The production signer uses Windows user-protected keys.' }
if (-not ('WispCompatibilityTestPublicKey' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Security.Cryptography;
public static class WispCompatibilityTestPublicKey
{
    public static void Import(ECDsa key, byte[] bytes)
    {
        key.ImportSubjectPublicKeyInfo(bytes, out int consumed);
        if (consumed != bytes.Length) throw new CryptographicException("Trailing public-key data.");
    }
}
'@
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$signer = Join-Path $repo 'tools\Sign-CompatibilityBundle.ps1'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$leaf = 'wisp-signing-contract-' + [Guid]::NewGuid().ToString('N')
$directory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot $leaf))
if ([IO.Path]::GetDirectoryName($directory).TrimEnd('\') -ne $temporaryRoot.TrimEnd('\') -or
    [IO.Path]::GetFileName($directory) -ne $leaf) { throw 'The isolated test directory is invalid.' }
[void][IO.Directory]::CreateDirectory($directory)
$keyFile = Join-Path $directory 'ephemeral-key.bin'
$publicFile = Join-Path $directory 'public.json'
$outputFile = Join-Path $directory 'bundle.json'
$key = $null
try {
    $null = & $signer -InitializeKey -KeyFile $keyFile -PublicKeyOutput $publicFile
    $packs = @('fh6-6.430.771.0.json', 'fh6-store-3.430.771.0.json',
        'fh6-6.440.853.0.json', 'fh6-store-3.440.853.0.json') | ForEach-Object {
            Join-Path $repo ('src\Wisp.App\NativeCompatibility\' + $_)
        }
    $rejected = $false
    try { $null = & $signer -KeyFile $keyFile -Pack $packs -Output $outputFile -Reviewed:$false }
    catch { $rejected = $true }
    if (-not $rejected -or (Test-Path -LiteralPath $outputFile)) { throw 'Signing must require explicit review.' }
    $result = & $signer -KeyFile $keyFile -Pack $packs -Output $outputFile -Reviewed
    if (-not $result.signed -or $result.published -or $result.packs -ne 4) { throw 'Signing returned an invalid result.' }
    $raw = [IO.File]::ReadAllBytes($outputFile)
    if ($raw.Length -gt 131072) { throw 'The signed envelope exceeded its existing bound.' }
    $envelope = [Text.Encoding]::UTF8.GetString($raw) | ConvertFrom-Json
    $publicRecord = Get-Content -LiteralPath $publicFile -Raw | ConvertFrom-Json
    $publicBytes = [Convert]::FromBase64String($publicRecord.subjectPublicKeyInfo)
    $key = [Security.Cryptography.ECDsa]::Create()
    [WispCompatibilityTestPublicKey]::Import($key, $publicBytes)
    $payload = [Convert]::FromBase64String($envelope.payload)
    if ($payload.Length -gt 98304) { throw 'The signed payload exceeded its existing bound.' }
    $prefix = [Text.Encoding]::ASCII.GetBytes("Wisp.NativeHud.Compatibility/v1`0")
    $signedInput = [byte[]]::new($prefix.Length + $payload.Length)
    [Array]::Copy($prefix, 0, $signedInput, 0, $prefix.Length)
    [Array]::Copy($payload, 0, $signedInput, $prefix.Length, $payload.Length)
    $signature = [Convert]::FromBase64String($envelope.signature)
    if (-not $key.VerifyData($signedInput, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'The signature did not verify.' }
    $decoded = [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
    if (($decoded.packs.schemaVersion -join ',') -ne '3,4,5,6' -or
        $decoded.packs[2].tune.codeGuards.Count -ne 58 -or $decoded.packs[3].tune.codeGuards.Count -ne 65) {
        throw 'The signed bundle did not preserve the four parser contracts.'
    }
    $signedInput[$signedInput.Length - 1] = $signedInput[$signedInput.Length - 1] -bxor 1
    if ($key.VerifyData($signedInput, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Changed payload bytes were accepted.' }
    $rejected = $false
    try { $null = & $signer -KeyFile $keyFile -Pack $packs -Output $outputFile -Reviewed }
    catch { $rejected = $true }
    if (-not $rejected -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw)) -ne
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($outputFile)))) {
        throw 'Signing did not preserve an existing output.'
    }
    [pscustomobject]@{ passed = $true; schemas = @(3, 4, 5, 6); published = $false }
}
finally {
    if ($null -ne $key) { $key.Dispose() }
    if ([IO.Path]::GetFullPath($directory) -ne (Join-Path $temporaryRoot $leaf)) { throw 'Unsafe cleanup target.' }
    Remove-Item -LiteralPath $directory -Recurse -Force
}
