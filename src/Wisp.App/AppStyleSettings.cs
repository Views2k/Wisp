namespace Wisp.App;

public sealed class AppStyleSettings
{
    public double GlowStrength { get; set; } = 100;
    public double SurfaceOpacity { get; set; } = 100;
    public double BorderThickness { get; set; } = 1;
    public double CornerRadius { get; set; } = 28;
    public double CardPadding { get; set; } = 24;
    public string? BorderColor { get; set; }
    public string? TextColor { get; set; }
    public string? MutedTextColor { get; set; }

    public AppStyleSettings Clone() => (AppStyleSettings)MemberwiseClone();

    public void Normalize()
    {
        GlowStrength = Clamp(GlowStrength, 0, 100, 100);
        SurfaceOpacity = Clamp(SurfaceOpacity, 0, 100, 100);
        BorderThickness = Clamp(BorderThickness, 0, 3, 1);
        CornerRadius = Clamp(CornerRadius, 0, 48, 28);
        CardPadding = Clamp(CardPadding, 12, 36, 24);
        BorderColor = NormalizeColor(BorderColor);
        TextColor = NormalizeColor(TextColor);
        MutedTextColor = NormalizeColor(MutedTextColor);
    }

    internal bool HasSameValues(AppStyleSettings other) =>
        GlowStrength == other.GlowStrength && SurfaceOpacity == other.SurfaceOpacity &&
        BorderThickness == other.BorderThickness && CornerRadius == other.CornerRadius &&
        CardPadding == other.CardPadding && BorderColor == other.BorderColor &&
        TextColor == other.TextColor && MutedTextColor == other.MutedTextColor;

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static string? NormalizeColor(string? value) =>
        ColorCustomization.TryParse(value, out var color) ? ColorCustomization.ToHex(color) : null;
}
