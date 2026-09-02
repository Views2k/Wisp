using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

public static class BoostGaugeThemeResources
{
    public static void Apply(ResourceDictionary resources, string? themeName)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var theme = BoostGaugeThemes.Resolve(themeName);
        resources["BoostLowBrush"] = FrozenBrush(theme.Low);
        resources["BoostMidBrush"] = FrozenBrush(theme.Mid);
        resources["BoostHighBrush"] = FrozenBrush(theme.High);
    }

    private static SolidColorBrush FrozenBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
