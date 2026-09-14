using System.IO;
using System.Text.Json;
using Wisp.App;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class PowerTorqueGaugeSettingsTests
{
    [Fact]
    public void ExistingSettingsKeepBothGaugesOffWithoutChangingTheSavedHud()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"SettingsRevision":9,"OverlayOpacity":0.7,"NativeGaugeMode":1,"CpuRenderingEnabled":true}""")!;
        settings.MigrateSettings();
        Assert.False(settings.PowerGaugeEnabled);
        Assert.False(settings.TorqueGaugeEnabled);
        Assert.Equal(1, settings.PowerTorqueGaugeScale);
        Assert.Equal(1000, settings.PowerGaugeMaximum);
        Assert.Equal(1200, settings.TorqueGaugeMaximumNm);
        Assert.Equal(.7, settings.OverlayOpacity);
        Assert.Equal(NativeGaugeMode.Analogue, settings.NativeGaugeMode);
        Assert.True(settings.CpuRenderingEnabled);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProfileRoundTripRestoresIndependentTogglesAndPhysicalScale(bool power, bool torque)
    {
        var source = new AppSettings
        {
            PowerGaugeEnabled = power,
            TorqueGaugeEnabled = torque,
            PowerTorqueGaugeScale = 1.35,
            PowerGaugeMaximum = 1500,
            TorqueGaugeMaximumNm = 2100,
            TorqueUnit = TorqueUnit.PoundFeet
        };
        var preset = JsonSerializer.Deserialize<HudPreset>(JsonSerializer.Serialize(HudPreset.Capture(source, "Power test")))!;
        var target = new AppSettings { UdpPort = 5501, CpuRenderingEnabled = true };
        preset.ApplyTo(target);
        Assert.Equal(power, target.PowerGaugeEnabled);
        Assert.Equal(torque, target.TorqueGaugeEnabled);
        Assert.Equal(1.35, target.PowerTorqueGaugeScale);
        Assert.Equal(1500, target.PowerGaugeMaximum);
        Assert.Equal(2100, target.TorqueGaugeMaximumNm);
        Assert.Equal(TorqueUnit.PoundFeet, target.TorqueUnit);
        Assert.Equal(5501, target.UdpPort);
        Assert.True(target.CpuRenderingEnabled);
    }

    [Fact]
    public void OldProfilesRestoreTheOptInDefaults()
    {
        var preset = JsonSerializer.Deserialize<HudPreset>("""{"Name":"Existing"}""")!;
        var target = new AppSettings { PowerGaugeEnabled = true, TorqueGaugeEnabled = true, PowerGaugeMaximum = 4500 };
        preset.ApplyTo(target);
        Assert.False(target.PowerGaugeEnabled);
        Assert.False(target.TorqueGaugeEnabled);
        Assert.Equal(1000, target.PowerGaugeMaximum);
        Assert.Equal(1200, target.TorqueGaugeMaximumNm);
        Assert.Equal(1, target.PowerTorqueGaugeScale);
    }

    [Theory]
    [InlineData(-1, .5, 100, 100)]
    [InlineData(20000, 2, 5000, 10000)]
    [InlineData(double.NaN, 1, 1000, 1200)]
    [InlineData(double.PositiveInfinity, 1, 1000, 1200)]
    [InlineData(double.NegativeInfinity, 1, 1000, 1200)]
    public void SettingsAndProfilesNormalizeInvalidScalesConsistently(double input, double scale, double power, double torque)
    {
        var settings = new AppSettings { PowerTorqueGaugeScale = input, PowerGaugeMaximum = input, TorqueGaugeMaximumNm = input };
        var preset = HudPreset.Capture(settings, "Invalid scales");
        settings.MigrateSettings();
        Assert.True(preset.Normalize());
        Assert.Equal(scale, settings.PowerTorqueGaugeScale);
        Assert.Equal(power, settings.PowerGaugeMaximum);
        Assert.Equal(torque, settings.TorqueGaugeMaximumNm);
        Assert.Equal(scale, preset.PowerTorqueGaugeScale);
        Assert.Equal(power, preset.PowerGaugeMaximum);
        Assert.Equal(torque, preset.TorqueGaugeMaximumNm);
        Assert.False(settings.PowerGaugeEnabled);
        Assert.False(settings.TorqueGaugeEnabled);
    }

    [Fact]
    public void SaveNormalizesBeforeJsonSerializationAndKeepsProfileValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var service = new SettingsService(Path.Combine(directory, "settings.json"));
        try
        {
            var settings = new AppSettings
            {
                PowerGaugeEnabled = true,
                TorqueGaugeEnabled = false,
                PowerTorqueGaugeScale = double.NaN,
                PowerGaugeMaximum = double.PositiveInfinity,
                TorqueGaugeMaximumNm = 2400
            };
            settings.MigrateSettings();
            settings.HudPresets.Add(HudPreset.Capture(settings, "Saved output"));
            settings.PowerGaugeMaximum = double.NaN;
            service.Save(settings);
            var restored = service.Load();
            Assert.True(restored.PowerGaugeEnabled);
            Assert.False(restored.TorqueGaugeEnabled);
            Assert.Equal(1, restored.PowerTorqueGaugeScale);
            Assert.Equal(1000, restored.PowerGaugeMaximum);
            Assert.Equal(2400, restored.TorqueGaugeMaximumNm);
            var profile = Assert.Single(restored.HudPresets);
            Assert.True(profile.PowerGaugeEnabled);
            Assert.False(profile.TorqueGaugeEnabled);
            Assert.Equal(2400, profile.TorqueGaugeMaximumNm);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
