using System.Diagnostics;
using System.Text;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class InstallerPackagingContractTests
{
    [Theory]
    [InlineData("__pycache__/")]
    [InlineData("*.py[cod]")]
    public void PythonGeneratedCachesAreIgnored(string rule)
    {
        Assert.Contains(rule, File.ReadAllLines(Path.Combine(RepositoryRoot(), ".gitignore")));
    }

    [Fact]
    public void InstallerIsBuiltAndValidatedInUniqueStagingBeforePromotion()
    {
        var script = InstallerScript();
        Assert.Contains("$stageName = 'Wisp-' + $projectVersion + '-' + [guid]::NewGuid()", script, StringComparison.Ordinal);
        Assert.Contains("& $innoExecutable \"/O$stageDirectory\" $innoScript", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $setupPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $checksumPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $archivePath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $archiveChecksumPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$previousArtifact", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $stageDirectory", script, StringComparison.Ordinal);

        var tests = script.IndexOf("& $dotnetExecutable test $solution", StringComparison.Ordinal);
        var publish = script.IndexOf("& $dotnetExecutable publish $project", StringComparison.Ordinal);
        var compile = script.IndexOf("& $innoExecutable \"/O$stageDirectory\"", StringComparison.Ordinal);
        var validate = script.IndexOf("'Wisp installer' `", compile, StringComparison.Ordinal);
        var archive = script.IndexOf("New-InstallerArchive $stagedSetupPath $stagedArchivePath $hash", StringComparison.Ordinal);
        var promote = script.LastIndexOf("Publish-ReleaseBundle", StringComparison.Ordinal);
        Assert.True(tests >= 0 && publish > tests && compile > publish && validate > compile &&
                    archive > validate && promote > archive);
    }

    [Fact]
    public void ReleasePackagingRequiresResolvableGitHeadAndCleanSourceByDefault()
    {
        var script = InstallerScript();
        var sourceStateFunction = script.IndexOf("function Get-RepositorySourceState", StringComparison.Ordinal);
        var resolveGit = script.IndexOf("$gitExecutable = Resolve-Executable", StringComparison.Ordinal);
        var resolveHead = script.IndexOf("rev-parse --verify 'HEAD^{commit}'", StringComparison.Ordinal);
        var inspectStatus = script.IndexOf("status --porcelain=v1 --untracked-files=all", StringComparison.Ordinal);
        var dirtyFailure = script.IndexOf("if ($isDirty -and -not $AllowDirty)", StringComparison.Ordinal);
        var printRevision = script.IndexOf("Write-Output \"Source revision: $($sourceState.Revision)\"", StringComparison.Ordinal);
        var stageCreation = script.IndexOf("$stageName = 'Wisp-'", StringComparison.Ordinal);

        Assert.Contains("[switch]$AllowDirty", script, StringComparison.Ordinal);
        Assert.Contains("rev-parse --show-toplevel", script, StringComparison.Ordinal);
        Assert.Contains("^[0-9a-fA-F]{40,64}$", script, StringComparison.Ordinal);
        Assert.Contains("-AllowDirty for a private test build", script, StringComparison.Ordinal);
        Assert.True(sourceStateFunction >= 0 && resolveGit > sourceStateFunction && resolveHead > sourceStateFunction &&
                    inspectStatus > resolveHead && dirtyFailure > inspectStatus && printRevision > resolveGit &&
                    stageCreation > printRevision);
    }

    [Fact]
    public void DirtyOverrideDoesNotBypassRepositoryOrHeadValidation()
    {
        var script = InstallerScript();
        var functionStart = script.IndexOf("function Get-RepositorySourceState", StringComparison.Ordinal);
        var functionEnd = script.IndexOf("function Assert-ReleasePath", functionStart, StringComparison.Ordinal);
        var sourceState = script[functionStart..functionEnd];

        var repositoryValidation = sourceState.IndexOf("rev-parse --show-toplevel", StringComparison.Ordinal);
        var headValidation = sourceState.IndexOf("rev-parse --verify 'HEAD^{commit}'", StringComparison.Ordinal);
        var dirtyOverride = sourceState.IndexOf("if ($isDirty -and -not $AllowDirty)", StringComparison.Ordinal);
        Assert.True(repositoryValidation >= 0 && headValidation > repositoryValidation && dirtyOverride > headValidation);
        Assert.DoesNotContain("if ($AllowDirty)", sourceState, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("git-clean")]
    [InlineData("git-dirty")]
    [InlineData("git-missing-head")]
    public Task RepositorySourceGateEnforcesCleanHeadAndNarrowOverride(string scenario) => RunHelperCase(scenario);

    [Fact]
    public void PackagingRunsEveryDocumentedReleaseGateBeforePublish()
    {
        var script = InstallerScript();
        var restore = script.IndexOf("& $dotnetExecutable restore $solution", StringComparison.Ordinal);
        var format = script.IndexOf("& $dotnetExecutable format $solution", StringComparison.Ordinal);
        var tests = script.IndexOf("& $dotnetExecutable test $solution", StringComparison.Ordinal);
        var uiBuild = script.IndexOf("& $dotnetExecutable build $uiReviewProject", StringComparison.Ordinal);
        var pythonTests = script.IndexOf("& $pythonExecutable -m unittest discover", StringComparison.Ordinal);
        var publish = script.IndexOf("& $dotnetExecutable publish $project", StringComparison.Ordinal);

        Assert.Contains("--verify-no-changes", script, StringComparison.Ordinal);
        Assert.Contains("-s $pythonTests -p 'test_*.py' -v", script, StringComparison.Ordinal);
        Assert.Contains("$env:PYTHONDONTWRITEBYTECODE = '1'", script, StringComparison.Ordinal);
        Assert.True(restore >= 0 && format > restore && tests > format && uiBuild > tests &&
                    pythonTests > uiBuild && publish > pythonTests);
    }

    [Fact]
    public void ExistingRuntimePublishFlagsAndInstallerFormatArePreserved()
    {
        var script = InstallerScript();
        Assert.Contains("--configuration Release --runtime win-x64 --self-contained true", script, StringComparison.Ordinal);
        Assert.Contains("--output $publishDirectory", script, StringComparison.Ordinal);
        Assert.Contains("-p:PublishSingleFile=true", script, StringComparison.Ordinal);
        Assert.Contains("-p:IncludeNativeLibrariesForSelfExtract=true", script, StringComparison.Ordinal);
        Assert.Contains("-p:PublishTrimmed=false", script, StringComparison.Ordinal);
        Assert.Contains("-p:UseSharedCompilation=false", script, StringComparison.Ordinal);
        Assert.Contains("-nodeReuse:false", script, StringComparison.Ordinal);
        Assert.Contains("-m:1", script, StringComparison.Ordinal);
        Assert.Contains("& $dotnetExecutable restore $solution", script, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", script, StringComparison.Ordinal);
        Assert.Contains("-p:NuGetAudit=true", script, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreIgnoreFailedSources=true", script, StringComparison.Ordinal);
        Assert.DoesNotContain("NuGetAudit=false", script, StringComparison.Ordinal);
        Assert.Contains("[System.Text.UTF8Encoding]::new($false)", script, StringComparison.Ordinal);
        Assert.Contains("$checksumLine = \"$hash *$([System.IO.Path]::GetFileName($setupPath))$([Environment]::NewLine)\"", script, StringComparison.Ordinal);
        Assert.Contains("$archiveChecksumLine = \"$archiveHash *$([System.IO.Path]::GetFileName($archivePath))$([Environment]::NewLine)\"", script, StringComparison.Ordinal);
        Assert.Contains("Assert-InstallerArchive $ArchivePath", script, StringComparison.Ordinal);
        Assert.Contains("$entries.Count -ne 2", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("& $setupPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("& $stagedSetupPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagingPublishesAndBundlesValidatedUpdateHelperBeforeCompilation()
    {
        var script = InstallerScript();
        var updaterProject = script.IndexOf(
            "$updaterProject = Join-Path $repository 'src\\Wisp.Updater\\Wisp.Updater.csproj'",
            StringComparison.Ordinal);
        var restore = script.IndexOf("& $dotnetExecutable restore $updaterProject", StringComparison.Ordinal);
        var publish = script.IndexOf("& $dotnetExecutable publish $updaterProject", StringComparison.Ordinal);
        var publishedPath = script.IndexOf(
            "$publishedUpdaterPath = Join-Path $updaterPublishFullPath 'Wisp.Updater.exe'",
            StringComparison.Ordinal);
        var validatePublished = script.IndexOf(
            "$publishedUpdaterPath `",
            publishedPath,
            StringComparison.Ordinal);
        var smoke = script.IndexOf(
            "Invoke-UpdaterGuardSmoke $publishedUpdaterPath $stageDirectory",
            validatePublished,
            StringComparison.Ordinal);
        var copy = script.IndexOf(
            "[System.IO.File]::Copy($publishedUpdaterPath, $bundledUpdaterPath, $false)",
            smoke,
            StringComparison.Ordinal);
        var validateBundled = script.IndexOf(
            "$bundledUpdaterPath `",
            copy,
            StringComparison.Ordinal);
        var compile = script.IndexOf("& $innoExecutable \"/O$stageDirectory\"", StringComparison.Ordinal);

        Assert.Contains("-p:PublishTrimmed=true", script, StringComparison.Ordinal);
        Assert.Contains("-p:EnableCompressionInSingleFile=true", script, StringComparison.Ordinal);
        Assert.Contains("$startInfo.Arguments = '--packaging-smoke'", script, StringComparison.Ordinal);
        Assert.Contains("if ($process.ExitCode -ne 2)", script, StringComparison.Ordinal);
        Assert.Contains("$diagnostic.errorCode -cne 'UPDATE_ARGUMENTS'", script, StringComparison.Ordinal);
        Assert.Contains(
            "$diagnostic.message -cne 'The update helper received invalid arguments.'",
            script,
            StringComparison.Ordinal);
        Assert.Contains("($characteristics -band 0x2000) -ne 0", script, StringComparison.Ordinal);
        Assert.Contains("$versionPadding = [char[]]@([char]0, [char]0x20)", script, StringComparison.Ordinal);
        Assert.Contains("([string]$versionInfo.ProductName).TrimEnd($versionPadding)", script, StringComparison.Ordinal);
        Assert.Contains("([string]$versionInfo.FileDescription).TrimEnd($versionPadding)", script, StringComparison.Ordinal);
        Assert.Contains("([string]$versionInfo.ProductVersion).TrimEnd($versionPadding)", script, StringComparison.Ordinal);
        Assert.Contains("$productName -cne $ExpectedProductName", script, StringComparison.Ordinal);
        Assert.Contains("$fileDescription -cne $ExpectedFileDescription", script, StringComparison.Ordinal);
        Assert.Contains("$productVersion -cne $ExpectedProductVersion", script, StringComparison.Ordinal);
        Assert.Contains(
            "Source: \"..\\artifacts\\publish\\*\"; DestDir: \"{app}\"",
            InnoScript(),
            StringComparison.Ordinal);
        Assert.True(updaterProject >= 0 && restore > updaterProject && publish > restore &&
                    publishedPath > publish && validatePublished > publishedPath && smoke > validatePublished &&
                    copy > smoke &&
                    validateBundled > copy && compile > validateBundled);
    }

    [Fact]
    public void SelfContainedDistributionCarriesPinnedRuntimeNotices()
    {
        const string runtimeVersion = "8.0.30";
        var root = RepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "Wisp.App", "Wisp.App.csproj"));
        Assert.Contains($"<RuntimeFrameworkVersion>{runtimeVersion}</RuntimeFrameworkVersion>", project,
            StringComparison.Ordinal);
        Assert.Contains("Include=\"..\\..\\LICENSE\" Link=\"LICENSE.txt\"", project, StringComparison.Ordinal);
        Assert.Contains("Include=\"..\\..\\THIRD-PARTY-NOTICES.md\"", project, StringComparison.Ordinal);

        var notices = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"dotnet-runtime-{runtimeVersion}-LICENSE.txt"] =
                "D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B",
            [$"dotnet-runtime-{runtimeVersion}-THIRD-PARTY-NOTICES.txt"] =
                "B60B2912DA28EAA6518593C9E2EFB5334EE062D3C42E80D8FDFA806B3DC52977",
            [$"windowsdesktop-runtime-{runtimeVersion}-LICENSE.txt"] =
                "A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB"
        };

        foreach (var notice in notices)
        {
            var path = Path.Combine(root, "LICENSES", notice.Key);
            Assert.True(File.Exists(path), $"Missing runtime notice: {notice.Key}");
            var actualHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(notice.Value, actualHash);
            Assert.Contains($"Include=\"..\\..\\LICENSES\\{notice.Key}\"", project, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FreshInstallRequiresSetupWhileVerifiedInPlaceUpdatePreservesCompletion()
    {
        var script = InnoScript();
        var initializeSetup = script.IndexOf("function InitializeSetup(): Boolean;", StringComparison.Ordinal);
        var updateSwitch = script.IndexOf("function UpdateSwitchPresent(): Boolean;", StringComparison.Ordinal);
        var existingInstall = script.IndexOf("function ExistingInstallationPresent(): Boolean;", StringComparison.Ordinal);
        var captureUpdate = script.IndexOf(
            "UpdatingExistingInstallation := UpdateSwitchPresent() and ExistingInstallationPresent()",
            StringComparison.Ordinal);
        var callback = script.IndexOf("procedure CurStepChanged(CurStep: TSetupStep);", StringComparison.Ordinal);
        var postInstall = script.IndexOf("if CurStep <> ssPostInstall then", callback, StringComparison.Ordinal);
        var preserveCompletedSetup = script.IndexOf("if UpdatingExistingInstallation then", callback, StringComparison.Ordinal);
        var directory = script.IndexOf("ForceDirectories(SettingsDirectory)", callback, StringComparison.Ordinal);
        var marker = script.IndexOf(
            "SaveStringToFile(SetupRequiredMarkerPath(), 'setup-required', False)",
            callback,
            StringComparison.Ordinal);

        Assert.Contains(
            $"ExpandConstant('{{localappdata}}\\Wisp\\{SettingsService.SetupRequiredMarkerFileName}')",
            script,
            StringComparison.Ordinal);
        Assert.Contains("CompareText(ParamStr(Index), '/WISPUPDATE') = 0", script, StringComparison.Ordinal);
        Assert.Contains("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall", script, StringComparison.Ordinal);
        Assert.True(updateSwitch >= 0 && existingInstall > updateSwitch && initializeSetup > existingInstall);
        Assert.True(captureUpdate > initializeSetup && callback > captureUpdate);
        Assert.True(postInstall > callback && preserveCompletedSetup > postInstall);
        Assert.True(directory > preserveCompletedSetup && marker > directory);
        Assert.DoesNotContain("FileExists(SetupRequiredMarkerPath()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteFile(SetupRequiredMarkerPath()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedPublishScratchIsLockedAndPromotionRetainsRecoveryCopies()
    {
        var script = InstallerScript();
        var lockOpen = script.IndexOf("$buildLock = [System.IO.File]::Open", StringComparison.Ordinal);
        var startupRecovery = script.LastIndexOf(
            "Recover-ReleaseTransactionIfPresent $repository $outputsDirectory $transactionMarkerPath",
            StringComparison.Ordinal);
        var stageCreation = script.IndexOf("$stageName = 'Wisp-'", StringComparison.Ordinal);
        var cleanup = script.IndexOf("Remove-Item -LiteralPath $publishFullPath", StringComparison.Ordinal);
        var publishFunction = script.IndexOf("function Publish-ReleaseBundle", StringComparison.Ordinal);
        var backupCopy = script.IndexOf(
            "[System.IO.File]::Copy($artifact.Destination, $artifact.Backup, $false)",
            publishFunction,
            StringComparison.Ordinal);
        var backupValidation = script.IndexOf(
            "Get-FileHash -LiteralPath $artifact.Backup -Algorithm SHA256",
            backupCopy,
            StringComparison.Ordinal);
        var markerWrite = script.IndexOf("Write-ReleaseTransactionMarker `", StringComparison.Ordinal);
        var firstPromotion = script.IndexOf("[System.IO.File]::Replace(", markerWrite, StringComparison.Ordinal);
        Assert.True(lockOpen >= 0 && startupRecovery > lockOpen && stageCreation > startupRecovery &&
                    cleanup > stageCreation);
        Assert.True(publishFunction >= 0 && backupCopy > publishFunction && backupValidation > backupCopy &&
                    markerWrite > backupValidation && firstPromotion > markerWrite);
        Assert.Contains("[System.IO.FileShare]::None", script, StringComparison.Ordinal);
        Assert.Contains("$buildLock.Dispose()", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.FileAttributes]::ReparsePoint", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::Copy($artifact.Destination, $artifact.Backup, $false)", script, StringComparison.Ordinal);
        Assert.Contains("for ($index = $transaction.Artifacts.Count - 1; $index -ge 0; $index--)", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.FileOptions]::WriteThrough", script, StringComparison.Ordinal);
        Assert.Contains("$stream.Flush($true)", script, StringComparison.Ordinal);
        Assert.Contains("$marker.SchemaVersion -is [long]", script, StringComparison.Ordinal);
        Assert.Contains("Restore-InstallerArtifact", script, StringComparison.Ordinal);
        Assert.Contains("Label = 'archive checksum'", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("archive-valid")]
    [InlineData("archive-extra-entry")]
    [InlineData("archive-wrong-installer-hash")]
    [InlineData("archive-corrupt-inner-checksum")]
    public Task InstallerArchiveIsReopenedAndValidated(string scenario) => RunHelperCase(scenario);

    [Theory]
    [InlineData("replace")]
    [InlineData("new")]
    [InlineData("checksum-locked")]
    [InlineData("checksum-locked-without-installer")]
    [InlineData("archive-checksum-locked")]
    [InlineData("invalid-staged-checksum")]
    [InlineData("invalid-staged-archive")]
    [InlineData("invalid-staged-archive-checksum")]
    public Task PromotionHelperPreservesThePreviousBundleOnFailure(string scenario) => RunHelperCase(scenario);

    [Theory]
    [InlineData("interrupted-promotion-recovery")]
    [InlineData("post-promotion-validation-failure")]
    public Task DurableTransactionRestoresTheCompletePreviousBundle(string scenario) => RunHelperCase(scenario);

    [Fact]
    public Task InstallerVersionGuardAcceptsPaddedTextButRejectsWrongNumericVersion() =>
        RunHelperCase("padded-file-version");

    private static async Task RunHelperCase(string scenario)
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), "Wisp.PackagingTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            var harnessPath = Path.Combine(testDirectory, "InstallerPackagingHarness.ps1");
            await File.WriteAllTextAsync(harnessPath, HelperHarness, new UTF8Encoding(false));
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            Assert.True(File.Exists(shell), "Windows PowerShell is required to validate the Windows installer helper.");
            var startInfo = new ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(harnessPath);
            // Windows PowerShell must not inherit the invoking PowerShell 7 module directories.
            startInfo.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(shell)!, "Modules");
            startInfo.Environment["WISP_PACKAGING_SCRIPT"] = Path.Combine(RepositoryRoot(), "installer", "Build-Installer.ps1");
            startInfo.Environment["WISP_PACKAGING_TEST_ROOT"] = testDirectory;
            startInfo.Environment["WISP_PACKAGING_CASE"] = scenario;

            using var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start());
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("The synthetic installer transaction test did not complete.");
            }

            var result = await output;
            var diagnostics = await errors;
            Assert.True(process.ExitCode == 0, $"Installer helper case '{scenario}' failed: {result} {diagnostics}");
            Assert.Contains("packaging-helper-ok", result, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static string InstallerScript() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "Build-Installer.ps1"));

    private static string InnoScript() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "Wisp.iss"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Wisp.sln from the test output directory.");
    }

    private const string HelperHarness = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        Set-StrictMode -Version Latest
        try {
            Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
            Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
            $tokens = $null
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $env:WISP_PACKAGING_SCRIPT, [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors.Count -ne 0) { throw 'Installer script syntax is invalid.' }
            foreach ($name in @(
                'Assert-ReleasePath',
                'Get-RepositorySourceState',
                'Assert-InstallerExecutable',
                'Restore-InstallerArtifact',
                'Initialize-ArchiveSupport',
                'Assert-InstallerArchive',
                'New-InstallerArchive',
                'Read-ReleaseTransactionMarker',
                'Write-ReleaseTransactionMarker',
                'Recover-ReleaseTransactionIfPresent',
                'Publish-ReleaseBundle')) {
                $definition = $ast.Find({ param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $false)
                if ($null -eq $definition) { throw 'A required installer transaction helper is missing.' }
                . ([scriptblock]::Create($definition.Extent.Text))
            }

            function Require([bool]$condition, [string]$message) {
                if (-not $condition) { throw $message }
            }

            $root = $env:WISP_PACKAGING_TEST_ROOT
            $scenario = $env:WISP_PACKAGING_CASE
            if ($scenario.StartsWith('git-', [StringComparison]::Ordinal)) {
                $source = Join-Path $root 'source'
                [System.IO.Directory]::CreateDirectory($source) | Out-Null
                $git = Join-Path $root 'git.cmd'
                $fakeGit = @'
        @echo off
        echo %* | %SystemRoot%\System32\findstr.exe /C:"--show-toplevel" >nul
        if not errorlevel 1 (
          echo %WISP_FAKE_GIT_ROOT%
          exit /b 0
        )
        echo %* | %SystemRoot%\System32\findstr.exe /C:"HEAD" >nul
        if not errorlevel 1 (
          if "%WISP_FAKE_GIT_MODE%"=="missing-head" exit /b 1
          echo 0123456789abcdef0123456789abcdef01234567
          exit /b 0
        )
        echo %* | %SystemRoot%\System32\findstr.exe /C:"status" >nul
        if not errorlevel 1 (
          if "%WISP_FAKE_GIT_MODE%"=="dirty" echo ?? untracked.txt
          exit /b 0
        )
        exit /b 2
        '@
                [System.IO.File]::WriteAllText($git, $fakeGit, [System.Text.Encoding]::ASCII)
                $env:WISP_FAKE_GIT_ROOT = $source
                $env:WISP_FAKE_GIT_MODE = switch ($scenario) {
                    'git-dirty' { 'dirty' }
                    'git-missing-head' { 'missing-head' }
                    default { 'clean' }
                }

                if ($scenario -eq 'git-clean') {
                    $state = Get-RepositorySourceState $source $git
                    Require (-not $state.IsDirty) 'A clean repository was reported as dirty.'
                    Require ($state.Revision -match '^[0-9a-f]{40,64}$') 'The clean repository did not return its exact HEAD.'
                }
                elseif ($scenario -eq 'git-dirty') {
                    [System.IO.File]::WriteAllText((Join-Path $source 'untracked.txt'), 'dirty')
                    $cleanFailure = $null
                    try { Get-RepositorySourceState $source $git | Out-Null }
                    catch { $cleanFailure = $_ }
                    Require ($null -ne $cleanFailure -and
                        $cleanFailure.Exception.Message.Contains('tracked or untracked changes')) `
                        'A dirty repository passed the default source gate.'
                    $state = Get-RepositorySourceState $source $git -AllowDirty
                    Require $state.IsDirty 'The private override did not preserve the dirty-state result.'
                }
                else {
                    foreach ($allowDirty in @($false, $true)) {
                        $headFailure = $null
                        try { Get-RepositorySourceState $source $git -AllowDirty:$allowDirty | Out-Null }
                        catch { $headFailure = $_ }
                        Require ($null -ne $headFailure -and
                            $headFailure.Exception.Message.Contains('resolvable Git HEAD')) `
                            'A repository without HEAD passed the source gate.'
                    }
                }
                Write-Output 'packaging-helper-ok'
                exit 0
            }
            if ($scenario -eq 'padded-file-version') {
                $fixtureName = 'PaddedVersion.exe'
                $fixture = Join-Path $root $fixtureName
                $paddedVersion = '17.23.42.0          '
                $assemblyName = [System.Reflection.AssemblyName]::new('Wisp.PackagingVersionFixture')
                $assemblyName.Version = [System.Version]::new(17, 23, 42, 0)
                $assembly = [AppDomain]::CurrentDomain.DefineDynamicAssembly(
                    $assemblyName, [System.Reflection.Emit.AssemblyBuilderAccess]::Save, $root)
                $constructor = [System.Reflection.AssemblyFileVersionAttribute].GetConstructor(@([string]))
                $assembly.SetCustomAttribute([System.Reflection.Emit.CustomAttributeBuilder]::new(
                    $constructor, [object[]]@($paddedVersion)))
                $informationalConstructor = `
                    [System.Reflection.AssemblyInformationalVersionAttribute].GetConstructor(@([string]))
                $assembly.SetCustomAttribute([System.Reflection.Emit.CustomAttributeBuilder]::new(
                    $informationalConstructor, [object[]]@('17.23.42.0')))
                $productConstructor = [System.Reflection.AssemblyProductAttribute].GetConstructor(@([string]))
                $assembly.SetCustomAttribute([System.Reflection.Emit.CustomAttributeBuilder]::new(
                    $productConstructor, [object[]]@('Synthetic Wisp')))
                $titleConstructor = [System.Reflection.AssemblyTitleAttribute].GetConstructor(@([string]))
                $assembly.SetCustomAttribute([System.Reflection.Emit.CustomAttributeBuilder]::new(
                    $titleConstructor, [object[]]@('Synthetic installer')))
                $module = $assembly.DefineDynamicModule('VersionFixture', $fixtureName)
                $type = $module.DefineType(
                    'Program',
                    [System.Reflection.TypeAttributes]::Public -bor [System.Reflection.TypeAttributes]::Class)
                $main = $type.DefineMethod(
                    'Main',
                    [System.Reflection.MethodAttributes]::Public -bor [System.Reflection.MethodAttributes]::Static,
                    [void],
                    [Type[]]@())
                $main.GetILGenerator().Emit([System.Reflection.Emit.OpCodes]::Ret)
                $type.CreateType() | Out-Null
                $assembly.SetEntryPoint($main, [System.Reflection.Emit.PEFileKinds]::WindowApplication)
                $assembly.DefineVersionInfoResource()
                $assembly.Save($fixtureName)

                $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($fixture)
                Require ($info.FileVersion -ceq $paddedVersion) 'The synthetic fixture did not preserve padded display text.'
                Require ($info.ProductName -ceq 'Synthetic Wisp') `
                    ("The synthetic product name is unexpected: [$($info.ProductName)]")
                Require ($info.FileDescription -ceq 'Synthetic installer') `
                    ("The synthetic description is unexpected: [$($info.FileDescription)]")
                Require ($info.ProductVersion -ceq '17.23.42.0') `
                    ("The synthetic product version is unexpected: [$($info.ProductVersion)]")
                Require ($info.FileMajorPart -eq 17 -and $info.FileMinorPart -eq 23 -and
                    $info.FileBuildPart -eq 42 -and $info.FilePrivatePart -eq 0) 'The fixture numeric version is incorrect.'
                Assert-InstallerExecutable `
                    $fixture '17.23.42' 'Synthetic Wisp' 'Synthetic installer' '17.23.42.0'

                $identityFailure = $null
                try {
                    Assert-InstallerExecutable `
                        $fixture '17.23.42' 'Other product' 'Synthetic installer' '17.23.42.0'
                }
                catch { $identityFailure = $_ }
                Require ($null -ne $identityFailure -and
                    $identityFailure.Exception.Message.Contains('identity does not match')) `
                    'A mismatched product identity passed packaging validation.'

                $bytes = [System.IO.File]::ReadAllBytes($fixture)
                $peOffset = [BitConverter]::ToUInt32($bytes, 0x3c)
                $characteristicsOffset = $peOffset + 22
                $originalCharacteristics = [BitConverter]::ToUInt16($bytes, $characteristicsOffset)
                [BitConverter]::GetBytes([uint16]($originalCharacteristics -bor 0x2000)).CopyTo(
                    $bytes, $characteristicsOffset)
                [System.IO.File]::WriteAllBytes($fixture, $bytes)
                $dllFailure = $null
                try {
                    Assert-InstallerExecutable `
                        $fixture '17.23.42' 'Synthetic Wisp' 'Synthetic installer' '17.23.42.0'
                }
                catch { $dllFailure = $_ }
                Require ($null -ne $dllFailure -and
                    $dllFailure.Exception.Message.Contains('non-DLL Windows executable')) `
                    'An image with the DLL characteristic passed packaging validation.'
                [BitConverter]::GetBytes($originalCharacteristics).CopyTo($bytes, $characteristicsOffset)
                [System.IO.File]::WriteAllBytes($fixture, $bytes)

                # Change only VS_FIXEDFILEINFO; the matching padded display string stays intact.
                $bytes = [System.IO.File]::ReadAllBytes($fixture)
                $fixedInfo = @()
                for ($index = 0; $index -le $bytes.Length - 52; $index++) {
                    if ([BitConverter]::ToUInt32($bytes, $index) -eq 4277077181) { $fixedInfo += $index }
                }
                Require ($fixedInfo.Count -eq 1) 'The fixture must contain exactly one fixed version record.'
                [BitConverter]::GetBytes([uint32](43 -shl 16)).CopyTo($bytes, $fixedInfo[0] + 12)
                [System.IO.File]::WriteAllBytes($fixture, $bytes)
                $changedInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($fixture)
                Require ($changedInfo.FileVersion -ceq $paddedVersion -and $changedInfo.FileBuildPart -eq 43) 'The numeric-only mismatch was not created.'
                $failure = $null
                try {
                    Assert-InstallerExecutable `
                        $fixture '17.23.42' 'Synthetic Wisp' 'Synthetic installer' '17.23.42.0'
                }
                catch { $failure = $_ }
                Require ($null -ne $failure) 'A wrong numeric version was accepted despite matching display text.'
                Require ($failure.Exception.Message.Contains('file version does not match')) 'The numeric mismatch failed for an unrelated reason.'
                Write-Output 'packaging-helper-ok'
                exit 0
            }

            $output = Join-Path $root 'outputs'
            $stage = Join-Path $output '.staging\synthetic'
            [System.IO.Directory]::CreateDirectory($stage) | Out-Null
            $setup = Join-Path $output 'Wisp-Setup-99.0.0.exe'
            $checksum = $setup + '.sha256'
            $archive = Join-Path $output 'Wisp-Setup-99.0.0.zip'
            $archiveChecksum = $archive + '.sha256'
            $stagedSetup = Join-Path $stage ([System.IO.Path]::GetFileName($setup))
            $stagedChecksum = $stagedSetup + '.sha256'
            $stagedArchive = Join-Path $stage ([System.IO.Path]::GetFileName($archive))
            $stagedArchiveChecksum = $stagedArchive + '.sha256'
            $encoding = [System.Text.UTF8Encoding]::new($false)
            $newContents = 'synthetic-new-installer'
            $oldContents = 'synthetic-previous-installer'
            $oldChecksum = 'synthetic-previous-checksum'
            $oldArchive = 'synthetic-previous-archive'
            $oldArchiveChecksum = 'synthetic-previous-archive-checksum'
            [System.IO.File]::WriteAllText($stagedSetup, $newContents, $encoding)
            $hash = (Get-FileHash -LiteralPath $stagedSetup -Algorithm SHA256).Hash.ToLowerInvariant()
            $expectedChecksum = "$hash *$([System.IO.Path]::GetFileName($setup))$([Environment]::NewLine)"
            [System.IO.File]::WriteAllText($stagedChecksum, $expectedChecksum, $encoding)
            New-InstallerArchive $stagedSetup $stagedArchive $hash
            $archiveHash = (Get-FileHash -LiteralPath $stagedArchive -Algorithm SHA256).Hash.ToLowerInvariant()
            $expectedArchiveChecksum = "$archiveHash *$([System.IO.Path]::GetFileName($archive))$([Environment]::NewLine)"
            [System.IO.File]::WriteAllText($stagedArchiveChecksum, $expectedArchiveChecksum, $encoding)

            if ($scenario.StartsWith('archive-', [StringComparison]::Ordinal) -and
                $scenario -ne 'archive-checksum-locked') {
                $validationHash = $hash
                if ($scenario -eq 'archive-extra-entry' -or $scenario -eq 'archive-corrupt-inner-checksum') {
                    $zip = [System.IO.Compression.ZipFile]::Open(
                        $stagedArchive, [System.IO.Compression.ZipArchiveMode]::Update)
                    try {
                        if ($scenario -eq 'archive-extra-entry') {
                            $entry = $zip.CreateEntry('unexpected.txt')
                        }
                        else {
                            $entryName = [System.IO.Path]::GetFileName($stagedChecksum)
                            $existing = $zip.GetEntry($entryName)
                            Require ($null -ne $existing) 'The archive checksum fixture is missing.'
                            $existing.Delete()
                            $entry = $zip.CreateEntry($entryName)
                        }
                        $entryStream = $entry.Open()
                        try {
                            $bytes = $encoding.GetBytes('invalid')
                            $entryStream.Write($bytes, 0, $bytes.Length)
                        }
                        finally { $entryStream.Dispose() }
                    }
                    finally { $zip.Dispose() }
                }
                elseif ($scenario -eq 'archive-wrong-installer-hash') {
                    $validationHash = '0' * 64
                }

                $archiveFailure = $null
                try {
                    Assert-InstallerArchive $stagedArchive ([System.IO.Path]::GetFileName($setup)) $validationHash
                }
                catch { $archiveFailure = $_ }
                $expectArchiveSuccess = $scenario -eq 'archive-valid'
                Require (($null -eq $archiveFailure) -eq $expectArchiveSuccess) 'Unexpected archive validation result.'
                Write-Output 'packaging-helper-ok'
                exit 0
            }

            if ($scenario -eq 'invalid-staged-checksum') {
                [System.IO.File]::WriteAllText($stagedChecksum, 'invalid', $encoding)
            }
            elseif ($scenario -eq 'invalid-staged-archive') {
                $zip = [System.IO.Compression.ZipFile]::Open(
                    $stagedArchive, [System.IO.Compression.ZipArchiveMode]::Update)
                try {
                    $entry = $zip.CreateEntry('unexpected.txt')
                    $entryStream = $entry.Open()
                    try { $entryStream.WriteByte(1) }
                    finally { $entryStream.Dispose() }
                }
                finally { $zip.Dispose() }
                $archiveHash = (Get-FileHash -LiteralPath $stagedArchive -Algorithm SHA256).Hash.ToLowerInvariant()
                $expectedArchiveChecksum = "$archiveHash *$([System.IO.Path]::GetFileName($archive))$([Environment]::NewLine)"
                [System.IO.File]::WriteAllText($stagedArchiveChecksum, $expectedArchiveChecksum, $encoding)
            }
            elseif ($scenario -eq 'invalid-staged-archive-checksum') {
                [System.IO.File]::WriteAllText($stagedArchiveChecksum, 'invalid', $encoding)
            }

            $hadInstaller = $scenario -notin @('new', 'checksum-locked-without-installer')
            $hadChecksum = $scenario -ne 'new'
            $hadArchive = $scenario -ne 'new'
            $hadArchiveChecksum = $scenario -ne 'new'
            if ($hadInstaller) { [System.IO.File]::WriteAllText($setup, $oldContents, $encoding) }
            if ($hadChecksum) { [System.IO.File]::WriteAllText($checksum, $oldChecksum, $encoding) }
            if ($hadArchive) { [System.IO.File]::WriteAllText($archive, $oldArchive, $encoding) }
            if ($hadArchiveChecksum) {
                [System.IO.File]::WriteAllText($archiveChecksum, $oldArchiveChecksum, $encoding)
            }

            $transactionMarker = Join-Path $output '.installer-transaction.json'
            if ($scenario -eq 'interrupted-promotion-recovery') {
                $transactionArtifacts = @(
                    [pscustomobject]@{
                        Staged = $stagedSetup
                        Destination = $setup
                        Backup = Join-Path $stage ('previous-' + [System.IO.Path]::GetFileName($setup))
                        Hash = $hash
                        Label = 'installer'
                        HadPrevious = $true
                        PreviousHash = ''
                    },
                    [pscustomobject]@{
                        Staged = $stagedChecksum
                        Destination = $checksum
                        Backup = Join-Path $stage ('previous-' + [System.IO.Path]::GetFileName($checksum))
                        Hash = (Get-FileHash -LiteralPath $stagedChecksum -Algorithm SHA256).Hash
                        Label = 'installer checksum'
                        HadPrevious = $true
                        PreviousHash = ''
                    },
                    [pscustomobject]@{
                        Staged = $stagedArchive
                        Destination = $archive
                        Backup = Join-Path $stage ('previous-' + [System.IO.Path]::GetFileName($archive))
                        Hash = $archiveHash
                        Label = 'archive'
                        HadPrevious = $true
                        PreviousHash = ''
                    },
                    [pscustomobject]@{
                        Staged = $stagedArchiveChecksum
                        Destination = $archiveChecksum
                        Backup = Join-Path $stage ('previous-' + [System.IO.Path]::GetFileName($archiveChecksum))
                        Hash = (Get-FileHash -LiteralPath $stagedArchiveChecksum -Algorithm SHA256).Hash
                        Label = 'archive checksum'
                        HadPrevious = $true
                        PreviousHash = ''
                    }
                )
                foreach ($artifact in $transactionArtifacts) {
                    $artifact.PreviousHash = (
                        Get-FileHash -LiteralPath $artifact.Destination -Algorithm SHA256).Hash.ToLowerInvariant()
                    [System.IO.File]::Copy($artifact.Destination, $artifact.Backup, $false)
                }
                Write-ReleaseTransactionMarker `
                    $root $output $transactionMarker $stage $transactionArtifacts

                foreach ($artifact in $transactionArtifacts[0..2]) {
                    [System.IO.File]::Replace(
                        $artifact.Staged,
                        $artifact.Destination,
                        [System.Management.Automation.Language.NullString]::Value)
                }
                Require ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -eq $hash) `
                    'The interrupted transaction fixture did not promote the installer.'
                Require ([System.IO.File]::ReadAllText($archiveChecksum) -ceq $oldArchiveChecksum) `
                    'The interrupted transaction fixture advanced too far.'

                $recovered = Recover-ReleaseTransactionIfPresent $root $output $transactionMarker
                Require $recovered 'The interrupted release transaction was not detected.'
                Require ([System.IO.File]::ReadAllText($setup) -ceq $oldContents) `
                    'Interrupted recovery did not restore the installer.'
                Require ([System.IO.File]::ReadAllText($checksum) -ceq $oldChecksum) `
                    'Interrupted recovery did not restore the installer checksum.'
                Require ([System.IO.File]::ReadAllText($archive) -ceq $oldArchive) `
                    'Interrupted recovery did not restore the archive.'
                Require ([System.IO.File]::ReadAllText($archiveChecksum) -ceq $oldArchiveChecksum) `
                    'Interrupted recovery did not preserve the archive checksum.'
                Require (-not (Test-Path -LiteralPath $transactionMarker)) `
                    'The completed recovery left its transaction marker behind.'
                Write-Output 'packaging-helper-ok'
                exit 0
            }

            if ($scenario -eq 'post-promotion-validation-failure') {
                $script:originalArchiveValidator = (
                    Get-Command Assert-InstallerArchive -CommandType Function).ScriptBlock
                $script:archiveValidationCalls = 0
                $script:allFourArtifactsWerePromoted = $false
                $script:promotedSetup = $setup
                $script:promotedChecksum = $checksum
                $script:promotedArchive = $archive
                $script:promotedArchiveChecksum = $archiveChecksum
                $script:promotedInstallerHash = $hash
                $script:promotedChecksumContents = $expectedChecksum
                $script:promotedArchiveHash = $archiveHash
                $script:promotedArchiveChecksumContents = $expectedArchiveChecksum
                function Assert-InstallerArchive {
                    param([string]$ArchivePath, [string]$SetupFileName, [string]$ExpectedInstallerHash)

                    $script:archiveValidationCalls++
                    if ($script:archiveValidationCalls -eq 2) {
                        Require ((Get-FileHash -LiteralPath $script:promotedSetup -Algorithm SHA256).Hash -eq
                            $script:promotedInstallerHash) 'Final validation ran before installer promotion.'
                        Require ([System.IO.File]::ReadAllText($script:promotedChecksum) -ceq
                            $script:promotedChecksumContents) 'Final validation ran before installer checksum promotion.'
                        Require ((Get-FileHash -LiteralPath $script:promotedArchive -Algorithm SHA256).Hash -eq
                            $script:promotedArchiveHash) 'Final validation ran before archive promotion.'
                        Require ([System.IO.File]::ReadAllText($script:promotedArchiveChecksum) -ceq
                            $script:promotedArchiveChecksumContents) 'Final validation ran before archive checksum promotion.'
                        $script:allFourArtifactsWerePromoted = $true
                        throw 'Synthetic post-promotion validation failure.'
                    }
                    & $script:originalArchiveValidator @PSBoundParameters
                }
            }

            $locked = $null
            $failure = $null
            try {
                if ($scenario.StartsWith('checksum-locked', [StringComparison]::Ordinal)) {
                    $locked = [System.IO.File]::Open($checksum, [System.IO.FileMode]::Open,
                        [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
                }
                elseif ($scenario -eq 'archive-checksum-locked') {
                    $locked = [System.IO.File]::Open($archiveChecksum, [System.IO.FileMode]::Open,
                        [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
                }
                Publish-ReleaseBundle `
                    $root $stagedSetup $setup $hash $stagedArchive $archive $archiveHash
            }
            catch { $failure = $_ }
            finally { if ($null -ne $locked) { $locked.Dispose() } }

            $expectSuccess = $scenario -in @('replace', 'new')
            if ($expectSuccess -and $null -ne $failure) {
                throw ('Unexpected installer promotion failure: ' + $failure.Exception.GetBaseException().Message.Replace($root, '<test-root>') + ' | ' + $failure.ScriptStackTrace)
            }
            Require (($null -eq $failure) -eq $expectSuccess) 'Unexpected installer promotion result.'
            if ($expectSuccess) {
                Require ([System.IO.File]::ReadAllText($setup) -ceq $newContents) 'New installer was not promoted.'
                Require ([System.IO.File]::ReadAllText($checksum) -ceq $expectedChecksum) 'New checksum was not promoted.'
                Require ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -eq $archiveHash) 'New archive was not promoted.'
                Require ([System.IO.File]::ReadAllText($archiveChecksum) -ceq $expectedArchiveChecksum) 'New archive checksum was not promoted.'
                Assert-InstallerArchive $archive ([System.IO.Path]::GetFileName($setup)) $hash
            }
            else {
                Require ((Test-Path -LiteralPath $setup -PathType Leaf) -eq $hadInstaller) 'Previous installer existence changed.'
                if ($hadInstaller) {
                    Require ([System.IO.File]::ReadAllText($setup) -ceq $oldContents) 'Previous installer bytes changed.'
                }
                Require ([System.IO.File]::ReadAllText($checksum) -ceq $oldChecksum) 'Previous checksum bytes changed.'
                Require ([System.IO.File]::ReadAllText($archive) -ceq $oldArchive) 'Previous archive bytes changed.'
                Require ([System.IO.File]::ReadAllText($archiveChecksum) -ceq $oldArchiveChecksum) 'Previous archive checksum bytes changed.'
                if ($scenario.StartsWith('checksum-locked', [StringComparison]::Ordinal)) {
                    Require (-not (Test-Path -LiteralPath $stagedSetup -PathType Leaf)) 'The checksum failure did not occur after installer promotion.'
                    Require ($failure.Exception.Message.Contains('previous release state was restored')) 'Paired promotion did not complete rollback.'
                }
                elseif ($scenario -eq 'archive-checksum-locked') {
                    Require (-not (Test-Path -LiteralPath $stagedArchive -PathType Leaf)) 'The failure did not occur after archive promotion.'
                    Require ($failure.Exception.Message.Contains('previous release state was restored')) 'Bundle promotion did not complete rollback.'
                }
                elseif ($scenario -eq 'post-promotion-validation-failure') {
                    Require ($script:archiveValidationCalls -eq 2) 'The synthetic failure did not occur after promotion.'
                    Require $script:allFourArtifactsWerePromoted `
                        'The synthetic failure did not observe a fully promoted four-file bundle.'
                    Require ($failure.Exception.Message.Contains('previous release state was restored')) `
                        'Post-promotion validation failure did not complete recovery.'
                    Require (-not (Test-Path -LiteralPath $transactionMarker)) `
                        'Post-promotion recovery left its transaction marker behind.'
                }
            }

            if (-not $scenario.StartsWith('invalid-staged-', [StringComparison]::Ordinal)) {
                $setupBackup = Join-Path $stage ('previous-' + [System.IO.Path]::GetFileName($setup))
                if ($hadInstaller) {
                    Require ([System.IO.File]::ReadAllText($setupBackup) -ceq $oldContents) 'Previous installer recovery copy was not retained.'
                }
                if ($hadChecksum) {
                    $checksumBackup = Join-Path $stage ('previous-' + [System.IO.Path]::GetFileName($checksum))
                    Require ([System.IO.File]::ReadAllText($checksumBackup) -ceq $oldChecksum) 'Previous checksum recovery copy was not retained.'
                }
                if ($hadArchive) {
                    $archiveBackup = Join-Path $stage ('previous-' + [System.IO.Path]::GetFileName($archive))
                    Require ([System.IO.File]::ReadAllText($archiveBackup) -ceq $oldArchive) 'Previous archive recovery copy was not retained.'
                }
                if ($hadArchiveChecksum) {
                    $archiveChecksumBackup = Join-Path $stage ('previous-' + [System.IO.Path]::GetFileName($archiveChecksum))
                    Require ([System.IO.File]::ReadAllText($archiveChecksumBackup) -ceq $oldArchiveChecksum) 'Previous archive checksum recovery copy was not retained.'
                }
            }
            Write-Output 'packaging-helper-ok'
        }
        catch {
            Write-Output ('packaging-helper-failed: ' + $_.Exception.Message)
            exit 1
        }
        """;
}
