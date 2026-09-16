using System.IO;
using System.Text.Json;
using Wisp.App;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class PowerTorqueGaugeSettingsTests
{
    [Theory]
    [InlineData("{\"PowerTorqueGaugeScale\":1.4}", 1.4, 1.4)]
    [InlineData("{\"PowerTorqueGaugeScale\":1.4,\"PowerGaugeScale\":1}", 1, 1.4)]
    [InlineData("{\"TorqueGaugeScale\":0.75,\"PowerTorqueGaugeScale\":1.4}", 1.4, 0.75)]
    [InlineData("{\"PowerGaugeScale\":1.2,\"TorqueGaugeScale\":1.6,\"PowerTorqueGaugeScale\":1.4}", 1.2, 1.6)]
    [InlineData("{}", 1, 1)]
    public void SettingsAndProfilesMigrateSharedSizeOnlyWhenIndividualSizeIsMissing(string json, double power, double torque)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        settings.MigrateSettings();
        var preset = JsonSerializer.Deserialize<HudPreset>(json)!;
        preset.Name = "Migrated sizes";
        Assert.True(preset.Normalize());
        Assert.Equal(power, settings.PowerGaugeScale);
        Assert.Equal(torque, settings.TorqueGaugeScale);
        Assert.Equal(power, preset.PowerGaugeScale);
        Assert.Equal(torque, preset.TorqueGaugeScale);
    }

    [Fact]
    public void IndependentSizesSurviveSettingsAndProfileRoundTrips()
    {
        var settings = new AppSettings
        {
            PowerTorqueGaugeScale = 1.35,
            PowerGaugeScale = 0.8,
            TorqueGaugeScale = 1.65,
            BoostGaugeScale = 1.1,
            TireTemperatureGaugeScale = 1.2
        };
        settings.MigrateSettings();
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.MigrateSettings();
        Assert.Equal(0.8, restored.PowerGaugeScale);
        Assert.Equal(1.65, restored.TorqueGaugeScale);
        var preset = JsonSerializer.Deserialize<HudPreset>(JsonSerializer.Serialize(HudPreset.Capture(restored, "Independent sizes")))!;
        var target = new AppSettings { PowerGaugeScale = 2, TorqueGaugeScale = 0.5 };
        preset.ApplyTo(target);
        Assert.Equal(0.8, target.PowerGaugeScale);
        Assert.Equal(1.65, target.TorqueGaugeScale);
        Assert.Equal(1.1, target.BoostGaugeScale);
        Assert.Equal(1.2, target.TireTemperatureGaugeScale);
    }

    [Theory]
    [InlineData(-1, 0.5)]
    [InlineData(3, 2)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void IndependentSizesNormalizeWithoutChangingTheOtherGauge(double input, double expected)
    {
        var settings = new AppSettings { PowerGaugeScale = input, TorqueGaugeScale = 1.6 };
        var preset = HudPreset.Capture(settings, "Normalized sizes");
        settings.MigrateSettings();
        preset.Normalize();
        Assert.Equal(expected, settings.PowerGaugeScale);
        Assert.Equal(expected, preset.PowerGaugeScale);
        Assert.Equal(1.6, settings.TorqueGaugeScale);
        Assert.Equal(1.6, preset.TorqueGaugeScale);
    }

    [Fact]
    public void OnlyAMissingSettingsFileStartsWithNativeAnalogueAndAllGauges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var service = new SettingsService(path);
        try
        {
            var fresh = service.Load();
            Assert.Equal(HudLayoutMode.Native, fresh.LayoutMode);
            Assert.Equal(NativeGaugeMode.Analogue, fresh.NativeGaugeMode);
            Assert.True(fresh.GForceEnabled);
            Assert.True(fresh.BoostGaugeEnabled);
            Assert.True(fresh.TireTemperatureGaugeEnabled);
            Assert.True(fresh.PowerGaugeEnabled);
            Assert.True(fresh.TorqueGaugeEnabled);
            Assert.True(fresh.DriftGaugeEnabled);
            Assert.True(fresh.PowerGaugeAttached);
            Assert.True(fresh.TorqueGaugeAttached);
            Assert.Equal(250, fresh.PowerTorqueSmoothingMilliseconds);
            Assert.False(fresh.PowerTorqueShowNegative);
            service.Save(fresh);
            var reopened = service.Load();
            Assert.True(reopened.PowerGaugeEnabled);
            Assert.Equal(NativeGaugeMode.Analogue, reopened.NativeGaugeMode);

            File.WriteAllText(path, """{"SettingsRevision":9,"LayoutMode":"Minimal","NativeGaugeMode":"Digital","BoostGaugeScale":1.35,"TireTemperatureGaugeScale":0.75,"GForceEnabled":false,"BoostGaugeEnabled":false,"TireTemperatureGaugeEnabled":false}""");
            var existing = service.Load();
            Assert.Equal(HudLayoutMode.Minimal, existing.LayoutMode);
            Assert.Equal(NativeGaugeMode.Digital, existing.NativeGaugeMode);
            Assert.False(existing.GForceEnabled);
            Assert.False(existing.BoostGaugeEnabled);
            Assert.False(existing.TireTemperatureGaugeEnabled);
            Assert.False(existing.PowerGaugeEnabled);
            Assert.False(existing.TorqueGaugeEnabled);
            Assert.False(existing.DriftGaugeEnabled);
            Assert.Equal(1.35, existing.BoostGaugeScale);
            Assert.Equal(.75, existing.TireTemperatureGaugeScale);

            File.WriteAllText(path, "{}");
            var sparse = service.Load();
            Assert.Equal(HudLayoutMode.Minimal, sparse.LayoutMode);
            Assert.Equal(NativeGaugeMode.Digital, sparse.NativeGaugeMode);
            Assert.False(sparse.PowerGaugeEnabled);
            Assert.False(sparse.TorqueGaugeEnabled);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2000, 1500)]
    [InlineData(double.NaN, 250)]
    [InlineData(double.PositiveInfinity, 250)]
    public void SmoothingNormalizesForSettingsAndProfiles(double value, double expected)
    {
        var settings = new AppSettings { PowerTorqueSmoothingMilliseconds = value };
        var profile = HudPreset.Capture(settings, "Smoothing");
        settings.MigrateSettings();
        profile.Normalize();
        Assert.Equal(expected, settings.PowerTorqueSmoothingMilliseconds);
        Assert.Equal(expected, profile.PowerTorqueSmoothingMilliseconds);
    }

    [Fact]
    public void NewSmoothingDefaultDoesNotReplaceExplicitSavedSettingsOrProfiles()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"PowerTorqueSmoothingMilliseconds":500}""")!;
        settings.MigrateSettings();
        Assert.Equal(500, settings.PowerTorqueSmoothingMilliseconds);
        var profile = JsonSerializer.Deserialize<HudPreset>("""{"Name":"Saved","PowerTorqueSmoothingMilliseconds":950}""")!;
        profile.ApplyTo(settings);
        Assert.Equal(950, settings.PowerTorqueSmoothingMilliseconds);
        Assert.Equal(250, new AppSettings().PowerTorqueSmoothingMilliseconds);
        Assert.Equal(250, new HudPreset().PowerTorqueSmoothingMilliseconds);
    }

    [Fact]
    public void ProfileRoundTripPreservesSeparateAttachmentsOutputOptionsAndColorsWithoutPlacements()
    {
        var source = new AppSettings
        {
            PowerGaugeAttached = false,
            TorqueGaugeAttached = true,
            PowerTorqueSmoothingMilliseconds = 850,
            PowerTorqueShowNegative = true,
            PowerGaugeColorNumber = true,
            TorqueGaugeColorNumber = false,
            CustomPowerLowColor = "#FF102030",
            CustomPowerMidColor = "#FF405060",
            CustomPowerHighColor = "#FF708090",
            CustomTorqueLowColor = "#FF203040",
            CustomTorqueMidColor = "#FF506070",
            CustomTorqueHighColor = "#FF8090A0",
            PowerGaugePlacements = new() { ["power-display"] = new(10, 20, 1, 1) }
        };
        var serialized = JsonSerializer.Serialize(HudPreset.Capture(source, "Output options"));
        var preset = JsonSerializer.Deserialize<HudPreset>(serialized)!;
        var target = new AppSettings
        {
            PowerGaugePlacements = new() { ["existing-display"] = new(30, 40, 1, 1) },
            LastPowerGaugePlacementKey = "existing-display"
        };
        preset.ApplyTo(target);
        Assert.False(target.PowerGaugeAttached);
        Assert.True(target.TorqueGaugeAttached);
        Assert.Equal(850, target.PowerTorqueSmoothingMilliseconds);
        Assert.True(target.PowerTorqueShowNegative);
        Assert.True(target.PowerGaugeColorNumber);
        Assert.False(target.TorqueGaugeColorNumber);
        Assert.Equal(source.CustomPowerLowColor, target.CustomPowerLowColor);
        Assert.Equal(source.CustomPowerMidColor, target.CustomPowerMidColor);
        Assert.Equal(source.CustomPowerHighColor, target.CustomPowerHighColor);
        Assert.Equal(source.CustomTorqueLowColor, target.CustomTorqueLowColor);
        Assert.Equal(source.CustomTorqueMidColor, target.CustomTorqueMidColor);
        Assert.Equal(source.CustomTorqueHighColor, target.CustomTorqueHighColor);
        Assert.Equal("existing-display", target.LastPowerGaugePlacementKey);
        Assert.Equal(30, Assert.Single(target.PowerGaugePlacements).Value.Left);
        Assert.DoesNotContain("Placements", serialized);
    }

    [Fact]
    public void DetachedPlacementsNormalizeAndRoundTripIndependently()
    {
        var source = new AppSettings
        {
            PowerGaugePlacements = new() { ["power-display"] = new(10, 20, 1.25, 1.25), ["invalid"] = new(double.NaN, 0, 1, 1) },
            TorqueGaugePlacements = new() { ["torque-display"] = new(30, 40, 1.5, 1.5) },
            LastPowerGaugePlacementKey = "invalid",
            LastTorqueGaugePlacementKey = "torque-display"
        };
        source.MigrateSettings();
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source))!;
        restored.MigrateSettings();
        Assert.Null(restored.LastPowerGaugePlacementKey);
        Assert.Equal("torque-display", restored.LastTorqueGaugePlacementKey);
        Assert.Equal(10, Assert.Single(restored.PowerGaugePlacements).Value.Left);
        Assert.Equal(30, Assert.Single(restored.TorqueGaugePlacements).Value.Left);
    }

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
        Assert.Equal(1.35, target.PowerGaugeScale);
        Assert.Equal(1.35, target.TorqueGaugeScale);
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
        var target = new AppSettings
        {
            PowerGaugeEnabled = true,
            TorqueGaugeEnabled = true,
            PowerGaugeMaximum = 4500,
            PowerGaugeScale = 1.5,
            TorqueGaugeScale = 1.7
        };
        preset.ApplyTo(target);
        Assert.False(target.PowerGaugeEnabled);
        Assert.False(target.TorqueGaugeEnabled);
        Assert.Equal(1000, target.PowerGaugeMaximum);
        Assert.Equal(1200, target.TorqueGaugeMaximumNm);
        Assert.Equal(1, target.PowerTorqueGaugeScale);
        Assert.Equal(1, target.PowerGaugeScale);
        Assert.Equal(1, target.TorqueGaugeScale);
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
