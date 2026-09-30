using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HudProfileCompletenessTests
{
    private static readonly string[] SeparateSettings =
    [
        "SettingsRevision", "UdpPort", "AggregationMode", "StartWithWindows", "StartWithForza", "StartMinimizedWithForza",
        "BackgroundParticlesEnabled", "AnimatedBackground", "AutomaticApplicationUpdateChecks",
        "LastApplicationUpdateCheckUtc", "CpuRenderingEnabled", "DebugLoggingEnabled", "DebugLoggingExpiresAtUtc",
        "ApplicationStyle", "HudPresets", "SidebarCollapsed", "UseLegacyInterface", "CompletedFeatureTourId",
        "DismissedWhatsNewId", "ResizableDashboardDisplay", "RunWorkspace", "RunStatisticsView", "Clips",
        "AutoMinimizeOnTelemetry", "HasCompletedSetup", "SetupCompletion", "Calibrations",
        "LapReviewBenchmarkRunId", "LapReviewBenchmarkLapNumber", "LapReviewBenchmarkTimingMode", "LapReviewBenchmarkSampleIndex",
        "LastOverlayPlacementKey", "LastGForcePlacementKey", "LastBoostGaugePlacementKey",
        "LastTireTemperatureGaugePlacementKey", "LastDriftGaugePlacementKey", "LastPowerGaugePlacementKey",
        "LastTorqueGaugePlacementKey", "LastLapDeltaPlacementKey", "LastLapMapPlacementKey",
        "LegacyOverlayScale", "LegacyGForceScale"
    ];

    private static PropertyInfo[] ProfileSettings => typeof(AppSettings).GetProperties()
        .Where(property => property.SetMethod?.IsPublic == true && !SeparateSettings.Contains(property.Name))
        .ToArray();

    [Fact]
    public void EverySettingsFieldIsExplicitlyProfileOwnedOrKeptSeparate()
    {
        foreach (var property in ProfileSettings)
        {
            var profileProperty = typeof(HudPreset).GetProperty(property.Name);
            Assert.NotNull(profileProperty);
            Assert.Equal(property.PropertyType, profileProperty.PropertyType);
        }
        Assert.All(SeparateSettings, name => Assert.NotNull(typeof(AppSettings).GetProperty(name)));
        // App colors keep their existing profile behavior; app chrome geometry does not.
        Assert.NotNull(typeof(HudPreset).GetProperty(nameof(HudPreset.AppBorderColor)));
        Assert.DoesNotContain(ProfileSettings, property => property.Name == nameof(AppSettings.Calibrations));
    }

    [Fact]
    public void EveryProfileOwnedSettingIsCapturedSerializedAndApplied()
    {
        var source = new AppSettings();
        foreach (var property in ProfileSettings) SetDifferentValue(source, property);
        var saved = HudPreset.Capture(source, "Full driving setup");
        foreach (var property in ProfileSettings)
        {
            Assert.Equal(JsonSerializer.Serialize(property.GetValue(source), property.PropertyType),
                JsonSerializer.Serialize(typeof(HudPreset).GetProperty(property.Name)!.GetValue(saved), property.PropertyType));
        }
        var restored = JsonSerializer.Deserialize<HudPreset>(JsonSerializer.Serialize(saved))!;
        var target = new AppSettings();
        restored.ApplyTo(target);
        foreach (var property in ProfileSettings)
        {
            Assert.Equal(JsonSerializer.Serialize(typeof(HudPreset).GetProperty(property.Name)!.GetValue(restored), property.PropertyType),
                JsonSerializer.Serialize(property.GetValue(target), property.PropertyType));
        }
    }

    [Fact]
    public void AllNinePlacementCollectionsAreIndependentInBothDirections()
    {
        var source = new AppSettings();
        var maps = ProfileSettings.Where(property => property.PropertyType == typeof(Dictionary<string, OverlayPlacement>)).ToArray();
        Assert.Equal(9, maps.Length);
        foreach (var property in maps) SetDifferentValue(source, property);
        var preset = HudPreset.Capture(source, "Arrangement");
        var target = new AppSettings();
        preset.ApplyTo(target);
        foreach (var property in maps)
        {
            var original = (Dictionary<string, OverlayPlacement>)property.GetValue(source)!;
            var stored = (Dictionary<string, OverlayPlacement>)typeof(HudPreset).GetProperty(property.Name)!.GetValue(preset)!;
            var applied = (Dictionary<string, OverlayPlacement>)property.GetValue(target)!;
            original["display"].Left = 777;
            applied["display"].Top = 888;
            Assert.Equal(120, stored["display"].Left);
            Assert.Equal(180, stored["display"].Top);
            Assert.NotSame(original, stored);
            Assert.NotSame(stored, applied);
            Assert.NotSame(stored["display"], applied["display"]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OlderProfilesPreserveNewDrivingOptionsAndAllPlacements(int revision)
    {
        var target = new AppSettings();
        foreach (var property in ProfileSettings) SetDifferentValue(target, property);
        var before = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(target))!;
        var preset = JsonSerializer.Deserialize<HudPreset>($$"""{"Name":"Older profile","Revision":{{revision}}}""")!;
        preset.ApplyTo(target);
        foreach (var name in new[] { "SpeedSource", "AggregationMode", "Smoothing", "OverlayLocked", "GameAwareVisibility",
                     "DriftGaugeGuidanceMode", "DriftTargetDegrees", "DriftToleranceDegrees",
                     "OverlayHotkeyEnabled", "OverlayHotkeyModifiers", "OverlayHotkeyKey",
                     "RecordingShortcutEnabled", "RecordingShortcutModifiers", "RecordingShortcutKey",
                     "MarkerShortcutEnabled", "MarkerShortcutModifiers", "MarkerShortcutKey",
                     "RecordingCountdownSeconds", "RecordingStopAfterSeconds", "RunPurpose", "LapReviewRecordingEnabled",
                     "PowerTorqueGaugeRanges", "Placements", "GForcePlacements", "BoostGaugePlacements",
                     "TireTemperatureGaugePlacements", "DriftGaugePlacements", "PowerGaugePlacements", "TorqueGaugePlacements",
                     "LapDeltaPlacements", "LapMapPlacements" })
        {
            var property = typeof(AppSettings).GetProperty(name)!;
            Assert.Equal(JsonSerializer.Serialize(property.GetValue(before)), JsonSerializer.Serialize(property.GetValue(target)));
        }
    }

    [Fact]
    public void MissingPlacementCollectionsDoNotEraseExistingArrangements()
    {
        var target = new AppSettings { Placements = new() { ["current"] = new(50, 70, 1, 1) } };
        var preset = JsonSerializer.Deserialize<HudPreset>("""{"Name":"No placements","Revision":2}""")!;
        preset.ApplyTo(target);
        Assert.Equal(50, target.Placements["current"].Left);
    }

    [Fact]
    public void InvalidDrivingOptionsAndPlacementsAreNormalizedWithoutTouchingTheSource()
    {
        var preset = new HudPreset
        {
            Name = "Invalid values",
            Revision = 2,
            Smoothing = double.NaN,
            DriftTargetDegrees = 500,
            DriftToleranceDegrees = -5,
            RecordingCountdownSeconds = -1,
            RecordingStopAfterSeconds = int.MaxValue,
            DriftGaugeGuidanceMode = (DriftGaugeGuidanceMode)999,
            Placements = new() { ["bad"] = new(double.PositiveInfinity, 0, 1, 1), ["good"] = new(0, 0, 99, .1) }
        };
        Assert.True(preset.Normalize());
        Assert.Equal(0, preset.Smoothing);
        Assert.Equal(75, preset.DriftTargetDegrees);
        Assert.Equal(2, preset.DriftToleranceDegrees);
        Assert.Equal(0, preset.RecordingCountdownSeconds);
        Assert.Equal(0, preset.RecordingStopAfterSeconds);
        Assert.Equal(DriftGaugeGuidanceMode.DriftZoneAngleBonus, preset.DriftGaugeGuidanceMode);
        Assert.Single(preset.Placements!);
        Assert.Equal(2, preset.Placements!["good"].WidthScale);
        Assert.Equal(.5, preset.Placements["good"].HeightScale);
    }

    [Fact]
    public void StaleAbsoluteCoordinatesStayInsideTheCurrentDisplayBeforeRestore()
    {
        var result = AppController.ClampProfilePosition(new(5000, -900, 1, 1), new Rect(-1920, 0, 1920, 1080), new Size(400, 240));
        Assert.Equal(-400, result.X);
        Assert.Equal(0, result.Y);
    }

    private static void SetDifferentValue(AppSettings settings, PropertyInfo property)
    {
        object value;
        if (property.PropertyType == typeof(bool)) value = !(bool)property.GetValue(settings)!;
        else if (property.PropertyType == typeof(Key)) value = Key.J;
        else if (property.PropertyType.IsEnum) value = Enum.GetValues(property.PropertyType).GetValue(
            Enum.GetValues(property.PropertyType).Length - 1)!;
        else if (property.PropertyType == typeof(double))
            value = property.Name switch
            {
                "Smoothing" or "OverlayOpacity" or "DriftGaugeBackgroundOpacity" => .75,
                "DriftTargetDegrees" => 55d,
                "DriftToleranceDegrees" => 12d,
                "PowerGaugeMaximum" or "TorqueGaugeMaximumNm" => 1750d,
                "PowerTorqueSmoothingMilliseconds" => 700d,
                _ => 1.35
            };
        else if (property.PropertyType == typeof(int))
            value = property.Name == "RecordingCountdownSeconds" ? 5 : 60;
        else if (property.PropertyType == typeof(string))
        {
            if (!property.Name.Contains("Color", StringComparison.Ordinal) || property.Name == "ColorTheme") return;
            value = "#FF123456";
        }
        else if (property.PropertyType == typeof(Dictionary<string, OverlayPlacement>))
            value = new Dictionary<string, OverlayPlacement> { ["display"] = new(120, 180, 1.35, 1.35) };
        else if (property.PropertyType == typeof(Dictionary<int, PowerTorqueGaugeRange>))
            value = new Dictionary<int, PowerTorqueGaugeRange> { [42] = new(1250, 1500) };
        else throw new InvalidOperationException($"Add a test value for {property.Name}.");
        property.SetValue(settings, value);
    }
}
