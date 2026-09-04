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

if (-not $IsWindows) {
    throw 'The installer lifecycle canary requires Windows.'
}
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:CI -cne 'true') {
    throw 'The installer lifecycle canary runs only in GitHub Actions CI.'
}

$installer = (Resolve-Path -LiteralPath $InstallerPath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw 'The installer lifecycle canary requires an existing installer file.'
}

if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or
    -not (Test-Path -LiteralPath $env:RUNNER_TEMP -PathType Container)) {
    throw 'The installer lifecycle canary runs only on an ephemeral GitHub Actions runner.'
}

$runnerTemp = [System.IO.Path]::GetFullPath($env:RUNNER_TEMP)
$canaryRoot = [System.IO.Path]::GetFullPath((Join-Path $runnerTemp (
    'wisp-installer-lifecycle-' + [guid]::NewGuid().ToString('N'))))
$runnerPrefix = $runnerTemp.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $canaryRoot.StartsWith($runnerPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installer lifecycle directory escaped RUNNER_TEMP.'
}

$installDirectory = Join-Path $canaryRoot 'app'
$localApplicationData = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localApplicationData) -or
    -not [System.IO.Path]::IsPathFullyQualified($localApplicationData)) {
    throw 'The local application-data directory is unavailable for the installer lifecycle canary.'
}
$stateDirectory = Join-Path $localApplicationData 'Wisp'
$setupMarker = Join-Path $stateDirectory 'setup-required'
$settingsPath = Join-Path $stateDirectory 'settings.json'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A8FC0D58-11E3-4B25-B78D-3B98E9855473}_is1'
$ownsCanaryState = $false
$settingsSentinel = [ordered]@{
    SettingsRevision = 7
    UdpPort = 5500
    StartWithWindows = $false
    HasCompletedSetup = $true
    SetupCompletion = [ordered]@{
        Version = 1
        CompletedAtUtc = '2026-08-31T00:00:00+00:00'
        ValidatedUdpPort = 5500
        ValidatedPackets = 12
        MovingPackets = 3
        ValidatedElapsedMilliseconds = 500
        DataOutConfirmed = $true
        DisplayModeConfirmed = $true
        StockHudConfirmed = $true
    }
} | ConvertTo-Json -Depth 4
$versionPadding = [char[]]@([char]0, [char]' ')

function Invoke-CheckedProcess {
    param(
        [string]$Path,
        [string[]]$Arguments,
        [string]$Label,
        [int]$TimeoutSeconds = 180
    )

    Write-Output "$Label started."
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Write-Warning "$Label exceeded its $TimeoutSeconds-second timeout; terminating its process tree."
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) {
                Write-Warning "$Label did not terminate after it was stopped."
            }
            throw "$Label exceeded its $TimeoutSeconds-second timeout."
        }
        if ($process.ExitCode -ne 0) {
            throw "$Label failed with exit code $($process.ExitCode)."
        }
        Write-Output "$Label completed."
    }
    finally {
        $process.Dispose()
    }
}

function Assert-InstalledExecutable {
    param(
        [string]$Path,
        [string]$ExpectedDescription
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The installed file is missing: $([System.IO.Path]::GetFileName($Path))"
    }

    $identity = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $fileVersion = $identity.FileVersion?.TrimEnd($versionPadding)
    $productVersion = $identity.ProductVersion?.TrimEnd($versionPadding)
    $productName = $identity.ProductName?.TrimEnd($versionPadding)
    $description = $identity.FileDescription?.TrimEnd($versionPadding)
    if ($fileVersion -cne "$ExpectedVersion.0" -or
        $productVersion -cne $ExpectedVersion -or
        $productName -cne 'Wisp' -or
        $description -cne $ExpectedDescription) {
        throw "The installed identity is invalid: $([System.IO.Path]::GetFileName($Path))"
    }
}

function Assert-RegisteredInstallation {
    if (-not (Test-Path -LiteralPath $uninstallKey)) {
        throw 'The installer did not create its current-user uninstall registration.'
    }

    $registration = Get-ItemProperty -LiteralPath $uninstallKey
    $registeredLocation = [System.IO.Path]::GetFullPath(
        ([string]$registration.InstallLocation).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar))
    if ([string]$registration.DisplayVersion -cne $ExpectedVersion -or
        -not $registeredLocation.Equals(
            [System.IO.Path]::GetFullPath($installDirectory),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The current-user uninstall registration does not match the canary installation.'
    }
}

function Assert-FirstRunSetupLaunch {
    param([string]$ApplicationPath)

    Write-Output 'First-run Wisp Setup launch started.'
    $process = Start-Process -FilePath $ApplicationPath -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $setupWindowFound = $false
        while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.MainWindowHandle -ne [IntPtr]::Zero -and
                $process.MainWindowTitle -ceq 'Wisp Setup') {
                $setupWindowFound = $true
                break
            }
            Start-Sleep -Milliseconds 200
        }

        if (-not $setupWindowFound) {
            if ($process.HasExited) {
                throw "The installed application exited before showing Wisp Setup (exit code $($process.ExitCode))."
            }
            throw 'The installed application did not show Wisp Setup before the deadline.'
        }

        Write-Output 'First-run Wisp Setup window detected.'
        if (-not $process.CloseMainWindow()) {
            throw 'The Wisp Setup window did not accept a bounded close request.'
        }
        Write-Output 'First-run Wisp Setup close requested.'
        if (-not $process.WaitForExit(10000)) {
            throw 'The installed application did not exit after its setup window closed.'
        }
        if ($process.ExitCode -ne 0) {
            throw "The installed application returned exit code $($process.ExitCode) after closing Wisp Setup."
        }
        Write-Output 'First-run Wisp Setup launch completed.'
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) {
                Write-Warning 'The installer canary had to abandon a Wisp process that did not terminate.'
            }
        }
        $process.Dispose()
    }
}

function Wait-ForRemoval {
    param(
        [string]$Path,
        [bool]$RegistryPath
    )

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (Test-Path -LiteralPath $Path) {
        if ([DateTime]::UtcNow -ge $deadline) {
            $kind = if ($RegistryPath) { 'registration' } else { 'file' }
            throw "The uninstaller did not remove its $kind before the deadline."
        }
        Start-Sleep -Milliseconds 200
    }
}

function Remove-CanaryPath {
    param(
        [string]$Path,
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    try {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    catch {
        Write-Warning "Could not remove the $Label during ephemeral-runner cleanup."
    }
}

if (Test-Path -LiteralPath $uninstallKey) {
    throw 'Refusing to run over an existing Wisp uninstall registration.'
}
if (Test-Path -LiteralPath $stateDirectory) {
    throw 'Refusing to run over an existing Wisp local-state directory.'
}

try {
    [System.IO.Directory]::CreateDirectory($canaryRoot) | Out-Null
    $ownsCanaryState = $true
    $installArguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/SP-',
        "/DIR=`"$installDirectory`""
    )

    Invoke-CheckedProcess $installer $installArguments 'Fresh installer canary'
    Assert-RegisteredInstallation
    Assert-InstalledExecutable (Join-Path $installDirectory 'Wisp.exe') 'Wisp'
    Assert-InstalledExecutable (Join-Path $installDirectory 'Wisp.Updater.exe') 'Wisp Update Helper'
    if (-not (Test-Path -LiteralPath $setupMarker -PathType Leaf)) {
        throw 'A fresh installation did not require first-run setup.'
    }
    Assert-FirstRunSetupLaunch (Join-Path $installDirectory 'Wisp.exe')

    [System.IO.File]::WriteAllText(
        $settingsPath,
        $settingsSentinel,
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::Delete($setupMarker)

    Invoke-CheckedProcess $installer ($installArguments + '/WISPUPDATE') 'In-place update canary'
    Assert-RegisteredInstallation
    Assert-InstalledExecutable (Join-Path $installDirectory 'Wisp.exe') 'Wisp'
    Assert-InstalledExecutable (Join-Path $installDirectory 'Wisp.Updater.exe') 'Wisp Update Helper'
    if (Test-Path -LiteralPath $setupMarker) {
        throw 'An in-place update incorrectly required first-run setup again.'
    }
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf) -or
        [System.IO.File]::ReadAllText($settingsPath) -cne $settingsSentinel) {
        throw 'An in-place update did not preserve the existing settings file.'
    }

    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    Invoke-CheckedProcess $uninstaller @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART'
    ) 'Uninstaller canary'
    Wait-ForRemoval (Join-Path $installDirectory 'Wisp.exe') $false
    Wait-ForRemoval $uninstaller $false
    Wait-ForRemoval $uninstallKey $true

    Write-Output 'Verified fresh install, Wisp Setup launch, in-place update preservation, and uninstall.'
}
finally {
    if ($ownsCanaryState) {
        Write-Output 'Installer lifecycle cleanup started.'
        Remove-CanaryPath $uninstallKey 'uninstall registration'
        Remove-CanaryPath $canaryRoot 'installation directory'
        Remove-CanaryPath $stateDirectory 'local-state directory'
        Write-Output 'Installer lifecycle cleanup completed.'
    }
}
