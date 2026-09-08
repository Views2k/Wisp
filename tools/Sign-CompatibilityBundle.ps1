#Requires -Version 7.4
[CmdletBinding(DefaultParameterSetName = 'Sign')]
param(
    [Parameter(Mandatory)][string]$KeyFile,
    [Parameter(Mandatory, ParameterSetName = 'Initialize')][switch]$InitializeKey,
    [Parameter(Mandatory, ParameterSetName = 'Initialize')][string]$PublicKeyOutput,
    [Parameter(Mandatory, ParameterSetName = 'Sign')][string[]]$Pack,
    [Parameter(Mandatory, ParameterSetName = 'Sign')][string]$Output,
    [Parameter(Mandatory, ParameterSetName = 'Sign')][switch]$Reviewed,
    [Parameter(ParameterSetName = 'Sign')][ValidateRange(1, 365)][int]$ValidityDays = 90
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) {
    throw 'This signing key is protected for its owning Windows user.'
}
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
if (-not ('WispCompatibilitySigningKey' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Security.Cryptography;
public static class WispCompatibilitySigningKey
{
    public static void Import(ECDsa key, byte[] bytes)
    {
        key.ImportPkcs8PrivateKey(bytes, out int consumed);
        if (consumed != bytes.Length || key.KeySize != 256 ||
            key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new CryptographicException("The publisher key is not ECDSA P-256.");
    }
}
'@
}
$utf8 = [Text.UTF8Encoding]::new($false)
$entropy = [Text.Encoding]::ASCII.GetBytes('Wisp.NativeHud.Compatibility/publisher-key/v1')
$privateBytes = $null
$key = $null

function Write-NewBytes([string]$Path, [byte[]]$Bytes) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $parent = [IO.Path]::GetDirectoryName($fullPath)
    [void][IO.Directory]::CreateDirectory($parent)
    $stream = [IO.FileStream]::new($fullPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

try {
    if ($InitializeKey) {
        if ((Test-Path -LiteralPath $KeyFile) -or (Test-Path -LiteralPath $PublicKeyOutput)) {
            throw 'Key initialization refuses to replace an existing file.'
        }
        $key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
        $privateBytes = $key.ExportPkcs8PrivateKey()
        $protected = [Security.Cryptography.ProtectedData]::Protect($privateBytes, $entropy,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $publicBytes = $key.ExportSubjectPublicKeyInfo()
        $publicRecord = [ordered]@{
            format = 1
            algorithm = 'ECDSA-P256-SHA256-P1363'
            keyId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publicBytes))
            subjectPublicKeyInfo = [Convert]::ToBase64String($publicBytes)
        }
        Write-NewBytes $KeyFile $protected
        Write-NewBytes $PublicKeyOutput $utf8.GetBytes(($publicRecord | ConvertTo-Json -Compress))
        [pscustomobject]@{ initialized = $true; keyId = $publicRecord.keyId; plaintextPrivateKeyWritten = $false }
        return
    }

    if (-not $Reviewed) { throw 'Only reviewed compatibility packs may be signed.' }
    if ($Pack.Count -lt 1 -or $Pack.Count -gt 8) { throw 'A bundle must contain between one and eight reviewed packs.' }
    if (Test-Path -LiteralPath $Output) { throw 'Signing refuses to replace an existing output.' }
    $protectedFile = Get-Item -LiteralPath $KeyFile
    if ($protectedFile.Length -lt 1 -or $protectedFile.Length -gt 16384 -or
        ($protectedFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The publisher key file is invalid.'
    }
    $privateBytes = [Security.Cryptography.ProtectedData]::Unprotect(
        [IO.File]::ReadAllBytes($protectedFile.FullName), $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $key = [Security.Cryptography.ECDsa]::Create()
    [WispCompatibilitySigningKey]::Import($key, $privateBytes)

    $packs = [Collections.Generic.List[object]]::new()
    $identities = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $Pack) {
        $file = Get-Item -LiteralPath $path
        if ($file.Length -lt 1 -or $file.Length -gt 65536 -or
            ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A compatibility pack is not a bounded regular file.'
        }
        $value = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json -AsHashtable
        if ($value.schemaVersion -notin @(1, 2, 3, 4) -or $value.revision -lt 1 -or
            $value.gameVersion -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$') {
            throw 'A compatibility pack has invalid release metadata.'
        }
        $identity = if ($value.schemaVersion -eq 4) {
            'store:' + $value.storeIdentity.packageFullName + ':' + $value.imageSize
        } else {
            'steam:' + $value.gameVersion + ':' + $value.executableLength + ':' + $value.executableSha256
        }
        if (-not $identities.Add($identity)) { throw 'A bundle cannot contain duplicate build identities.' }
        $packs.Add($value)
    }

    $issued = [DateTime]::UtcNow
    $payload = [ordered]@{
        format = 2
        purpose = 'wisp-native-hud-compatibility'
        issuedUtc = $issued.ToString('O')
        expiresUtc = $issued.AddDays($ValidityDays).ToString('O')
        packs = $packs.ToArray()
    }
    $payloadBytes = $utf8.GetBytes(($payload | ConvertTo-Json -Depth 32 -Compress))
    if ($payloadBytes.Length -gt 98304) { throw 'The compatibility bundle exceeds the signed payload limit.' }
    $prefix = [Text.Encoding]::ASCII.GetBytes("Wisp.NativeHud.Compatibility/v1`0")
    $inputBytes = [byte[]]::new($prefix.Length + $payloadBytes.Length)
    [Array]::Copy($prefix, 0, $inputBytes, 0, $prefix.Length)
    [Array]::Copy($payloadBytes, 0, $inputBytes, $prefix.Length, $payloadBytes.Length)
    $signature = $key.SignData($inputBytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    if (-not $key.VerifyData($inputBytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) {
        throw 'The generated compatibility signature failed verification.'
    }
    $publicBytes = $key.ExportSubjectPublicKeyInfo()
    $envelope = [ordered]@{
        format = 1
        keyId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publicBytes))
        payload = [Convert]::ToBase64String($payloadBytes)
        signature = [Convert]::ToBase64String($signature)
    }
    $envelopeBytes = $utf8.GetBytes(($envelope | ConvertTo-Json -Compress))
    if ($envelopeBytes.Length -gt 131072) { throw 'The signed envelope exceeds the download limit.' }
    Write-NewBytes $Output $envelopeBytes
    [pscustomobject]@{
        signed = $true
        packs = $packs.Count
        sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($envelopeBytes))
        published = $false
    }
}
finally {
    if ($null -ne $privateBytes) { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
    if ($null -ne $key) { $key.Dispose() }
}
