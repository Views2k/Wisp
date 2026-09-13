using System.Windows.Media;

namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    private string? _customGForceColor;
    private string? _customGForceTrailColor;
    public Brush? GForceDotBrush { get; private set; }
    public Brush? GForceDotStrokeBrush { get; private set; }
    public Brush? GForceTrailBrush { get; private set; }
    public Color? GForceGlowColor { get; private set; }

    internal void UpdateGForceColors(AppSettings settings)
    {
        var dot = ColorCustomization.NormalizeGauge(settings.CustomGForceColor);
        var trail = ColorCustomization.NormalizeGauge(settings.CustomGForceTrailColor);
        if (dot != _customGForceColor)
        {
            _customGForceColor = dot;
            var color = ColorCustomization.TryParse(dot, out var parsed) ? parsed : (Color?)null;
            GForceDotBrush = Brush(color);
            GForceGlowColor = color;
            GForceDotStrokeBrush = color is { } stroke ? Brush(Color.FromArgb((byte)Math.Round(stroke.A * .5), stroke.R, stroke.G, stroke.B)) : null;
            OnPropertyChanged(nameof(GForceDotBrush));
            OnPropertyChanged(nameof(GForceDotStrokeBrush));
            OnPropertyChanged(nameof(GForceGlowColor));
        }
        if (trail != _customGForceTrailColor)
        {
            _customGForceTrailColor = trail;
            GForceTrailBrush = ColorCustomization.TryParse(trail, out var color) ? Brush(color) : null;
            OnPropertyChanged(nameof(GForceTrailBrush));
        }

        static Brush? Brush(Color? color)
        {
            if (color is not { } value) return null;
            var brush = new SolidColorBrush(value);
            brush.Freeze();
            return brush;
        }
    }
}
