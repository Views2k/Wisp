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

function Get-InstallerLogStages {
    param([string]$LogPath)

    $stream = $null
    try {
        # Only inspect a bounded tail. Native logs can contain private paths and names;
        # neither their contents nor read exceptions may reach the workflow output.
        $stream = [System.IO.File]::Open($LogPath, [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
        $header = [byte[]]::new(3)
        $headerLength = $stream.Read($header, 0, $header.Length)
        $encoding = [System.Text.Encoding]::UTF8
        if ($headerLength -ge 2 -and $header[0] -eq 255 -and $header[1] -eq 254) {
            $encoding = [System.Text.Encoding]::Unicode
        }
        $offset = [Math]::Max(0, $stream.Length - 65536)
        if ($encoding.CodePage -eq 1200) {
            $offset -= $offset % 2
        }
        [void]$stream.Seek($offset, [System.IO.SeekOrigin]::Begin)
        $buffer = [byte[]]::new(65536)
        $count = $stream.Read($buffer, 0, $buffer.Length)
        $tail = $encoding.GetString($buffer, 0, $count)
        if ($offset -gt 0) {
            $firstNewline = $tail.IndexOf("`n")
            if ($firstNewline -lt 0) { return }
            $tail = $tail.Substring($firstNewline + 1)
        }
        $lastNewline = $tail.LastIndexOf("`n")
        if ($lastNewline -lt 0) { return }
        $tail = $tail.Substring(0, $lastNewline + 1)
    }
    catch {
        return
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
    }

    # Best-effort whitelist for CI's pinned Inno Setup 6.7.1, not a success check.
    # Source: jrsoftware/issrc tag is-6_7_1, Setup.WizardForm/MainFunc/Install.pas.
    foreach ($line in ($tail -split "`r?`n")) {
        if ($line -cnotmatch '^\uFEFF?[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}[ \t]+(?<Message>.+)$') {
            continue
        }
        switch -CaseSensitive -Regex ($matches.Message) {
            '^Found a file to register with RestartManager: .+$' { 'restart-manager-registering-files'; break }
            '^Found [0-9]+ files to register with RestartManager\.$' { 'restart-manager-resources-ready'; break }
            '^Calling RestartManager''s RmGetList\.$' { 'restart-manager-query-started'; break }
            '^RmGetList finished successfully\.$' { 'restart-manager-query-completed'; break }
            '^RmGetList failed\.$' { 'restart-manager-query-failed'; break }
            '^RestartManager found no applications using one of our files\.$' { 'restart-manager-no-applications'; break }
            '^RestartManager found an application using one of our files: Wisp$' { 'restart-manager-application-wisp'; break }
            '^RestartManager found an application using one of our files: Wisp Update Helper$' { 'restart-manager-application-wisp-updater'; break }
            '^RestartManager found an application using one of our files: .+$' { 'restart-manager-application-found'; break }
            '^Starting the installation process\.$' { 'installation-started'; break }
            '^Installation process succeeded\.$' { 'installation-succeeded'; break }
            '^Deinitializing Setup\.$' { 'setup-deinitializing'; break }
        }
    }
}

function Write-InstallerLifecycleMarker {
    param(
        [string]$Label,
        [string]$Stage,
        [System.Diagnostics.Stopwatch]$Timer
    )

    if ($Label -cnotin @('Fresh installer canary', 'In-place update canary',
            'Uninstaller canary', 'First-run Wisp Setup') -or
        $Stage -cnotmatch '\A(?:started|launch returned|before process wait|completed|window detected|close requested|native stage observed: (?:restart-manager-(?:registering-files|resources-ready|query-started|query-completed|query-failed|no-applications|application-wisp|application-wisp-updater|application-found)|installation-started|installation-succeeded|setup-deinitializing))\z') {
        return
    }
    $message = "{0} {1}. utc={2:o} elapsed_ms={3}" -f
        $Label, $Stage, [DateTime]::UtcNow, $Timer.ElapsedMilliseconds
    Write-Host $message
    if (-not [string]::IsNullOrWhiteSpace($env:WISP_INSTALLER_STAGE_LOG)) {
        try {
            if (-not [System.IO.Path]::IsPathFullyQualified($env:WISP_INSTALLER_STAGE_LOG)) {
                throw 'The stage evidence path must be absolute.'
            }
            [System.IO.File]::AppendAllText($env:WISP_INSTALLER_STAGE_LOG,
                $message + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
        }
        catch {
            Write-Warning 'Could not persist installer lifecycle stage evidence.'
        }
    }
}

function New-InstallerProcess {
    param(
        [string]$Path,
        [string[]]$Arguments
    )

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo.FileName = $Path
    # Launch the executable directly, avoiding desktop Start-Process's ShellExecute path.
    # Preserve the existing, explicitly quoted Inno command line and working directory.
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.Arguments = [string]::Join(' ', $Arguments)
    $process.StartInfo.WorkingDirectory = (Get-Location).ProviderPath
    return $process
}

function Write-InstallerLogStages {
    param(
        [string]$LogPath,
        [string]$Label,
        [System.Diagnostics.Stopwatch]$Timer,
        [System.Collections.Generic.HashSet[string]]$ObservedStages
    )

    foreach ($stage in (Get-InstallerLogStages $LogPath)) {
        if ($ObservedStages.Add($stage)) {
            Write-InstallerLifecycleMarker $Label "native stage observed: $stage" $Timer
        }
    }
}

function Wait-InstallerProcessExit {
    param(
        $Process,
        [string]$LogPath,
        [string]$Label,
        [System.Diagnostics.Stopwatch]$OperationTimer,
        [int]$TimeoutSeconds
    )

    $observedStages = [System.Collections.Generic.HashSet[string]]::new()
    while ($OperationTimer.ElapsedMilliseconds -lt $TimeoutSeconds * 1000) {
        Write-InstallerLogStages $LogPath $Label $OperationTimer $observedStages
        # Launch and diagnostics consume the same deadline; no slice resets it.
        $remaining = [Math]::Max(0, $TimeoutSeconds * 1000 - $OperationTimer.ElapsedMilliseconds)
        if ($Process.WaitForExit([int][Math]::Min(1000, $remaining))) {
            Write-InstallerLogStages $LogPath $Label $OperationTimer $observedStages
            return $true
        }
    }
    return $Process.WaitForExit(0)
}

function Invoke-CheckedProcess {
    param(
        [string]$Path,
        [string[]]$Arguments,
        [string]$Label,
        [string]$LogPath,
        [int]$TimeoutSeconds = 180
    )

    $operationTimer = [System.Diagnostics.Stopwatch]::StartNew()
    Write-InstallerLifecycleMarker $Label 'started' $operationTimer
    $process = New-InstallerProcess $Path $Arguments
    try {
        try { $started = $process.Start() }
        catch { throw "$Label launch failed." }
        if (-not $started) { throw "$Label launch failed." }
        Write-InstallerLifecycleMarker $Label 'launch returned' $operationTimer
        Write-InstallerLifecycleMarker $Label 'before process wait' $operationTimer
        if (-not (Wait-InstallerProcessExit $process $LogPath $Label $operationTimer $TimeoutSeconds)) {
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
        Write-InstallerLifecycleMarker $Label 'completed' $operationTimer
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

    $operationTimer = [System.Diagnostics.Stopwatch]::StartNew()
    Write-InstallerLifecycleMarker 'First-run Wisp Setup' 'started' $operationTimer
    $process = New-InstallerProcess $ApplicationPath @()
    $started = $false
    try {
        try { $started = $process.Start() }
        catch { throw 'The installed application could not be started.' }
        if (-not $started) { throw 'The installed application could not be started.' }
        Write-InstallerLifecycleMarker 'First-run Wisp Setup' 'launch returned' $operationTimer
        $setupWindowFound = $false
        while (-not $process.HasExited -and $operationTimer.ElapsedMilliseconds -lt 30000) {
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

        Write-InstallerLifecycleMarker 'First-run Wisp Setup' 'window detected' $operationTimer
        if (-not $process.CloseMainWindow()) {
            throw 'The Wisp Setup window did not accept a bounded close request.'
        }
        Write-InstallerLifecycleMarker 'First-run Wisp Setup' 'close requested' $operationTimer
        if (-not $process.WaitForExit(10000)) {
            throw 'The installed application did not exit after its setup window closed.'
        }
        if ($process.ExitCode -ne 0) {
            throw "The installed application returned exit code $($process.ExitCode) after closing Wisp Setup."
        }
        Write-InstallerLifecycleMarker 'First-run Wisp Setup' 'completed' $operationTimer
    }
    finally {
        if ($started -and -not $process.HasExited) {
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
    $freshLog = Join-Path $canaryRoot 'fresh-install.log'
    $updateLog = Join-Path $canaryRoot 'in-place-update.log'
    $uninstallLog = Join-Path $canaryRoot 'uninstall.log'

    Invoke-CheckedProcess $installer ($installArguments + @(
        "/LOG=`"$freshLog`"", '/LOGCLOSEAPPLICATIONS'
    )) 'Fresh installer canary' $freshLog
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

    Invoke-CheckedProcess $installer ($installArguments + @(
        '/WISPUPDATE', "/LOG=`"$updateLog`"", '/LOGCLOSEAPPLICATIONS'
    )) 'In-place update canary' $updateLog
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
        '/NORESTART',
        "/LOG=`"$uninstallLog`""
    ) 'Uninstaller canary' $uninstallLog
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
