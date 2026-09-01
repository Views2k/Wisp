$ErrorActionPreference = 'Stop'

function Assert-Policy {
    param(
        [Parameter(Mandatory)]
        [bool] $Condition,

        [Parameter(Mandatory)]
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$branchRulesetPath = Join-Path $repositoryRoot '.github\rulesets\protect-main.json'
$tagRulesetPath = Join-Path $repositoryRoot '.github\rulesets\protect-release-tags.json'
$workflowDirectory = Join-Path $repositoryRoot '.github\workflows'
$workflowPath = Join-Path $repositoryRoot '.github\workflows\ci.yml'
$codeOwnersPath = Join-Path $repositoryRoot '.github\CODEOWNERS'
$appProjectPath = Join-Path $repositoryRoot 'src\Wisp.App\Wisp.App.csproj'
$appManifestPath = Join-Path $repositoryRoot 'src\Wisp.App\app.manifest'
$mainWindowPath = Join-Path $repositoryRoot 'src\Wisp.App\MainWindow.xaml'
$innoScriptPath = Join-Path $repositoryRoot 'installer\Wisp.iss'

$branchRuleset = Get-Content -LiteralPath $branchRulesetPath -Raw | ConvertFrom-Json
$tagRuleset = Get-Content -LiteralPath $tagRulesetPath -Raw | ConvertFrom-Json
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$codeOwners = Get-Content -LiteralPath $codeOwnersPath
$appProject = [xml](Get-Content -LiteralPath $appProjectPath -Raw)
$appManifest = Get-Content -LiteralPath $appManifestPath -Raw
$mainWindow = Get-Content -LiteralPath $mainWindowPath -Raw
$innoScript = Get-Content -LiteralPath $innoScriptPath -Raw
$workflowPaths = @(
    Get-ChildItem -LiteralPath $workflowDirectory -File |
        Where-Object { $_.Extension -in '.yml', '.yaml' } |
        Sort-Object -Property FullName |
        Select-Object -ExpandProperty FullName
)
Assert-Policy ($workflowPaths.Count -gt 0) 'At least one GitHub Actions workflow must be present.'

$versionGroup = @($appProject.Project.PropertyGroup | Where-Object { $null -ne $_.Version })
Assert-Policy ($versionGroup.Count -eq 1) 'Wisp.App.csproj must define one application version.'
$applicationVersion = [string]$versionGroup[0].Version
Assert-Policy ($applicationVersion -match '^\d+\.\d+\.\d+$') 'The application version must use major.minor.patch format.'
$assemblyVersion = "$applicationVersion.0"
Assert-Policy (
    [string]$versionGroup[0].FileVersion -eq $assemblyVersion -and
    [string]$versionGroup[0].AssemblyVersion -eq $assemblyVersion
) 'FileVersion and AssemblyVersion must match the application version.'
Assert-Policy (
    $appManifest -match ('<assemblyIdentity\s+version="' + [regex]::Escape($assemblyVersion) + '"\s+name="Wisp\.App"\s*/>')
) 'The application manifest version must match Wisp.App.csproj.'
Assert-Policy (
    $innoScript -match ('(?m)^\s*#define\s+MyAppVersion\s+"' + [regex]::Escape($applicationVersion) + '"\s*$')
) 'The Inno Setup version must match Wisp.App.csproj.'
Assert-Policy (
    $mainWindow -match ('Text="WHEEL-INDICATED SPEED PANEL ' + [regex]::Escape($applicationVersion) + '"')
) 'The control-center footer version must match Wisp.App.csproj.'

foreach ($ruleset in $branchRuleset, $tagRuleset) {
    $bypassActors = @($ruleset.bypass_actors)
    Assert-Policy ($bypassActors.Count -eq 0) "$($ruleset.name) must not grant bypass access."
}

Assert-Policy ($branchRuleset.target -eq 'branch') 'Protect main must target branches.'
Assert-Policy ($branchRuleset.enforcement -eq 'active') 'Protect main must be defined as active.'
Assert-Policy ($branchRuleset.conditions.ref_name.include -contains '~DEFAULT_BRANCH') 'Protect main must target the default branch.'

$branchRuleTypes = @($branchRuleset.rules.type)
foreach ($requiredRule in 'deletion', 'non_fast_forward', 'required_linear_history', 'pull_request', 'required_status_checks') {
    Assert-Policy ($branchRuleTypes -contains $requiredRule) "Protect main is missing the $requiredRule rule."
}
Assert-Policy ($branchRuleTypes -notcontains 'update') 'Protect main must allow a green pull request to update the branch.'

$pullRequestRule = $branchRuleset.rules | Where-Object type -eq 'pull_request'
Assert-Policy (
    $pullRequestRule.parameters.allowed_merge_methods.Count -eq 1 -and
    $pullRequestRule.parameters.allowed_merge_methods -contains 'squash'
) 'Protect main must allow only squash merges.'
Assert-Policy ($pullRequestRule.parameters.dismiss_stale_reviews_on_push -eq $true) 'Protect main must dismiss stale reviews.'
Assert-Policy ($pullRequestRule.parameters.require_code_owner_review -eq $false) 'Solo-maintainer protection must not require code-owner review.'
Assert-Policy ($pullRequestRule.parameters.require_last_push_approval -eq $false) 'Solo-maintainer protection must not require another approver.'
Assert-Policy ($pullRequestRule.parameters.required_approving_review_count -eq 0) 'Solo-maintainer protection must allow a green pull request without another reviewer.'
Assert-Policy ($pullRequestRule.parameters.required_review_thread_resolution -eq $true) 'Protect main must require resolved review threads.'

$requiredChecks = @(
    $branchRuleset.rules |
        Where-Object type -eq 'required_status_checks' |
        ForEach-Object { $_.parameters.required_status_checks.context }
)
Assert-Policy ($requiredChecks -contains 'Build and test') 'Protect main must require the Build and test status.'
Assert-Policy ($requiredChecks -contains 'Build installer') 'Protect main must require the installer-build status.'
Assert-Policy ($requiredChecks.Count -eq 2) 'Protect main must require exactly the two release-gate checks.'
$statusCheckRule = $branchRuleset.rules | Where-Object type -eq 'required_status_checks'
Assert-Policy (
    $statusCheckRule.parameters.strict_required_status_checks_policy -eq $true
) 'Protect main must require an up-to-date branch.'
Assert-Policy ($workflow -match '(?m)^    name: Build and test\r?$') 'The CI job name must match the required status context.'
Assert-Policy ($workflow -match '(?m)^    name: Build installer\r?$') 'The installer job name must match the required status context.'
Assert-Policy ($workflow -match '(?m)^          persist-credentials: false\r?$') 'Checkout credentials must not persist after checkout.'

foreach ($currentWorkflowPath in $workflowPaths) {
    $currentWorkflow = Get-Content -LiteralPath $currentWorkflowPath -Raw
    $displayPath = [System.IO.Path]::GetRelativePath($repositoryRoot, $currentWorkflowPath).Replace('\', '/')
    $usesEntries = @(
        [regex]::Matches($currentWorkflow, '(?m)^\s*(?:-\s*)?uses:\s*(?<reference>[^\s#]+)') |
            ForEach-Object { $_.Groups['reference'].Value }
    )
    foreach ($reference in $usesEntries) {
        if ($reference.StartsWith('./', [StringComparison]::Ordinal)) {
            continue
        }

        if ($reference.StartsWith('docker://', [StringComparison]::OrdinalIgnoreCase)) {
            Assert-Policy (
                $reference -match '^docker://[^@\s]+@sha256:[0-9a-fA-F]{64}$'
            ) "Docker workflow action '$reference' in $displayPath must use a full SHA-256 image digest."
            continue
        }

        Assert-Policy (
            $reference -match '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.\/-]+@[0-9a-fA-F]{40}$'
        ) "Workflow action '$reference' in $displayPath must use a full 40-character commit SHA."
    }
}

Assert-Policy ($tagRuleset.target -eq 'tag') 'Protect release tags must target tags.'
Assert-Policy ($tagRuleset.enforcement -eq 'active') 'Protect release tags must be defined as active.'
Assert-Policy ($tagRuleset.conditions.ref_name.include -contains 'refs/tags/v*') 'Protect release tags must target v* tags.'

$tagRuleTypes = @($tagRuleset.rules.type)
foreach ($requiredRule in 'update', 'deletion', 'non_fast_forward') {
    Assert-Policy ($tagRuleTypes -contains $requiredRule) "Protect release tags is missing the $requiredRule rule."
}
Assert-Policy ($tagRuleTypes -notcontains 'creation') 'Release-tag creation must remain available to the solo release owner.'

$ownerRules = @($codeOwners | Where-Object { $_.Trim() -eq '* @Views2k' })
Assert-Policy ($ownerRules.Count -eq 1) 'CODEOWNERS must retain the repository-wide owner rule.'

Write-Host 'Repository policy definitions are internally consistent.'
