[CmdletBinding()]
param(
    [string]$DotNet,
    [string]$InnoCompiler,
    [string]$Python,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'

$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repository 'Wisp.sln'
$project = Join-Path $repository 'src\Wisp.App\Wisp.App.csproj'
$updaterProject = Join-Path $repository 'src\Wisp.Updater\Wisp.Updater.csproj'
$uiReviewProject = Join-Path $repository 'tools\Wisp.UiReview\Wisp.UiReview.csproj'
$updateTestsProject = Join-Path $repository 'tests\Wisp.Update.Tests\Wisp.Update.Tests.csproj'
$updaterTestsProject = Join-Path $repository 'tests\Wisp.Updater.Tests\Wisp.Updater.Tests.csproj'
$pythonTests = Join-Path $repository 'tools\tests'
$publishDirectory = Join-Path $repository 'artifacts\publish'
$updaterPublishDirectory = Join-Path $repository 'artifacts\updater-publish'
$icon = Join-Path $repository 'src\Wisp.App\Assets\Wisp.ico'
$innoScript = Join-Path $PSScriptRoot 'Wisp.iss'

function Resolve-Executable {
    param(
        [string]$ExplicitValue,
        [string[]]$Candidates,
        [string[]]$CommandNames
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitValue)) {
        if (Test-Path -LiteralPath $ExplicitValue -PathType Leaf) {
            return (Resolve-Path -LiteralPath $ExplicitValue).Path
        }

        $explicitCommand = Get-Command -Name $ExplicitValue -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $explicitCommand) {
            return $explicitCommand.Source
        }

        throw "The requested executable was not found: $ExplicitValue"
    }

    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    foreach ($commandName in $CommandNames) {
        $command = Get-Command -Name $commandName -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $command) {
            return $command.Source
        }
    }

    return $null
}

function Get-RepositorySourceState {
    param(
        [string]$RepositoryRoot,
        [string]$GitExecutable,
        [switch]$AllowDirty
    )

    $topLevel = @(& $GitExecutable -C $RepositoryRoot rev-parse --show-toplevel 2>$null)
    if ($LASTEXITCODE -ne 0 -or $topLevel.Count -ne 1 -or
        [string]::IsNullOrWhiteSpace($topLevel[0])) {
        throw 'The release source is not a readable Git worktree.'
    }

    try {
        $resolvedTopLevel = [System.IO.Path]::GetFullPath($topLevel[0])
    }
    catch {
        throw [System.IO.InvalidDataException]::new('Git returned an invalid repository root.', $_.Exception)
    }
    $resolvedRepository = [System.IO.Path]::GetFullPath($RepositoryRoot)
    if (-not $resolvedTopLevel.Equals($resolvedRepository, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The packaging workspace is not the root of the resolved Git worktree.'
    }

    $head = @(& $GitExecutable -C $RepositoryRoot rev-parse --verify 'HEAD^{commit}' 2>$null)
    if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $head[0] -notmatch '^[0-9a-fA-F]{40,64}$') {
        throw 'The release source does not have a resolvable Git HEAD commit.'
    }

    $status = @(& $GitExecutable -C $RepositoryRoot status --porcelain=v1 --untracked-files=all 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw 'Git could not determine whether the release source is clean.'
    }
    $isDirty = $status.Count -ne 0
    if ($isDirty -and -not $AllowDirty) {
        throw 'The release source has tracked or untracked changes. Commit them or use -AllowDirty for a private test build.'
    }

    return [pscustomobject]@{
        Revision = $head[0].ToLowerInvariant()
        IsDirty = $isDirty
    }
}

function Assert-ReleasePath {
    param([string]$Path, [string]$Root)

    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPath + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release paths must remain inside the workspace.'
    }

    $current = $fullPath
    while ($current.Length -ge $rootPath.Length) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Release paths must not traverse symbolic links or junctions.'
            }
        }

        if ($current.Equals($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $current = [System.IO.Path]::GetDirectoryName($current)
    }

    return $fullPath
}

function Assert-InstallerExecutable {
    param(
        [string]$Path,
        [string]$Version,
        [string]$ExpectedProductName,
        [string]$ExpectedFileDescription,
        [string]$ExpectedProductVersion
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedProductName) -or
        [string]::IsNullOrWhiteSpace($ExpectedFileDescription) -or
        [string]::IsNullOrWhiteSpace($ExpectedProductVersion)) {
        throw 'Executable identity expectations must be explicit.'
    }

    $reader = [System.IO.BinaryReader]::new([System.IO.File]::OpenRead($Path))
    try {
        if ($reader.BaseStream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw 'The staged executable is empty or has an invalid executable header.'
        }
        $reader.BaseStream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -lt 64 -or $peOffset -gt $reader.BaseStream.Length - 4) {
            throw 'The staged executable has invalid PE header bounds.'
        }
        $reader.BaseStream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw 'The staged executable has an invalid PE signature.'
        }
        if ($peOffset -gt $reader.BaseStream.Length - 24) {
            throw 'The staged executable has truncated COFF characteristics.'
        }
        $reader.BaseStream.Position = $peOffset + 22
        $characteristics = $reader.ReadUInt16()
        if (($characteristics -band 0x0002) -eq 0 -or ($characteristics -band 0x2000) -ne 0) {
            throw 'The staged executable is not a non-DLL Windows executable image.'
        }
    }
    finally {
        $reader.Dispose()
    }

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    # Inno Setup 6.7 right-pads version-resource strings. Strip only the
    # padding permitted by Win32 resources; preserve all semantic characters.
    $versionPadding = [char[]]@([char]0, [char]0x20)
    $productName = ([string]$versionInfo.ProductName).TrimEnd($versionPadding)
    $fileDescription = ([string]$versionInfo.FileDescription).TrimEnd($versionPadding)
    $productVersion = ([string]$versionInfo.ProductVersion).TrimEnd($versionPadding)
    if ($productName -cne $ExpectedProductName -or
        $fileDescription -cne $ExpectedFileDescription -or
        $productVersion -cne $ExpectedProductVersion) {
        throw 'The staged executable identity does not match the requested release.'
    }
    $fileVersion = [System.Version]::new(
        $versionInfo.FileMajorPart, $versionInfo.FileMinorPart,
        $versionInfo.FileBuildPart, $versionInfo.FilePrivatePart)
    if ($fileVersion -ne [System.Version]::Parse("$Version.0")) {
        throw 'The staged executable file version does not match the requested release.'
    }
}

function Invoke-UpdaterGuardSmoke {
    param([string]$UpdaterPath, [string]$StagingDirectory)

    $smokeDirectory = Join-Path $StagingDirectory 'updater-smoke'
    if (Test-Path -LiteralPath $smokeDirectory) {
        throw 'The updater smoke-test directory unexpectedly already exists.'
    }
    [System.IO.Directory]::CreateDirectory($smokeDirectory) | Out-Null

    $smokeUpdaterPath = Join-Path $smokeDirectory 'Wisp.Updater.exe'
    $backupPath = Join-Path $smokeDirectory 'previous-update-result.bin'
    $localApplicationData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localApplicationData)) {
        throw 'The local application-data directory is unavailable for the updater smoke test.'
    }
    $resultDirectory = Join-Path $localApplicationData 'Wisp'
    $resultPath = Join-Path $resultDirectory 'update-result.json'
    $resultDirectoryExisted = Test-Path -LiteralPath $resultDirectory -PathType Container
    if (Test-Path -LiteralPath $resultDirectory) {
        $resultDirectoryItem = Get-Item -LiteralPath $resultDirectory -Force
        if (-not $resultDirectoryItem.PSIsContainer -or
            ($resultDirectoryItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The updater diagnostic directory is not a regular local directory.'
        }
    }

    $hadResult = Test-Path -LiteralPath $resultPath -PathType Leaf
    if ((Test-Path -LiteralPath $resultPath) -and -not $hadResult) {
        throw 'The updater diagnostic path is not a regular file.'
    }
    $previousHash = $null
    $previousAttributes = $null
    $previousCreationTime = $null
    $previousWriteTime = $null
    if ($hadResult) {
        $resultItem = Get-Item -LiteralPath $resultPath -Force
        if (($resultItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The updater diagnostic file must not be a symbolic link.'
        }
        $previousHash = (Get-FileHash -LiteralPath $resultPath -Algorithm SHA256).Hash
        $previousAttributes = $resultItem.Attributes
        $previousCreationTime = $resultItem.CreationTimeUtc
        $previousWriteTime = $resultItem.LastWriteTimeUtc
        [System.IO.File]::Copy($resultPath, $backupPath, $false)
        if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne $previousHash) {
            throw 'The existing updater diagnostic could not be backed up for the smoke test.'
        }
    }

    try {
        [System.IO.File]::Copy($UpdaterPath, $smokeUpdaterPath, $false)
        if ((Get-FileHash -LiteralPath $smokeUpdaterPath -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $UpdaterPath -Algorithm SHA256).Hash) {
            throw 'The staged update-helper smoke copy failed identity validation.'
        }

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $smokeUpdaterPath
        $startInfo.Arguments = '--packaging-smoke'
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $process = [System.Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            throw 'The staged update helper did not start for its guard smoke test.'
        }
        try {
            if (-not $process.WaitForExit(30000)) {
                $process.Kill()
                $process.WaitForExit()
                throw 'The staged update-helper guard smoke test timed out.'
            }
            if ($process.ExitCode -ne 2) {
                throw "The staged update helper returned unexpected guard exit code $($process.ExitCode)."
            }
        }
        finally {
            $process.Dispose()
        }

        if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
            $diagnostic = ConvertFrom-Json `
                -InputObject ([System.IO.File]::ReadAllText($resultPath)) `
                -ErrorAction Stop
            if ($diagnostic.schemaVersion -ne 1 -or $diagnostic.state -cne 'failed' -or
                $diagnostic.sourceVersion -cne '' -or $diagnostic.targetVersion -cne '' -or
                $diagnostic.errorCode -cne 'UPDATE_ARGUMENTS' -or
                $diagnostic.message -cne 'The update helper received invalid arguments.') {
                throw 'The staged update helper wrote an unexpected guard diagnostic.'
            }
        }
    }
    finally {
        if ($hadResult) {
            [System.IO.File]::Copy($backupPath, $resultPath, $true)
            [System.IO.File]::SetCreationTimeUtc($resultPath, $previousCreationTime)
            [System.IO.File]::SetLastWriteTimeUtc($resultPath, $previousWriteTime)
            [System.IO.File]::SetAttributes($resultPath, $previousAttributes)
            if ((Get-FileHash -LiteralPath $resultPath -Algorithm SHA256).Hash -ne $previousHash) {
                throw 'The updater diagnostic was not restored after the smoke test.'
            }
        }
        elseif (Test-Path -LiteralPath $resultPath -PathType Leaf) {
            [System.IO.File]::Delete($resultPath)
        }

        if (-not $resultDirectoryExisted -and (Test-Path -LiteralPath $resultDirectory -PathType Container) -and
            [System.IO.Directory]::GetFileSystemEntries($resultDirectory).Length -eq 0) {
            [System.IO.Directory]::Delete($resultDirectory, $false)
        }
        if (Test-Path -LiteralPath $smokeUpdaterPath -PathType Leaf) {
            [System.IO.File]::Delete($smokeUpdaterPath)
        }
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
            [System.IO.File]::Delete($backupPath)
        }
        if ((Test-Path -LiteralPath $smokeDirectory -PathType Container) -and
            [System.IO.Directory]::GetFileSystemEntries($smokeDirectory).Length -eq 0) {
            [System.IO.Directory]::Delete($smokeDirectory, $false)
        }
    }
}

function Assert-SinglePassedTestResult {
    param(
        [string]$TrxPath,
        [string]$ValidationLabel
    )

    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        throw "$ValidationLabel did not produce its required TRX result."
    }

    try {
        [xml]$trx = [System.IO.File]::ReadAllText($TrxPath)
    }
    catch {
        throw "$ValidationLabel produced an unreadable TRX result: $($_.Exception.Message)"
    }

    $counters = $trx.SelectSingleNode(
        "/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
    $results = @($trx.SelectNodes(
        "/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']"))
    if ($null -eq $counters -or
        [int]$counters.GetAttribute('total') -ne 1 -or
        [int]$counters.GetAttribute('executed') -ne 1 -or
        [int]$counters.GetAttribute('passed') -ne 1 -or
        [int]$counters.GetAttribute('failed') -ne 0 -or
        [int]$counters.GetAttribute('error') -ne 0 -or
        [int]$counters.GetAttribute('aborted') -ne 0 -or
        [int]$counters.GetAttribute('notExecuted') -ne 0 -or
        $results.Count -ne 1 -or
        $results[0].GetAttribute('outcome') -cne 'Passed') {
        throw "$ValidationLabel must execute and pass exactly one test."
    }
}

function Invoke-InstallerRuntimeValidation {
    param(
        [string]$DotNetExecutable,
        [string]$InstallerPath,
        [string]$Version,
        [string]$UpdateTestsProject,
        [string]$UpdaterTestsProject
    )

    $previousInstallerPath = $env:WISP_TEST_INSTALLER_PATH
    $previousInstallerVersion = $env:WISP_TEST_INSTALLER_VERSION
    $resultsDirectory = Join-Path `
        ([System.IO.Path]::GetDirectoryName($InstallerPath)) `
        'runtime-validation-results'
    if (Test-Path -LiteralPath $resultsDirectory) {
        throw 'The installer runtime-validation results directory unexpectedly already exists.'
    }
    [System.IO.Directory]::CreateDirectory($resultsDirectory) | Out-Null
    $applicationUpdaterTrx = Join-Path $resultsDirectory 'application-updater.trx'
    $updateHelperTrx = Join-Path $resultsDirectory 'update-helper.trx'
    try {
        $env:WISP_TEST_INSTALLER_PATH = $InstallerPath
        $env:WISP_TEST_INSTALLER_VERSION = $Version

        & $DotNetExecutable test $UpdateTestsProject --configuration Release `
            --no-build `
            --no-restore `
            --nologo `
            --filter 'FullyQualifiedName=Wisp.Update.Tests.InstallerArtifactVerifierIntegrationTests.ConfiguredInstallerPassesRuntimeVerification' `
            --logger 'trx;LogFileName=application-updater.trx' `
            --results-directory $resultsDirectory `
            --disable-build-servers `
            -m:1 `
            -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) {
            throw "The application updater rejected the staged installer with exit code $LASTEXITCODE."
        }
        Assert-SinglePassedTestResult $applicationUpdaterTrx 'Application updater installer validation'

        & $DotNetExecutable test $UpdaterTestsProject --configuration Release `
            --no-build `
            --no-restore `
            --nologo `
            --filter 'FullyQualifiedName=Wisp.Updater.Tests.InstallerArtifactValidatorTests.ConfiguredInstallerPassesRealRuntimeValidation' `
            --logger 'trx;LogFileName=update-helper.trx' `
            --results-directory $resultsDirectory `
            --disable-build-servers `
            -m:1 `
            -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) {
            throw "The staged update helper rejected the staged installer with exit code $LASTEXITCODE."
        }
        Assert-SinglePassedTestResult $updateHelperTrx 'Update helper installer validation'
    }
    finally {
        if ($null -eq $previousInstallerPath) {
            Remove-Item Env:\WISP_TEST_INSTALLER_PATH -ErrorAction SilentlyContinue
        }
        else {
            $env:WISP_TEST_INSTALLER_PATH = $previousInstallerPath
        }
        if ($null -eq $previousInstallerVersion) {
            Remove-Item Env:\WISP_TEST_INSTALLER_VERSION -ErrorAction SilentlyContinue
        }
        else {
            $env:WISP_TEST_INSTALLER_VERSION = $previousInstallerVersion
        }
    }
}

function Restore-InstallerArtifact {
    param([string]$Destination, [string]$Backup, [string]$NewHash, [string]$StagingDirectory)

    if (Test-Path -LiteralPath $Backup -PathType Leaf) {
        $backupHash = (Get-FileHash -LiteralPath $Backup -Algorithm SHA256).Hash
        if ((Test-Path -LiteralPath $Destination -PathType Leaf) -and
            (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $backupHash) {
            return
        }

        $restorePath = Join-Path $StagingDirectory ([guid]::NewGuid().ToString('N') + '.restore')
        [System.IO.File]::Copy($Backup, $restorePath, $false)
        if (Test-Path -LiteralPath $Destination -PathType Leaf) {
            [System.IO.File]::Replace($restorePath, $Destination, [System.Management.Automation.Language.NullString]::Value)
        }
        else {
            [System.IO.File]::Move($restorePath, $Destination)
        }

        if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $backupHash) {
            throw 'The previous release could not be verified after restoration.'
        }
    }
    elseif (Test-Path -LiteralPath $Destination -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $NewHash) {
            throw 'The destination changed during promotion; recovery files have been retained.'
        }
        $rejectedPath = Join-Path $StagingDirectory ('unpromoted-' + [System.IO.Path]::GetFileName($Destination))
        [System.IO.File]::Move($Destination, $rejectedPath)
    }
}

function Initialize-ArchiveSupport {
    Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
}

function Assert-InstallerArchive {
    param([string]$ArchivePath, [string]$SetupFileName, [string]$ExpectedInstallerHash)

    Initialize-ArchiveSupport
    if ($ExpectedInstallerHash -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'The expected installer hash is invalid.'
    }
    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
        throw 'The staged installer archive was not found.'
    }

    $ExpectedInstallerHash = $ExpectedInstallerHash.ToLowerInvariant()
    $checksumFileName = $SetupFileName + '.sha256'
    $expectedChecksum = "$ExpectedInstallerHash *$SetupFileName$([Environment]::NewLine)"
    $archive = [System.IO.Compression.ZipFile]::Open(
        $ArchivePath, [System.IO.Compression.ZipArchiveMode]::Read)
    try {
        $entries = @($archive.Entries)
        if ($entries.Count -ne 2) {
            throw 'The installer archive must contain exactly two files.'
        }
        foreach ($entry in $entries) {
            if ($entry.FullName -cne $entry.Name -or
                @($SetupFileName, $checksumFileName) -cnotcontains $entry.FullName) {
                throw 'The installer archive contains an unexpected path or file.'
            }
        }

        $setupEntries = @($entries | Where-Object { $_.FullName -ceq $SetupFileName })
        $checksumEntries = @($entries | Where-Object { $_.FullName -ceq $checksumFileName })
        if ($setupEntries.Count -ne 1 -or $checksumEntries.Count -ne 1) {
            throw 'The installer archive is missing a required file or contains a duplicate.'
        }

        $setupStream = $setupEntries[0].Open()
        $hasher = [System.Security.Cryptography.SHA256]::Create()
        try {
            $entryHash = ([BitConverter]::ToString($hasher.ComputeHash($setupStream))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $hasher.Dispose()
            $setupStream.Dispose()
        }
        if ($entryHash -cne $ExpectedInstallerHash) {
            throw 'The archived installer does not match the staged installer hash.'
        }

        $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
        if ($checksumEntries[0].Length -ne $utf8.GetByteCount($expectedChecksum)) {
            throw 'The archived installer checksum has an unexpected length.'
        }
        $checksumStream = $checksumEntries[0].Open()
        $reader = [System.IO.StreamReader]::new($checksumStream, $utf8, $false)
        try {
            $archivedChecksum = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
        if ($archivedChecksum -cne $expectedChecksum) {
            throw 'The archived installer checksum does not validate the archived installer.'
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-InstallerArchive {
    param([string]$SetupPath, [string]$ArchivePath, [string]$ExpectedInstallerHash)

    Initialize-ArchiveSupport
    $checksumPath = $SetupPath + '.sha256'
    foreach ($source in @($SetupPath, $checksumPath)) {
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw 'The installer archive source pair is incomplete.'
        }
    }
    if (Test-Path -LiteralPath $ArchivePath) {
        throw 'The staged installer archive path already exists.'
    }

    $archive = [System.IO.Compression.ZipFile]::Open(
        $ArchivePath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($source in @($SetupPath, $checksumPath)) {
            $entry = $archive.CreateEntry(
                [System.IO.Path]::GetFileName($source),
                [System.IO.Compression.CompressionLevel]::Optimal)
            $input = [System.IO.File]::Open(
                $source, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
            $output = $entry.Open()
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    Assert-InstallerArchive $ArchivePath ([System.IO.Path]::GetFileName($SetupPath)) $ExpectedInstallerHash
}

function Read-ReleaseTransactionMarker {
    param([string]$RepositoryRoot, [string]$OutputsDirectory, [string]$MarkerPath)

    $OutputsDirectory = Assert-ReleasePath $OutputsDirectory $RepositoryRoot
    $MarkerPath = Assert-ReleasePath $MarkerPath $RepositoryRoot
    if (-not [System.IO.Path]::GetDirectoryName($MarkerPath).Equals(
            $OutputsDirectory, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($MarkerPath) -cne '.installer-transaction.json') {
        throw 'The release transaction marker must remain directly inside the outputs directory.'
    }
    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) {
        return $null
    }

    try {
        $marker = [System.IO.File]::ReadAllText($MarkerPath) | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw [System.IO.InvalidDataException]::new('The release transaction marker is not valid JSON.', $_.Exception)
    }
    $expectedMarkerProperties = @('SchemaVersion', 'StagingDirectory', 'Artifacts')
    if (@($marker.PSObject.Properties).Count -ne $expectedMarkerProperties.Count) {
        throw 'The release transaction marker contains unexpected fields.'
    }
    foreach ($property in $expectedMarkerProperties) {
        if ($null -eq $marker.PSObject.Properties[$property]) {
            throw "The release transaction marker is missing $property."
        }
    }
    $schemaVersionIsInteger = $marker.SchemaVersion -is [int] -or
        $marker.SchemaVersion -is [long]
    if (-not $schemaVersionIsInteger -or [long]$marker.SchemaVersion -ne 1 -or
        $marker.StagingDirectory -isnot [string]) {
        throw 'The release transaction marker schema is unsupported.'
    }

    $stagingRoot = Assert-ReleasePath (Join-Path $OutputsDirectory '.staging') $RepositoryRoot
    $stagingDirectory = Assert-ReleasePath $marker.StagingDirectory $RepositoryRoot
    if (-not [System.IO.Path]::GetDirectoryName($stagingDirectory).Equals(
            $stagingRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $stagingDirectory -PathType Container)) {
        throw 'The release transaction staging directory is invalid.'
    }

    $artifactNodes = @($marker.Artifacts)
    $expectedLabels = @('installer', 'installer checksum', 'archive', 'archive checksum')
    if ($artifactNodes.Count -ne $expectedLabels.Count) {
        throw 'The release transaction marker must describe exactly four artifacts.'
    }

    $destinations = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $backups = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $artifacts = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $artifactNodes.Count; $index++) {
        $node = $artifactNodes[$index]
        $expectedArtifactProperties = @(
            'Label', 'Destination', 'Backup', 'NewHash', 'HadPrevious', 'PreviousHash')
        if (@($node.PSObject.Properties).Count -ne $expectedArtifactProperties.Count) {
            throw 'A release transaction artifact contains unexpected fields.'
        }
        foreach ($property in $expectedArtifactProperties) {
            if ($null -eq $node.PSObject.Properties[$property]) {
                throw "A release transaction artifact is missing $property."
            }
        }
        if ($node.Label -isnot [string] -or $node.Label -cne $expectedLabels[$index] -or
            $node.Destination -isnot [string] -or $node.Backup -isnot [string] -or
            $node.NewHash -isnot [string] -or $node.HadPrevious -isnot [bool] -or
            $node.PreviousHash -isnot [string]) {
            throw 'A release transaction artifact has an invalid field type or order.'
        }

        $destination = Assert-ReleasePath $node.Destination $RepositoryRoot
        $backup = Assert-ReleasePath $node.Backup $RepositoryRoot
        if (-not [System.IO.Path]::GetDirectoryName($destination).Equals(
                $OutputsDirectory, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [System.IO.Path]::GetDirectoryName($backup).Equals(
                $stagingDirectory, [System.StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($backup) -cne
                ('previous-' + [System.IO.Path]::GetFileName($destination)) -or
            -not $destinations.Add($destination) -or -not $backups.Add($backup)) {
            throw 'A release transaction artifact path is invalid or duplicated.'
        }
        if ($node.NewHash -notmatch '^[0-9a-fA-F]{64}$' -or
            ($node.HadPrevious -and $node.PreviousHash -notmatch '^[0-9a-fA-F]{64}$') -or
            (-not $node.HadPrevious -and $node.PreviousHash.Length -ne 0)) {
            throw 'A release transaction artifact hash is invalid.'
        }

        $artifacts.Add([pscustomobject]@{
            Label = $node.Label
            Destination = $destination
            Backup = $backup
            NewHash = $node.NewHash.ToLowerInvariant()
            HadPrevious = $node.HadPrevious
            PreviousHash = $node.PreviousHash.ToLowerInvariant()
        })
    }

    if ($artifacts[1].Destination -cne ($artifacts[0].Destination + '.sha256') -or
        $artifacts[3].Destination -cne ($artifacts[2].Destination + '.sha256') -or
        [System.IO.Path]::GetExtension($artifacts[0].Destination) -cne '.exe' -or
        [System.IO.Path]::GetExtension($artifacts[2].Destination) -cne '.zip' -or
        [System.IO.Path]::GetFileNameWithoutExtension($artifacts[0].Destination) -cne
            [System.IO.Path]::GetFileNameWithoutExtension($artifacts[2].Destination) -or
        [System.IO.Path]::GetFileName($artifacts[0].Destination) -notmatch
            '^Wisp-Setup-\d+\.\d+\.\d+\.exe$') {
        throw 'The release transaction artifact set is inconsistent.'
    }

    foreach ($artifact in $artifacts) {
        if ($artifact.HadPrevious) {
            if (-not (Test-Path -LiteralPath $artifact.Backup -PathType Leaf) -or
                (Get-FileHash -LiteralPath $artifact.Backup -Algorithm SHA256).Hash -ne $artifact.PreviousHash) {
                throw "The previous $($artifact.Label) backup is missing or invalid."
            }
        }
        elseif (Test-Path -LiteralPath $artifact.Backup) {
            throw "The $($artifact.Label) marker has an unexpected previous backup."
        }

        if (Test-Path -LiteralPath $artifact.Destination) {
            if (-not (Test-Path -LiteralPath $artifact.Destination -PathType Leaf)) {
                throw "The $($artifact.Label) destination is not a regular file."
            }
            $destinationHash = (Get-FileHash -LiteralPath $artifact.Destination -Algorithm SHA256).Hash
            if ($artifact.HadPrevious) {
                if ($destinationHash -ne $artifact.PreviousHash -and $destinationHash -ne $artifact.NewHash) {
                    throw "The $($artifact.Label) destination changed outside the release transaction."
                }
            }
            elseif ($destinationHash -ne $artifact.NewHash) {
                throw "The new $($artifact.Label) destination changed outside the release transaction."
            }
        }
    }

    return [pscustomobject]@{
        StagingDirectory = $stagingDirectory
        Artifacts = $artifacts.ToArray()
    }
}

function Write-ReleaseTransactionMarker {
    param(
        [string]$RepositoryRoot,
        [string]$OutputsDirectory,
        [string]$MarkerPath,
        [string]$StagingDirectory,
        [object[]]$Artifacts
    )

    $OutputsDirectory = Assert-ReleasePath $OutputsDirectory $RepositoryRoot
    $MarkerPath = Assert-ReleasePath $MarkerPath $RepositoryRoot
    $StagingDirectory = Assert-ReleasePath $StagingDirectory $RepositoryRoot
    if (Test-Path -LiteralPath $MarkerPath) {
        throw 'An unresolved release transaction marker already exists.'
    }

    $markerArtifacts = foreach ($artifact in $Artifacts) {
        [ordered]@{
            Label = $artifact.Label
            Destination = $artifact.Destination
            Backup = $artifact.Backup
            NewHash = $artifact.Hash.ToLowerInvariant()
            HadPrevious = [bool]$artifact.HadPrevious
            PreviousHash = $artifact.PreviousHash.ToLowerInvariant()
        }
    }
    $payload = [ordered]@{
        SchemaVersion = 1
        StagingDirectory = $StagingDirectory
        Artifacts = @($markerArtifacts)
    }
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
        ($payload | ConvertTo-Json -Depth 5 -Compress))
    $temporaryPath = Assert-ReleasePath (
        Join-Path $OutputsDirectory ('.installer-transaction-' + [guid]::NewGuid().ToString('N') + '.tmp')) `
        $RepositoryRoot
    try {
        $stream = [System.IO.FileStream]::new(
            $temporaryPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None,
            4096,
            [System.IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }
        [System.IO.File]::Move($temporaryPath, $MarkerPath)
        Read-ReleaseTransactionMarker $RepositoryRoot $OutputsDirectory $MarkerPath | Out-Null
    }
    catch {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            [System.IO.File]::Delete($temporaryPath)
        }
        throw
    }
}

function Recover-ReleaseTransactionIfPresent {
    param([string]$RepositoryRoot, [string]$OutputsDirectory, [string]$MarkerPath)

    $transaction = Read-ReleaseTransactionMarker $RepositoryRoot $OutputsDirectory $MarkerPath
    if ($null -eq $transaction) {
        return $false
    }

    for ($index = $transaction.Artifacts.Count - 1; $index -ge 0; $index--) {
        $artifact = $transaction.Artifacts[$index]
        Restore-InstallerArtifact `
            $artifact.Destination $artifact.Backup $artifact.NewHash $transaction.StagingDirectory
    }
    foreach ($artifact in $transaction.Artifacts) {
        if ($artifact.HadPrevious) {
            if (-not (Test-Path -LiteralPath $artifact.Destination -PathType Leaf) -or
                (Get-FileHash -LiteralPath $artifact.Destination -Algorithm SHA256).Hash -ne
                    $artifact.PreviousHash) {
                throw "The previous $($artifact.Label) was not restored."
            }
        }
        elseif (Test-Path -LiteralPath $artifact.Destination) {
            throw "The new $($artifact.Label) was not removed during recovery."
        }
    }

    [System.IO.File]::Delete($MarkerPath)
    if (Test-Path -LiteralPath $MarkerPath) {
        throw 'The completed release transaction marker could not be removed.'
    }
    return $true
}

function Publish-ReleaseBundle {
    param(
        [string]$RepositoryRoot,
        [string]$StagedSetupPath,
        [string]$SetupPath,
        [string]$ExpectedInstallerHash,
        [string]$StagedArchivePath,
        [string]$ArchivePath,
        [string]$ExpectedArchiveHash
    )

    $StagedSetupPath = Assert-ReleasePath $StagedSetupPath $RepositoryRoot
    $SetupPath = Assert-ReleasePath $SetupPath $RepositoryRoot
    $stagedChecksumPath = Assert-ReleasePath ($StagedSetupPath + '.sha256') $RepositoryRoot
    $checksumPath = Assert-ReleasePath ($SetupPath + '.sha256') $RepositoryRoot
    $StagedArchivePath = Assert-ReleasePath $StagedArchivePath $RepositoryRoot
    $ArchivePath = Assert-ReleasePath $ArchivePath $RepositoryRoot
    $stagedArchiveChecksumPath = Assert-ReleasePath ($StagedArchivePath + '.sha256') $RepositoryRoot
    $archiveChecksumPath = Assert-ReleasePath ($ArchivePath + '.sha256') $RepositoryRoot
    $stagingDirectory = [System.IO.Path]::GetDirectoryName($StagedSetupPath)
    if ($stagingDirectory.Equals([System.IO.Path]::GetDirectoryName($SetupPath),
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $stagingDirectory.Equals([System.IO.Path]::GetDirectoryName($StagedArchivePath),
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetDirectoryName($SetupPath).Equals(
            [System.IO.Path]::GetDirectoryName($ArchivePath),
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($StagedSetupPath) -ne [System.IO.Path]::GetFileName($SetupPath) -or
        [System.IO.Path]::GetFileName($StagedArchivePath) -ne [System.IO.Path]::GetFileName($ArchivePath)) {
        throw 'Release promotion requires one separate staging directory and matching filenames.'
    }
    foreach ($destination in @($SetupPath, $checksumPath, $ArchivePath, $archiveChecksumPath)) {
        if ((Test-Path -LiteralPath $destination) -and -not (Test-Path -LiteralPath $destination -PathType Leaf)) {
            throw 'A release destination is not a regular file.'
        }
    }

    if ($ExpectedInstallerHash -notmatch '^[0-9a-fA-F]{64}$' -or
        $ExpectedArchiveHash -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'A staged release hash is invalid.'
    }
    $ExpectedInstallerHash = $ExpectedInstallerHash.ToLowerInvariant()
    $ExpectedArchiveHash = $ExpectedArchiveHash.ToLowerInvariant()
    $expectedChecksum = "$ExpectedInstallerHash *$([System.IO.Path]::GetFileName($SetupPath))$([Environment]::NewLine)"
    $expectedArchiveChecksum = "$ExpectedArchiveHash *$([System.IO.Path]::GetFileName($ArchivePath))$([Environment]::NewLine)"
    if ((Get-FileHash -LiteralPath $StagedSetupPath -Algorithm SHA256).Hash -ne $ExpectedInstallerHash -or
        [System.IO.File]::ReadAllText($stagedChecksumPath) -cne $expectedChecksum) {
        throw 'The staged installer and checksum did not pass paired validation.'
    }
    Assert-InstallerArchive $StagedArchivePath ([System.IO.Path]::GetFileName($SetupPath)) $ExpectedInstallerHash
    if ((Get-FileHash -LiteralPath $StagedArchivePath -Algorithm SHA256).Hash -ne $ExpectedArchiveHash -or
        [System.IO.File]::ReadAllText($stagedArchiveChecksumPath) -cne $expectedArchiveChecksum) {
        throw 'The staged installer archive and checksum did not pass paired validation.'
    }

    $artifacts = @(
        [pscustomobject]@{
            Staged = $StagedSetupPath
            Destination = $SetupPath
            Backup = Join-Path $stagingDirectory ('previous-' + [System.IO.Path]::GetFileName($SetupPath))
            Hash = $ExpectedInstallerHash
            Label = 'installer'
            HadPrevious = $false
            PreviousHash = ''
        },
        [pscustomobject]@{
            Staged = $stagedChecksumPath
            Destination = $checksumPath
            Backup = Join-Path $stagingDirectory ('previous-' + [System.IO.Path]::GetFileName($checksumPath))
            Hash = (Get-FileHash -LiteralPath $stagedChecksumPath -Algorithm SHA256).Hash
            Label = 'installer checksum'
            HadPrevious = $false
            PreviousHash = ''
        },
        [pscustomobject]@{
            Staged = $StagedArchivePath
            Destination = $ArchivePath
            Backup = Join-Path $stagingDirectory ('previous-' + [System.IO.Path]::GetFileName($ArchivePath))
            Hash = $ExpectedArchiveHash
            Label = 'archive'
            HadPrevious = $false
            PreviousHash = ''
        },
        [pscustomobject]@{
            Staged = $stagedArchiveChecksumPath
            Destination = $archiveChecksumPath
            Backup = Join-Path $stagingDirectory ('previous-' + [System.IO.Path]::GetFileName($archiveChecksumPath))
            Hash = (Get-FileHash -LiteralPath $stagedArchiveChecksumPath -Algorithm SHA256).Hash
            Label = 'archive checksum'
            HadPrevious = $false
            PreviousHash = ''
        }
    )

    # Retain verified originals before any destination changes. Recovery copies are never deleted here.
    foreach ($artifact in $artifacts) {
        if (Test-Path -LiteralPath $artifact.Destination -PathType Leaf) {
            $artifact.HadPrevious = $true
            $artifact.PreviousHash = (
                Get-FileHash -LiteralPath $artifact.Destination -Algorithm SHA256).Hash.ToLowerInvariant()
            [System.IO.File]::Copy($artifact.Destination, $artifact.Backup, $false)
            if ((Get-FileHash -LiteralPath $artifact.Backup -Algorithm SHA256).Hash -ne
                $artifact.PreviousHash -or
                (Get-FileHash -LiteralPath $artifact.Destination -Algorithm SHA256).Hash -ne
                    $artifact.PreviousHash) {
                throw 'The existing release changed while its recovery copy was being verified.'
            }
        }
    }

    $outputsDirectory = Assert-ReleasePath ([System.IO.Path]::GetDirectoryName($SetupPath)) $RepositoryRoot
    $transactionMarkerPath = Assert-ReleasePath (
        Join-Path $outputsDirectory '.installer-transaction.json') $RepositoryRoot
    Write-ReleaseTransactionMarker `
        $RepositoryRoot $outputsDirectory $transactionMarkerPath $stagingDirectory $artifacts

    try {
        foreach ($artifact in $artifacts) {
            if (Test-Path -LiteralPath $artifact.Destination -PathType Leaf) {
                [System.IO.File]::Replace(
                    $artifact.Staged,
                    $artifact.Destination,
                    [System.Management.Automation.Language.NullString]::Value)
            }
            else {
                [System.IO.File]::Move($artifact.Staged, $artifact.Destination)
            }
        }
        foreach ($artifact in $artifacts) {
            if ((Get-FileHash -LiteralPath $artifact.Destination -Algorithm SHA256).Hash -ne $artifact.Hash) {
                throw "The promoted $($artifact.Label) failed final hash validation."
            }
        }
        if ([System.IO.File]::ReadAllText($checksumPath) -cne $expectedChecksum -or
            [System.IO.File]::ReadAllText($archiveChecksumPath) -cne $expectedArchiveChecksum) {
            throw 'The promoted release checksums did not pass final validation.'
        }
        Assert-InstallerArchive $ArchivePath ([System.IO.Path]::GetFileName($SetupPath)) $ExpectedInstallerHash
        [System.IO.File]::Delete($transactionMarkerPath)
        if (Test-Path -LiteralPath $transactionMarkerPath) {
            throw 'The completed release transaction marker could not be removed.'
        }
    }
    catch {
        $promotionFailure = $_
        try {
            $recovered = Recover-ReleaseTransactionIfPresent `
                $RepositoryRoot $outputsDirectory $transactionMarkerPath
            if (-not $recovered) {
                throw 'The release transaction marker was unavailable; recovery could not be verified.'
            }
        }
        catch {
            throw [System.IO.IOException]::new(
                'Release promotion failed and automatic recovery did not complete. Verified backups remain in the staging directory.',
                $_.Exception)
        }
        throw [System.IO.IOException]::new(
            'Release promotion failed; the previous release state was restored. Recovery files remain in the staging directory.',
            $promotionFailure.Exception)
    }
}

$outputsDirectory = Assert-ReleasePath (Join-Path $repository 'outputs') $repository
[System.IO.Directory]::CreateDirectory($outputsDirectory) | Out-Null
$transactionMarkerPath = Assert-ReleasePath (
    Join-Path $outputsDirectory '.installer-transaction.json') $repository
$lockPath = Assert-ReleasePath (Join-Path $outputsDirectory '.installer-build.lock') $repository
try {
    $buildLock = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
}
catch [System.IO.IOException] {
    throw 'Installer output is locked by another build or could not be opened safely.'
}

try {
    if (Recover-ReleaseTransactionIfPresent $repository $outputsDirectory $transactionMarkerPath) {
        Write-Output 'Recovered an interrupted release transaction.'
    }

    $gitExecutable = Resolve-Executable -ExplicitValue $null -Candidates @() -CommandNames @('git.exe', 'git')
    if ($null -eq $gitExecutable) {
        throw 'Git was not found. A resolvable source revision is required for packaging.'
    }
    $sourceState = Get-RepositorySourceState $repository $gitExecutable -AllowDirty:$AllowDirty
    Write-Output "Source revision: $($sourceState.Revision)"
    if ($sourceState.IsDirty) {
        Write-Warning 'Building a private test installer from a dirty worktree because -AllowDirty was supplied.'
    }

    $dotnetCandidates = @(
        (Join-Path $repository '.dotnet\dotnet.exe'),
        (Join-Path $repository 'work\.dotnet\dotnet.exe')
    )
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $dotnetCandidates += Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    }

    $innoCandidates = @(
        (Join-Path $repository 'work\innosetup\ISCC.exe')
    )
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $innoCandidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $innoCandidates += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $innoCandidates += Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
    }

    $dotnetExecutable = Resolve-Executable $DotNet $dotnetCandidates @('dotnet.exe', 'dotnet')
    if ($null -eq $dotnetExecutable) {
        throw 'The .NET 8 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 or pass -DotNet <path>.'
    }

    $innoExecutable = Resolve-Executable $InnoCompiler $innoCandidates @('ISCC.exe', 'ISCC')
    if ($null -eq $innoExecutable) {
        throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php or pass -InnoCompiler <path>.'
    }

    $pythonExecutable = Resolve-Executable -ExplicitValue $Python -Candidates @() -CommandNames @('python.exe', 'python')
    if ($null -eq $pythonExecutable) {
        throw 'Python was not found. Pass -Python <path> to run the compatibility-audit release gate.'
    }

    $projectText = [System.IO.File]::ReadAllText($project)
    $projectVersionMatch = [System.Text.RegularExpressions.Regex]::Match(
        $projectText,
        '<Version>\s*([^<]+?)\s*</Version>',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $projectVersionMatch.Success) {
        throw "Could not read the application version from $project"
    }

    $projectVersion = $projectVersionMatch.Groups[1].Value
    if ($projectVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "Application version '$projectVersion' must use major.minor.patch format."
    }

    $setupPath = Join-Path $outputsDirectory "Wisp-Setup-$projectVersion.exe"
    $checksumPath = $setupPath + '.sha256'
    $archivePath = Join-Path $outputsDirectory "Wisp-Setup-$projectVersion.zip"
    $archiveChecksumPath = $archivePath + '.sha256'

    $innoText = [System.IO.File]::ReadAllText($innoScript)
    $innoVersionMatch = [System.Text.RegularExpressions.Regex]::Match(
        $innoText,
        '(?m)^\s*#define\s+MyAppVersion\s+"([^"]+)"\s*$')
    if (-not $innoVersionMatch.Success -or $innoVersionMatch.Groups[1].Value -ne $projectVersion) {
        throw "Wisp.iss and Wisp.App.csproj must use the same version ($projectVersion)."
    }

    if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) {
        throw "The checked-in application icon was not found at $icon"
    }

    $iconBytes = [System.IO.File]::ReadAllBytes($icon)
    if ($iconBytes.Length -lt 6 -or $iconBytes[0] -ne 0 -or $iconBytes[1] -ne 0 -or
        $iconBytes[2] -ne 1 -or $iconBytes[3] -ne 0) {
        throw "The checked-in application icon is not a valid Windows icon: $icon"
    }

    $publishFullPath = Assert-ReleasePath $publishDirectory $repository
    $updaterPublishFullPath = Assert-ReleasePath $updaterPublishDirectory $repository
    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
    if (-not $publishFullPath.StartsWith(
            $artifactsRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $updaterPublishFullPath.StartsWith(
            $artifactsRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean a publish directory outside the repository artifacts folder.'
    }

    $stageName = 'Wisp-' + $projectVersion + '-' + [guid]::NewGuid().ToString('N')
    $stageDirectory = Assert-ReleasePath (Join-Path $outputsDirectory ('.staging\' + $stageName)) $repository
    if (Test-Path -LiteralPath $stageDirectory) {
        throw 'The unique staging directory unexpectedly already exists.'
    }
    [System.IO.Directory]::CreateDirectory($stageDirectory) | Out-Null
    $stagedSetupPath = Join-Path $stageDirectory ([System.IO.Path]::GetFileName($setupPath))
    $stagedChecksumPath = $stagedSetupPath + '.sha256'
    $stagedArchivePath = Join-Path $stageDirectory ([System.IO.Path]::GetFileName($archivePath))
    $stagedArchiveChecksumPath = $stagedArchivePath + '.sha256'
    Write-Output "Staging and recovery files: $stageDirectory"

    if (Test-Path -LiteralPath $publishFullPath) {
        Remove-Item -LiteralPath $publishFullPath -Recurse -Force
    }
    if (Test-Path -LiteralPath $updaterPublishFullPath) {
        Remove-Item -LiteralPath $updaterPublishFullPath -Recurse -Force
    }

    $env:DOTNET_CLI_HOME = Join-Path $repository 'work\dotnet-home'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:PYTHONDONTWRITEBYTECODE = '1'

    & $dotnetExecutable --version
    if ($LASTEXITCODE -ne 0) {
        throw 'The selected .NET SDK could not satisfy global.json.'
    }

    & $dotnetExecutable restore $solution `
        --nologo `
        --locked-mode `
        -p:NuGetAudit=true `
        -p:NuGetAuditMode=all `
        --disable-parallel `
        -m:1 `
        -nodeReuse:false
    if ($LASTEXITCODE -ne 0) {
        throw "Locked dependency restore or audit failed with exit code $LASTEXITCODE."
    }

    & $dotnetExecutable format $solution `
        --verify-no-changes `
        --no-restore `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Source formatting verification failed with exit code $LASTEXITCODE. The installer was not built."
    }

    & $dotnetExecutable test $solution --configuration Release `
        --no-restore `
        --nologo `
        -p:ContinuousIntegrationBuild=true `
        -m:1 `
        -nodeReuse:false `
        -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "Release tests failed with exit code $LASTEXITCODE. The installer was not built."
    }

    & $dotnetExecutable build $uiReviewProject --configuration Release `
        --no-restore `
        --nologo `
        -p:ContinuousIntegrationBuild=true `
        -m:1 `
        -nodeReuse:false `
        -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "UI review harness build failed with exit code $LASTEXITCODE. The installer was not built."
    }

    & $pythonExecutable -m unittest discover -s $pythonTests -p 'test_*.py' -v
    if ($LASTEXITCODE -ne 0) {
        throw "Compatibility-audit tests failed with exit code $LASTEXITCODE. The installer was not built."
    }

    & $dotnetExecutable restore $project --runtime win-x64 `
        --nologo `
        --locked-mode `
        -p:NuGetAudit=true `
        -p:NuGetAuditMode=all `
        -p:ContinuousIntegrationBuild=true `
        --disable-parallel `
        -m:1 `
        -nodeReuse:false
    if ($LASTEXITCODE -ne 0) {
        throw "Runtime restore failed with exit code $LASTEXITCODE."
    }

    & $dotnetExecutable restore $updaterProject --runtime win-x64 `
        --nologo `
        --locked-mode `
        -p:NuGetAudit=true `
        -p:NuGetAuditMode=all `
        -p:ContinuousIntegrationBuild=true `
        --disable-parallel `
        -m:1 `
        -nodeReuse:false
    if ($LASTEXITCODE -ne 0) {
        throw "Update helper runtime restore failed with exit code $LASTEXITCODE."
    }

    & $dotnetExecutable publish $project --configuration Release --runtime win-x64 --self-contained true `
        --output $publishDirectory `
        --no-restore `
        --nologo `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:ContinuousIntegrationBuild=true `
        -m:1 `
        -nodeReuse:false `
        -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
    Assert-InstallerExecutable `
        (Join-Path $publishFullPath 'Wisp.exe') `
        $projectVersion `
        'Wisp' `
        'Wisp' `
        $projectVersion

    & $dotnetExecutable publish $updaterProject --configuration Release --runtime win-x64 --self-contained true `
        --output $updaterPublishDirectory `
        --no-restore `
        --nologo `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=true `
        -p:TrimMode=full `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:ContinuousIntegrationBuild=true `
        -m:1 `
        -nodeReuse:false `
        -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "Update helper publish failed with exit code $LASTEXITCODE."
    }

    $publishedUpdaterPath = Join-Path $updaterPublishFullPath 'Wisp.Updater.exe'
    Assert-InstallerExecutable `
        $publishedUpdaterPath `
        $projectVersion `
        'Wisp' `
        'Wisp Update Helper' `
        $projectVersion
    Invoke-UpdaterGuardSmoke $publishedUpdaterPath $stageDirectory
    $bundledUpdaterPath = Join-Path $publishFullPath 'Wisp.Updater.exe'
    if (Test-Path -LiteralPath $bundledUpdaterPath) {
        throw 'The application publish unexpectedly contained an update helper.'
    }
    [System.IO.File]::Copy($publishedUpdaterPath, $bundledUpdaterPath, $false)
    Assert-InstallerExecutable `
        $bundledUpdaterPath `
        $projectVersion `
        'Wisp' `
        'Wisp Update Helper' `
        $projectVersion

    Push-Location $PSScriptRoot
    try {
        & $innoExecutable "/O$stageDirectory" $innoScript
        $innoExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($innoExitCode -ne 0) {
        throw "Inno Setup failed with exit code $innoExitCode. Existing release files were not replaced."
    }
    if (-not (Test-Path -LiteralPath $stagedSetupPath -PathType Leaf)) {
        throw 'Inno Setup completed without producing the expected staged installer.'
    }
    Assert-InstallerExecutable `
        $stagedSetupPath `
        $projectVersion `
        'Wisp' `
        'Wisp installer' `
        "$projectVersion.0"

    Invoke-InstallerRuntimeValidation `
        $dotnetExecutable $stagedSetupPath $projectVersion $updateTestsProject $updaterTestsProject

    $hash = (Get-FileHash -LiteralPath $stagedSetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumLine = "$hash *$([System.IO.Path]::GetFileName($setupPath))$([Environment]::NewLine)"
    [System.IO.File]::WriteAllText($stagedChecksumPath, $checksumLine, [System.Text.UTF8Encoding]::new($false))
    New-InstallerArchive $stagedSetupPath $stagedArchivePath $hash
    $archiveHash = (Get-FileHash -LiteralPath $stagedArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $archiveChecksumLine = "$archiveHash *$([System.IO.Path]::GetFileName($archivePath))$([Environment]::NewLine)"
    [System.IO.File]::WriteAllText(
        $stagedArchiveChecksumPath, $archiveChecksumLine, [System.Text.UTF8Encoding]::new($false))
    Publish-ReleaseBundle `
        $repository $stagedSetupPath $setupPath $hash $stagedArchivePath $archivePath $archiveHash

    Write-Output "Built with .NET SDK $(& $dotnetExecutable --version)"
    Write-Output "Installer: $setupPath"
    Write-Output "Installer SHA-256: $checksumPath"
    Write-Output "Release archive:     $archivePath"
    Write-Output "Archive SHA-256:     $archiveChecksumPath"
}
finally {
    $buildLock.Dispose()
}
