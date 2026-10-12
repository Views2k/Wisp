$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:CI -cne 'true' -or
    [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'Network isolation verification requires an ephemeral Windows CI runner.'
}

# Exercise the same helper without executing the guarded installer entrypoint.
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Test-InstallerLifecycle.ps1'), [ref]$tokens, [ref]$errors)
$functions = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -ceq 'Invoke-IsolatedCanaryApplication'
}, $false))
if ($errors.Count -ne 0 -or $functions.Count -ne 1) {
    throw 'The canary network isolation helper is invalid.'
}
. ([scriptblock]::Create($functions[0].Extent.Text))

$runnerRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP)
$probeRoot = [IO.Path]::GetFullPath((Join-Path $runnerRoot ('wisp-network-probe-' + [guid]::NewGuid().ToString('N'))))
if (-not $probeRoot.StartsWith($runnerRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Network probe directory escaped the runner temporary directory.'
}
$probe = Join-Path $probeRoot 'Wisp.exe'
try {
    [void][IO.Directory]::CreateDirectory($probeRoot)
    # This is Windows curl under a disposable name, not an application launch.
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32/curl.exe') -Destination $probe
    $arguments = @('--head', '--silent', '--output', 'NUL', '--write-out', '%{http_code}',
        '--max-time', '8', 'https://github.com')
    $before = & $probe @arguments
    if ($LASTEXITCODE -ne 0 -or $before -notmatch '^[23][0-9]{2}$') {
        throw 'The unblocked control request failed; isolation is unproven.'
    }
    Invoke-IsolatedCanaryApplication $probe {
        $blocked = & $probe @arguments
        if ($LASTEXITCODE -notin @(7, 28) -or $blocked -cne '000') {
            throw 'The isolated executable was not blocked as expected.'
        }
    }
    $after = & $probe @arguments
    if ($LASTEXITCODE -ne 0 -or $after -notmatch '^[23][0-9]{2}$') {
        throw 'The network rule was not cleanly removed.'
    }
    Write-Output 'Verified canary network isolation: reachable, blocked, reachable after cleanup.'
}
finally {
    if (Test-Path -LiteralPath $probeRoot) { Remove-Item -LiteralPath $probeRoot -Recurse -Force }
}
