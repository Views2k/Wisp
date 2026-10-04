[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function New-InstallerLifecycleWorker {
    param(
        [string]$PowerShellPath,
        [string]$CanaryScript,
        [string]$InstallerPath,
        [string]$ExpectedVersion,
        [string]$StageLogPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $PowerShellPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = (Get-Location).Path
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
        $CanaryScript, '-InstallerPath', $InstallerPath, '-ExpectedVersion', $ExpectedVersion)) {
        $startInfo.ArgumentList.Add($argument)
    }
    $startInfo.Environment['WISP_INSTALLER_STAGE_LOG'] = $StageLogPath
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    return $process
}

function Invoke-InstallerLifecycleWorker {
    param(
        $Process,
        [ValidateRange(1, 240)]
        [int]$TimeoutSeconds = 240
    )

    $started = $false
    try {
        try {
            $started = $Process.Start()
        }
        catch {
            throw 'The installer lifecycle worker could not start.'
        }
        if (-not $started) {
            throw 'The installer lifecycle worker could not start.'
        }
        # The installer launch runs inside this worker, so its entire launch and wait
        # are bounded even if a native launch call never returns to the inner script.
        if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) {
            throw "The installer lifecycle worker exceeded its $TimeoutSeconds-second deadline."
        }
        if ($Process.ExitCode -ne 0) {
            throw "The installer lifecycle worker failed with exit code $($Process.ExitCode)."
        }
    }
    finally {
        try {
            if ($started -and -not $Process.HasExited) {
                $Process.Kill($true)
                if (-not $Process.WaitForExit(10000)) {
                    throw 'The installer lifecycle worker did not stop after termination.'
                }
            }
        }
        finally {
            $Process.Dispose()
        }
    }
}

if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:CI -cne 'true') {
    throw 'The installer lifecycle supervisor runs only in Windows GitHub Actions CI.'
}

$installer = (Resolve-Path -LiteralPath $InstallerPath -ErrorAction Stop).Path
$evidenceDirectory = Join-Path $PSScriptRoot '../../outputs/installer-lifecycle'
[void][System.IO.Directory]::CreateDirectory($evidenceDirectory)
$stageLog = [System.IO.Path]::GetFullPath((Join-Path $evidenceDirectory 'stages.log'))
[System.IO.File]::WriteAllText($stageLog, '', [System.Text.UTF8Encoding]::new($false))
$worker = New-InstallerLifecycleWorker (Join-Path $PSHOME 'pwsh.exe') `
    (Join-Path $PSScriptRoot 'Test-InstallerLifecycle.ps1') $installer $ExpectedVersion $stageLog

Write-Output 'Installer lifecycle supervisor started.'
Invoke-InstallerLifecycleWorker $worker
Write-Output 'Installer lifecycle supervisor completed.'
