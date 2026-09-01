[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Tag,
    [string]$MainRef = 'refs/remotes/origin/main'
)

$ErrorActionPreference = 'Stop'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

if ($Tag -notmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Release tags must use canonical vX.Y.Z syntax.'
}
$version = $Tag.Substring(1)

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

$releaseNotesPath = Join-Path $repository "Wisp-$version-release-notes.md"
if (-not (Test-Path -LiteralPath $releaseNotesPath -PathType Leaf)) {
    throw "The release notes for $Tag are missing."
}
$releaseNotesHeading = [System.IO.File]::ReadLines($releaseNotesPath) | Select-Object -First 1
if ($releaseNotesHeading -cne "# Wisp $version") {
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
