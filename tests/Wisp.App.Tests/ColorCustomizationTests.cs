using System.Windows;
using System.Windows.Media;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ColorCustomizationTests
{
    [Theory]
    [InlineData(152, 76, 100, 76, 0)]
    [InlineData(76, 152, 76, 100, 90)]
    [InlineData(0, 76, 52, 76, 180)]
    [InlineData(76, 0, 76, 52, 270)]
    public void ColorWheelPositionSelectsHueAndSaturation(
        double outerX,
        double outerY,
        double innerX,
        double innerY,
        double expectedHue)
    {
        var outer = ColorWheelEditor.SelectionFromPoint(new Point(outerX, outerY), 76);
        var inner = ColorWheelEditor.SelectionFromPoint(new Point(innerX, innerY), 76);

        Assert.Equal(expectedHue, outer.Hue, 6);
        Assert.Equal(expectedHue, inner.Hue, 6);
        Assert.Equal(1, outer.Saturation, 6);
        Assert.InRange(inner.Saturation, 0.31, 0.32);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void ColorWheelMarkerReflectsTheSelectedWheelLocation(double hue)
    {
        var position = ColorWheelEditor.WheelMarkerPosition(hue, 0.5, 76, 14, 14);
        var markerCenter = new Point(position.X + 7, position.Y + 7);
        var distanceFromWheelCenter = Math.Sqrt(
            Math.Pow(markerCenter.X - 76, 2) + Math.Pow(markerCenter.Y - 76, 2));

        Assert.Equal(38, distanceFromWheelCenter, 6);
    }

    [Fact]
    public void ExistingSettingsKeepEveryLegacyThemeUntilAColorIsCustomized()
    {
        var settings = new AppSettings
        {
            ColorTheme = "Plum",
            BackgroundTheme = "Navy",
            HudBorderTheme = "Orange",
            BoostGaugeTheme = "Red"
        };

        settings.MigrateSettings();

        Assert.Null(settings.CustomAccentColor);
        Assert.Null(settings.CustomBackgroundColor);
        Assert.Null(settings.CustomHudBorderColor);
        Assert.Null(settings.CustomBoostLowColor);
        Assert.Equal(Parse(AppColorThemes.Resolve("Plum").Accent), ColorCustomization.ResolveAccent(settings));
        Assert.Same(AppBackgroundThemes.Resolve("Navy"), ColorCustomization.ResolveBackground(settings));
        Assert.Equal(BoostGaugeThemes.Resolve("Red"), ColorCustomization.ResolveGauge(settings));
    }

    [Fact]
    public void CustomColorsNormalizeArgbAndEnforceReadabilityBounds()
    {
        Assert.Equal("#59AABBCC", ColorCustomization.NormalizeAccent("#10AABBCC"));
        Assert.StartsWith("#D1", ColorCustomization.NormalizeBackground("#10FFFFFF"), StringComparison.Ordinal);
        Assert.Equal("#00112233", ColorCustomization.NormalizeHudBorder("#00112233"));
        Assert.Equal("#40112233", ColorCustomization.NormalizeGauge("#00112233"));
        Assert.Null(ColorCustomization.NormalizeAccent("not a color"));

        ColorCustomization.TryParse(ColorCustomization.NormalizeBackground("#FFFFFFFF"), out var background);
        Assert.InRange(ColorCustomization.ToHsv(background).Value, 0, ColorCustomization.BackgroundMaximumBrightness);
    }

    [Fact]
    public void CustomBackgroundBuildsAStableSurfaceHierarchy()
    {
        var theme = ColorCustomization.CreateBackgroundTheme(Color.FromArgb(0xD1, 0x10, 0x18, 0x20));
        var colors = new[]
        {
            theme.Window, theme.Panel, theme.Card, theme.Raised, theme.Stroke,
            theme.Input, theme.Hover, theme.SliderTrack, theme.ToggleTrack, theme.ScrollThumb
        };

        Assert.Equal("Custom", theme.Name);
        Assert.Equal(colors.Length, colors.Distinct(StringComparer.Ordinal).Count());
        Assert.All(colors, value => Assert.StartsWith("#D1", value, StringComparison.Ordinal));
    }

    [Fact]
    public void PartialGaugeCustomizationLeavesOtherLegacyStopsExact()
    {
        var settings = new AppSettings
        {
            BoostGaugeTheme = "Red",
            CustomBoostMidColor = "#80ABCDEF"
        };
        settings.MigrateSettings();

        var resolved = ColorCustomization.ResolveGauge(settings);
        var legacy = BoostGaugeThemes.Resolve("Red");
        Assert.Equal("Custom", resolved.Name);
        Assert.Equal("#FF" + legacy.Low[1..], resolved.Low);
        Assert.Equal("#80ABCDEF", resolved.Mid);
        Assert.Equal("#FF" + legacy.High[1..], resolved.High);
    }

    [Fact]
    public void CustomResourceApplicationDoesNotMutateLegacyThemeDefinitions()
    {
        var legacyAccent = AppColorThemes.Resolve("Aqua");
        var legacyBackground = AppBackgroundThemes.Resolve("Neutral");
        var resources = new ResourceDictionary();

        AppThemeResources.Apply(resources, legacyAccent, legacyBackground, "#80FF0000", "#D10A1018");
        HudBorderThemeResources.Apply(resources, "Aqua", "#66112233");
        BoostGaugeThemeResources.Apply(resources, "Red", "#40FF0000", null, "#800000FF");

        Assert.Equal(Color.FromArgb(0x80, 0xFF, 0, 0), Brush(resources, "AccentBrush").Color);
        Assert.Equal(Color.FromArgb(0x66, 0x11, 0x22, 0x33), Brush(resources, "HudBorderBrush").Color);
        Assert.Equal(Color.FromArgb(0x40, 0xFF, 0, 0), Brush(resources, "BoostLowBrush").Color);
        Assert.Equal(Parse(BoostGaugeThemes.Resolve("Red").Mid), Brush(resources, "BoostMidBrush").Color);
        Assert.Equal(Color.FromArgb(0x80, 0, 0, 0xFF), Brush(resources, "BoostHighBrush").Color);
        Assert.Equal("#63D8D4", legacyAccent.Accent);
        Assert.Equal("#090C11", legacyBackground.Window);
    }

    private static SolidColorBrush Brush(ResourceDictionary resources, string key) =>
        Assert.IsType<SolidColorBrush>(resources[key]);

    private static Color Parse(string value) =>
        (Color)ColorConverter.ConvertFromString(value);
}
