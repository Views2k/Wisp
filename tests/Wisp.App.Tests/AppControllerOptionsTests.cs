using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AppControllerOptionsTests
{
    [Fact]
    public async Task StandaloneGForceWindowPolicyAppliesAtStartupAndAfterLayoutChanges()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "Wisp.App.Tests",
            Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(temporaryDirectory, "settings.json");

        try
        {
            var settings = new AppSettings
            {
                LayoutMode = HudLayoutMode.Combined,
                GForceEnabled = true,
                GForceAttached = true,
                StartWithWindows = false
            };
            CompleteSetupFixture(settings);

            await using var controller = new AppController(settings, new SettingsService(settingsPath));

            Assert.False(controller.IsStandaloneGForceWindowEnabled);

            controller.ViewModel.LayoutSelectionIndex = (int)HudLayoutMode.Minimal;
            controller.ApplyViewOptions();
            Assert.True(controller.IsStandaloneGForceWindowEnabled);

            controller.ViewModel.LayoutSelectionIndex = (int)HudLayoutMode.Combined;
            controller.ApplyViewOptions();
            Assert.False(controller.IsStandaloneGForceWindowEnabled);
            Assert.True(settings.GForceEnabled);

            controller.ViewModel.LayoutSelectionIndex = (int)HudLayoutMode.SeparateBoxes;
            controller.ApplyViewOptions();
            Assert.True(controller.IsStandaloneGForceWindowEnabled);

            controller.ViewModel.LayoutSelectionIndex = (int)HudLayoutMode.Native;
            controller.ApplyViewOptions();
            Assert.False(controller.IsStandaloneGForceWindowEnabled);

            controller.ViewModel.GForceAttached = false;
            controller.ApplyViewOptions();
            Assert.True(controller.IsStandaloneGForceWindowEnabled);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ApplyViewOptionsCopiesHeadlessViewModelOptionsToSettings()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "Wisp.App.Tests",
            Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(temporaryDirectory, "settings.json");

        try
        {
            var settings = new AppSettings
            {
                StartWithWindows = false
            };
            CompleteSetupFixture(settings);

            await using (var controller = new AppController(settings, new SettingsService(settingsPath)))
            {
                controller.ViewModel.UnitSelectionIndex = 1;
                controller.ViewModel.TorqueUnitSelectionIndex = 1;
                controller.ViewModel.SpeedSourceSelectionIndex = (int)SpeedSourceMode.Fh6VehicleSpeed;
                controller.ViewModel.LayoutSelectionIndex = (int)HudLayoutMode.SeparateBoxes;
                controller.ViewModel.NativeGaugeSelectionIndex = (int)NativeGaugeMode.Analogue;
                controller.ViewModel.GearDisplaySelectionIndex = (int)GearDisplayMode.Automatic;
                controller.ViewModel.InvertLateralG = false;
                controller.ViewModel.InvertLongitudinalG = true;
                controller.ViewModel.OverlayWidthScale = 1.25;
                controller.ViewModel.OverlayHeightScale = 0.75;
                controller.ViewModel.OverlayOpacity = 0.68;
                controller.ViewModel.Smoothing = 0.42;
                controller.ViewModel.GForceEnabled = false;
                controller.ViewModel.GForceAttached = false;
                controller.ViewModel.GForceWidthScale = 1.35;
                controller.ViewModel.GForceHeightScale = 0.85;
                controller.ViewModel.BoostGaugeScale = 1.4;
                controller.ViewModel.BoostGaugeColorNumber = true;
                controller.ViewModel.DigitalBoostGaugeColorNumber = true;
                controller.ViewModel.DigitalBoostGaugeStockColors = true;
                controller.ViewModel.UseBarBoostPressure = true;
                controller.ViewModel.TireTemperatureGaugeEnabled = false;
                controller.ViewModel.TireTemperatureGaugeAttached = false;
                controller.ViewModel.TireTemperatureReactiveColors = false;
                controller.ViewModel.UseCelsiusTireTemperature = true;
                controller.ViewModel.TireTemperatureGaugeScale = 1.25;
                controller.ViewModel.GameAwareVisibility = false;
                controller.ViewModel.AutoMinimizeOnTelemetry = false;
                controller.ViewModel.StartWithWindows = false;
                controller.ViewModel.TractionCueEnabled = false;

                controller.ApplyViewOptions();

                Assert.Equal(SpeedUnit.KilometersPerHour, settings.SpeedUnit);
                Assert.Equal(TorqueUnit.PoundFeet, settings.TorqueUnit);
                Assert.Equal(SpeedSourceMode.Fh6VehicleSpeed, settings.SpeedSource);
                Assert.Equal(HudLayoutMode.SeparateBoxes, settings.LayoutMode);
                Assert.Equal(NativeGaugeMode.Analogue, settings.NativeGaugeMode);
                Assert.Equal(GearDisplayMode.Automatic, settings.GearDisplayMode);
                Assert.False(settings.InvertLateralG);
                Assert.True(settings.InvertLongitudinalG);
                Assert.Equal(WheelAggregationMode.RawDrivenWheels, settings.AggregationMode);
                Assert.Equal(1.25, settings.OverlayWidthScale);
                Assert.Equal(0.75, settings.OverlayHeightScale);
                Assert.Equal(0.68, settings.OverlayOpacity);
                Assert.Equal(0.42, settings.Smoothing);
                Assert.False(settings.GForceEnabled);
                Assert.False(settings.GForceAttached);
                Assert.Equal(1.35, settings.GForceWidthScale);
                Assert.Equal(0.85, settings.GForceHeightScale);
                Assert.Equal(1.4, settings.BoostGaugeScale);
                Assert.True(settings.BoostGaugeColorNumber);
                Assert.True(settings.DigitalBoostGaugeColorNumber);
                Assert.True(settings.DigitalBoostGaugeStockColors);
                Assert.Equal(BoostPressureUnit.Bar, settings.BoostPressureUnit);
                Assert.False(settings.TireTemperatureGaugeEnabled);
                Assert.False(settings.TireTemperatureGaugeAttached);
                Assert.False(settings.TireTemperatureReactiveColors);
                Assert.Equal(TireTemperatureUnit.Celsius, settings.TireTemperatureUnit);
                Assert.Equal(1.25, settings.TireTemperatureGaugeScale);
                Assert.False(settings.GameAwareVisibility);
                Assert.False(settings.AutoMinimizeOnTelemetry);
                Assert.False(settings.StartWithWindows);
                Assert.False(settings.TractionCueEnabled);
            }
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static void CompleteSetupFixture(AppSettings settings)
    {
        var now = DateTimeOffset.UtcNow;
        var preferences = SetupPreferences.FromSettings(settings) with
        {
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        };
        SetupCompletion.Save(
            settings, preferences,
            new SetupTelemetryEvidence(settings.UdpPort, 12, 12, TimeSpan.FromMilliseconds(550), now),
            _ => { }, now);
    }
}
