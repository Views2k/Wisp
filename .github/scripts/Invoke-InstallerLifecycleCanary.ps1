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
    $startInfo.RedirectStandardInput = $true
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

function Write-InstallerLifecycleProgress {
    param([string]$StageLogPath, [ref]$LinesRead)

    if (-not $StageLogPath) { return }
    try {
        $lines = [IO.File]::ReadAllText($StageLogPath) -split '\r?\n'
    }
    catch [IO.IOException], [UnauthorizedAccessException] { return }
    $stamp = '\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z'
    $marker = "\A$installerStageLabels $installerStages\. utc=$stamp elapsed_ms=[0-9]+\z"
    $failure = '\AInstaller lifecycle failure: type=[A-Za-z][A-Za-z0-9]{0,79}; line=[0-9]+(?:; updater_state=(?:failed|installed); updater_error=(?:UPDATE_[A-Z0-9_]{1,80})?)?\z'
    while ($LinesRead.Value -lt ($lines.Count - 1)) {
        $line = $lines[$LinesRead.Value]
        $LinesRead.Value++
        if ($line -cmatch $marker -or $line -cmatch $failure) {
            Write-Host "Supervisor diagnostic: $line"
        }
    }
}

function Invoke-InstallerLifecycleWorker {
    param(
        $Process,
        [ValidateRange(1, 240)]
        [int]$TimeoutSeconds = 240,
        [string]$StageLogPath
    )

    $started = $false
    $assigned = $false
    $outputTasks = @()
    $linesRead = 0
    $job = [InstallerLifecycleJob]::new()
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
        try {
            $job.Assign($Process)
            $assigned = $true
        }
        catch { throw 'The installer lifecycle worker could not join its Windows job.' }
        # A hidden console keeps the Windows installer lifecycle responsive. Drain both
        # streams concurrently; only the sanitized stage file reaches public CI output.
        $outputTasks = @(
            $Process.StandardOutput.BaseStream.CopyToAsync([System.IO.Stream]::Null)
            $Process.StandardError.BaseStream.CopyToAsync([System.IO.Stream]::Null)
        )
        $Process.StandardInput.WriteLine('run')
        $Process.StandardInput.Close()
        Write-Host 'Supervisor diagnostic: output drains scheduled; entering process wait.'
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $remaining = $TimeoutSeconds * 1000 - $timer.ElapsedMilliseconds
            if ($Process.WaitForExit([int][Math]::Clamp($remaining, 0, 1000))) { break }
            Write-InstallerLifecycleProgress $StageLogPath ([ref]$linesRead)
            if ($timer.ElapsedMilliseconds -ge $TimeoutSeconds * 1000) {
                Write-Host 'Supervisor diagnostic: process wait reached its deadline.'
                throw "The installer lifecycle worker exceeded its $TimeoutSeconds-second deadline."
            }
        }
        Write-Host 'Supervisor diagnostic: process wait completed.'
        if ($Process.ExitCode -ne 0) {
            throw "The installer lifecycle worker failed with exit code $($Process.ExitCode)."
        }
    }
    finally {
        try {
            # Closing this handle also stops descendants whose immediate parent
            # already exited, without enumerating processes on the runner.
            Write-Host 'Supervisor diagnostic: closing the owned worker job.'
            $job.Dispose()
            if ($started -and -not $assigned -and -not $Process.HasExited) {
                $Process.Kill() # The worker is still waiting for our start signal.
            }
            if ($started -and -not $Process.WaitForExit(10000)) {
                throw 'The installer lifecycle worker did not stop after termination.'
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
            try { Write-InstallerLifecycleProgress $StageLogPath ([ref]$linesRead) }
            finally {
                $Process.Dispose()
                Write-Host 'Supervisor diagnostic: worker handles disposed.'
            }
        }
    }
}

if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:CI -cne 'true') {
    throw 'The installer lifecycle supervisor runs only in Windows GitHub Actions CI.'
}

Add-Type -Path (Join-Path $PSScriptRoot 'InstallerLifecycleJob.cs')
. (Join-Path $PSScriptRoot 'InstallerLifecycleStages.ps1')
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
Invoke-InstallerLifecycleWorker $worker -StageLogPath $stageLog
Write-Output 'Installer lifecycle supervisor completed.'
