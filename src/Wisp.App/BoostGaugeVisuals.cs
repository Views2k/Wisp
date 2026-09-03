using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wisp.App;

public abstract class BoostVisualBase : Grid
{
    protected BoostVisualBase()
    {
        Loaded += (_, _) => CompositionTarget.Rendering += OnCompositionRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnCompositionRendering;
    }

    public static readonly DependencyProperty DisplayProperty = DependencyProperty.Register(
        nameof(Display), typeof(BoostDisplay), typeof(BoostVisualBase),
        new FrameworkPropertyMetadata(BoostDisplay.Unavailable, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColorNumberProperty = DependencyProperty.Register(
        nameof(ColorNumber), typeof(bool), typeof(BoostVisualBase),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LowBrushProperty = DependencyProperty.Register(
        nameof(LowBrush), typeof(Brush), typeof(BoostVisualBase),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MidBrushProperty = DependencyProperty.Register(
        nameof(MidBrush), typeof(Brush), typeof(BoostVisualBase),
        new FrameworkPropertyMetadata(Brushes.RoyalBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighBrushProperty = DependencyProperty.Register(
        nameof(HighBrush), typeof(Brush), typeof(BoostVisualBase),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public BoostDisplay Display { get => (BoostDisplay)GetValue(DisplayProperty); set => SetValue(DisplayProperty, value); }
    public bool ColorNumber { get => (bool)GetValue(ColorNumberProperty); set => SetValue(ColorNumberProperty, value); }
    public Brush LowBrush { get => (Brush)GetValue(LowBrushProperty); set => SetValue(LowBrushProperty, value); }
    public Brush MidBrush { get => (Brush)GetValue(MidBrushProperty); set => SetValue(MidBrushProperty, value); }
    public Brush HighBrush { get => (Brush)GetValue(HighBrushProperty); set => SetValue(HighBrushProperty, value); }

    protected Brush NumberBrush()
    {
        if (!ColorNumber || IsStockPalette || Display.PressurePsi <= 5) return Brushes.WhiteSmoke;
        var color = PaletteColor(Display.Fraction);
        if (Display.Fraction < 0.88) return FrozenBrush(color);
        var phase = (Environment.TickCount64 % 620) / 620d * Math.PI * 2;
        color.A = (byte)Math.Round(175 + 80 * ((Math.Sin(phase) + 1) / 2));
        return FrozenBrush(color);
    }

    protected Color NumberColor() => NumberBrush() is SolidColorBrush brush
        ? brush.Color
        : Colors.WhiteSmoke;

    protected bool IsStockPalette
    {
        get
        {
            var low = BrushColor(LowBrush);
            var middle = BrushColor(MidBrush);
            var high = BrushColor(HighBrush);
            return low.R == middle.R && low.G == middle.G && low.B == middle.B &&
                   middle.R == high.R && middle.G == high.G && middle.B == high.B;
        }
    }

    protected Color PaletteColor(double fraction, byte alpha = byte.MaxValue)
    {
        const double middleStop = 0.56;
        var value = Math.Clamp(fraction, 0, 1);
        var low = BrushColor(LowBrush);
        var middle = BrushColor(MidBrush);
        var high = BrushColor(HighBrush);
        var color = value <= middleStop
            ? Interpolate(low, middle, value / middleStop)
            : Interpolate(middle, high, (value - middleStop) / (1 - middleStop));
        color.A = alpha;
        return color;
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (IsVisible && ColorNumber && Display.IsAvailable && Display.Fraction >= 0.88)
        {
            InvalidateVisual();
        }
    }

    protected FormattedText Text(string value, double size, Brush brush, FontWeight weight = default, FontStyle style = default) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Bahnschrift SemiCondensed, Bahnschrift, Segoe UI"),
                style == default ? FontStyles.Normal : style,
                weight == default ? FontWeights.Normal : weight,
                FontStretches.Condensed), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected LinearGradientBrush Gradient(Point start, Point end, byte alpha = byte.MaxValue)
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = start,
            EndPoint = end,
            GradientStops =
            {
                new GradientStop(ColorWithAlpha(LowBrush, alpha), 0),
                new GradientStop(ColorWithAlpha(MidBrush, alpha), 0.56),
                new GradientStop(ColorWithAlpha(HighBrush, alpha), 1)
            }
        };
        brush.Freeze();
        return brush;
    }

    private static Color ColorWithAlpha(Brush source, byte alpha)
    {
        var color = BrushColor(source);
        color.A = alpha;
        return color;
    }

    private static Color BrushColor(Brush source) =>
        source is SolidColorBrush solid ? solid.Color : Colors.DodgerBlue;

    private static Color Interpolate(Color start, Color end, double amount) => Color.FromArgb(
        byte.MaxValue,
        Lerp(start.R, end.R, amount),
        Lerp(start.G, end.G, amount),
        Lerp(start.B, end.B, amount));

    private static byte Lerp(byte start, byte end, double amount) =>
        (byte)Math.Round(start + ((end - start) * Math.Clamp(amount, 0, 1)));

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public sealed class DigitalBoostRailView : BoostVisualBase
{
    private readonly NativeDigitalGaugeVisual _stockGaugeMaterial;

    public static readonly DependencyProperty UseStockColorsProperty = DependencyProperty.Register(
        nameof(UseStockColors),
        typeof(bool),
        typeof(DigitalBoostRailView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public DigitalBoostRailView()
    {
        ClipToBounds = false;
        _stockGaugeMaterial = new NativeDigitalGaugeVisual
        {
            Width = 302,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 55.75, 0, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        Children.Add(_stockGaugeMaterial);
    }

    public bool UseStockColors
    {
        get => (bool)GetValue(UseStockColorsProperty);
        set => SetValue(UseStockColorsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!Display.IsAvailable || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _stockGaugeMaterial.Visibility = Visibility.Collapsed;
            return;
        }

        // Native DigitalGauge.hlsl uses h = x - 17 + (y * .2) over a 283-DIP
        // span. Keep that silhouette and halve only the rail thickness.
        const double leftTop = 9.05;
        const double rightTop = 292.05;
        const double top = 58.75;
        const double bottom = 65.25;
        const double endRake = 1.3;
        var track = RailGeometry(leftTop, rightTop, top, bottom, endRake);
        var trackBrush = new SolidColorBrush(Color.FromArgb(54, 236, 239, 244));
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(168, 238, 241, 246)), 1.0);

        var fraction = Math.Clamp(Display.Fraction, 0, 1);
        _stockGaugeMaterial.UpdateGaugeParameters(fraction, 1);
        _stockGaugeMaterial.Visibility = UseStockColors ? Visibility.Visible : Visibility.Collapsed;

        if (!UseStockColors)
        {
            dc.DrawGeometry(trackBrush, null, track);
            if (fraction > 0)
            {
                var activeRight = leftTop + ((rightTop - leftTop) * fraction);
                var active = RailGeometry(leftTop, activeRight, top, bottom, endRake);
                var gradientStart = new Point(leftTop, top);
                var gradientEnd = new Point(rightTop, top);

                // The active color stays inside the authored rail silhouette. The
                // neutral outline is restored afterward so the fill never paints
                // over the rail material.
                dc.PushClip(track);
                dc.DrawGeometry(null, new Pen(Gradient(gradientStart, gradientEnd, 48), 4.2), active);
                dc.DrawGeometry(Gradient(gradientStart, gradientEnd), null, active);
                dc.Pop();

                // Match the moving native tachometer marker: a narrow white blade
                // with two restrained halo passes, independent of the palette.
                var markerTop = new Point(activeRight, top - 0.25);
                var markerBottom = new Point(activeRight - endRake, bottom + 0.25);
                dc.DrawLine(
                    new Pen(new SolidColorBrush(Color.FromArgb(28, 248, 250, 253)), 7.2),
                    markerTop,
                    markerBottom);
                dc.DrawLine(
                    new Pen(new SolidColorBrush(Color.FromArgb(72, 248, 250, 253)), 3.6),
                    markerTop,
                    markerBottom);
                dc.DrawLine(
                    new Pen(new SolidColorBrush(Color.FromArgb(242, 248, 250, 253)), 1.35),
                    markerTop,
                    markerBottom);
            }

            dc.DrawGeometry(null, outline, track);
        }

        // Float the connector symmetrically between the rails. Its -0.2 rake
        // matches both authored end caps, with equal clearance at each end.
        var connectorStart = new Point(13.42, 26.5);
        var connectorEnd = new Point(7.38, 56.7);
        dc.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(38, 225, 229, 236)), 3.2),
            connectorStart,
            connectorEnd);
        dc.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(204, 225, 229, 236)), 1.1),
            connectorStart,
            connectorEnd);

        var value = Text($"{Display.PressurePsi:0} PSI", 16, UseStockColors ? Brushes.WhiteSmoke : NumberBrush(),
            FontWeights.SemiBold, FontStyles.Italic);
        const double upperRailVisualBottom = 31;
        var valueTop = upperRailVisualBottom + ((top - upperRailVisualBottom - value.Height) / 2);
        dc.DrawText(value, new Point(20, valueTop));

        var label = Text("BOOST", 12, new SolidColorBrush(Color.FromArgb(210, 224, 228, 236)),
            FontWeights.SemiBold, FontStyles.Italic);
        dc.DrawText(label, new Point(13.85, 69));
    }

    private static Geometry RailGeometry(
        double leftTop,
        double rightTop,
        double top,
        double bottom,
        double endRake)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(new Point(leftTop, top), true, true);
        context.LineTo(new Point(rightTop, top), true, false);
        context.LineTo(new Point(rightTop - endRake, bottom), true, false);
        context.LineTo(new Point(leftTop - endRake, bottom), true, false);
        geometry.Freeze();
        return geometry;
    }
}

public sealed class AnalogBoostGaugeView : BoostVisualBase
{
    private const double StartAngle = 110;
    private const double SweepAngle = 260;
    private const double MaximumPsi = 70;
    private readonly NativeAnalogNeedleVisual _needleMaterial;
    private readonly RotateTransform _needleRotation = new();

    public static readonly DependencyProperty IsElectricMaterialProperty = DependencyProperty.Register(
        nameof(IsElectricMaterial), typeof(bool), typeof(AnalogBoostGaugeView),
        new FrameworkPropertyMetadata(false, OnIsElectricMaterialChanged));

    public AnalogBoostGaugeView()
    {
        ClipToBounds = false;
        var needle = new Canvas
        {
            Width = 288,
            Height = 288,
            ClipToBounds = false,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _needleRotation
        };
        _needleMaterial = new NativeAnalogNeedleVisual
        {
            Width = 110,
            Height = 180
        };
        Canvas.SetLeft(_needleMaterial, 178.5);
        Canvas.SetTop(_needleMaterial, 54);
        needle.Children.Add(_needleMaterial);
        Children.Add(new Viewbox
        {
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            Child = needle
        });
    }

    public bool IsElectricMaterial
    {
        get => (bool)GetValue(IsElectricMaterialProperty);
        set => SetValue(IsElectricMaterialProperty, value);
    }

    private static void OnIsElectricMaterialChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs) =>
        ((AnalogBoostGaugeView)dependencyObject)._needleMaterial.IsElectricMaterial = (bool)eventArgs.NewValue;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!Display.IsAvailable || ActualWidth <= 0 || ActualHeight <= 0) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(ActualWidth, ActualHeight) * 0.43;
        var trackPen = new Pen(new SolidColorBrush(Color.FromArgb(145, 142, 147, 156)), 2.2);
        DrawArc(dc, center, radius, StartAngle, SweepAngle, trackPen);

        var gaugeFraction = Math.Clamp(Display.PressurePsi / MaximumPsi, 0, 1);
        if (gaugeFraction > 0)
        {
            DrawActiveArc(dc, center, radius, gaugeFraction);
        }

        const double minimumPsi = 0;
        const double majorInterval = 10;
        const double minorInterval = 5;
        var majorCount = (int)(MaximumPsi / majorInterval);
        for (var index = 0; index <= majorCount; index++)
        {
            var psi = minimumPsi + index * majorInterval;
            var angle = StartAngle + SweepAngle * (psi / MaximumPsi);
            DrawTick(dc, center, radius, angle, 7, 1.4, psi.ToString("0", CultureInfo.InvariantCulture));
            if (index < majorCount)
            {
                DrawTick(dc, center, radius, angle + SweepAngle * (minorInterval / MaximumPsi), 3.5, 0.8, null);
            }
        }

        var needleAngle = StartAngle + SweepAngle * gaugeFraction;
        _needleRotation.Angle = needleAngle;

        DrawNativeGearRing(dc, center);
        DrawNativeBoostDigits(dc, center);
        var unit = Text("PSI", 9, new SolidColorBrush(Color.FromArgb(190, 185, 193, 207)), FontWeights.SemiBold);
        dc.DrawText(unit, new Point(center.X - unit.Width / 2, center.Y + 30));

        var title = Text("BOOST", 10.5, new SolidColorBrush(Color.FromArgb(205, 210, 215, 225)),
            FontWeights.SemiBold, FontStyles.Italic);
        dc.DrawText(title, new Point(center.X + 27, center.Y + 40));
    }

    private void DrawActiveArc(DrawingContext dc, Point center, double radius, double gaugeFraction)
    {
        var segmentCount = Math.Max(1, (int)Math.Ceiling(SweepAngle * gaugeFraction / 3));
        var learnedScale = Math.Max(Display.LearnedPeakPsi, 5);
        for (var index = 0; index < segmentCount; index++)
        {
            var startFraction = gaugeFraction * index / segmentCount;
            var endFraction = gaugeFraction * (index + 1) / segmentCount;
            var start = StartAngle + (SweepAngle * startFraction);
            var sweep = (SweepAngle * (endFraction - startFraction)) + 0.35;
            var pressure = MaximumPsi * endFraction;
            var colorFraction = Math.Clamp(pressure / learnedScale, 0, 1);
            var arc = ArcGeometry(center, radius, start, sweep);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(PaletteColor(colorFraction, 58)), 8), arc);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(PaletteColor(colorFraction)), 3.2), arc);
        }
    }

    private static void DrawNativeGearRing(DrawingContext dc, Point center)
    {
        const double assetSize = 84;
        var outer = new EllipseGeometry(center, 29, 29);
        var inner = new EllipseGeometry(center, 22.5, 22.5);
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner));
        dc.DrawImage(
            NativeAssetCache.Get(NativeGaugeMode.Analogue, "HUD_Dial_Analog_Gear_1.png"),
            new Rect(center.X - assetSize / 2, center.Y - assetSize / 2, assetSize, assetSize));
        dc.Pop();
    }

    private void DrawNativeBoostDigits(DrawingContext dc, Point center)
    {
        var pressure = Math.Clamp(
            (int)Math.Round(Display.PressurePsi, MidpointRounding.AwayFromZero),
            0,
            (int)MaximumPsi);
        var digits = pressure.ToString("00", CultureInfo.InvariantCulture);
        var tint = NumberColor();
        const double height = 28;
        const double width = 18;
        const double gap = -1;
        var left = center.X - ((width * digits.Length) + (gap * (digits.Length - 1))) / 2;
        foreach (var digit in digits)
        {
            var image = NativeAssetCache.GetTinted(
                NativeGaugeMode.Analogue,
                $"HUD_Dial_Speed_Analogue_{digit}.png",
                tint);
            dc.DrawImage(image, new Rect(left, center.Y - height / 2, width, height));
            left += width + gap;
        }
    }

    private void DrawTick(DrawingContext dc, Point center, double radius, double angle,
        double length, double thickness, string? label)
    {
        var brush = new SolidColorBrush(Color.FromArgb(185, 190, 195, 205));
        dc.DrawLine(new Pen(brush, thickness), Polar(center, radius - length, angle), Polar(center, radius, angle));
        if (label is null) return;
        var text = Text(label, 7, brush, FontWeights.SemiBold);
        var point = Polar(center, radius - 15, angle);
        dc.DrawText(text, new Point(point.X - text.Width / 2, point.Y - text.Height / 2));
    }

    private static Point Polar(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }

    private static void DrawArc(DrawingContext dc, Point center, double radius, double start, double sweep, Pen pen) =>
        dc.DrawGeometry(null, pen, ArcGeometry(center, radius, start, sweep));

    private static Geometry ArcGeometry(Point center, double radius, double start, double sweep)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(Polar(center, radius, start), false, false);
        context.ArcTo(Polar(center, radius, start + sweep), new Size(radius, radius), 0,
            sweep > 180, SweepDirection.Clockwise, true, false);
        geometry.Freeze();
        return geometry;
    }

}
