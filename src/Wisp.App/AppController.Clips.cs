using Wisp.App.Clips;

namespace Wisp.App;

public sealed partial class AppController
{
    private ClipRecorderService _clipRecorder = null!;
    private readonly ClipShortcutFeedback _clipShortcutFeedback = new();
    private Func<bool, OverlayHotkeyChord, OverlayHotkeyRegistrationResult>? _clipToggleRegistration, _clipSaveRegistration;
    private long _clipsRuntimeRevision;
    private long _clipTargetObservationGeneration;
    public ClipsViewModel Clips { get; private set; } = null!;
    internal bool ShortcutCaptureActive => Runs.ShortcutCaptureActive || Clips.ShortcutCaptureActive ||
        ControlPanel?.IsCapturingOverlayHotkey == true;

    private void InitializeClips(string? helperPath, string? clipLibraryDirectory)
    {
        var libraryDirectory = clipLibraryDirectory ?? "";
        // Preview/test controllers cannot discover or launch the installed helper.
        _clipRecorder = helperPath is null
            ? new ClipRecorderService(() => libraryDirectory,
                () => throw new InvalidOperationException("The recorder is unavailable in this host."), helperAvailable: false)
            : new ClipRecorderService(helperPath, () => libraryDirectory);
        Clips = new(Settings.Clips, _clipRecorder, _dispatcher,
            helperPath is null ? null : new RecorderThumbnailProvider(helperPath), libraryDirectory: libraryDirectory);
        Clips.SetRuntimeActive(false);
        Clips.SetShortcutRegistration(ConfigureClipShortcut);
        if (helperPath is not null) Clips.ShortcutFeedbackRequested += OnClipShortcutFeedbackRequested;
        Clips.PreferencesChanged += (_, _) =>
        {
            Settings.Clips = Clips.Preferences;
            ScheduleSettingsSave();
        };
    }

    private void OnClipShortcutFeedbackRequested(ClipShortcutFeedbackKind kind)
    {
        if (!_disposed && !_runtimeSuspended && !Settings.RequiresSetup)
            _clipShortcutFeedback.Play(kind, Clips.ShortcutSoundsEnabled);
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
        if (enabled)
        {
            if (Settings.OverlayHotkeyEnabled && chord == new OverlayHotkeyChord(Settings.OverlayHotkeyModifiers, Settings.OverlayHotkeyKey))
                return "this shortcut already controls Wisp HUD visibility";
            if (Settings.RecordingShortcutEnabled && chord == new OverlayHotkeyChord(Settings.RecordingShortcutModifiers, Settings.RecordingShortcutKey))
                return "this shortcut already starts and stops Wisp run recording";
            if (Settings.MarkerShortcutEnabled && chord == new OverlayHotkeyChord(Settings.MarkerShortcutModifiers, Settings.MarkerShortcutKey))
                return "this shortcut already marks a moment in Wisp runs";
        }
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
        Clips.SetShortcutStatus(string.Join(" ",
            ClipShortcutStatus("Toggle clipping", settings.ToggleShortcutEnabled, settings.ToggleShortcut, toggleError),
            ClipShortcutStatus("Save a clip", settings.SaveShortcutEnabled, settings.SaveShortcut, saveError)));
        await Clips.InitializeAsync();
        if (!_disposed && !_runtimeSuspended && revision == _clipsRuntimeRevision)
            await Clips.RestoreEnabledPreferenceAsync();
    }

    internal static string ClipShortcutStatus(string action, bool enabled, OverlayHotkeyChord chord, string? error) =>
        !enabled ? $"{action} shortcut is off." : error is null ? $"{action}: {chord}." :
        $"{action} ({chord}) unavailable: {error}. Choose another shortcut in Recording settings.";

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
        return new(settings.LengthSeconds, settings.ResolutionHeight, settings.FrameRate, settings.Quality, settings.CaptureSystemAudio);
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
        Clips.ShortcutFeedbackRequested -= OnClipShortcutFeedbackRequested;
        Clips.Dispose();
        return _clipRecorder.DisposeAsync().AsTask();
    }
}
