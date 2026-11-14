[CmdletBinding()]
param([string]$DotNet = 'dotnet', [string]$BaseRevision, [switch]$Full, [switch]$ListOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $root
try {
    $isolated = @(
        'TachRendererDiagnosticsTests.DisabledRendererDoesNotAllocateOrCreateCapture',
        'TachRendererDiagnosticsTests.EnabledUncontendedProducerHasNoPerEventAllocations',
        'TachDiagnosticsTests.EnabledNeedleRecordingReportsOfflineCostWithoutATimingThreshold',
        'TachDiagnosticsTests.DisabledRecordCallsCreateNoHistoryOrPerCallAllocations')
    if ($Full) {
        $filter = ($isolated | ForEach-Object { "FullyQualifiedName!~$_" }) -join '&'
        & $DotNet test Wisp.sln -c Release --no-build --no-restore --filter $filter -m:1 --blame-hang --blame-hang-timeout 10m
        if ($LASTEXITCODE -ne 0) { throw 'Nightly application tests failed.' }
        foreach ($test in $isolated) {
            & $DotNet test tests/Wisp.App.Tests -c Release --no-build --no-restore --filter "FullyQualifiedName~$test"
            if ($LASTEXITCODE -ne 0) { throw "Isolated nightly test failed: $test" }
        }
        return
    }
    # These behavioral guards always run, regardless of which product files changed.
    if (-not $ListOnly) {
        foreach ($project in @('Core', 'Telemetry', 'Update', 'Updater')) {
            $options = @()
            if ($project -eq 'Core') { $options = @('--filter', 'FullyQualifiedName!~LapDeltaScenarioTests') }
            & $DotNet test "tests/Wisp.$project.Tests" -c Release --no-build --no-restore @options
            if ($LASTEXITCODE -ne 0) { throw "$project safety tests failed." }
        }
    }
    $classes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $critical = @(
        'ApplicationUpdate*', 'AutomaticUpdateCoordinator*', 'AppControllerLifecycle*',
        'BoostDisplayModel*', 'BoostMaximum*', 'BoostPressureUnits*', 'HudPreset*',
        'Settings*', 'SetupCompletion*', 'SetupTelemetry*', 'PrivateSetupSkip*',
        'FeatureControl*', 'NativeGameVerification*', 'NativeGameIdentityProbe*', 'NativeHudSessionSafety*',
        'NativeSteamPublisherTrust*', 'NativeStorePackageIdentity*', 'NativeHudProcessMemory*',
        'NativeGaugeGeometry*', 'NativeNeedleDelivery*', 'NativeTachometerInterpolator*',
        'NativeTachometerRecovery*', 'TelemetryNeedleDelivery*', 'CompositorNeedleGeometry*',
        'CompositorNeedleCurve*', 'RunStore*', 'RunExitMarkerStore*', 'RunMarkerStore*',
        'PreviousRunRecovery*', 'ClipBufferStore*', 'ClipPendingRecovery*', 'ClipLibraryReconciliation*',
        'SupplementaryClient*', 'SupplementaryIncident*', 'SupplementaryCacheRecovery*',
        'SupplementaryCurrentGameIdentity*', 'SupplementaryDiagnostic*', 'SupportInbox*', 'MessagingActivation*')
    # Slow or environment-bound classes stay out of the gate even when a change touches them; the nightly runs them.
    $excluded = '(Ui|Xaml|Layout|Contract|Presentation|Performance|Recorded|Replay|Rendering)'
    # A type used by more test classes than this is a hub (AppController, DiagnosticsViewModel); selecting its
    # users would turn the gate into the full suite, so only the file-name rule applies to it.
    $referenceLimit = 12
    $testFiles = @(Get-ChildItem tests/Wisp.App.Tests -Filter '*Tests.cs')
    foreach ($file in $testFiles) {
        foreach ($pattern in $critical) {
            if ($file.BaseName -like $pattern) { [void]$classes.Add($file.BaseName); break }
        }
    }
    if ($BaseRevision) {
        if ($BaseRevision -notmatch '^[0-9a-fA-F]{40}$') { throw 'Expected an exact base commit.' }
        $files = @(& git diff --name-only "$BaseRevision...HEAD")
        if ($LASTEXITCODE -ne 0) { throw 'Changed files could not be determined.' }
        $contents = @{}
        foreach ($test in $testFiles) {
            if ($test.BaseName -notmatch $excluded) { $contents[$test.BaseName] = Get-Content -Raw -LiteralPath $test.FullName }
        }
        foreach ($file in $files) {
            if ($file -notmatch '\.cs$') { continue }
            $stem = [IO.Path]::GetFileNameWithoutExtension($file).Split('.')[0]
            # Tests named after the changed file.
            foreach ($test in $testFiles) {
                if ($test.Name -like "$stem*Tests.cs" -and $test.BaseName -notmatch $excluded) {
                    [void]$classes.Add($test.BaseName)
                }
            }
            # Tests that use the changed type by name, wherever they live. The three tests the nightly caught
            # in November 2026 (RunsAutosaveTests through ApplicationUpdateLauncher, FeatureUpdateTransportTests
            # through WispUpdateClient) were missed by the file-name rule alone.
            if ($file -notmatch '^src/' -or $stem.Length -lt 4) { continue }
            $typeUse = [regex]::new("\b$([regex]::Escape($stem))\b")
            $users = @($contents.Keys | Where-Object { $typeUse.IsMatch($contents[$_]) })
            if ($users.Count -gt $referenceLimit) { continue }
            foreach ($user in $users) { [void]$classes.Add($user) }
        }
    }
    if ($classes.Count -eq 0) { throw 'No application safety tests were selected.' }
    if ($ListOnly) {
        $classes | Sort-Object
        return
    }
    $filter = (($classes | Sort-Object | ForEach-Object { "FullyQualifiedName~Wisp.App.Tests.$_." }) -join '|')
    # A hung test is named and the run ends before the job's time limit does.
    & $DotNet test tests/Wisp.App.Tests -c Release --no-build --no-restore --filter $filter --blame-hang --blame-hang-timeout 3m
    if ($LASTEXITCODE -ne 0) { throw 'Application behavioral tests failed.' }
}
finally { Pop-Location }
