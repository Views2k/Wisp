using System.Text.Json;
using System.Windows.Input;
using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HudPresetTests
{
    [Fact]
    public void CaptureAndApplyUseAnExplicitPresentationAndPaletteAllowlist()
    {
        var source = new AppSettings
        {
            SpeedUnit = SpeedUnit.KilometersPerHour,
            TorqueUnit = TorqueUnit.NewtonMeters,
            LayoutMode = HudLayoutMode.Native,
            NativeGaugeMode = NativeGaugeMode.Analogue,
            GearDisplayMode = GearDisplayMode.Automatic,
            OverlayWidthScale = 1.35,
            OverlayHeightScale = 0.85,
            OverlayOpacity = 0.72,
            GForceEnabled = false,
            GForceAttached = false,
            GForceWidthScale = 1.4,
            GForceHeightScale = 1.2,
            InvertLateralG = false,
            InvertLongitudinalG = true,
            BoostGaugeEnabled = true,
            BoostGaugeAttached = false,
            BoostGaugeColorNumber = true,
            DigitalBoostGaugeColorNumber = true,
            DigitalBoostGaugeStockColors = true,
            BoostPressureUnit = BoostPressureUnit.Bar,
            BoostGaugeScale = 1.25,
            TireTemperatureGaugeEnabled = true,
            TireTemperatureGaugeAttached = false,
            TireTemperatureReactiveColors = false,
            TireTemperatureUnit = TireTemperatureUnit.Celsius,
            TireTemperatureGaugeScale = 1.15,
            TractionCueEnabled = false,
            ColorTheme = "Rose",
            BackgroundTheme = "Forest",
            HudBorderTheme = "Red",
            BoostGaugeTheme = "Purple",
            CustomAccentColor = "#FFAA3300",
            CustomBackgroundColor = "#FF101820",
            CustomHudBorderColor = "#66556677",
            CustomBoostLowColor = "#FF112233",
            CustomBoostMidColor = "#FF445566",
            CustomBoostHighColor = "#FF778899",
            CustomTractionCueColor = "#FFAABBCC"
        };
        var calibration = new CalibrationSnapshot(42, 0.34, 120);
        var placement = new OverlayPlacement(125, 240, 1.1, 0.9);
        var target = new AppSettings
        {
            ColorTheme = "Aqua",
            BackgroundTheme = "Navy",
            CustomAccentColor = "#FF010203",
            CustomBackgroundColor = "#FF040506",
            UdpPort = 5601,
            TorqueUnit = TorqueUnit.PoundFeet,
            SpeedSource = SpeedSourceMode.Fh6VehicleSpeed,
            AggregationMode = WheelAggregationMode.Robust,
            Smoothing = 0.91,
            OverlayLocked = false,
            StartWithWindows = false,
            StartWithForza = true,
            StartMinimizedWithForza = true,
            AnimatedBackground = false,
            AutomaticApplicationUpdateChecks = false,
            LastApplicationUpdateCheckUtc = DateTimeOffset.Parse("2026-09-04T12:00:00Z"),
            DebugLoggingEnabled = true,
            DebugLoggingExpiresAtUtc = DateTimeOffset.Parse("2026-09-04T13:00:00Z"),
            GameAwareVisibility = false,
            OverlayHotkeyEnabled = true,
            OverlayHotkeyModifiers = OverlayHotkeyModifiers.Alt,
            OverlayHotkeyKey = Key.F8,
            AutoMinimizeOnTelemetry = false,
            SidebarCollapsed = true,
            HasCompletedSetup = true,
            Placements = new Dictionary<string, OverlayPlacement> { ["display"] = placement },
            LastOverlayPlacementKey = "display",
            Calibrations = [calibration]
        };

        var preset = HudPreset.Capture(source, "  Drift   night  ");
        preset.ApplyTo(target);

        Assert.Equal("Drift night", preset.Name);
        Assert.Equal(source.SpeedUnit, target.SpeedUnit);
        Assert.Equal(source.TorqueUnit, target.TorqueUnit);
        Assert.Equal(source.LayoutMode, target.LayoutMode);
        Assert.Equal(source.NativeGaugeMode, target.NativeGaugeMode);
        Assert.Equal(source.GearDisplayMode, target.GearDisplayMode);
        Assert.Equal(source.OverlayOpacity, target.OverlayOpacity);
        Assert.Equal(source.InvertLateralG, target.InvertLateralG);
        Assert.Equal(source.InvertLongitudinalG, target.InvertLongitudinalG);
        Assert.Equal(source.BoostGaugeAttached, target.BoostGaugeAttached);
        Assert.Equal(source.TireTemperatureUnit, target.TireTemperatureUnit);
        Assert.Equal(source.ColorTheme, target.ColorTheme);
        Assert.Equal(source.BackgroundTheme, target.BackgroundTheme);
        Assert.Equal(source.CustomAccentColor, target.CustomAccentColor);
        Assert.Equal(source.CustomBackgroundColor, target.CustomBackgroundColor);
        Assert.Equal(source.HudBorderTheme, target.HudBorderTheme);
        Assert.Equal(source.CustomHudBorderColor, target.CustomHudBorderColor);
        Assert.Equal(source.CustomBoostLowColor, target.CustomBoostLowColor);
        Assert.Equal(source.CustomBoostMidColor, target.CustomBoostMidColor);
        Assert.Equal(source.CustomBoostHighColor, target.CustomBoostHighColor);
        Assert.Equal(source.CustomTractionCueColor, target.CustomTractionCueColor);

        Assert.Equal(5601, target.UdpPort);
        Assert.Equal(SpeedSourceMode.Fh6VehicleSpeed, target.SpeedSource);
        Assert.Equal(WheelAggregationMode.Robust, target.AggregationMode);
        Assert.Equal(0.91, target.Smoothing);
        Assert.False(target.OverlayLocked);
        Assert.False(target.StartWithWindows);
        Assert.True(target.StartWithForza);
        Assert.True(target.StartMinimizedWithForza);
        Assert.False(target.AnimatedBackground);
        Assert.False(target.AutomaticApplicationUpdateChecks);
        Assert.True(target.DebugLoggingEnabled);
        Assert.False(target.GameAwareVisibility);
        Assert.True(target.OverlayHotkeyEnabled);
        Assert.Equal(OverlayHotkeyModifiers.Alt, target.OverlayHotkeyModifiers);
        Assert.Equal(Key.F8, target.OverlayHotkeyKey);
        Assert.False(target.AutoMinimizeOnTelemetry);
        Assert.True(target.SidebarCollapsed);
        Assert.Same(placement, target.Placements["display"]);
        Assert.Equal("display", target.LastOverlayPlacementKey);
        Assert.Same(calibration, Assert.Single(target.Calibrations));
    }

    [Fact]
    public void PresetDtoContainsOnlyReviewedHudPresentationFields()
    {
        string[] expected =
        [
            "Id", "Name", "SpeedUnit", "TorqueUnit", "LayoutMode", "NativeGaugeMode", "GearDisplayMode",
            "OverlayWidthScale", "OverlayHeightScale", "OverlayOpacity",
            "GForceEnabled", "GForceAttached", "GForceWidthScale", "GForceHeightScale",
            "InvertLateralG", "InvertLongitudinalG",
            "BoostGaugeEnabled", "BoostGaugeAttached", "BoostGaugeColorNumber",
            "DigitalBoostGaugeColorNumber", "DigitalBoostGaugeStockColors", "BoostPressureUnit",
            "BoostGaugeScale", "TireTemperatureGaugeEnabled", "TireTemperatureGaugeAttached",
            "TireTemperatureReactiveColors", "TireTemperatureUnit", "TireTemperatureGaugeScale",
            "TractionCueEnabled", "ColorTheme", "BackgroundTheme", "HudBorderTheme", "BoostGaugeTheme",
            "CustomAccentColor", "CustomBackgroundColor", "CustomHudBorderColor",
            "CustomBoostLowColor", "CustomBoostMidColor", "CustomBoostHighColor", "CustomTractionCueColor"
        ];

        var writable = typeof(HudPreset).GetProperties()
            .Where(property => property.SetMethod?.IsPublic == true)
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(expected.OrderBy(name => name), writable);
        Assert.DoesNotContain(typeof(HudPreset).GetProperties(), property =>
            property.Name.Contains("Calibration", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Placement", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Hotkey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProfilesRoundTripWithoutChangingTheSettingsRevision()
    {
        var settings = new AppSettings
        {
            HudPresets = [HudPreset.Capture(new AppSettings { LayoutMode = HudLayoutMode.Combined }, "Racing")]
        };

        settings.MigrateSettings();
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.MigrateSettings();

        Assert.Equal(9, restored.SettingsRevision);
        var preset = Assert.Single(restored.HudPresets);
        Assert.Equal("Racing", preset.Name);
        Assert.Equal(HudLayoutMode.Combined, preset.LayoutMode);
    }

    [Fact]
    public void NormalizationRejectsInvalidAndDuplicateProfileNames()
    {
        var first = HudPreset.Capture(new AppSettings(), "Drift");
        var duplicate = HudPreset.Capture(new AppSettings(), "drift");
        var invalid = new HudPreset { Name = "   " };
        var profiles = new List<HudPreset> { first, duplicate, invalid };

        HudPreset.NormalizeList(profiles);

        Assert.Same(first, Assert.Single(profiles));
    }
}
