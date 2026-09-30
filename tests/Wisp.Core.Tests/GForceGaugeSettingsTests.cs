using System.Text.Json;
using Wisp.App;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class GForceGaugeSettingsTests
{
    [Theory]
    [InlineData("{}", 1, 1)]
    [InlineData("{\"GForceWidthScale\":1.35,\"GForceHeightScale\":0.85}", 1.35, 0.85)]
    [InlineData("{\"GForceScale\":1.4}", 1.4, 1.4)]
    public void NewSizeDefaultsToIdentityAndPreservesExistingShape(string json, double width, double height)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        settings.MigrateSettings();

        Assert.Equal(1, settings.GForceGaugeScale);
        Assert.Equal(width, settings.GForceWidthScale);
        Assert.Equal(height, settings.GForceHeightScale);
        Assert.Null(settings.LegacyGForceScale);
    }

    [Fact]
    public void LegacyShapeAndNewSizeAreDistinctJsonSettings()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            "{\"GForceScale\":1.4,\"GForceGaugeScale\":0.75}")!;
        settings.MigrateSettings();
        Assert.Equal(0.75, settings.GForceGaugeScale);
        Assert.Equal(1.4, settings.GForceWidthScale);
        Assert.Equal(1.4, settings.GForceHeightScale);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SizeAndShapeRoundTripThroughSettingsAndProfilesWithVersionedPlacements(int revision)
    {
        var source = new AppSettings
        {
            GForceGaugeScale = 1.6,
            GForceWidthScale = 1.35,
            GForceHeightScale = 0.85,
            GForcePlacements = new() { ["display"] = new(10, 20, 1.35, 0.85) }
        };
        source.MigrateSettings();
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source))!;
        restored.MigrateSettings();
        Assert.Equal(1.6, restored.GForceGaugeScale);
        Assert.Equal(1.35, restored.GForceWidthScale);
        Assert.Equal(0.85, restored.GForceHeightScale);
        Assert.Equal(1.35, restored.GForcePlacements["display"].WidthScale);
        Assert.Equal(0.85, restored.GForcePlacements["display"].HeightScale);

        var captured = HudPreset.Capture(restored, "G-force size");
        Assert.Equal(2, captured.Revision);
        Assert.NotSame(restored.GForcePlacements, captured.GForcePlacements);
        Assert.NotSame(restored.GForcePlacements["display"], captured.GForcePlacements!["display"]);
        captured.Revision = revision;
        var preset = JsonSerializer.Deserialize<HudPreset>(JsonSerializer.Serialize(captured))!;
        var existingPlacement = new OverlayPlacement(30, 40, 0.8, 1.2);
        var target = new AppSettings
        {
            GForcePlacements = new() { ["existing"] = existingPlacement },
            LastGForcePlacementKey = "existing"
        };
        preset.ApplyTo(target);
        Assert.Equal(1.6, target.GForceGaugeScale);
        Assert.Equal(1.35, target.GForceWidthScale);
        Assert.Equal(0.85, target.GForceHeightScale);
        if (revision == 1)
        {
            Assert.Equal("existing", Assert.Single(target.GForcePlacements).Key);
            Assert.Same(existingPlacement, target.GForcePlacements["existing"]);
            Assert.Equal(30, existingPlacement.Left);
            Assert.Equal(40, existingPlacement.Top);
        }
        else
        {
            var placement = Assert.Single(target.GForcePlacements);
            Assert.Equal("display", placement.Key);
            Assert.Equal(10, placement.Value.Left);
            Assert.Equal(20, placement.Value.Top);
            Assert.Equal(1.35, placement.Value.WidthScale);
            Assert.Equal(0.85, placement.Value.HeightScale);
            Assert.NotSame(preset.GForcePlacements, target.GForcePlacements);
            Assert.NotSame(preset.GForcePlacements!["display"], placement.Value);
            placement.Value.Left = 99;
            Assert.Equal(10, preset.GForcePlacements["display"].Left);
            Assert.Equal(10, restored.GForcePlacements["display"].Left);
        }
        Assert.Equal(0.8, existingPlacement.WidthScale);
        Assert.Equal(1.2, existingPlacement.HeightScale);
        Assert.Equal("existing", target.LastGForcePlacementKey);
    }

    [Fact]
    public void OlderProfileKeepsItsShapeWithoutMultiplyingItAgain()
    {
        var preset = JsonSerializer.Deserialize<HudPreset>(
            "{\"Name\":\"Old profile\",\"GForceWidthScale\":1.4,\"GForceHeightScale\":0.8}")!;
        var target = new AppSettings { GForceGaugeScale = 1.75 };
        preset.ApplyTo(target);
        Assert.Equal(1, target.GForceGaugeScale);
        Assert.Equal(1.4, target.GForceWidthScale);
        Assert.Equal(0.8, target.GForceHeightScale);
    }

    [Theory]
    [InlineData(-1, 0.5)]
    [InlineData(3, 2)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void SettingsAndProfileSizeNormalizeWithoutChangingShape(double input, double expected)
    {
        var settings = new AppSettings
        {
            GForceGaugeScale = input,
            GForceWidthScale = 1.35,
            GForceHeightScale = 0.85
        };
        var preset = HudPreset.Capture(settings, "Normalize size");
        settings.MigrateSettings();
        Assert.True(preset.Normalize());
        Assert.Equal(expected, settings.GForceGaugeScale);
        Assert.Equal(expected, preset.GForceGaugeScale);
        Assert.Equal(1.35, settings.GForceWidthScale);
        Assert.Equal(0.85, settings.GForceHeightScale);
        Assert.Equal(1.35, preset.GForceWidthScale);
        Assert.Equal(0.85, preset.GForceHeightScale);
    }
}
