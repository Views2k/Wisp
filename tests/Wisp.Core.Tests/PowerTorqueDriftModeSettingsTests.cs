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
        Assert.Equal("#FF123456", restored.PowerTorqueDriftFlashColor);
        var profile = JsonSerializer.Deserialize<HudPreset>(JsonSerializer.Serialize(HudPreset.Capture(restored, "Output hold")))!;
        var target = new AppSettings { PowerTorqueDriftMode = !enabled };
        profile.ApplyTo(target);
        Assert.Equal(enabled, target.PowerTorqueDriftMode);
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
        var target = new AppSettings { PowerTorqueDriftMode = true, PowerTorqueDriftFlashColor = "#FF123456" };
        var profile = JsonSerializer.Deserialize<HudPreset>("""{"Name":"Existing"}""")!;
        profile.ApplyTo(target);
        Assert.False(target.PowerTorqueDriftMode);
        Assert.Null(target.PowerTorqueDriftFlashColor);
        Assert.Equal("#FFFF0088", ColorCustomization.ToHex(ColorCustomization.ResolvePowerTorqueDriftFlash(target.PowerTorqueDriftFlashColor)));
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
