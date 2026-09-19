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
            PowerTorqueSmoothingMilliseconds = 850,
            PowerTorqueShowNegative = true,
            PowerGaugeColorNumber = true,
            CustomPowerLowColor = "#FF102030",
            CustomTorqueHighColor = "#FF405060"
        };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source))!;
        restored.MigrateSettings();
        Assert.Equal(enabled, restored.PowerTorqueDriftMode);
        var profile = JsonSerializer.Deserialize<HudPreset>(JsonSerializer.Serialize(HudPreset.Capture(restored, "Output hold")))!;
        var target = new AppSettings { PowerTorqueDriftMode = !enabled };
        profile.ApplyTo(target);
        Assert.Equal(enabled, target.PowerTorqueDriftMode);
        Assert.Equal(850, target.PowerTorqueSmoothingMilliseconds);
        Assert.True(target.PowerTorqueShowNegative);
        Assert.True(target.PowerGaugeColorNumber);
        Assert.Equal("#FF102030", target.CustomPowerLowColor);
        Assert.Equal("#FF405060", target.CustomTorqueHighColor);
    }

    [Fact]
    public void ApplyingAnOlderProfileClearsAnExplicitDriftModeSelection()
    {
        var target = new AppSettings { PowerTorqueDriftMode = true };
        var profile = JsonSerializer.Deserialize<HudPreset>("""{"Name":"Existing"}""")!;
        profile.ApplyTo(target);
        Assert.False(target.PowerTorqueDriftMode);
    }
}
