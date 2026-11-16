[CmdletBinding()]
param([Parameter(Mandatory)][string]$TargetVersion)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Select-UpgradeBaselineRelease {
    param([object[]]$Releases, [string]$TargetVersion)

    $canonical = '(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)'
    if ($TargetVersion -cnotmatch "\A$canonical\z") { throw 'The candidate version is invalid.' }
    $eligible = @($Releases | Where-Object {
        $_.draft -eq $false -and $_.prerelease -eq $false -and $_.immutable -eq $true -and
        $_.tag_name -cmatch "\Av$canonical\z" -and
        [version]$_.tag_name.Substring(1) -lt [version]$TargetVersion
    } | Sort-Object { [version]$_.tag_name.Substring(1) } -Descending)
    if ($eligible.Count -eq 0) { throw 'No immutable stable release precedes the candidate.' }
    return $eligible[0]
}

function Get-UpgradeInstallerAsset {
    param($Release, [string]$TargetVersion)

    $canonical = '(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)'
    if ($TargetVersion -cnotmatch "\A$canonical\z" -or
        $Release.tag_name -cnotmatch "\Av$canonical\z" -or
        $Release.draft -ne $false -or $Release.prerelease -ne $false -or
        $Release.immutable -ne $true) {
        throw 'The upgrade baseline must be an immutable stable Wisp release.'
    }
    $version = $Release.tag_name.Substring(1)
    if ([version]$version -ge [version]$TargetVersion) {
        throw 'The upgrade baseline must precede the candidate version.'
    }
    $name = "Wisp-Setup-$version.exe"
    $assets = @($Release.assets | Where-Object { $_.name -ceq $name })
    if ($assets.Count -ne 1 -or $assets[0].state -cne 'uploaded' -or
        $assets[0].size -le 0 -or $assets[0].size -gt 536870912 -or
        $assets[0].digest -cnotmatch '\Asha256:[0-9a-f]{64}\z' -or
        $assets[0].browser_download_url -cne "https://github.com/Views2k/Wisp/releases/download/v$version/$name") {
        throw 'The published upgrade installer metadata is invalid.'
    }
    return [pscustomobject]@{
        Version = $version; Name = $name; Size = $assets[0].size
        Sha256 = $assets[0].digest.Substring(7)
    }
}

function Assert-UpgradeInstallerBytes {
    param([string]$Path, $Asset)

    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($file.Length -ne $Asset.Size -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Asset.Sha256) {
        throw 'The upgrade installer does not match its immutable release digest and size.'
    }
}

if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:CI -cne 'true' -or
    $env:GITHUB_REPOSITORY -cne 'Views2k/Wisp') {
    throw 'The upgrade baseline is downloaded only in Wisp Windows release CI.'
}
$releaseJson = & gh api 'repos/Views2k/Wisp/releases?per_page=100' --paginate --slurp
if ($LASTEXITCODE -ne 0) { throw 'The published upgrade baseline could not be read.' }
$releases = @(foreach ($page in ($releaseJson | ConvertFrom-Json)) { foreach ($release in $page) { $release } })
$release = Select-UpgradeBaselineRelease $releases $TargetVersion
$asset = Get-UpgradeInstallerAsset $release $TargetVersion
$directory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../outputs/upgrade-baseline'))
[void][IO.Directory]::CreateDirectory($directory)
& gh release download "v$($asset.Version)" --repo Views2k/Wisp --pattern $asset.Name --dir $directory
if ($LASTEXITCODE -ne 0) { throw 'The published upgrade installer could not be downloaded.' }
$installer = Join-Path $directory $asset.Name
Assert-UpgradeInstallerBytes $installer $asset
"installer=$installer" >> $env:GITHUB_OUTPUT
"version=$($asset.Version)" >> $env:GITHUB_OUTPUT
Write-Output "Verified published upgrade baseline $($asset.Version)."
