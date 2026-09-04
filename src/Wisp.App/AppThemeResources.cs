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
        => Apply(resources, accentTheme, backgroundTheme, null, null);

    public static void Apply(
        ResourceDictionary resources,
        AppColorTheme accentTheme,
        AppBackgroundTheme backgroundTheme,
        string? customAccentColor,
        string? customBackgroundColor)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(accentTheme);
        ArgumentNullException.ThrowIfNull(backgroundTheme);

        var accentColor = ColorCustomization.TryParse(customAccentColor, out var customAccent)
            ? customAccent
            : Parse(accentTheme.Accent);
        var resolvedBackground = ColorCustomization.TryParse(customBackgroundColor, out var customBackground)
            ? ColorCustomization.CreateBackgroundTheme(customBackground)
            : backgroundTheme;

        // Resolve every color before touching the dictionary so a malformed
        // custom theme cannot leave a partially-applied window palette.
        var colors = new[]
        {
            ("WindowBrush", Parse(resolvedBackground.Window)),
            ("PanelBrush", Parse(resolvedBackground.Panel)),
            ("SidebarBrush", Parse(resolvedBackground.Panel)),
            ("CardBrush", Parse(resolvedBackground.Card)),
            ("RaisedBrush", Parse(resolvedBackground.Raised)),
            ("StrokeBrush", Parse(resolvedBackground.Stroke)),
            ("TextBrush", Parse("#F5F8FC")),
            ("MutedBrush", Parse("#91A0B3")),
            ("FaintBrush", Parse("#8191A5")),
            ("AccentBrush", accentColor),
            ("AccentBlueBrush", accentColor),
            ("InputBrush", Parse(resolvedBackground.Input)),
            ("HoverBrush", Parse(resolvedBackground.Hover)),
            ("SliderTrackBrush", Parse(resolvedBackground.SliderTrack)),
            ("ToggleTrackBrush", Parse(resolvedBackground.ToggleTrack)),
            ("ScrollThumbBrush", Parse(resolvedBackground.ScrollThumb))
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
