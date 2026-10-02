using System.Text.Json;
using System.Windows.Input;
using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HudPresetTests
{
    [Fact]
    public void CaptureAndApplyIncludeDrivingSettingsAndPreserveApplicationAndCalibrationState()
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
            GForceGaugeScale = 1.6,
            GForceWidthScale = 1.4,
            GForceHeightScale = 1.2,
            InvertLateralG = false,
            InvertLongitudinalG = true,
            BoostGaugeEnabled = true,
            BoostGaugeAttached = false,
            BoostGaugeColorNumber = true,
            DigitalBoostGaugeColorNumber = true,
            DigitalBoostGaugeStockColors = true,
            ShowBoostVacuum = true,
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
            CustomTractionCueColor = "#FFAABBCC",
            CustomGForceColor = "#FFDDAA55",
            CustomGForceTrailColor = "#AA5599FF"
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
        Assert.Equal(source.GForceGaugeScale, target.GForceGaugeScale);
        Assert.Equal(source.GForceWidthScale, target.GForceWidthScale);
        Assert.Equal(source.GForceHeightScale, target.GForceHeightScale);
        Assert.Equal(source.InvertLateralG, target.InvertLateralG);
        Assert.Equal(source.InvertLongitudinalG, target.InvertLongitudinalG);
        Assert.Equal(source.BoostGaugeAttached, target.BoostGaugeAttached);
        Assert.True(target.ShowBoostVacuum);
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
        Assert.Equal(source.CustomGForceColor, target.CustomGForceColor);
        Assert.Equal(source.CustomGForceTrailColor, target.CustomGForceTrailColor);

        Assert.Equal(5601, target.UdpPort);
        Assert.Equal(source.SpeedSource, target.SpeedSource);
        Assert.Equal(WheelAggregationMode.Robust, target.AggregationMode);
        Assert.Equal(source.Smoothing, target.Smoothing);
        Assert.Equal(source.OverlayLocked, target.OverlayLocked);
        Assert.False(target.StartWithWindows);
        Assert.True(target.StartWithForza);
        Assert.True(target.StartMinimizedWithForza);
        Assert.False(target.AnimatedBackground);
        Assert.False(target.AutomaticApplicationUpdateChecks);
        Assert.True(target.DebugLoggingEnabled);
        Assert.Equal(source.GameAwareVisibility, target.GameAwareVisibility);
        Assert.Equal(source.OverlayHotkeyEnabled, target.OverlayHotkeyEnabled);
        Assert.Equal(source.OverlayHotkeyModifiers, target.OverlayHotkeyModifiers);
        Assert.Equal(source.OverlayHotkeyKey, target.OverlayHotkeyKey);
        Assert.False(target.AutoMinimizeOnTelemetry);
        Assert.True(target.SidebarCollapsed);
        Assert.Empty(target.Placements);
        Assert.Equal("display", target.LastOverlayPlacementKey);
        Assert.Same(calibration, Assert.Single(target.Calibrations));
    }

    [Fact]
    public void PresetDtoContainsOnlyReviewedHudAndDrivingFields()
    {
        string[] expected =
        [
            "Id", "Name", "SpeedUnit", "TorqueUnit", "LayoutMode", "NativeGaugeMode", "GearDisplayMode",
            "OverlayWidthScale", "OverlayHeightScale", "OverlayOpacity",
            "LapTimingMode", "LapDeltaEnabled", "LapDeltaReference", "LapDeltaShowBar", "LapDeltaScale",
            "LapDeltaAheadColor", "LapDeltaBehindColor", "LapMapEnabled", "LapMapScale",
            "LapMapTrackColor", "LapMapCarColor", "LapMapBackgroundColor",
            "GForceEnabled", "GForceAttached", "GForceGaugeScale", "GForceWidthScale", "GForceHeightScale",
            "InvertLateralG", "InvertLongitudinalG",
            "BoostGaugeEnabled", "BoostGaugeAttached", "BoostGaugeColorNumber",
            "DigitalBoostGaugeColorNumber", "DigitalBoostGaugeStockColors", "ShowBoostVacuum", "BoostPressureUnit",
            "BoostGaugeScale", "TireTemperatureGaugeEnabled", "TireTemperatureGaugeAttached",
            "TireTemperatureReactiveColors", "TireTemperatureUnit", "TireTemperatureGaugeScale",
            "PowerGaugeEnabled", "TorqueGaugeEnabled", "PowerTorqueGaugeScale", "PowerGaugeScale", "TorqueGaugeScale", "PowerGaugeMaximum", "TorqueGaugeMaximumNm",
            "PowerGaugeAttached", "TorqueGaugeAttached", "PowerTorqueSmoothingMilliseconds", "PowerTorqueShowNegative", "PowerTorqueDriftMode", "PowerTorqueDriftFlashFrequencyHz", "PowerTorqueDriftFlashColor",
            "PowerGaugeColorNumber", "TorqueGaugeColorNumber", "CustomPowerLowColor", "CustomPowerMidColor", "CustomPowerHighColor",
            "CustomTorqueLowColor", "CustomTorqueMidColor", "CustomTorqueHighColor",
            "AccelerationShiftCueEnabled", "ShiftCueGreenColor", "ShiftCueYellowColor", "ShiftCueRedColor",
            "TractionCueEnabled", "ColorTheme", "BackgroundTheme", "HudBorderTheme", "BoostGaugeTheme",
            "CustomAccentColor", "CustomBackgroundColor", "CustomHudBorderColor",
            "CustomBoostLowColor", "CustomBoostMidColor", "CustomBoostHighColor", "CustomTractionCueColor",
            "CustomGForceColor", "CustomGForceTrailColor",
            "Revision", "CustomParticleColor", "AppBorderColor", "AppTextColor", "AppMutedTextColor",
            "DriftGaugeEnabled", "DriftGaugeScale", "DriftGaugeDarkMode", "DriftGaugeBackgroundEnabled", "DriftGaugeBackgroundOpacity",
            "SpeedSource",
            "Smoothing",
            "OverlayLocked",
            "GameAwareVisibility",
            "DriftGaugeGuidanceMode",
            "DriftTargetDegrees",
            "DriftToleranceDegrees",
            "OverlayHotkeyEnabled",
            "OverlayHotkeyModifiers",
            "OverlayHotkeyKey",
            "RecordingShortcutEnabled",
            "RecordingShortcutModifiers",
            "RecordingShortcutKey",
            "RecordingCountdownSeconds",
            "RecordingStopAfterSeconds",
            "MarkerShortcutEnabled",
            "MarkerShortcutModifiers",
            "MarkerShortcutKey",
            "RunPurpose",
            "LapReviewRecordingEnabled",
            "Placements",
            "GForcePlacements",
            "BoostGaugePlacements",
            "TireTemperatureGaugePlacements",
            "DriftGaugePlacements",
            "PowerGaugePlacements",
            "TorqueGaugePlacements",
            "LapDeltaPlacements",
            "LapMapPlacements",
            "PowerTorqueGaugeRanges"
        ];

        var writable = typeof(HudPreset).GetProperties()
            .Where(property => property.SetMethod?.IsPublic == true)
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(expected.OrderBy(name => name), writable);
        Assert.DoesNotContain(typeof(HudPreset).GetProperties(), property =>
            property.Name.Contains("Calibration", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProfilesKeepParticleAppTextBorderAndDriftGaugeColors()
    {
        var source = new AppSettings
        {
            CustomParticleColor = "#80FF3366",
            ApplicationStyle = new AppStyleSettings { BorderColor = "#FF223344", TextColor = "#FFEEDDCC", MutedTextColor = "#FF998877", GlowStrength = 40 },
            DriftGaugeEnabled = true,
            DriftGaugeScale = 1.4,
            DriftGaugeDarkMode = true,
            DriftGaugeBackgroundEnabled = true,
            DriftGaugeBackgroundOpacity = .7
        };
        var target = new AppSettings { ApplicationStyle = new AppStyleSettings { GlowStrength = 90 } };

        HudPreset.Capture(source, "Night").ApplyTo(target);

        Assert.Equal(ColorCustomization.NormalizeParticle(source.CustomParticleColor), target.CustomParticleColor);
        Assert.Equal("#FF223344", target.ApplicationStyle.BorderColor);
        Assert.Equal("#FFEEDDCC", target.ApplicationStyle.TextColor);
        Assert.Equal("#FF998877", target.ApplicationStyle.MutedTextColor);
        // Profiles carry the app's colors, not its other style settings.
        Assert.Equal(90, target.ApplicationStyle.GlowStrength);
        Assert.True(target.DriftGaugeEnabled);
        Assert.Equal(1.4, target.DriftGaugeScale);
        Assert.True(target.DriftGaugeDarkMode);
        Assert.True(target.DriftGaugeBackgroundEnabled);
        Assert.Equal(.7, target.DriftGaugeBackgroundOpacity);
    }

    [Fact]
    public void ProfilesSavedBefore251LeaveTheirNewerSettingsAlone()
    {
        var saved = HudPreset.Capture(new AppSettings(), "Older");
        var json = JsonSerializer.Serialize(saved);
        foreach (var name in new[] { "Revision", "CustomParticleColor", "AppBorderColor", "AppTextColor", "AppMutedTextColor",
                     "DriftGaugeEnabled", "DriftGaugeScale", "DriftGaugeDarkMode", "DriftGaugeBackgroundEnabled", "DriftGaugeBackgroundOpacity" })
        {
            using var document = JsonDocument.Parse(json);
            var fields = document.RootElement.EnumerateObject().Where(property => property.Name != name)
                .ToDictionary(property => property.Name, property => property.Value.Clone());
            json = JsonSerializer.Serialize(fields);
        }
        var older = JsonSerializer.Deserialize<HudPreset>(json)!;
        var target = new AppSettings
        {
            CustomParticleColor = "#80FF3366",
            ApplicationStyle = new AppStyleSettings { TextColor = "#FFEEDDCC" },
            DriftGaugeEnabled = true,
            DriftGaugeDarkMode = true,
            LapTimingMode = LapTimingMode.TimeAttack,
            LapDeltaEnabled = true,
            LapMapEnabled = true,
            LapDeltaAheadColor = "#FF00FF00"
        };

        older.ApplyTo(target);

        Assert.Equal(LapTimingMode.TimeAttack, target.LapTimingMode);
        Assert.True(target.LapDeltaEnabled);
        Assert.True(target.LapMapEnabled);
        Assert.Equal("#FF00FF00", target.LapDeltaAheadColor);
        Assert.Equal("#80FF3366", target.CustomParticleColor);
        Assert.Equal("#FFEEDDCC", target.ApplicationStyle.TextColor);
        Assert.True(target.DriftGaugeEnabled);
        Assert.True(target.DriftGaugeDarkMode);
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
