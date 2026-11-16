[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$ExpectedVersion,
    [string]$BaselineInstallerPath,
    [string]$BaselineVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function New-InstallerLifecycleWorker {
    param(
        [string]$PowerShellPath,
        [string]$CanaryScript,
        [string]$InstallerPath,
        [string]$ExpectedVersion,
        [string]$StageLogPath,
        [string]$BaselineInstallerPath,
        [string]$BaselineVersion
    )

    if ([string]::IsNullOrWhiteSpace($BaselineInstallerPath) -ne
        [string]::IsNullOrWhiteSpace($BaselineVersion)) {
        throw 'The published upgrade installer and version must be supplied together.'
    }
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $PowerShellPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $false
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.WorkingDirectory = (Get-Location).Path
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
        $CanaryScript, '-InstallerPath', $InstallerPath, '-ExpectedVersion', $ExpectedVersion)) {
        $startInfo.ArgumentList.Add($argument)
    }
    $startInfo.Environment['WISP_INSTALLER_STAGE_LOG'] = $StageLogPath
    if ($BaselineInstallerPath) {
        foreach ($argument in @('-BaselineInstallerPath', $BaselineInstallerPath,
            '-BaselineVersion', $BaselineVersion)) {
            $startInfo.ArgumentList.Add($argument)
        }
    }
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
    $outputTasks = @()
    try {
        try {
            Write-Host 'Supervisor diagnostic: before worker start.'
            $started = $Process.Start()
            Write-Host 'Supervisor diagnostic: worker start returned.'
        }
        catch {
            throw 'The installer lifecycle worker could not start.'
        }
        if (-not $started) {
            throw 'The installer lifecycle worker could not start.'
        }
        # A hidden console keeps the Windows installer lifecycle responsive. Drain both
        # streams concurrently; only the sanitized stage file reaches public CI output.
        $outputTasks = @(
            $Process.StandardOutput.BaseStream.CopyToAsync([System.IO.Stream]::Null)
            $Process.StandardError.BaseStream.CopyToAsync([System.IO.Stream]::Null)
        )
        Write-Host 'Supervisor diagnostic: output drains scheduled; entering process wait.'
        # The installer launch runs inside this worker, so its entire launch and wait
        # are bounded even if a native launch call never returns to the inner script.
        if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) {
            Write-Host 'Supervisor diagnostic: process wait reached its deadline.'
            throw "The installer lifecycle worker exceeded its $TimeoutSeconds-second deadline."
        }
        Write-Host 'Supervisor diagnostic: process wait completed.'
        if ($Process.ExitCode -ne 0) {
            throw "The installer lifecycle worker failed with exit code $($Process.ExitCode)."
        }
    }
    finally {
        try {
            if ($started -and -not $Process.HasExited) {
                Write-Host 'Supervisor diagnostic: terminating the owned worker tree.'
                $Process.Kill($true)
                Write-Host 'Supervisor diagnostic: termination returned.'
                if (-not $Process.WaitForExit(10000)) {
                    throw 'The installer lifecycle worker did not stop after termination.'
                }
            }
            if ($outputTasks.Count -gt 0) {
                Write-Host 'Supervisor diagnostic: waiting for output drains.'
                try {
                    if (-not [System.Threading.Tasks.Task]::WaitAll(
                            [System.Threading.Tasks.Task[]]$outputTasks, 10000)) {
                        throw 'The worker output did not close before its deadline.'
                    }
                }
                catch { throw 'The installer lifecycle worker output could not be drained.' }
                Write-Host 'Supervisor diagnostic: output drains completed.'
            }
        }
        finally {
            Write-Host 'Supervisor diagnostic: disposing worker handles.'
            $Process.Dispose()
            Write-Host 'Supervisor diagnostic: worker handles disposed.'
        }
    }
}

if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:CI -cne 'true') {
    throw 'The installer lifecycle supervisor runs only in Windows GitHub Actions CI.'
}

$installer = (Resolve-Path -LiteralPath $InstallerPath -ErrorAction Stop).Path
$evidenceDirectory = Join-Path $PSScriptRoot '../../outputs/installer-lifecycle'
[void][System.IO.Directory]::CreateDirectory($evidenceDirectory)
$scenario = if ($BaselineInstallerPath) { 'upgrade' } else { 'fresh' }
$stageLog = [System.IO.Path]::GetFullPath((Join-Path $evidenceDirectory "$scenario-stages.log"))
[System.IO.File]::WriteAllText($stageLog, '', [System.Text.UTF8Encoding]::new($false))
$worker = New-InstallerLifecycleWorker (Join-Path $PSHOME 'pwsh.exe') `
    (Join-Path $PSScriptRoot 'Test-InstallerLifecycle.ps1') $installer $ExpectedVersion $stageLog `
    $BaselineInstallerPath $BaselineVersion

Write-Output 'Installer lifecycle supervisor started.'
try { Invoke-InstallerLifecycleWorker $worker }
finally {
    Get-Content -LiteralPath $stageLog -Tail 10 -ErrorAction SilentlyContinue |
        Where-Object { $_ -cmatch '\AInstaller lifecycle failure: type=[A-Za-z][A-Za-z0-9]{0,79}; line=[0-9]+(?:; updater_state=(?:failed|installed); updater_error=(?:UPDATE_[A-Z0-9_]{1,80})?)?\z' } |
        Write-Output
}
Write-Output 'Installer lifecycle supervisor completed.'
