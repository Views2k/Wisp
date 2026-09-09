using System.Diagnostics;
using System.IO;
using Wisp.App.Runs;
using Wisp.Core;

namespace Wisp.App;

public sealed partial class AppController
{
    private RunRecordingService _runRecording = null!;
    private Func<bool, OverlayHotkeyChord, OverlayHotkeyRegistrationResult>? _recordingHotkeyRegistration;
    private Func<bool, OverlayHotkeyChord, OverlayHotkeyRegistrationResult>? _markerHotkeyRegistration;
    private bool _runsInitialized;
    private DrivetrainType? _runCalibrationDrivetrain;

    public RunsViewModel Runs { get; private set; } = null!;

    private void InitializeRuns(string? directory)
    {
        // Tests and preview hosts without a SettingsService never open the real library.
        directory ??= Path.Combine(Path.GetTempPath(), "Wisp", "RunReview", Guid.NewGuid().ToString("N"));
        _runRecording = new RunRecordingService(_receiver, directory);
        Runs = new RunsViewModel(_runRecording, Settings, _dispatcher);
        Runs.BeforeStart = () => PublishRunContext(force: true);
        Runs.PreferencesChanged += (_, _) => ScheduleSettingsSave();
    }

    internal void SetRecordingHotkeyRegistration(
        Func<bool, OverlayHotkeyChord, OverlayHotkeyRegistrationResult> registration)
    {
        _recordingHotkeyRegistration = registration;
        Runs.SetHotkeyHandler(ConfigureRecordingShortcut);
    }

    private string? ConfigureRecordingShortcut(bool enabled, OverlayHotkeyChord chord)
    {
        if (_disposed || _runtimeSuspended || Settings.RequiresSetup)
        {
            return "The recording shortcut becomes available when Wisp is running.";
        }
        var result = _recordingHotkeyRegistration?.Invoke(enabled, chord)
            ?? new OverlayHotkeyRegistrationResult(false, "the shortcut service is unavailable");
        if (!result.Succeeded)
        {
            return result.Error;
        }
        Settings.RecordingShortcutEnabled = enabled;
        Settings.RecordingShortcutModifiers = chord.Modifiers;
        Settings.RecordingShortcutKey = chord.Key;
        ScheduleSettingsSave();
        return null;
    }

    private void StartRunsUi()
    {
        Runs.RefreshHotkey();
        Runs.RefreshMarkerHotkey();
        if (!_runsInitialized && ControlPanel is not null)
        {
            _runsInitialized = true;
            _ = Runs.InitializeAsync();
        }
    }

    private void SuspendRunShortcut()
    {
        _ = _recordingHotkeyRegistration?.Invoke(false,
            new OverlayHotkeyChord(Settings.RecordingShortcutModifiers, Settings.RecordingShortcutKey));
        Runs.SuspendHotkeyStatus();
        _ = _markerHotkeyRegistration?.Invoke(false,
            new OverlayHotkeyChord(Settings.MarkerShortcutModifiers, Settings.MarkerShortcutKey));
        Runs.SuspendMarkerHotkeyStatus();
    }

    internal void SetMarkerHotkeyRegistration(
        Func<bool, OverlayHotkeyChord, OverlayHotkeyRegistrationResult> registration)
    {
        _markerHotkeyRegistration = registration;
        Runs.SetMarkerHotkeyHandler(ConfigureMarkerShortcut);
    }

    private string? ConfigureMarkerShortcut(bool enabled, OverlayHotkeyChord chord)
    {
        if (_disposed || _runtimeSuspended || Settings.RequiresSetup)
        {
            return "The marker shortcut becomes available when Wisp is running.";
        }
        var result = _markerHotkeyRegistration?.Invoke(enabled, chord)
            ?? new OverlayHotkeyRegistrationResult(false, "the shortcut service is unavailable");
        if (!result.Succeeded) return result.Error;
        Settings.MarkerShortcutEnabled = enabled;
        Settings.MarkerShortcutModifiers = chord.Modifiers;
        Settings.MarkerShortcutKey = chord.Key;
        ScheduleSettingsSave();
        return null;
    }

    private void PublishRunContext(bool force = false)
    {
        if (!force && !_runRecording.IsRecording)
        {
            return;
        }
        var state = _receiver.Latest;
        if (state is null)
        {
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var snapshot = _nativeHudProcessService.SnapshotFor(state.CarOrdinal);
        var visibility = EvaluateNativeGameplayVisibility(snapshot, now);
        var fresh = state.ReceivedTimestamp is { } received && now >= received &&
            Stopwatch.GetElapsedTime(received, now) <= TelemetryTimeout;
        var radii = _hasDebugDerivedTelemetry && _debugDerivedCarOrdinal == state.CarOrdinal &&
            _runCalibrationDrivetrain == state.Drivetrain &&
            _debugCalibration.IsTrusted && fresh ? _debugCalibration.TrustedRadii : null;
        _runRecording.UpdateContext(new RunRecordingContext(
            now, state.CarOrdinal, state.Drivetrain,
            fresh && state.IsRaceOn && visibility.Fresh && visibility.Visibility == NativeGameplayVisibility.Visible,
            radii?.FrontMeters, radii?.RearMeters,
            visibility.Fresh ? snapshot.VisibilityObservedTimestamp + NativeVisibilityFreshnessTicks : now));
    }
}
