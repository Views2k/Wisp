using System.Windows.Input;
using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HudProfileControllerTests
{
    [Fact]
    public async Task CompleteProfileRestoresCurrentCarsRangeInsteadOfTheCaptureCarsEffectiveRange()
    {
        var settings = CompletedSettings();
        settings.PowerTorqueGaugeRanges = new() { [10] = new(350, 450), [20] = new(950, 1200) };
        await using var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        SetCurrentCar(controller.ViewModel, 10);
        Assert.True(controller.TryCreateHudPreset("Two car ranges", out var profile, out var error), error);
        Assert.Equal(350, profile!.PowerGaugeMaximum);
        settings.PowerTorqueGaugeRanges[20] = new(1550, 1700);
        SetCurrentCar(controller.ViewModel, 20);
        Assert.Equal(1550, controller.ViewModel.PowerGaugeMaximum);

        Assert.True(controller.TryApplyHudPreset(profile.Id, out error), error);

        Assert.Equal(950, controller.ViewModel.PowerGaugeMaximum);
        Assert.Equal(1200, controller.ViewModel.TorqueGaugeMaximumNm);
        Assert.Equal(new PowerTorqueGaugeRange(950, 1200), settings.PowerTorqueGaugeRanges[20]);
        Assert.Equal(new PowerTorqueGaugeRange(350, 450), settings.PowerTorqueGaugeRanges[10]);
        Assert.Equal(new PowerTorqueGaugeRange(950, 1200), profile.PowerTorqueGaugeRanges![20]);
    }

    [Fact]
    public async Task CompleteProfileWithoutRangeMapPreservesTheExistingCurrentCarRange()
    {
        var settings = CompletedSettings();
        settings.PowerTorqueGaugeRanges[20] = new(950, 1200);
        await using var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        SetCurrentCar(controller.ViewModel, 20);
        var profile = HudPreset.Capture(new AppSettings { PowerGaugeMaximum = 350, TorqueGaugeMaximumNm = 450 }, "No range map");
        profile.PowerTorqueGaugeRanges = null;
        settings.HudPresets.Add(profile);

        Assert.True(controller.TryApplyHudPreset(profile.Id, out var error), error);

        Assert.Equal(950, controller.ViewModel.PowerGaugeMaximum);
        Assert.Equal(1200, controller.ViewModel.TorqueGaugeMaximumNm);
        Assert.Equal(new PowerTorqueGaugeRange(950, 1200), settings.PowerTorqueGaugeRanges[20]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task LegacyProfileStillAppliesItsSingleRangeToTheCurrentCar(int revision)
    {
        var settings = CompletedSettings();
        settings.PowerTorqueGaugeRanges = new() { [10] = new(350, 450), [20] = new(950, 1200) };
        await using var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        SetCurrentCar(controller.ViewModel, 20);
        var profile = HudPreset.Capture(new AppSettings { PowerGaugeMaximum = 650, TorqueGaugeMaximumNm = 850 }, "Legacy range");
        profile.Revision = revision;
        settings.HudPresets.Add(profile);

        Assert.True(controller.TryApplyHudPreset(profile.Id, out var error), error);

        Assert.Equal(650, controller.ViewModel.PowerGaugeMaximum);
        Assert.Equal(850, controller.ViewModel.TorqueGaugeMaximumNm);
        Assert.Equal(new PowerTorqueGaugeRange(650, 850), settings.PowerTorqueGaugeRanges[20]);
        Assert.Equal(new PowerTorqueGaugeRange(350, 450), settings.PowerTorqueGaugeRanges[10]);
    }

    [Fact]
    public async Task ApplyingAProfileSynchronizesDrivingOptionsWithoutReplacingCalibration()
    {
        var settings = CompletedSettings();
        var calibration = new CalibrationSnapshot(42, .34, 120);
        settings.Calibrations.Add(calibration);
        await using var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        var source = new AppSettings
        {
            SpeedSource = SpeedSourceMode.Fh6VehicleSpeed,
            Smoothing = .65,
            GameAwareVisibility = false,
            OverlayWidthScale = 1.35,
            OverlayHeightScale = .85,
            BoostGaugeScale = 1.25,
            DriftGaugeGuidanceMode = DriftGaugeGuidanceMode.CustomTarget,
            DriftTargetDegrees = 55,
            DriftToleranceDegrees = 12,
            RecordingCountdownSeconds = 5,
            RecordingStopAfterSeconds = 60,
            LapReviewRecordingEnabled = true,
            RunPurpose = Wisp.Core.Runs.RunPurpose.Acceleration
        };
        var profile = HudPreset.Capture(source, "Driving");
        settings.HudPresets.Add(profile);
        Assert.True(controller.TryApplyHudPreset(profile.Id, out var error), error);
        Assert.Equal((int)source.SpeedSource, controller.ViewModel.SpeedSourceSelectionIndex);
        Assert.Equal(.65, controller.ViewModel.Smoothing);
        Assert.False(controller.ViewModel.GameAwareVisibility);
        Assert.Equal(1.35, settings.OverlayWidthScale);
        Assert.Equal(.85, settings.OverlayHeightScale);
        Assert.Equal(1.25, settings.BoostGaugeScale);
        Assert.Equal(55, settings.DriftTargetDegrees);
        Assert.Equal(5, controller.Runs.CountdownSeconds);
        Assert.Equal(60, controller.Runs.StopAfterSeconds);
        Assert.True(settings.LapReviewRecordingEnabled);
        Assert.Same(calibration, Assert.Single(settings.Calibrations));
    }

    [Fact]
    public async Task ProfileShortcutsCanSwapChordsWithoutChangingTheSavedProfile()
    {
        var settings = CompletedSettings();
        settings.OverlayHotkeyEnabled = settings.RecordingShortcutEnabled = settings.MarkerShortcutEnabled = true;
        await using var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        var active = new Dictionary<int, OverlayHotkeyChord>
        {
            [0] = new(settings.OverlayHotkeyModifiers, settings.OverlayHotkeyKey),
            [1] = new(settings.RecordingShortcutModifiers, settings.RecordingShortcutKey),
            [2] = new(settings.MarkerShortcutModifiers, settings.MarkerShortcutKey)
        };
        OverlayHotkeyRegistrationResult Register(int action, bool enabled, OverlayHotkeyChord chord)
        {
            if (!enabled) { active.Remove(action); return OverlayHotkeyRegistrationResult.Success; }
            if (active.Any(pair => pair.Key != action && pair.Value == chord))
                return new(false, "already used");
            active[action] = chord;
            return OverlayHotkeyRegistrationResult.Success;
        }
        controller.SetOverlayHotkeyRegistration((enabled, chord) => Register(0, enabled, chord));
        controller.SetRecordingHotkeyRegistration((enabled, chord) => Register(1, enabled, chord));
        controller.SetMarkerHotkeyRegistration((enabled, chord) => Register(2, enabled, chord));
        var profile = HudPreset.Capture(settings, "Swap shortcuts");
        (profile.OverlayHotkeyKey, profile.RecordingShortcutKey) = (profile.RecordingShortcutKey, profile.OverlayHotkeyKey);
        settings.HudPresets.Add(profile);
        Assert.True(controller.TryApplyHudPreset(profile.Id, out var error), error);
        Assert.Equal(Key.R, settings.OverlayHotkeyKey);
        Assert.Equal(Key.H, settings.RecordingShortcutKey);
        Assert.Equal(Key.R, active[0].Key);
        Assert.Equal(Key.H, active[1].Key);
        Assert.Equal(Key.M, active[2].Key);
    }

    [Fact]
    public async Task ShortcutConflictRollsBackRegistrationsAndDoesNotApplyAnyProfileSetting()
    {
        var settings = CompletedSettings();
        settings.OverlayHotkeyEnabled = true;
        await using var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        var active = new Dictionary<int, OverlayHotkeyChord>
        {
            [0] = new(settings.OverlayHotkeyModifiers, settings.OverlayHotkeyKey)
        };
        OverlayHotkeyRegistrationResult Register(int action, bool enabled, OverlayHotkeyChord chord)
        {
            if (!enabled) { active.Remove(action); return OverlayHotkeyRegistrationResult.Success; }
            if (chord.Key == Key.J) return new(false, "another app is already using it");
            active[action] = chord;
            return OverlayHotkeyRegistrationResult.Success;
        }
        controller.SetOverlayHotkeyRegistration((enabled, chord) => Register(0, enabled, chord));
        controller.SetRecordingHotkeyRegistration((enabled, chord) => Register(1, enabled, chord));
        controller.SetMarkerHotkeyRegistration((enabled, chord) => Register(2, enabled, chord));
        var profile = HudPreset.Capture(settings, "Unavailable shortcut");
        profile.RecordingShortcutEnabled = true;
        profile.RecordingShortcutKey = Key.J;
        profile.Smoothing = .85;
        profile.LayoutMode = HudLayoutMode.Native;
        settings.HudPresets.Add(profile);
        Assert.False(controller.TryApplyHudPreset(profile.Id, out var error));
        Assert.Contains("profile was not applied", error, StringComparison.Ordinal);
        Assert.Equal(0, settings.Smoothing);
        Assert.Equal(HudLayoutMode.Minimal, settings.LayoutMode);
        Assert.False(settings.RecordingShortcutEnabled);
        Assert.Equal(Key.H, Assert.Single(active).Value.Key);
    }

    private static void SetCurrentCar(DiagnosticsViewModel model, int car) => model.Update(
        RunTestData.State(1000) with { CarOrdinal = car }, new IndicatedSpeed(0, 30, true, false, "Rear"),
        new CalibrationResult(null, .3, .2, 0, true, string.Empty, false), default,
        TimeSpan.Zero, SpeedUnit.MilesPerHour, 60, refreshDiagnostics: false, updateGForce: false);

    private static AppSettings CompletedSettings()
    {
        var settings = new AppSettings { StartWithWindows = false, AutomaticApplicationUpdateChecks = false };
        var now = DateTimeOffset.UtcNow;
        SetupCompletion.Save(settings, SetupPreferences.FromSettings(settings) with
        {
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        }, new SetupTelemetryEvidence(settings.UdpPort, 12, 12, TimeSpan.FromMilliseconds(550), now), _ => { }, now);
        return settings;
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
