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
        string? customBackgroundColor,
        AppStyleSettings? applicationStyle = null,
        string? customParticleColor = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(accentTheme);
        ArgumentNullException.ThrowIfNull(backgroundTheme);

        var style = applicationStyle?.Clone() ?? new AppStyleSettings();
        style.Normalize();

        var accentColor = ColorCustomization.TryParse(customAccentColor, out var customAccent)
            ? customAccent
            : Parse(accentTheme.Accent);
        var resolvedBackground = ColorCustomization.TryParse(customBackgroundColor, out var customBackground)
            ? ColorCustomization.CreateBackgroundTheme(customBackground)
            : backgroundTheme;

        var windowColor = Parse(resolvedBackground.Window);
        var panelColor = Parse(resolvedBackground.Panel);
        var hasCustomBorder = ColorCustomization.TryParse(style.BorderColor, out var customBorder);
        var strokeColor = hasCustomBorder ? customBorder : Parse(resolvedBackground.Stroke);
        var cardColor = Parse(resolvedBackground.Card);
        var raisedColor = Parse(resolvedBackground.Raised);

        // Resolve every color before touching the dictionary so a malformed
        // custom theme cannot leave a partially-applied window palette.
        var colors = new[]
        {
            ("WindowBrush", windowColor),
            ("PanelBrush", panelColor),
            ("SidebarBrush", panelColor),
            ("CardBrush", cardColor),
            ("RaisedBrush", raisedColor),
            ("StrokeBrush", strokeColor),
            ("TextBrush", ColorCustomization.TryParse(style.TextColor, out var textColor) ? textColor : Parse("#F5F8FC")),
            ("MutedBrush", ColorCustomization.TryParse(style.MutedTextColor, out var mutedColor) ? mutedColor : Parse("#91A0B3")),
            ("FaintBrush", Parse("#8191A5")),
            ("AccentBrush", accentColor),
            ("AccentBlueBrush", accentColor),
            ("AccentTextBrush", ResolveAccentText(accentColor, cardColor, raisedColor)),
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

        SetValue(resources, "AppParticleColor", ColorCustomization.TryParse(customParticleColor, out var particleColor)
            ? particleColor : accentColor);

        SetFrozenGradientBrush(resources, "OrbitCardBrush", new Point(0.85, 1),
            (AccentTint(cardColor, accentColor, 0.14), 0),
            (cardColor, 0.46),
            (Mix(cardColor, windowColor, 0.90), 1));
        SetFrozenGradientBrush(resources, "OrbitBorderBrush", new Point(1, 1),
            (hasCustomBorder ? strokeColor : AccentTint(strokeColor, accentColor, 0.88), 0),
            (hasCustomBorder ? strokeColor : AccentTint(strokeColor, accentColor, 0.38), 0.45),
            (hasCustomBorder ? strokeColor : Mix(strokeColor, windowColor, 0.62), 1));
        SetFrozenGradientBrush(resources, "OrbitButtonBrush", new Point(0, 1),
            (Mix(raisedColor, Colors.White, 0.09), 0),
            (raisedColor, 0.44),
            (Mix(raisedColor, windowColor, 0.60), 1));
        SetFrozenGradientBrush(resources, "OrbitSelectedBrush", new Point(0.85, 1),
            (AccentTint(panelColor, accentColor, 0.32), 0),
            (AccentTint(panelColor, accentColor, 0.13), 0.5),
            (AccentTint(panelColor, accentColor, 0.03), 1));

        // Modern content uses quiet surfaces. Explicit border customization still
        // applies; the older palette keys remain unchanged for the legacy shell.
        SetFrozenBrush(resources, "OrbitSurfaceBorderBrush",
            hasCustomBorder || style.BorderThickness != 1 ? strokeColor : Colors.Transparent);
        SetFrozenBrush(resources, "OrbitControlBrush", raisedColor);
        SetFrozenBrush(resources, "OrbitSelectionBrush", AccentTint(raisedColor, accentColor, 0.13));
        SetFrozenBrush(resources, "OrbitFocusBrush", ResolveAccentText(Colors.Transparent, cardColor, raisedColor));

        SetValue(resources, "OrbitGlowOpacity", style.GlowStrength / 100);
        SetValue(resources, "OrbitSurfaceOpacity", style.SurfaceOpacity / 100);
        SetValue(resources, "OrbitStrokeThickness", new Thickness(style.BorderThickness));
        SetValue(resources, "OrbitCornerRadius", new CornerRadius(style.CornerRadius));
        SetValue(resources, "OrbitButtonRadius", new CornerRadius(Math.Min(style.CornerRadius, 26)));
        SetValue(resources, "OrbitCardPadding", new Thickness(style.CardPadding));
    }

    private static void SetValue(ResourceDictionary resources, string key, object value)
    {
        if (!resources.Keys.Cast<object>().Any(existingKey => Equals(existingKey, key)) ||
            !Equals(resources[key], value))
        {
            resources[key] = value;
        }
    }

    private static Color AccentTint(Color surface, Color accent, double amount) =>
        Mix(surface, accent, amount * accent.A / 255d);

    private static Color Mix(Color surface, Color tint, double amount) =>
        Color.FromArgb(surface.A,
            (byte)Math.Round(surface.R + (tint.R - surface.R) * amount),
            (byte)Math.Round(surface.G + (tint.G - surface.G) * amount),
            (byte)Math.Round(surface.B + (tint.B - surface.B) * amount));

    private static void SetFrozenGradientBrush(ResourceDictionary resources, string key,
        Point endPoint, params (Color Color, double Offset)[] stops)
    {
        var hasLocalValue = resources.Keys.Cast<object>().Any(existingKey => Equals(existingKey, key));
        if (hasLocalValue && resources[key] is LinearGradientBrush current && current.IsFrozen &&
            current.StartPoint == new Point(0, 0) && current.EndPoint == endPoint &&
            current.MappingMode == BrushMappingMode.RelativeToBoundingBox &&
            current.SpreadMethod == GradientSpreadMethod.Pad &&
            current.ColorInterpolationMode == ColorInterpolationMode.SRgbLinearInterpolation &&
            current.Opacity == 1 && current.Transform.Value.IsIdentity && current.RelativeTransform.Value.IsIdentity &&
            current.GradientStops.Count == stops.Length &&
            current.GradientStops.Select(stop => (stop.Color, stop.Offset)).SequenceEqual(stops))
        {
            return;
        }

        var replacement = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = endPoint,
            ColorInterpolationMode = ColorInterpolationMode.SRgbLinearInterpolation
        };
        foreach (var (color, offset) in stops)
        {
            replacement.GradientStops.Add(new GradientStop(color, offset));
        }
        replacement.Freeze();
        resources[key] = replacement;
    }

    private static Color ResolveAccentText(Color accent, Color card, Color raised)
    {
        // Use the visible fill: a translucent bright accent can still look dark.
        // Favor the foreground with the best minimum contrast on both surfaces.
        var cardLuminance = AccentLuminance(accent, card);
        var raisedLuminance = AccentLuminance(accent, raised);
        var blackContrast = (Math.Min(cardLuminance, raisedLuminance) + 0.05) / 0.05;
        var whiteContrast = 1.05 / (Math.Max(cardLuminance, raisedLuminance) + 0.05);
        return blackContrast >= whiteContrast ? Colors.Black : Colors.White;
    }

    private static double AccentLuminance(Color accent, Color surface)
    {
        var opacity = accent.A / 255d;
        return 0.2126 * LinearChannel((accent.R * opacity + surface.R * (1 - opacity)) / 255d) +
               0.7152 * LinearChannel((accent.G * opacity + surface.G * (1 - opacity)) / 255d) +
               0.0722 * LinearChannel((accent.B * opacity + surface.B * (1 - opacity)) / 255d);
    }

    private static double LinearChannel(double channel) =>
        channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

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
