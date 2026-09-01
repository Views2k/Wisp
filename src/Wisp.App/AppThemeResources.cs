using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

public static class AppThemeResources
{
    public static void Apply(ResourceDictionary resources, AppColorTheme accentTheme) =>
        Apply(resources, accentTheme, AppBackgroundThemes.Resolve(null));

    public static void Apply(
        ResourceDictionary resources,
        AppColorTheme accentTheme,
        AppBackgroundTheme backgroundTheme)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(accentTheme);
        ArgumentNullException.ThrowIfNull(backgroundTheme);

        // Resolve every color before touching the dictionary so a malformed
        // custom theme cannot leave a partially-applied window palette.
        var colors = new[]
        {
            ("WindowBrush", Parse(backgroundTheme.Window)),
            ("PanelBrush", Parse(backgroundTheme.Panel)),
            ("SidebarBrush", Parse(backgroundTheme.Panel)),
            ("CardBrush", Parse(backgroundTheme.Card)),
            ("RaisedBrush", Parse(backgroundTheme.Raised)),
            ("StrokeBrush", Parse(backgroundTheme.Stroke)),
            ("TextBrush", Parse("#F5F8FC")),
            ("MutedBrush", Parse("#91A0B3")),
            ("FaintBrush", Parse("#8191A5")),
            ("AccentBrush", Parse(accentTheme.Accent)),
            ("AccentBlueBrush", Parse(accentTheme.Accent)),
            ("InputBrush", Parse(backgroundTheme.Input)),
            ("HoverBrush", Parse(backgroundTheme.Hover)),
            ("SliderTrackBrush", Parse(backgroundTheme.SliderTrack)),
            ("ToggleTrackBrush", Parse(backgroundTheme.ToggleTrack)),
            ("ScrollThumbBrush", Parse(backgroundTheme.ScrollThumb))
        };

        foreach (var (key, color) in colors)
        {
            SetFrozenBrush(resources, key, color);
        }
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);

    private static void SetFrozenBrush(ResourceDictionary resources, string key, Color color)
    {
        // Preserve identical local brushes so WPF does not invalidate the
        // visual tree for the fourteen neutral tokens shared by every theme.
        var hasLocalValue = resources.Keys.Cast<object>().Any(existingKey => Equals(existingKey, key));
        if (hasLocalValue && resources[key] is SolidColorBrush current &&
            current.IsFrozen && current.Color == color)
        {
            return;
        }

        var replacement = new SolidColorBrush(color);
        replacement.Freeze();
        // Local replacements must not recolor shared brushes used by the HUD.
        resources[key] = replacement;
    }
}
