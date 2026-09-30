using Wisp.App.Clips;

namespace Wisp.App;

public sealed partial class AppController
{
    private ClipRecorderService _clipRecorder = null!;
    private Func<bool, OverlayHotkeyChord, OverlayHotkeyRegistrationResult>? _clipToggleRegistration, _clipSaveRegistration;
    private long _clipsRuntimeRevision;
    private long _clipTargetObservationGeneration;
    public ClipsViewModel Clips { get; private set; } = null!;
    internal bool ShortcutCaptureActive => Runs.ShortcutCaptureActive || Clips.ShortcutCaptureActive ||
        ControlPanel?.IsCapturingOverlayHotkey == true;

    private void InitializeClips(string? helperPath)
    {
        // Preview/test controllers cannot discover or launch the installed helper.
        _clipRecorder = helperPath is null
            ? new ClipRecorderService(() => Settings.Clips.StorageDirectory,
                () => throw new InvalidOperationException("The recorder is unavailable in this host."), helperAvailable: false)
            : new ClipRecorderService(helperPath, () => Settings.Clips.StorageDirectory);
        Clips = new(Settings.Clips, _clipRecorder, _dispatcher,
            helperPath is null ? null : new RecorderThumbnailProvider(helperPath));
        Clips.SetRuntimeActive(false);
        Clips.SetShortcutRegistration(ConfigureClipShortcut);
        Clips.PreferencesChanged += (_, _) =>
        {
            Settings.Clips = Clips.Preferences;
            ScheduleSettingsSave();
        };
    }

    internal void SetClipHotkeyRegistrations(
        Func<bool, OverlayHotkeyChord, OverlayHotkeyRegistrationResult> toggle,
        Func<bool, OverlayHotkeyChord, OverlayHotkeyRegistrationResult> save)
    {
        _clipToggleRegistration = toggle;
        _clipSaveRegistration = save;
    }

    private string? ConfigureClipShortcut(bool save, bool enabled, OverlayHotkeyChord chord)
    {
        if (_disposed || _runtimeSuspended || Settings.RequiresSetup)
            return "shortcuts become available when Wisp is running";
        var result = (save ? _clipSaveRegistration : _clipToggleRegistration)?.Invoke(enabled, chord)
            ?? new OverlayHotkeyRegistrationResult(false, "the shortcut service is unavailable");
        return result.Succeeded ? null : result.Error;
    }

    private async Task StartClipsAsync()
    {
        var revision = ++_clipsRuntimeRevision;
        Clips.SetRuntimeActive(true);
        var settings = Clips.Preferences;
        var toggleError = ConfigureClipShortcut(false, settings.ToggleShortcutEnabled, settings.ToggleShortcut);
        var saveError = ConfigureClipShortcut(true, settings.SaveShortcutEnabled, settings.SaveShortcut);
        Clips.SetShortcutStatus(toggleError is not null || saveError is not null
            ? $"Shortcut unavailable: {toggleError ?? saveError}."
            : settings.ToggleShortcutEnabled || settings.SaveShortcutEnabled ? "Shortcuts are ready." : "Shortcuts are off.");
        await Clips.InitializeAsync();
        if (!_disposed && !_runtimeSuspended && revision == _clipsRuntimeRevision)
            await Clips.RestoreEnabledPreferenceAsync();
    }

    private Task SuspendClipsAsync()
    {
        ++_clipsRuntimeRevision;
        Clips.SetRuntimeActive(false);
        _forzaFocusService.CaptureRequested = false;
        UnregisterClipShortcuts();
        return _clipRecorder.SetEnabledAsync(false, ClipRecordingPreferences(), CancellationToken.None);
    }

    private ClipRecordingSpec ClipRecordingPreferences()
    {
        var settings = Clips.Preferences;
        return new(settings.LengthSeconds, settings.ResolutionHeight, settings.FrameRate, settings.Quality);
    }

    private void UnregisterClipShortcuts()
    {
        var settings = Clips.Preferences;
        _ = _clipToggleRegistration?.Invoke(false, settings.ToggleShortcut);
        _ = _clipSaveRegistration?.Invoke(false, settings.SaveShortcut);
        Clips.SetShortcutStatus("Shortcuts are paused.");
    }

    private void PrepareClipTargetObservation()
    {
        var generation = _clipRecorder.TargetObservationGeneration;
        if (generation != _clipTargetObservationGeneration)
        {
            // An off/on pair may complete between visibility ticks. A new
            // demand must still discard the previous stable window observation.
            _forzaFocusService.CaptureRequested = false;
            _clipTargetObservationGeneration = generation;
        }
        _forzaFocusService.CaptureRequested = generation != 0;
    }

    private void ObserveClipTarget()
    {
        if (_forzaFocusService.CaptureRequested)
            _clipRecorder.ObserveTarget(_forzaFocusService.CaptureObservation, _clipTargetObservationGeneration);
    }

    private Task DisposeClipsAsync()
    {
        ++_clipsRuntimeRevision;
        _forzaFocusService.CaptureRequested = false;
        UnregisterClipShortcuts();
        Clips.Dispose();
        return _clipRecorder.DisposeAsync().AsTask();
    }
}
