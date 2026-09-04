using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

public static class HudBorderThemeResources
{
    public const string ResourceKey = "HudBorderBrush";
    public const byte BorderAlpha = 0x66;

    public static void Apply(ResourceDictionary resources, string? themeName) =>
        Apply(resources, AppColorThemes.Resolve(themeName));

    public static void Apply(ResourceDictionary resources, string? themeName, string? customColor)
    {
        if (ColorCustomization.TryParse(customColor, out var color))
        {
            Apply(resources, color);
            return;
        }

        Apply(resources, themeName);
    }

    public static void Apply(ResourceDictionary resources, AppColorTheme theme)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(theme);

        var color = (Color)ColorConverter.ConvertFromString(theme.Accent);
        color.A = BorderAlpha;
        Apply(resources, color);
    }

    public static void Apply(ResourceDictionary resources, Color color)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var hasLocalValue = resources.Keys.Cast<object>()
            .Any(existingKey => Equals(existingKey, ResourceKey));
        if (hasLocalValue && resources[ResourceKey] is SolidColorBrush current &&
            current.IsFrozen && current.Color == color)
        {
            return;
        }

        var replacement = new SolidColorBrush(color);
        replacement.Freeze();
        resources[ResourceKey] = replacement;
    }
}
