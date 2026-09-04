using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

public static class BoostGaugeThemeResources
{
    public static void Apply(ResourceDictionary resources, string? themeName)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var theme = BoostGaugeThemes.Resolve(themeName);
        Apply(resources, theme);
    }

    public static void Apply(
        ResourceDictionary resources,
        string? themeName,
        string? customLow,
        string? customMid,
        string? customHigh)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var legacy = BoostGaugeThemes.Resolve(themeName);
        var theme = new BoostGaugeTheme(
            "Custom",
            Resolve(customLow, legacy.Low),
            Resolve(customMid, legacy.Mid),
            Resolve(customHigh, legacy.High));
        Apply(resources, theme);
    }

    public static void Apply(ResourceDictionary resources, BoostGaugeTheme theme)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(theme);
        resources["BoostLowBrush"] = FrozenBrush(theme.Low);
        resources["BoostMidBrush"] = FrozenBrush(theme.Mid);
        resources["BoostHighBrush"] = FrozenBrush(theme.High);
    }

    private static string Resolve(string? custom, string fallback) =>
        ColorCustomization.TryParse(custom, out var color)
            ? ColorCustomization.ToHex(color)
            : fallback;

    private static SolidColorBrush FrozenBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
