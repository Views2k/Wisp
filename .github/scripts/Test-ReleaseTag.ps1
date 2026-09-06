[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Tag,
    [string]$MainRef = 'refs/remotes/origin/main'
)

$ErrorActionPreference = 'Stop'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function ConvertTo-CanonicalReleaseVersion {
    param([Parameter(Mandatory)][string]$Value)

    if ($Value.Length -gt 128) {
        throw 'The numeric release tag is too long.'
    }
    $match = [regex]::Match(
        $Value,
        '\A[vV]?(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?(?:\.(0|[1-9][0-9]*))?(?:-stable)?\z',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        throw 'Release tags must contain one to three numeric components, optionally prefixed with v and suffixed with -stable.'
    }
    $parts = @('0', '0', '0')
    for ($index = 0; $index -lt 3; $index++) {
        if ($match.Groups[$index + 1].Success) {
            [System.UInt16]$part = 0
            if (-not [System.UInt16]::TryParse(
                    $match.Groups[$index + 1].Value,
                    [System.Globalization.NumberStyles]::None,
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [ref]$part)) {
                throw 'Release-version components must fit the Windows PE version resource range.'
            }
            $parts[$index] = $part.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        }
    }
    return $parts -join '.'
}

$version = ConvertTo-CanonicalReleaseVersion $Tag

function Read-RequiredMatch {
    param(
        [string]$Path,
        [string]$Pattern,
        [string]$Label
    )

    $text = [System.IO.File]::ReadAllText($Path)
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $text,
        $Pattern,
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        throw "Could not read $Label from $Path."
    }
    return $match.Groups[1].Value
}

$projectPath = Join-Path $repository 'src\Wisp.App\Wisp.App.csproj'
$installerPath = Join-Path $repository 'installer\Wisp.iss'
$projectVersion = Read-RequiredMatch $projectPath '<Version>\s*([^<]+?)\s*</Version>' 'application version'
$installerVersion = Read-RequiredMatch `
    $installerPath '(?m)^\s*#define\s+MyAppVersion\s+"([^"]+)"\s*$' 'installer version'
if ($projectVersion -cne $version -or $installerVersion -cne $version) {
    throw "Tag $Tag does not match the application and installer version $version."
}

$releaseNotesPath = Join-Path $repository "docs\releases\Wisp-$version-release-notes.md"
if (-not (Test-Path -LiteralPath $releaseNotesPath -PathType Leaf)) {
    # Older release tags keep their notes at the repository root.
    $releaseNotesPath = Join-Path $repository "Wisp-$version-release-notes.md"
}
if (-not (Test-Path -LiteralPath $releaseNotesPath -PathType Leaf)) {
    throw "The release notes for $Tag are missing."
}
$releaseNotesHeading = [System.IO.File]::ReadLines($releaseNotesPath) | Select-Object -First 1
$displayVersion = if ($version.EndsWith('.0', [StringComparison]::Ordinal)) {
    $version.Substring(0, $version.Length - 2)
}
else {
    $version
}
if ($releaseNotesHeading -cne "# Wisp $version" -and
    $releaseNotesHeading -cne "# Wisp $displayVersion") {
    throw "The release-notes heading does not match $Tag."
}

$changelog = [System.IO.File]::ReadAllText((Join-Path $repository 'CHANGELOG.md'))
$escapedVersion = [System.Text.RegularExpressions.Regex]::Escape($version)
if ($changelog -notmatch "(?m)^## $escapedVersion - \d{4}-\d{2}-\d{2}$") {
    throw "CHANGELOG.md must contain a dated $version release before tagging."
}

$head = @(& git -C $repository rev-parse --verify 'HEAD^{commit}' 2>$null)
$main = @(& git -C $repository rev-parse --verify "$MainRef^{commit}" 2>$null)
$tagCommit = @(& git -C $repository rev-list -n 1 "refs/tags/$Tag" 2>$null)
if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $main.Count -ne 1 -or $tagCommit.Count -ne 1) {
    throw 'The release tag, checked-out commit, or main branch could not be resolved.'
}
if ($tagCommit[0] -cne $head[0]) {
    throw 'The checked-out commit does not match the release tag.'
}
if ($head[0] -cne $main[0]) {
    throw 'Release tags must point to the current main branch commit.'
}

Write-Output "Validated $Tag at $($head[0])."
