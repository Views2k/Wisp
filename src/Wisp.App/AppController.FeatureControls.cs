using Wisp.App.Supplementary;

namespace Wisp.App;

public sealed partial class AppController
{
    internal FeatureControlGate FeatureControls { get; } = new();
    internal event EventHandler? FeatureControlsChanged;
    internal bool FeatureAllowed(string feature) => FeatureControls.IsAllowed(feature);
    internal bool HudFeatureAllowed(string feature) => FeatureAllowed("hud") && FeatureAllowed(feature);

    private bool TryUseHudProfiles(out string error)
    {
        error = FeatureAllowed("hud-profiles") ? "" : FeatureControls.Reason("hud-profiles") ?? "HUD profiles are temporarily paused.";
        return error.Length == 0;
    }

    internal Task ApplyFeatureControlsAsync(FeatureControlState state)
    {
        if (_disposed || !FeatureControls.Apply(state)) return Task.CompletedTask;
        var recording = FeatureAllowed("run-recording");
        var clipping = FeatureAllowed("clip-recording");
        _tuneCapture?.SetFeatureAvailable(FeatureAllowed("tune-read"));
        Tunes.SetFeatureAvailability(FeatureAllowed("tune-read"), FeatureAllowed("tune-library"),
            FeatureControls.Reason("tune-read"), FeatureControls.Reason("tune-library"));
        Runs.SetTuneFeatureAvailability(FeatureAllowed("tune-read"), FeatureAllowed("tune-library"),
            FeatureControls.Reason("tune-read") ?? FeatureControls.Reason("tune-library") ?? "Tune reading is temporarily paused.");
        var libraryStopped = Runs.SetLibraryFeatureAvailableAsync(FeatureAllowed("run-library"), FeatureControls.Reason("run-library"));
        Runs.LapReview.SetFeatureAvailable(FeatureAllowed("lap-review") && FeatureAllowed("run-library"),
            FeatureControls.Reason("lap-review") ?? FeatureControls.Reason("run-library"));
        Clips.SetFeatureAvailable(clipping);
        Clips.SetLibraryFeatureAvailability(FeatureAllowed("clip-playback"), FeatureAllowed("clip-export"), FeatureAllowed("clip-library"),
            FeatureControls.Reason("clip-library") ?? FeatureControls.Reason("clip-playback") ?? FeatureControls.Reason("clip-export"));
        // Close entry points before awaiting file finalization or helper shutdown.
        var runsStopped = _runRecording.SetFeatureAvailableAsync(recording);
        var clipsStopped = _clipRecorder.SetFeatureAvailableAsync(clipping, ClipRecordingPreferences());
        ViewModel.SetShiftGuidanceFeatureAvailable(FeatureAllowed("shift-guidance"), FeatureControls.Reason("shift-guidance"));
        var calibrationStopped = ViewModel.SetShiftCalibrationFeatureAvailable(FeatureAllowed("shift-calibration") && FeatureAllowed("shift-guidance"),
            FeatureControls.Reason("shift-guidance") ?? FeatureControls.Reason("shift-calibration"));
        _nativeHudProcessService.ShiftCueEnabled = ShiftCueRuntimeEnabled;
        var shiftCaptureStopped = FeatureAllowed("shift-guidance") ? Task.CompletedTask : StopShiftCaptureAsync();
        ViewModel.SetBackgroundAnimationFeatureAvailable(FeatureAllowed("background-animation"));
        if (!FeatureAllowed("traction-cue"))
        {
            _tractionHookDetector.Reset();
            _tractionCueUntilUtc = DateTimeOffset.MinValue;
            ViewModel.IsTractionCueActive = false;
        }
        ApplyApplicationUpdateFeatureControl();
        ApplySupportFeatureControl();
        _calibration.SetLearningEnabled(FeatureAllowed("tire-calibration"));
        ViewModel.SetTireCalibrationFeatureAvailable(FeatureAllowed("tire-calibration"), FeatureControls.Reason("tire-calibration"));
        if (!recording)
        {
            Runs.PauseRecordingForFeatureControl(FeatureControls.Reason("run-recording") ?? "Recording is temporarily paused.");
            SuspendRunShortcut();
        }
        else if (_receiver.IsRunning && !_runtimeSuspended && !Settings.RequiresSetup)
        {
            Runs.RefreshHotkey();
            Runs.RefreshMarkerHotkey();
        }
        if (!clipping)
        {
            ++_clipsRuntimeRevision;
            _forzaFocusService.CaptureRequested = false;
            UnregisterClipShortcuts();
        }
        else if (Clips.IsRuntimeActive && _receiver.IsRunning && !_runtimeSuspended && !Settings.RequiresSetup)
        {
            var settings = Clips.Preferences;
            _ = ConfigureClipShortcut(false, settings.ToggleShortcutEnabled, settings.ToggleShortcut);
            _ = ConfigureClipShortcut(true, settings.SaveShortcutEnabled, settings.SaveShortcut);
        }
        FeatureControlsChanged?.Invoke(this, EventArgs.Empty);
        UpdateShiftCueObservation();
        Overlay?.RefreshFeatureControls();
        if (_uiTimer.IsEnabled || Overlay is not null || GForceOverlay is not null || DriftGaugeOverlay is not null)
            UpdateOverlayVisibility(DateTimeOffset.UtcNow, force: true);
        return Task.WhenAll(runsStopped, clipsStopped, calibrationStopped, libraryStopped, shiftCaptureStopped);
    }
}
