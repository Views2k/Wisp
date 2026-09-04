using System.Globalization;
using System.Windows.Media;

namespace Wisp.App;

public static class ColorCustomization
{
    public const double AccentMinimumOpacity = 0.35;
    public const double BackgroundMinimumOpacity = 0.82;
    public const double HudBorderMinimumOpacity = 0.0;
    public const double GaugeMinimumOpacity = 0.25;
    public const double BackgroundMaximumBrightness = 0.34;

    public static string? NormalizeAccent(string? value) =>
        Normalize(value, AccentMinimumOpacity);

    public static string? NormalizeBackground(string? value)
    {
        if (!TryParse(value, out var color))
        {
            return null;
        }

        color.A = ClampAlpha(color.A, BackgroundMinimumOpacity);
        var hsv = ToHsv(color);
        if (hsv.Value > BackgroundMaximumBrightness)
        {
            color = FromHsv(hsv.Hue, hsv.Saturation, BackgroundMaximumBrightness, color.A / 255d);
            var maximumChannel = (byte)Math.Floor(BackgroundMaximumBrightness * 255);
            color.R = Math.Min(color.R, maximumChannel);
            color.G = Math.Min(color.G, maximumChannel);
            color.B = Math.Min(color.B, maximumChannel);
        }

        return ToHex(color);
    }

    public static string? NormalizeHudBorder(string? value) =>
        Normalize(value, HudBorderMinimumOpacity);

    public static string? NormalizeGauge(string? value) =>
        Normalize(value, GaugeMinimumOpacity);

    public static Color ResolveAccent(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Resolve(settings.CustomAccentColor, AppColorThemes.Resolve(settings.ColorTheme).Accent);
    }

    public static AppBackgroundTheme ResolveBackground(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.CustomBackgroundColor is { } custom && TryParse(custom, out var color)
            ? CreateBackgroundTheme(color)
            : AppBackgroundThemes.Resolve(settings.BackgroundTheme);
    }

    public static Color ResolveHudBorder(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.CustomHudBorderColor is { } custom && TryParse(custom, out var color))
        {
            return color;
        }

        var legacy = Resolve(null, AppColorThemes.Resolve(settings.HudBorderTheme).Accent);
        legacy.A = HudBorderThemeResources.BorderAlpha;
        return legacy;
    }

    public static BoostGaugeTheme ResolveGauge(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var legacy = BoostGaugeThemes.Resolve(settings.BoostGaugeTheme);
        if (settings.CustomBoostLowColor is null &&
            settings.CustomBoostMidColor is null &&
            settings.CustomBoostHighColor is null)
        {
            return legacy;
        }

        return new BoostGaugeTheme(
            "Custom",
            ResolveHex(settings.CustomBoostLowColor, legacy.Low),
            ResolveHex(settings.CustomBoostMidColor, legacy.Mid),
            ResolveHex(settings.CustomBoostHighColor, legacy.High));
    }

    public static AppBackgroundTheme CreateBackgroundTheme(Color window)
    {
        return new AppBackgroundTheme(
            "Custom",
            ToHex(window),
            ToHex(Lighten(window, 0.035)),
            ToHex(Lighten(window, 0.075)),
            ToHex(Lighten(window, 0.12)),
            ToHex(Lighten(window, 0.20)),
            ToHex(Darken(window, 0.025)),
            ToHex(Lighten(window, 0.135)),
            ToHex(Lighten(window, 0.22)),
            ToHex(Lighten(window, 0.24)),
            ToHex(Lighten(window, 0.34)));
    }

    public static bool TryParse(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length is not (6 or 8) ||
            !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            return false;
        }

        color = text.Length == 6
            ? Color.FromArgb(0xFF, (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : Color.FromArgb((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        return true;
    }

    public static string ToHex(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    public static HsvColor ToHsv(Color color)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        var hue = 0d;

        if (delta > double.Epsilon)
        {
            if (Math.Abs(maximum - red) < double.Epsilon)
            {
                hue = 60 * (((green - blue) / delta) % 6);
            }
            else if (Math.Abs(maximum - green) < double.Epsilon)
            {
                hue = 60 * (((blue - red) / delta) + 2);
            }
            else
            {
                hue = 60 * (((red - green) / delta) + 4);
            }
        }

        if (hue < 0)
        {
            hue += 360;
        }

        return new HsvColor(
            hue,
            maximum <= double.Epsilon ? 0 : delta / maximum,
            maximum,
            color.A / 255d);
    }

    public static Color FromHsv(double hue, double saturation, double value, double opacity = 1)
    {
        hue = double.IsFinite(hue) ? ((hue % 360) + 360) % 360 : 0;
        saturation = double.IsFinite(saturation) ? Math.Clamp(saturation, 0, 1) : 0;
        value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1;

        var chroma = value * saturation;
        var section = hue / 60;
        var secondary = chroma * (1 - Math.Abs(section % 2 - 1));
        var match = value - chroma;
        var (red, green, blue) = section switch
        {
            < 1 => (chroma, secondary, 0d),
            < 2 => (secondary, chroma, 0d),
            < 3 => (0d, chroma, secondary),
            < 4 => (0d, secondary, chroma),
            < 5 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };

        return Color.FromArgb(
            ToByte(opacity),
            ToByte(red + match),
            ToByte(green + match),
            ToByte(blue + match));
    }

    private static string? Normalize(string? value, double minimumOpacity)
    {
        if (!TryParse(value, out var color))
        {
            return null;
        }

        color.A = ClampAlpha(color.A, minimumOpacity);
        return ToHex(color);
    }

    private static string ResolveHex(string? custom, string fallback) =>
        TryParse(custom, out var color) ? ToHex(color) : ToHex(Resolve(null, fallback));

    private static Color Resolve(string? custom, string fallback)
    {
        if (TryParse(custom, out var color) || TryParse(fallback, out color))
        {
            return color;
        }

        throw new FormatException($"Invalid color value '{fallback}'.");
    }

    private static Color Lighten(Color color, double amount) =>
        Mix(color, Colors.White, amount);

    private static Color Darken(Color color, double amount) =>
        Mix(color, Colors.Black, amount);

    private static Color Mix(Color source, Color target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            source.A,
            ToByte(source.R / 255d * (1 - amount) + target.R / 255d * amount),
            ToByte(source.G / 255d * (1 - amount) + target.G / 255d * amount),
            ToByte(source.B / 255d * (1 - amount) + target.B / 255d * amount));
    }

    private static byte ClampAlpha(byte alpha, double minimumOpacity) =>
        (byte)Math.Max(alpha, ToByte(Math.Clamp(minimumOpacity, 0, 1)));

    private static byte ToByte(double value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);
}

public readonly record struct HsvColor(double Hue, double Saturation, double Value, double Opacity);
