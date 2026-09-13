using System.Text.Json;
using System.Text.Json.Serialization;
using Wisp.App;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class DriftGaugeSettingsTests
{
    [Fact]
    public void ExistingPreferencesKeepTheGaugeOffAndTheirSavedHudUntouched()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"SettingsRevision":9,"OverlayOpacity":0.7,"NativeGaugeMode":1,"CpuRenderingEnabled":true}""")!;
        settings.MigrateSettings();
        Assert.False(settings.DriftGaugeEnabled);
        Assert.False(settings.DriftGaugeDarkMode);
        Assert.False(settings.DriftGaugeBackgroundEnabled);
        Assert.Equal(.5, settings.DriftGaugeBackgroundOpacity);
        Assert.Equal(DriftGaugeGuidanceMode.DriftZoneAngleBonus, settings.DriftGaugeGuidanceMode);
        Assert.Equal(40, settings.DriftTargetDegrees);
        Assert.Equal(10, settings.DriftToleranceDegrees);
        Assert.Equal(1, settings.DriftGaugeScale);
        Assert.Empty(settings.DriftGaugePlacements);
        Assert.True(settings.CpuRenderingEnabled);
        Assert.Equal(NativeGaugeMode.Analogue, settings.NativeGaugeMode);
        Assert.Equal(.7, settings.OverlayOpacity);
        Assert.Equal(9, settings.SettingsRevision);
    }

    [Fact]
    public void BlackBackingRoundTripsIndependentlyAndKeepsItsOpacityWhenDisabled()
    {
        var settings = new AppSettings { DriftGaugeBackgroundEnabled = true, DriftGaugeBackgroundOpacity = .75, DriftGaugeDarkMode = true };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.MigrateSettings();
        Assert.True(restored.DriftGaugeBackgroundEnabled);
        Assert.Equal(.75, restored.DriftGaugeBackgroundOpacity);
        Assert.True(restored.DriftGaugeDarkMode);
        Assert.False(restored.DriftGaugeEnabled);
        restored.DriftGaugeBackgroundEnabled = false;
        restored.NormalizeDriftGaugeSettings();
        Assert.Equal(.75, restored.DriftGaugeBackgroundOpacity);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    [InlineData(double.NaN, .5)]
    [InlineData(double.PositiveInfinity, .5)]
    public void BlackBackingOpacityNormalizesWithoutEnablingTheBacking(double input, double expected)
    {
        var settings = new AppSettings { DriftGaugeBackgroundOpacity = input };
        settings.NormalizeDriftGaugeSettings();
        Assert.Equal(expected, settings.DriftGaugeBackgroundOpacity);
        Assert.False(settings.DriftGaugeBackgroundEnabled);
    }

    [Fact]
    public void MissingGuidanceModeUsesZoneBonusWithoutDiscardingTheSavedCustomReference()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"SettingsRevision":9,"DriftGaugeEnabled":true,"DriftGaugeDarkMode":true,"DriftTargetDegrees":55,"DriftToleranceDegrees":6,"DriftGaugeScale":1.25}""")!;
        settings.MigrateSettings();
        Assert.Equal(DriftGaugeGuidanceMode.DriftZoneAngleBonus, settings.DriftGaugeGuidanceMode);
        Assert.True(settings.DriftGaugeEnabled);
        Assert.True(settings.DriftGaugeDarkMode);
        Assert.Equal(55, settings.DriftTargetDegrees);
        Assert.Equal(6, settings.DriftToleranceDegrees);
        Assert.Equal(1.25, settings.DriftGaugeScale);
    }

    [Theory]
    [InlineData(DriftGaugeGuidanceMode.CustomTarget)]
    [InlineData(DriftGaugeGuidanceMode.DriftZoneAngleBonus)]
    public void ExplicitGuidanceModeRoundTripsWithoutChangingCustomValuesOrPlacement(DriftGaugeGuidanceMode mode)
    {
        var settings = new AppSettings
        {
            SettingsRevision = 9,
            DriftGaugeGuidanceMode = mode,
            DriftGaugeEnabled = true,
            DriftGaugeDarkMode = true,
            DriftTargetDegrees = 52,
            DriftToleranceDegrees = 6,
            DriftGaugeScale = 1.25,
            DriftGaugePlacements = new() { ["display"] = new(12, 34, 1.25, 1.25) },
            LastDriftGaugePlacementKey = "display"
        };
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var serialized = JsonSerializer.Serialize(settings, options);
        using var document = JsonDocument.Parse(serialized);
        Assert.Equal(mode.ToString(), document.RootElement.GetProperty(nameof(AppSettings.DriftGaugeGuidanceMode)).GetString());
        var restored = JsonSerializer.Deserialize<AppSettings>(serialized, options)!;
        restored.MigrateSettings();
        Assert.Equal(mode, restored.DriftGaugeGuidanceMode);
        Assert.True(restored.DriftGaugeEnabled);
        Assert.True(restored.DriftGaugeDarkMode);
        Assert.Equal(52, restored.DriftTargetDegrees);
        Assert.Equal(6, restored.DriftToleranceDegrees);
        Assert.Equal(1.25, restored.DriftGaugeScale);
        Assert.Equal(12, restored.DriftGaugePlacements["display"].Left);
        Assert.Equal(34, restored.DriftGaugePlacements["display"].Top);
        Assert.Equal("display", restored.LastDriftGaugePlacementKey);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void InvalidGuidanceModeFallsBackWithoutResettingCustomSettings(int invalidMode)
    {
        var settings = new AppSettings
        {
            DriftGaugeGuidanceMode = (DriftGaugeGuidanceMode)invalidMode,
            DriftTargetDegrees = 55,
            DriftToleranceDegrees = 6,
            DriftGaugeDarkMode = true
        };
        settings.NormalizeDriftGaugeSettings();
        Assert.Equal(DriftGaugeGuidanceMode.DriftZoneAngleBonus, settings.DriftGaugeGuidanceMode);
        Assert.Equal(55, settings.DriftTargetDegrees);
        Assert.Equal(6, settings.DriftToleranceDegrees);
        Assert.True(settings.DriftGaugeDarkMode);
        Assert.False(settings.DriftGaugeEnabled);
    }

    [Fact]
    public void DarkModeRoundTripsIndependentlyOfVisibilityAndPlacement()
    {
        var settings = new AppSettings
        {
            DriftGaugeDarkMode = true,
            DriftTargetDegrees = 50,
            DriftToleranceDegrees = 7,
            DriftGaugeScale = 1.5,
            DriftGaugePlacements = new() { ["display"] = new(12, 34, 1.5, 1.5) },
            LastDriftGaugePlacementKey = "display"
        };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.MigrateSettings();
        Assert.True(restored.DriftGaugeDarkMode); Assert.False(restored.DriftGaugeEnabled);
        Assert.Equal(50, restored.DriftTargetDegrees); Assert.Equal(7, restored.DriftToleranceDegrees);
        Assert.Equal(1.5, restored.DriftGaugeScale);
        Assert.Equal(12, restored.DriftGaugePlacements["display"].Left);
        Assert.Equal(34, restored.DriftGaugePlacements["display"].Top);
        Assert.Equal("display", restored.LastDriftGaugePlacementKey);
        restored.DriftGaugeDarkMode = false;
        var disabled = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(restored))!;
        disabled.MigrateSettings();
        Assert.False(disabled.DriftGaugeDarkMode);
        Assert.Equal(12, disabled.DriftGaugePlacements["display"].Left);
        Assert.Equal(34, disabled.DriftGaugePlacements["display"].Top);
    }

    [Fact]
    public void MalformedValuesAndPlacementsNormalizeWithoutTurningTheGaugeOn()
    {
        var settings = new AppSettings
        {
            DriftTargetDegrees = double.NaN,
            DriftToleranceDegrees = double.PositiveInfinity,
            DriftGaugeScale = double.NegativeInfinity,
            DriftGaugePlacements = new() { ["valid"] = new(10, 20, 1, 1), ["broken"] = new(double.NaN, 0, 1, 1), ["null"] = null! },
            LastDriftGaugePlacementKey = "broken"
        };
        settings.NormalizeDriftGaugeSettings();
        Assert.False(settings.DriftGaugeEnabled);
        Assert.Equal(40, settings.DriftTargetDegrees);
        Assert.Equal(10, settings.DriftToleranceDegrees);
        Assert.Equal(1, settings.DriftGaugeScale);
        Assert.Equal("valid", Assert.Single(settings.DriftGaugePlacements).Key);
        Assert.Null(settings.LastDriftGaugePlacementKey);
        settings.DriftTargetDegrees = 100; settings.DriftToleranceDegrees = -1; settings.DriftGaugeScale = 10;
        settings.NormalizeDriftGaugeSettings();
        Assert.Equal(75, settings.DriftTargetDegrees); Assert.Equal(2, settings.DriftToleranceDegrees); Assert.Equal(2, settings.DriftGaugeScale);
        settings.DriftTargetDegrees = 0; settings.DriftToleranceDegrees = 100; settings.DriftGaugeScale = 0;
        settings.NormalizeDriftGaugeSettings();
        Assert.Equal(10, settings.DriftTargetDegrees); Assert.Equal(15, settings.DriftToleranceDegrees); Assert.Equal(.5, settings.DriftGaugeScale);
    }

    [Fact]
    public void PlacementHistoryIsBoundedAndValidPreferencesRoundTrip()
    {
        var settings = new AppSettings { DriftGaugeEnabled = true, DriftTargetDegrees = 45, DriftToleranceDegrees = 8, DriftGaugeScale = 1.25 };
        for (var index = 0; index < 40; index++) settings.DriftGaugePlacements["display-" + index] = new(index, index, 1.25, 1.25);
        settings.LastDriftGaugePlacementKey = "display-39";
        settings.NormalizeDriftGaugeSettings();
        Assert.Equal(32, settings.DriftGaugePlacements.Count);
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.NormalizeDriftGaugeSettings();
        Assert.True(restored.DriftGaugeEnabled);
        Assert.Equal(45, restored.DriftTargetDegrees);
        Assert.Equal(8, restored.DriftToleranceDegrees);
        Assert.Equal(1.25, restored.DriftGaugeScale);
        Assert.Equal("display-39", restored.LastDriftGaugePlacementKey);
        Assert.Equal(39, restored.DriftGaugePlacements["display-39"].Left);
    }
}
