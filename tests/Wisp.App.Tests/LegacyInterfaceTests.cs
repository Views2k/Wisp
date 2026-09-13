using System.Text.Json;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

[Collection("Tach diagnostics")]
public sealed class LegacyInterfaceTests
{
    [Fact]
    public void NewAndExistingSettingsDefaultToTheCurrentInterface()
    {
        Assert.False(new AppSettings().UseLegacyInterface);
        var settings = JsonSerializer.Deserialize<AppSettings>(
            "{\"SettingsRevision\":9,\"OverlayOpacity\":0.65,\"NativeGaugeMode\":1}")!;
        settings.MigrateSettings();
        Assert.False(settings.UseLegacyInterface);
        Assert.False(new DiagnosticsViewModel(settings).UseLegacyInterface);
        Assert.Equal(0.65, settings.OverlayOpacity);
        Assert.Equal(NativeGaugeMode.Analogue, settings.NativeGaugeMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterfacePreferenceRoundTripsWithoutChangingSavedHudState(bool legacy)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp-legacy-settings", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsService(Path.Combine(directory, "settings.json"));
            var calibration = new CalibrationSnapshot(42, 0.35, 120,
                DrivetrainType.AllWheelDrive, RollingRadiusEstimator.CurrentCalibrationRevision, 0.34, 0.36);
            var settings = new AppSettings
            {
                SettingsRevision = 9,
                UseLegacyInterface = !legacy,
                LayoutMode = HudLayoutMode.Native,
                NativeGaugeMode = NativeGaugeMode.Analogue,
                OverlayOpacity = 0.65,
                OverlayWidthScale = 1.25,
                CustomAccentColor = "#FF55BBCC",
                SidebarCollapsed = true,
                RecordingCountdownSeconds = 5,
                Calibrations = [calibration],
                Placements = new Dictionary<string, OverlayPlacement>
                {
                    ["display"] = new OverlayPlacement(125, 240, 1.1, 0.9)
                },
                LastOverlayPlacementKey = "display",
                HudPresets = [HudPreset.Capture(new AppSettings { LayoutMode = HudLayoutMode.Combined }, "Racing")]
            };
            store.Save(settings);
            var previous = store.Load();
            Assert.Equal(calibration, Assert.Single(previous.Calibrations));
            previous.UseLegacyInterface = legacy;
            store.Save(previous);
            var restored = store.Load();

            Assert.Equal(legacy, restored.UseLegacyInterface);
            Assert.Equal(legacy, new DiagnosticsViewModel(restored).UseLegacyInterface);
            Assert.Equal(JsonSerializer.Serialize(previous), JsonSerializer.Serialize(restored));
            Assert.Equal(calibration, Assert.Single(restored.Calibrations));
            Assert.Single(restored.Placements);
            Assert.Single(restored.HudPresets);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyingAHudPresetPreservesTheInterfacePreference(bool legacy)
    {
        var source = new AppSettings
        {
            UseLegacyInterface = !legacy,
            LayoutMode = HudLayoutMode.Native,
            OverlayOpacity = 0.72
        };
        var target = new AppSettings { UseLegacyInterface = legacy, OverlayOpacity = 1 };
        var preset = HudPreset.Capture(source, "Night driving");
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(preset));
        Assert.False(serialized.RootElement.TryGetProperty(nameof(AppSettings.UseLegacyInterface), out _));

        preset.ApplyTo(target);

        Assert.Equal(legacy, target.UseLegacyInterface);
        Assert.Equal(source.LayoutMode, target.LayoutMode);
        Assert.Equal(0.72, target.OverlayOpacity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControllerSynchronizesAndSavesThePreferenceWithoutReplacingModels(bool legacy)
    {
        var settings = CompletedSettings();
        settings.UseLegacyInterface = legacy;
        var saves = new List<string>();
        var startup = new NoStartupRegistration();
        await using var controller = new AppController(settings,
            value => saves.Add(JsonSerializer.Serialize(value)), startup);
        var viewModel = controller.ViewModel;
        var runs = controller.Runs;
        var telemetry = controller.SetupTelemetry;
        Assert.Equal(legacy, viewModel.UseLegacyInterface);

        foreach (var enabled in new[] { !legacy, legacy })
        {
            var saveCount = saves.Count;
            controller.SetUseLegacyInterface(enabled);
            Assert.Equal(saveCount, saves.Count);
            Assert.Equal(enabled, settings.UseLegacyInterface);
            Assert.Equal(enabled, viewModel.UseLegacyInterface);
            Assert.Same(settings, controller.Settings);
            Assert.Same(viewModel, controller.ViewModel);
            Assert.Same(runs, controller.Runs);
            Assert.Same(telemetry, controller.SetupTelemetry);
            Assert.Null(controller.ControlPanel);
            Assert.True(controller.TrySavePendingSettings());
            Assert.Equal(saveCount + 1, saves.Count);
            var restored = JsonSerializer.Deserialize<AppSettings>(saves[^1])!;
            Assert.Equal(enabled, restored.UseLegacyInterface);
            Assert.Equal(settings.OverlayOpacity, restored.OverlayOpacity);
        }
        Assert.Equal(0, startup.Calls);
    }

    internal static void AssertExistingWindowIsPreserved(MainWindow window, AppController controller)
    {
        var previousWindow = controller.ControlPanel;
        var previousPreference = controller.Settings.UseLegacyInterface;
        var runs = controller.Runs;
        controller.ControlPanel = window;
        try
        {
            controller.SetUseLegacyInterface(!previousPreference);
            Assert.Same(window, controller.ControlPanel);
            Assert.Same(runs, controller.Runs);
            Assert.Equal(!previousPreference, controller.ViewModel.UseLegacyInterface);
        }
        finally
        {
            controller.SetUseLegacyInterface(previousPreference);
            controller.ControlPanel = previousWindow;
        }
    }

    private static AppSettings CompletedSettings()
    {
        var settings = new AppSettings
        {
            StartWithWindows = false,
            AutomaticApplicationUpdateChecks = false,
            OverlayOpacity = 0.65
        };
        var now = DateTimeOffset.UtcNow;
        var preferences = SetupPreferences.FromSettings(settings) with
        {
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        };
        SetupCompletion.Save(settings, preferences,
            new SetupTelemetryEvidence(settings.UdpPort, 12, 12, TimeSpan.FromMilliseconds(550), now),
            _ => { }, now);
        return settings;
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public int Calls { get; private set; }
        public void Apply(bool startWithWindows, bool startWithForza) => Calls++;
    }
}
