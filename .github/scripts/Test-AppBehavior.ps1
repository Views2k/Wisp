[CmdletBinding()]
param([string]$DotNet = 'dotnet', [string]$BaseRevision, [switch]$Full)
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
        & $DotNet test Wisp.sln -c Release --no-build --no-restore --filter $filter -m:1
        if ($LASTEXITCODE -ne 0) { throw 'Nightly application tests failed.' }
        foreach ($test in $isolated) {
            & $DotNet test tests/Wisp.App.Tests -c Release --no-build --no-restore --filter "FullyQualifiedName~$test"
            if ($LASTEXITCODE -ne 0) { throw "Isolated nightly test failed: $test" }
        }
        return
    }
    # These behavioral guards always run, regardless of which product files changed.
    foreach ($project in @('Core', 'Telemetry', 'Update', 'Updater')) {
        $options = @()
        if ($project -eq 'Core') { $options = @('--filter', 'FullyQualifiedName!~LapDeltaScenarioTests') }
        & $DotNet test "tests/Wisp.$project.Tests" -c Release --no-build --no-restore @options
        if ($LASTEXITCODE -ne 0) { throw "$project safety tests failed." }
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
    foreach ($file in Get-ChildItem tests/Wisp.App.Tests -Filter '*Tests.cs') {
        foreach ($pattern in $critical) {
            if ($file.BaseName -like $pattern) { [void]$classes.Add($file.BaseName); break }
        }
    }
    if ($BaseRevision) {
        if ($BaseRevision -notmatch '^[0-9a-fA-F]{40}$') { throw 'Expected an exact base commit.' }
        $files = @(& git diff --name-only "$BaseRevision...HEAD")
        if ($LASTEXITCODE -ne 0) { throw 'Changed files could not be determined.' }
        foreach ($file in $files) {
            if ($file -notmatch '\.cs$') { continue }
            $stem = [IO.Path]::GetFileNameWithoutExtension($file).Split('.')[0]
            foreach ($test in Get-ChildItem tests/Wisp.App.Tests -Filter "$stem*Tests.cs") {
                if ($test.BaseName -notmatch '(Ui|Xaml|Layout|Contract|Presentation|Performance|Recorded|Replay|Rendering)') {
                    [void]$classes.Add($test.BaseName)
                }
            }
        }
    }
    if ($classes.Count -eq 0) { throw 'No application safety tests were selected.' }
    $filter = (($classes | Sort-Object | ForEach-Object { "FullyQualifiedName~Wisp.App.Tests.$_." }) -join '|')
    & $DotNet test tests/Wisp.App.Tests -c Release --no-build --no-restore --filter $filter
    if ($LASTEXITCODE -ne 0) { throw 'Application behavioral tests failed.' }
}
finally { Pop-Location }
