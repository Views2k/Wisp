using System.Text.Json;
using Wisp.App;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class PowerTorqueDriftModeSettingsTests
{
    [Fact]
    public void ExistingSettingsAndProfilesKeepDriftModeOff()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"PowerTorqueSmoothingMilliseconds":850,"PowerTorqueShowNegative":true}""")!;
        settings.MigrateSettings();
        var profile = JsonSerializer.Deserialize<HudPreset>("""{"Name":"Existing","PowerTorqueSmoothingMilliseconds":850,"PowerTorqueShowNegative":true}""")!;
        Assert.True(profile.Normalize());
        Assert.False(new AppSettings().PowerTorqueDriftMode);
        Assert.False(settings.PowerTorqueDriftMode);
        Assert.False(profile.PowerTorqueDriftMode);
        Assert.Equal(1.25, new AppSettings().PowerTorqueDriftFlashFrequencyHz);
        Assert.Equal(1.25, new HudPreset().PowerTorqueDriftFlashFrequencyHz);
        Assert.Equal(1.25, settings.PowerTorqueDriftFlashFrequencyHz);
        Assert.Equal(1.25, profile.PowerTorqueDriftFlashFrequencyHz);
        Assert.Null(settings.PowerTorqueDriftFlashColor);
        Assert.Null(profile.PowerTorqueDriftFlashColor);
        Assert.Equal("#FFFF0088", ColorCustomization.ToHex(ColorCustomization.ResolvePowerTorqueDriftFlash(settings.PowerTorqueDriftFlashColor)));
        Assert.Equal(850, settings.PowerTorqueSmoothingMilliseconds);
        Assert.True(settings.PowerTorqueShowNegative);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsAndProfileRoundTripsPreserveManualChoiceWithoutChangingOutputOptions(bool enabled)
    {
        var source = new AppSettings
        {
            PowerTorqueDriftMode = enabled,
            PowerTorqueDriftFlashFrequencyHz = 2.75,
            PowerTorqueDriftFlashColor = "#FF123456",
            PowerTorqueSmoothingMilliseconds = 850,
            PowerTorqueShowNegative = true,
            PowerGaugeColorNumber = true,
            CustomPowerLowColor = "#FF102030",
            CustomTorqueHighColor = "#FF405060"
        };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source))!;
        restored.MigrateSettings();
        Assert.Equal(enabled, restored.PowerTorqueDriftMode);
        Assert.Equal(2.75, restored.PowerTorqueDriftFlashFrequencyHz);
        Assert.Equal("#FF123456", restored.PowerTorqueDriftFlashColor);
        var profile = JsonSerializer.Deserialize<HudPreset>(JsonSerializer.Serialize(HudPreset.Capture(restored, "Output hold")))!;
        var target = new AppSettings { PowerTorqueDriftMode = !enabled };
        profile.ApplyTo(target);
        Assert.Equal(enabled, target.PowerTorqueDriftMode);
        Assert.Equal(2.75, target.PowerTorqueDriftFlashFrequencyHz);
        Assert.Equal("#FF123456", target.PowerTorqueDriftFlashColor);
        Assert.Equal(850, target.PowerTorqueSmoothingMilliseconds);
        Assert.True(target.PowerTorqueShowNegative);
        Assert.True(target.PowerGaugeColorNumber);
        Assert.Equal("#FF102030", target.CustomPowerLowColor);
        Assert.Equal("#FF405060", target.CustomTorqueHighColor);
    }

    [Fact]
    public void ApplyingAnOlderProfileClearsAnExplicitDriftModeSelection()
    {
        var target = new AppSettings { PowerTorqueDriftMode = true, PowerTorqueDriftFlashFrequencyHz = 3, PowerTorqueDriftFlashColor = "#FF123456" };
        var profile = JsonSerializer.Deserialize<HudPreset>("""{"Name":"Existing"}""")!;
        profile.ApplyTo(target);
        Assert.False(target.PowerTorqueDriftMode);
        Assert.Equal(1.25, target.PowerTorqueDriftFlashFrequencyHz);
        Assert.Null(target.PowerTorqueDriftFlashColor);
        Assert.Equal("#FFFF0088", ColorCustomization.ToHex(ColorCustomization.ResolvePowerTorqueDriftFlash(target.PowerTorqueDriftFlashColor)));
    }

    [Theory]
    [InlineData(double.NaN, 1.25)]
    [InlineData(double.PositiveInfinity, 1.25)]
    [InlineData(double.NegativeInfinity, 1.25)]
    [InlineData(-1, 0.5)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.25, 1.25)]
    [InlineData(2.75, 2.75)]
    [InlineData(3, 3)]
    [InlineData(12, 3)]
    public void FlashFrequencyNormalizesConsistentlyForSettingsAndProfiles(double value, double expected)
    {
        var settings = new AppSettings { PowerTorqueDriftFlashFrequencyHz = value };
        var profile = new HudPreset { Name = "Flash frequency", PowerTorqueDriftFlashFrequencyHz = value };
        settings.MigrateSettings();
        Assert.True(profile.Normalize());
        Assert.Equal(expected, settings.PowerTorqueDriftFlashFrequencyHz);
        Assert.Equal(expected, profile.PowerTorqueDriftFlashFrequencyHz);
    }

    [Theory]
    [InlineData("123456", "#FF123456")]
    [InlineData("#80123456", "#FF123456")]
    [InlineData("invalid", null)]
    public void FlashColorsNormalizeConsistentlyWithoutIntroducingOpacity(string value, string? expected)
    {
        var settings = new AppSettings { PowerTorqueDriftFlashColor = value };
        var profile = new HudPreset { Name = "Flash color", PowerTorqueDriftFlashColor = value };
        settings.MigrateSettings();
        Assert.True(profile.Normalize());
        Assert.Equal(expected, settings.PowerTorqueDriftFlashColor);
        Assert.Equal(expected, profile.PowerTorqueDriftFlashColor);
        Assert.Equal(expected ?? "#FFFF0088", ColorCustomization.ToHex(ColorCustomization.ResolvePowerTorqueDriftFlash(settings.PowerTorqueDriftFlashColor)));
        if (expected is null)
            Assert.Equal("#FFFF0088", ColorCustomization.ToHex(ColorCustomization.ResolvePowerTorqueDriftFlash(value)));
    }
}
