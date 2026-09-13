using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wisp.App;

public enum OrbitSurfaceShape
{
    Card,
    Capsule,
    Swept,
    Rounded
}

public enum OrbitSurfaceTreatment
{
    Quiet,
    Instrument
}

public sealed class OrbitSurface : Border
{
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(OrbitSurfaceShape), typeof(OrbitSurface),
        new FrameworkPropertyMetadata(OrbitSurfaceShape.Card, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is OrbitSurfaceShape.Card or OrbitSurfaceShape.Capsule or OrbitSurfaceShape.Swept or OrbitSurfaceShape.Rounded);

    public static readonly DependencyProperty TreatmentProperty = DependencyProperty.Register(
        nameof(Treatment), typeof(OrbitSurfaceTreatment), typeof(OrbitSurface),
        new FrameworkPropertyMetadata(OrbitSurfaceTreatment.Quiet, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is OrbitSurfaceTreatment.Quiet or OrbitSurfaceTreatment.Instrument);

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(OrbitSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowOpacityProperty = DependencyProperty.Register(
        nameof(GlowOpacity), typeof(double), typeof(OrbitSurface),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double opacity && double.IsFinite(opacity) && opacity is >= 0 and <= 1);

    public static readonly DependencyProperty SurfaceOpacityProperty = DependencyProperty.Register(
        nameof(SurfaceOpacity), typeof(double), typeof(OrbitSurface),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double opacity && double.IsFinite(opacity) && opacity is >= 0 and <= 1);

    private DrawingGroup? _surfaceDrawing;
    private Size _drawingSize;

    public OrbitSurface()
    {
        SetResourceReference(AccentBrushProperty, "AccentBrush");
        SetResourceReference(GlowOpacityProperty, "OrbitGlowOpacity");
        SetResourceReference(SurfaceOpacityProperty, "OrbitSurfaceOpacity");
    }

    public OrbitSurfaceShape Shape
    {
        get => (OrbitSurfaceShape)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public OrbitSurfaceTreatment Treatment
    {
        get => (OrbitSurfaceTreatment)GetValue(TreatmentProperty);
        set => SetValue(TreatmentProperty, value);
    }

    public double GlowOpacity
    {
        get => (double)GetValue(GlowOpacityProperty);
        set => SetValue(GlowOpacityProperty, value);
    }

    public double SurfaceOpacity
    {
        get => (double)GetValue(SurfaceOpacityProperty);
        set => SetValue(SurfaceOpacityProperty, value);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ShapeProperty || e.Property == TreatmentProperty ||
            (Treatment == OrbitSurfaceTreatment.Instrument &&
             (e.Property == AccentBrushProperty || e.Property == GlowOpacityProperty)) ||
            e.Property == SurfaceOpacityProperty ||
            e.Property == BackgroundProperty || e.Property == BorderBrushProperty ||
            e.Property == BorderThicknessProperty || e.Property == CornerRadiusProperty)
        {
            _surfaceDrawing = null;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0)
        {
            return;
        }

        if (_surfaceDrawing is null || _drawingSize != RenderSize)
        {
            _surfaceDrawing = CreateDrawing(RenderSize);
            _drawingSize = RenderSize;
        }
        drawingContext.DrawDrawing(_surfaceDrawing);
    }

    private DrawingGroup CreateDrawing(Size size)
    {
        var background = SampleBrush(Background);
        var accent = SampleBrush(AccentBrush);
        var border = SampleBrush(BorderBrush);
        var accentOpacity = accent.A / 255d * GlowOpacity;
        var thickness = BorderThickness;
        var edgeWidth = Math.Max(Math.Max(thickness.Left, thickness.Right), Math.Max(thickness.Top, thickness.Bottom));
        var bounds = new Rect(0, 0, size.Width, size.Height);
        var silhouette = CreateGeometry(bounds, Shape, CornerRadius);
        var surface = Opaque(background);
        var edge = Opaque(border);
        var drawing = new DrawingGroup
        {
            // Instrument lighting is composited once so it cannot increase custom alpha.
            Opacity = Treatment == OrbitSurfaceTreatment.Instrument
                ? background.A / 255d * SurfaceOpacity : SurfaceOpacity
        };

        using (var context = drawing.Open())
        {
            if (Treatment == OrbitSurfaceTreatment.Quiet)
            {
                var fill = Background?.CloneCurrentValue();
                fill?.Freeze();
                context.DrawGeometry(fill, null, silhouette);
            }
            else
            {
                context.PushClip(silhouette);
                context.DrawGeometry(Linear(
                    (Mix(surface, edge, 0.04), 0),
                    (Mix(surface, Colors.Black, 0.24), 0.48),
                    (Mix(surface, Colors.Black, 0.40), 1)), null, silhouette);

                // Broad, fading highlights lift the surface without making nested rims.
                context.DrawGeometry(Radial(accent, 0.18 * accentOpacity,
                    new Point(0.25, -0.30), 0.90, 0.75), null, silhouette);
                context.DrawGeometry(Radial(accent, 0.14 * accentOpacity,
                    new Point(-0.14, 0.40), 0.50, 0.95), null, silhouette);
                context.DrawGeometry(Radial(accent, 0.11 * accentOpacity,
                    new Point(1.12, 0.40), 0.44, 0.92), null, silhouette);
                context.DrawGeometry(Radial(accent, 0.10 * accentOpacity,
                    new Point(0.60, 1.30), 0.88, 0.60), null, silhouette);
                context.Pop();
            }
        }
        var combined = new DrawingGroup();
        combined.Children.Add(drawing);
        if (edgeWidth > 0 && BorderBrush is { } configuredBorder)
        {
            var innerBounds = new Rect(bounds.X + thickness.Left, bounds.Y + thickness.Top,
                Math.Max(0, bounds.Width - thickness.Left - thickness.Right),
                Math.Max(0, bounds.Height - thickness.Top - thickness.Bottom));
            var innerCorners = new CornerRadius(
                Math.Max(0, CornerRadius.TopLeft - Math.Max(thickness.Left, thickness.Top)),
                Math.Max(0, CornerRadius.TopRight - Math.Max(thickness.Right, thickness.Top)),
                Math.Max(0, CornerRadius.BottomRight - Math.Max(thickness.Right, thickness.Bottom)),
                Math.Max(0, CornerRadius.BottomLeft - Math.Max(thickness.Left, thickness.Bottom)));
            var inner = CreateGeometry(innerBounds, Shape, innerCorners);
            drawing.ClipGeometry = inner;
            // Keep both Bezier contours intact rather than flattening a Boolean
            // subtraction into a thin, tolerance-dependent corner outline.
            var borderGeometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
            borderGeometry.Children.Add(silhouette);
            borderGeometry.Children.Add(inner);
            borderGeometry.Freeze();
            // Border/focus opacity is independent from the glass fill. Snapshot mutable
            // brushes before freezing the drawing; never freeze a caller's theme brush.
            var borderBrush = configuredBorder.CloneCurrentValue();
            borderBrush.Freeze();
            combined.Children.Add(new GeometryDrawing(borderBrush, null, borderGeometry));
        }
        combined.Freeze();
        return combined;
    }

    internal static StreamGeometry CreateGeometry(Rect bounds, OrbitSurfaceShape shape, CornerRadius corners)
    {
        var geometry = new StreamGeometry();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            geometry.Freeze();
            return geometry;
        }

        using (var context = geometry.Open())
        {
            if (shape == OrbitSurfaceShape.Swept)
            {
                Point P(double x, double y) => new(bounds.X + x * bounds.Width / 1000,
                    bounds.Y + y * bounds.Height / 240);
                context.BeginFigure(P(0, 120), true, true);
                context.BezierTo(P(0, 35), P(150, 0), P(500, 0), true, false);
                context.BezierTo(P(850, 0), P(1000, 35), P(1000, 120), true, false);
                context.BezierTo(P(1000, 205), P(850, 240), P(500, 240), true, false);
                context.BezierTo(P(150, 240), P(0, 205), P(0, 120), true, false);
            }
            else
            {
                var radius = Math.Min(bounds.Width, bounds.Height) / 2;
                var tl = shape == OrbitSurfaceShape.Capsule ? radius : corners.TopLeft;
                var tr = shape == OrbitSurfaceShape.Capsule ? radius : corners.TopRight;
                var br = shape == OrbitSurfaceShape.Capsule ? radius : corners.BottomRight;
                var bl = shape == OrbitSurfaceShape.Capsule ? radius : corners.BottomLeft;
                var scale = Math.Min(1, Math.Min(
                    Math.Min(bounds.Width / Math.Max(1, tl + tr), bounds.Width / Math.Max(1, bl + br)),
                    Math.Min(bounds.Height / Math.Max(1, tl + bl), bounds.Height / Math.Max(1, tr + br))));
                tl *= scale; tr *= scale; br *= scale; bl *= scale;
                var curve = shape is OrbitSurfaceShape.Capsule or OrbitSurfaceShape.Rounded ? 0.55228475 : 0.72;
                var left = bounds.Left; var top = bounds.Top;
                var right = bounds.Right; var bottom = bounds.Bottom;
                context.BeginFigure(new Point(left + tl, top), true, true);
                context.LineTo(new Point(right - tr, top), true, false);
                context.BezierTo(new Point(right - tr + tr * curve, top),
                    new Point(right, top + tr - tr * curve), new Point(right, top + tr), true, false);
                context.LineTo(new Point(right, bottom - br), true, false);
                context.BezierTo(new Point(right, bottom - br + br * curve),
                    new Point(right - br + br * curve, bottom), new Point(right - br, bottom), true, false);
                context.LineTo(new Point(left + bl, bottom), true, false);
                context.BezierTo(new Point(left + bl - bl * curve, bottom),
                    new Point(left, bottom - bl + bl * curve), new Point(left, bottom - bl), true, false);
                context.LineTo(new Point(left, top + tl), true, false);
                context.BezierTo(new Point(left, top + tl - tl * curve),
                    new Point(left + tl - tl * curve, top), new Point(left + tl, top), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static LinearGradientBrush Linear(params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.10, 1),
            ColorInterpolationMode = ColorInterpolationMode.SRgbLinearInterpolation
        };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }
        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush Radial(Color color, double opacity, Point origin, double radiusX, double radiusY)
    {
        var brush = new RadialGradientBrush
        {
            Center = origin,
            GradientOrigin = origin,
            RadiusX = radiusX,
            RadiusY = radiusY,
            ColorInterpolationMode = ColorInterpolationMode.SRgbLinearInterpolation
        };
        brush.GradientStops.Add(new GradientStop(Alpha(color, opacity), 0));
        brush.GradientStops.Add(new GradientStop(Alpha(color, opacity * 0.34), 0.50));
        brush.GradientStops.Add(new GradientStop(Alpha(color, 0), 1));
        brush.Freeze();
        return brush;
    }

    private static Color SampleBrush(Brush? brush)
    {
        var color = Colors.Transparent;
        if (brush is SolidColorBrush solid)
        {
            color = solid.Color;
        }
        else if (brush is GradientBrush gradient && gradient.GradientStops.Count > 0)
        {
            var stops = gradient.GradientStops.OrderBy(stop => stop.Offset).ToArray();
            color = stops[0].Color;
            for (var i = 1; i < stops.Length; i++)
            {
                if (stops[i].Offset >= 0.5)
                {
                    var previous = stops[i - 1];
                    var span = stops[i].Offset - previous.Offset;
                    var amount = span <= 0 ? 0 : Math.Clamp((0.5 - previous.Offset) / span, 0, 1);
                    color = Mix(previous.Color, stops[i].Color, amount);
                    color.A = (byte)Math.Round(previous.Color.A + (stops[i].Color.A - previous.Color.A) * amount);
                    break;
                }
                color = stops[i].Color;
            }
        }
        color.A = (byte)Math.Round(color.A * (brush?.Opacity ?? 0));
        return color;
    }

    private static Color Opaque(Color color) => Color.FromRgb(color.R, color.G, color.B);

    private static Color Alpha(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), color.R, color.G, color.B);

    private static Color Mix(Color first, Color second, double amount) => Color.FromArgb(first.A,
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));
}
