[CmdletBinding()]
param(
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$Ref = $env:GITHUB_REF,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..'),
    [string]$CandidateCommit = 'HEAD',
    [string]$ApiFixturePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$plan = [ordered]@{ schema = 1; mode = 'full'; reason = 'Full validation is required.' }

function Read-Git {
    param([string[]]$Arguments)
    $value = @(& git -C $RepositoryRoot @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'Git evidence is unavailable.' }
    return ($value -join "`n")
}

function Read-Api {
    param([string]$Suffix)
    if ($ApiFixturePath) {
        if (-not $script:fixture.Contains($Suffix)) { throw 'Fixture evidence is unavailable.' }
        return $script:fixture[$Suffix]
    }
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $token = if ($env:GH_TOKEN) { $env:GH_TOKEN } else { $env:GITHUB_TOKEN }
    if ($token) { $headers.Authorization = "Bearer $token" }
    return Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$Repository/$Suffix" `
        -Headers $headers -TimeoutSec 15
}

function Is-Documentation {
    param([string]$Path)
    return $Path -ceq 'README.md' -or $Path -ceq 'CHANGELOG.md' -or
        $Path -cmatch '\Adocs/(?:[^/]+/)*[^/]+\.md\z'
}

function Read-Tree {
    param([string]$Commit)
    $tree = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $raw = Read-Git @('-c', 'core.quotepath=false', 'ls-tree', '-r', '-z', '--full-tree', $Commit)
    foreach ($entry in $raw.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        $match = [regex]::Match($entry, '\A(?<mode>[0-9]{6}) (?<type>blob|commit) (?<oid>[0-9a-f]{40,64})\t(?<path>[^\x00\r\n]+)\z')
        if (-not $match.Success) { throw 'A Git entry cannot be classified safely.' }
        $path = $match.Groups['path'].Value
        if ((Is-Documentation $path) -and
            ($match.Groups['mode'].Value -cne '100644' -or $match.Groups['type'].Value -cne 'blob')) {
            throw 'Documentation must be regular, non-executable Git blobs.'
        }
        $tree.Add($path, $entry)
    }
    return ,$tree
}

function Require-PassedStep {
    param([object]$Jobs, [string]$JobName, [string]$StepName)
    $job = @($Jobs | Where-Object { $_.name -ceq $JobName })
    if ($job.Count -ne 1 -or $job[0].status -cne 'completed' -or $job[0].conclusion -cne 'success') {
        throw 'A required baseline job did not complete successfully.'
    }
    $step = @($job[0].steps | Where-Object { $_.name -ceq $StepName })
    if ($step.Count -ne 1 -or $step[0].status -cne 'completed' -or $step[0].conclusion -cne 'success') {
        throw 'A required full-validation step was not executed successfully.'
    }
}

try {
    if ($ApiFixturePath) { $script:fixture = Get-Content -LiteralPath $ApiFixturePath -Raw | ConvertFrom-Json -AsHashtable }
    if ($EventName -cne 'pull_request' -and -not ($EventName -ceq 'push' -and $Ref -ceq 'refs/heads/main')) {
        throw 'Only pull requests and main pushes are eligible; tags and manual runs require full validation.'
    }
    if ($Repository -cne 'Views2k/Wisp') { throw 'The release baseline must belong to Views2k/Wisp.' }
    $RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    $head = Read-Git @('rev-parse', '--verify', 'HEAD^{commit}')
    if ($CandidateCommit -cne 'HEAD' -and $CandidateCommit -cnotmatch '\A[0-9a-f]{40,64}\z') {
        throw 'The candidate must be HEAD or an exact commit SHA.'
    }
    $candidate = Read-Git @('rev-parse', '--verify', "$CandidateCommit^{commit}")
    if ($candidate -cne $head -or $candidate -cnotmatch '\A[0-9a-f]{40,64}\z') { throw 'The candidate must be the checked-out commit.' }
    if (Read-Git @('status', '--porcelain', '--untracked-files=all')) { throw 'The candidate checkout is not clean.' }
    $plan.candidateCommit = $candidate

    $release = Read-Api 'releases/latest'
    $tag = [string]$release.tag_name
    if ($release.draft -ne $false -or $release.prerelease -ne $false -or $release.immutable -ne $true -or
        -not $release.published_at -or $tag.Length -gt 128 -or
        $tag -notmatch '\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-stable)?\z') {
        throw 'The latest release is not an immutable published stable release.'
    }
    $tagRef = Read-Api "git/ref/tags/$tag"
    $object = $tagRef.object
    for ($depth = 0; $object.type -ceq 'tag' -and $depth -lt 4; $depth++) {
        if ([string]$object.sha -cnotmatch '\A[0-9a-f]{40,64}\z') { throw 'Invalid tag object identity.' }
        $object = (Read-Api "git/tags/$($object.sha)").object
    }
    $baseline = [string]$object.sha
    if ($object.type -cne 'commit' -or $baseline -cnotmatch '\A[0-9a-f]{40,64}\z') { throw 'The stable tag does not resolve to an exact commit.' }
    if ((Read-Git @('rev-parse', '--verify', "$baseline^{commit}")) -cne $baseline) { throw 'The baseline commit is unavailable locally.' }
    & git -C $RepositoryRoot merge-base --is-ancestor $baseline $candidate 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'The candidate is not descended from the published baseline.' }
    $baseTree = Read-Tree $baseline
    $candidateTree = Read-Tree $candidate
    $paths = [Collections.Generic.HashSet[string]]::new($baseTree.Keys, [StringComparer]::Ordinal)
    $paths.UnionWith($candidateTree.Keys)
    $changed = [Collections.Generic.List[string]]::new()
    $inputEntries = [Collections.Generic.List[string]]::new()
    foreach ($path in $paths) {
        $old = if ($baseTree.ContainsKey($path)) { $baseTree[$path] } else { $null }
        $new = if ($candidateTree.ContainsKey($path)) { $candidateTree[$path] } else { $null }
        if ($old -cne $new) {
            if (-not (Is-Documentation $path)) { throw 'Executable, installer, test, toolchain or policy inputs differ from the published baseline.' }
            $changed.Add($path)
        }
        if (-not (Is-Documentation $path)) { $inputEntries.Add($new) }
    }
    if ($changed.Count -eq 0) { throw 'There is no documentation-only change to validate.' }

    $runs = Read-Api "actions/workflows/ci.yml/runs?head_sha=$baseline&event=push&status=success&per_page=100"
    $verifiedRun = $null
    foreach ($run in @($runs.workflow_runs)) {
        if ($run.head_sha -cne $baseline -or $run.head_branch -cne $tag -or
            $run.path -cne '.github/workflows/ci.yml' -or $run.event -cne 'push' -or
            $run.status -cne 'completed' -or $run.conclusion -cne 'success' -or
            $run.repository.full_name -cne $Repository -or $run.head_repository.full_name -cne $Repository -or
            [string]$run.id -cnotmatch '\A[1-9][0-9]*\z') { continue }
        $jobs = Read-Api "actions/runs/$($run.id)/jobs?filter=latest&per_page=100"
        if ($jobs.total_count -gt 100) { continue }
        try {
            Require-PassedStep $jobs.jobs 'Build and test' 'Build verified installer bundle'
            Require-PassedStep $jobs.jobs 'Build installer' 'Test installer lifecycle'
            Require-PassedStep $jobs.jobs 'Publish release' 'Publish immutable release'
            $verifiedRun = $run
            break
        }
        catch { continue }
    }
    if ($null -eq $verifiedRun) { throw 'No successful stable-tag CI run proves full tests, canonical packaging, lifecycle and publication.' }
    $changed.Sort([StringComparer]::Ordinal)
    $inputEntries.Sort([StringComparer]::Ordinal)
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $inputEntries)))).ToLowerInvariant()
    $plan = [ordered]@{
        schema = 1; mode = 'docs-only';
        reason = 'Documentation changed; executable and packaging inputs match a fully validated published release. Tests and installer are not rerun or represented as newly built.';
        candidateCommit = $candidate; baselineCommit = $baseline; baselineTag = $tag;
        baselineRunId = $verifiedRun.id; baselineRunUrl = "https://github.com/$Repository/actions/runs/$($verifiedRun.id)";
        inputDigest = $digest; changedDocumentation = @($changed); producesInstaller = $false
    }
}
catch {
    # Do not echo remote exception bodies, request headers or arbitrary API text.
    $known = $_.Exception.Message
    if ($known -match '\A(?:Only pull requests|The release baseline|The candidate|The latest release|The stable tag|The baseline commit|Documentation must|There is no documentation|Executable, installer|No successful stable-tag|Git evidence|A Git entry|Invalid tag object)') {
        $plan.reason = $known
    }
    else { $plan.reason = 'Baseline proof is unavailable or invalid; full validation is required.' }
}
if ($ApiFixturePath) {
    # Fixture transport can exercise the decision logic, but can never authorize a CI fast path.
    $plan = [ordered]@{ schema = 1; mode = 'full'; reason = 'Offline fixture evidence cannot authorize CI reuse.'; fixturePlan = $plan }
}
$plan | ConvertTo-Json -Depth 8 -Compress
