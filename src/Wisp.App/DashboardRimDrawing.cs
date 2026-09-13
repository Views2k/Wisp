using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

internal sealed class DashboardRimDrawing
{
    private readonly DashboardRimScene _scene = new();
    private readonly SolidColorBrush?[] _sparkBrushes = new SolidColorBrush?[256];
    private DrawingGroup? _halo;
    private Geometry? _silhouette;
    private Geometry? _outside;
    private Rect _bounds;
    private OrbitSurfaceShape _shape;
    private CornerRadius _corners;
    private Color _accent;
    private double _intensity;
    private double _dpiScale;
    private double _edgeWidth;

    internal int HaloBuildCount { get; private set; }
    internal int AnchorBuildCount => _scene.AnchorBuildCount;
    internal int CachedSparkBrushCount => _sparkBrushes.Count(brush => brush is not null);

    internal void Draw(DrawingContext context, Rect bounds, OrbitSurfaceShape shape,
        CornerRadius corners, Color accent, double intensity, bool particlesEnabled,
        double seconds, DpiScale dpi, double edgeWidth = 1)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 ||
            !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            !double.IsFinite(intensity) || intensity <= 0 || accent.A == 0)
            return;
        intensity = Math.Clamp(intensity, 0, 1) * accent.A / 255d;
        var dpiScale = Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
        dpiScale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        edgeWidth = double.IsFinite(edgeWidth) ? Math.Clamp(edgeWidth, 0, 3) : 0;
        if (_halo is null || _bounds != bounds || _shape != shape || _corners != corners ||
            _accent != accent || _intensity != intensity || _dpiScale != dpiScale || _edgeWidth != edgeWidth)
            Configure(bounds, shape, corners, accent, intensity, dpiScale, edgeWidth);

        context.PushClip(_outside!);
        context.DrawDrawing(_halo!);
        if (particlesEnabled)
        {
            _scene.Update(_silhouette!, seconds);
            foreach (var spark in _scene.Sparks)
            {
                var alpha = AlphaIndex(spark.Opacity * intensity);
                if (alpha == 0)
                    continue;
                var brush = _sparkBrushes[alpha] ??= Brush(Hot(accent, 0.30), alpha);
                context.DrawEllipse(brush, null, spark.Position, spark.Radius, spark.Radius);
            }
        }
        context.Pop();
    }

    private void Configure(Rect bounds, OrbitSurfaceShape shape, CornerRadius corners,
        Color accent, double intensity, double dpiScale, double edgeWidth)
    {
        if (_silhouette is null || _bounds != bounds || _shape != shape || _corners != corners)
        {
            _silhouette = OrbitSurface.CreateGeometry(bounds, shape, corners);
            var exteriorBounds = bounds;
            exteriorBounds.Inflate(32, 32);
            var clip = new GeometryGroup { FillRule = FillRule.EvenOdd };
            clip.Children.Add(new RectangleGeometry(exteriorBounds));
            clip.Children.Add(_silhouette);
            clip.Freeze();
            _outside = clip;
        }
        _bounds = bounds;
        _shape = shape;
        _corners = corners;
        _accent = accent;
        _intensity = intensity;
        _dpiScale = dpiScale;
        _edgeWidth = edgeWidth;
        Array.Clear(_sparkBrushes);
        var halo = new DrawingGroup();
        using (var drawing = halo.Open())
        {
            foreach (var (width, opacity) in new[]
            {
                (26d, 0.016), (22d, 0.020), (18d, 0.026), (14d, 0.036),
                (10d, 0.052), (7d, 0.082), (4d, 0.14), (2d, 0.26)
            })
                drawing.DrawGeometry(null, HaloPen(accent, opacity * intensity, width, bounds), _silhouette);
            if (edgeWidth > 0)
                drawing.DrawGeometry(null, HaloPen(Hot(accent, 0.34),
                    0.72 * intensity, Math.Max(0.8, 1.1 / dpiScale) * edgeWidth, bounds), _silhouette);
        }
        halo.Freeze();
        _halo = halo;
        HaloBuildCount++;
    }

    private static byte AlphaIndex(double value) =>
        (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);

    private static SolidColorBrush Brush(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Pen HaloPen(Color color, double opacity, double thickness, Rect bounds)
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(bounds.Left, bounds.Top),
            EndPoint = new Point(bounds.Left, bounds.Bottom)
        };
        foreach (var (position, amount) in new[] { (0d, 1d), (0.35, 1d), (0.65, 0.78), (1d, 0.38) })
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(AlphaIndex(opacity * amount), color.R, color.G, color.B), position));
        brush.Freeze();
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static Color Hot(Color accent, double amount) => Color.FromRgb(
        (byte)Math.Round(accent.R + (255 - accent.R) * amount),
        (byte)Math.Round(accent.G + (255 - accent.G) * amount),
        (byte)Math.Round(accent.B + (255 - accent.B) * amount));
}
