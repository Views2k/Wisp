using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wisp.App;

public abstract class TireTemperatureVisualBase : Grid
{
    public static readonly DependencyProperty DisplayProperty = DependencyProperty.Register(
        nameof(Display), typeof(TireTemperatureDisplay), typeof(TireTemperatureVisualBase),
        new FrameworkPropertyMetadata(
            TireTemperatureDisplay.Unavailable,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TemperatureUnitProperty = DependencyProperty.Register(
        nameof(TemperatureUnit), typeof(TireTemperatureUnit), typeof(TireTemperatureVisualBase),
        new FrameworkPropertyMetadata(
            TireTemperatureUnit.Fahrenheit,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ReactiveColorsProperty = DependencyProperty.Register(
        nameof(ReactiveColors), typeof(bool), typeof(TireTemperatureVisualBase),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsAttachedProperty = DependencyProperty.Register(
        nameof(IsAttached), typeof(bool), typeof(TireTemperatureVisualBase),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LowBrushProperty = DependencyProperty.Register(
        nameof(LowBrush), typeof(Brush), typeof(TireTemperatureVisualBase),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MidBrushProperty = DependencyProperty.Register(
        nameof(MidBrush), typeof(Brush), typeof(TireTemperatureVisualBase),
        new FrameworkPropertyMetadata(Brushes.RoyalBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighBrushProperty = DependencyProperty.Register(
        nameof(HighBrush), typeof(Brush), typeof(TireTemperatureVisualBase),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public TireTemperatureDisplay Display
    {
        get => (TireTemperatureDisplay)GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }

    public TireTemperatureUnit TemperatureUnit
    {
        get => (TireTemperatureUnit)GetValue(TemperatureUnitProperty);
        set => SetValue(TemperatureUnitProperty, value);
    }

    public bool ReactiveColors
    {
        get => (bool)GetValue(ReactiveColorsProperty);
        set => SetValue(ReactiveColorsProperty, value);
    }

    public bool IsAttached
    {
        get => (bool)GetValue(IsAttachedProperty);
        set => SetValue(IsAttachedProperty, value);
    }

    public Brush LowBrush { get => (Brush)GetValue(LowBrushProperty); set => SetValue(LowBrushProperty, value); }
    public Brush MidBrush { get => (Brush)GetValue(MidBrushProperty); set => SetValue(MidBrushProperty, value); }
    public Brush HighBrush { get => (Brush)GetValue(HighBrushProperty); set => SetValue(HighBrushProperty, value); }

    protected FormattedText Text(
        string value,
        double size,
        Brush brush,
        FontWeight weight = default,
        FontStyle style = default) =>
        new(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily("Bahnschrift SemiCondensed, Bahnschrift, Segoe UI"),
                style == default ? FontStyles.Normal : style,
                weight == default ? FontWeights.Normal : weight,
                FontStretches.Condensed),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected string UnitSymbol => TireTemperatureDisplay.UnitSymbol(TemperatureUnit);

    protected string TemperatureText(double fahrenheit) =>
        Math.Round(
                TireTemperatureDisplay.ConvertForReadout(fahrenheit, TemperatureUnit),
                MidpointRounding.AwayFromZero)
            .ToString("0", CultureInfo.InvariantCulture);

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

    protected static Color BrushColor(Brush source) =>
        source is SolidColorBrush solid ? solid.Color : Colors.DodgerBlue;
}

public sealed class DigitalTireTemperatureGaugeView : TireTemperatureVisualBase
{
    private const double LeftTop = 9.05;
    private const double RightTop = 292.05;
    private const double HousingTop = 58.75;
    private const double HousingBottom = 65.25;
    private const double EndRake = 1.3;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!Display.IsAvailable || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        if (IsAttached)
        {
            // Match the boost connector's rake while leaving the same clean
            // clearance above the rail. The shorter lead keeps the stacked
            // gauges connected without forming a tall left-side bracket.
            var connectorStart = new Point(12.28, 32.5);
            var connectorEnd = new Point(7.38, 56.7);
            dc.DrawLine(
                new Pen(new SolidColorBrush(Color.FromArgb(36, 225, 229, 236)), 3.2),
                connectorStart,
                connectorEnd);
            dc.DrawLine(
                new Pen(new SolidColorBrush(Color.FromArgb(204, 225, 229, 236)), 1.1),
                connectorStart,
                connectorEnd);
        }

        var frontText = Text(
            $"F  {TemperatureText(Display.FrontFahrenheit)}°",
            14,
            new SolidColorBrush(Color.FromArgb(238, 244, 246, 250)),
            FontWeights.SemiBold,
            FontStyles.Italic);
        dc.DrawText(frontText, new Point(78, 34));

        var rearText = Text(
            $"REAR  {TemperatureText(Display.RearFahrenheit)}°",
            14,
            new SolidColorBrush(Color.FromArgb(220, 224, 228, 236)),
            FontWeights.SemiBold,
            FontStyles.Italic);
        dc.DrawText(rearText, new Point(RightTop - rearText.Width - 1, 34));

        var housing = RailGeometry(LeftTop, RightTop, HousingTop, HousingBottom, EndRake);
        dc.DrawGeometry(
            new SolidColorBrush(Color.FromArgb(34, 236, 239, 244)),
            new Pen(new SolidColorBrush(Color.FromArgb(168, 238, 241, 246)), 1.0),
            housing);

        DrawMarker(dc, Display.RearFraction, MidBrush, 0.76);
        DrawMarker(dc, Display.FrontFraction, LowBrush, 1);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(168, 238, 241, 246)), 1.0), housing);

        var label = Text(
            $"TIRE TEMP  {UnitSymbol}",
            11.5,
            new SolidColorBrush(Color.FromArgb(210, 224, 228, 236)),
            FontWeights.SemiBold,
            FontStyles.Italic);
        dc.DrawText(label, new Point(13.85, 66));
    }

    private void DrawMarker(DrawingContext dc, double fraction, Brush paletteBrush, double strength)
    {
        var activeRight = LeftTop + ((RightTop - LeftTop) * Math.Clamp(fraction, 0, 1));
        var markerTop = new Point(activeRight, HousingTop - 0.2);
        var markerBottom = new Point(activeRight - EndRake, HousingBottom + 0.2);
        var color = !ReactiveColors || IsStockPalette
            ? Color.FromRgb(248, 250, 253)
            : BrushColor(paletteBrush);
        dc.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(28 * strength), color.R, color.G, color.B)), 7),
            markerTop,
            markerBottom);
        dc.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(72 * strength), color.R, color.G, color.B)), 3.5),
            markerTop,
            markerBottom);
        dc.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(242 * strength), color.R, color.G, color.B)), 1.3),
            markerTop,
            markerBottom);
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

public sealed class AnalogTireTemperatureGaugeView : TireTemperatureVisualBase
{
    private const double StartAngle = 110;
    private const double SweepAngle = 260;

    public static readonly DependencyProperty IsElectricMaterialProperty = DependencyProperty.Register(
        nameof(IsElectricMaterial), typeof(bool), typeof(AnalogTireTemperatureGaugeView),
        new FrameworkPropertyMetadata(false, OnIsElectricMaterialChanged));

    public AnalogTireTemperatureGaugeView()
    {
        ClipToBounds = false;
    }

    public bool IsElectricMaterial
    {
        get => (bool)GetValue(IsElectricMaterialProperty);
        set => SetValue(IsElectricMaterialProperty, value);
    }

    private static void OnIsElectricMaterialChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        ((AnalogTireTemperatureGaugeView)dependencyObject).InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!Display.IsAvailable || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(ActualWidth, ActualHeight) * 0.43;
        DrawArc(
            dc,
            center,
            radius,
            StartAngle,
            SweepAngle,
            new Pen(new SolidColorBrush(Color.FromArgb(145, 142, 147, 156)), 2.2));

        DrawTicks(dc, center, radius);

        var frontAngle = StartAngle + SweepAngle * Display.FrontFraction;
        var rearAngle = StartAngle + SweepAngle * Display.RearFraction;
        var frontColor = NeedleColor(LowBrush, 242);
        var rearColor = NeedleColor(MidBrush, 226);
        DrawNativeProfileNeedle(dc, center, radius, rearAngle, rearColor, 0.86);
        DrawNativeProfileNeedle(dc, center, radius, frontAngle, frontColor, 1);

        DrawNeedleLabel(dc, center, radius, frontAngle, "F", frontColor);
        DrawNeedleLabel(dc, center, radius, rearAngle, "R", rearColor);
        DrawNativeGearRing(dc, center);
        DrawReadout(dc, center);

        var unit = Text(
            UnitSymbol,
            8.5,
            new SolidColorBrush(Color.FromArgb(190, 185, 193, 207)),
            FontWeights.SemiBold);
        dc.DrawText(unit, new Point(center.X - unit.Width / 2, center.Y + 36));

        var title = Text(
            "TIRE TEMP",
            9.5,
            new SolidColorBrush(Color.FromArgb(205, 210, 215, 225)),
            FontWeights.SemiBold,
            FontStyles.Italic);
        dc.DrawText(title, new Point(center.X + 23, center.Y + 43));
    }

    private void DrawTicks(DrawingContext dc, Point center, double radius)
    {
        const int majorCount = 6;
        for (var index = 0; index <= majorCount; index++)
        {
            var fraction = index / (double)majorCount;
            var fahrenheit = TireTemperatureDisplay.MinimumFahrenheit +
                             (TireTemperatureDisplay.MaximumFahrenheit - TireTemperatureDisplay.MinimumFahrenheit) *
                             fraction;
            var angle = StartAngle + SweepAngle * fraction;
            var label = Math.Round(
                    TireTemperatureDisplay.Convert(fahrenheit, TemperatureUnit),
                    MidpointRounding.AwayFromZero)
                .ToString("0", CultureInfo.InvariantCulture);
            DrawTick(dc, center, radius, angle, 7, 1.4, label);
            if (index < majorCount)
            {
                DrawTick(dc, center, radius, angle + SweepAngle / majorCount / 2, 3.5, 0.8, null);
            }
        }
    }

    private Color NeedleColor(Brush paletteBrush, byte alpha)
    {
        var color = !ReactiveColors || IsStockPalette
            ? Color.FromRgb(244, 246, 250)
            : BrushColor(paletteBrush);
        color.A = alpha;
        return color;
    }

    private static void DrawNativeProfileNeedle(
        DrawingContext dc,
        Point center,
        double radius,
        double angle,
        Color color,
        double strength)
    {
        // Use one solid tapered profile. Layered line caps left visible blocks
        // at the inner endpoint when the small gauge was scaled in-game.
        var inner = Polar(center, radius * 0.57, angle);
        var outer = Polar(center, radius - 4.5, angle);
        var direction = outer - inner;
        direction.Normalize();
        var perpendicular = new Vector(-direction.Y, direction.X);
        var shoulder = inner + (direction * 2.8);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(inner, true, true);
            context.LineTo(shoulder + (perpendicular * 0.95), true, false);
            context.LineTo(outer + (perpendicular * 0.7), true, false);
            context.LineTo(outer - (perpendicular * 0.7), true, false);
            context.LineTo(shoulder - (perpendicular * 0.95), true, false);
        }
        geometry.Freeze();

        var needleColor = color;
        needleColor.A = (byte)Math.Round(color.A * strength);
        dc.DrawGeometry(new SolidColorBrush(needleColor), null, geometry);
    }

    private void DrawNeedleLabel(
        DrawingContext dc,
        Point center,
        double radius,
        double angle,
        string label,
        Color color)
    {
        var text = Text(label, 8, new SolidColorBrush(color), FontWeights.Bold);
        var point = Polar(center, radius - 19, angle);
        dc.DrawText(text, new Point(point.X - text.Width / 2, point.Y - text.Height / 2));
    }

    private void DrawReadout(DrawingContext dc, Point center)
    {
        var labelBrush = new SolidColorBrush(Color.FromArgb(205, 210, 215, 225));
        var frontLabel = Text("FRONT", 5.8, labelBrush, FontWeights.Bold);
        var rearLabel = Text("REAR", 5.8, labelBrush, FontWeights.Bold);
        dc.DrawText(frontLabel, new Point(center.X - 21, center.Y - 18));
        dc.DrawText(rearLabel, new Point(center.X - 21, center.Y + 5));

        DrawNativeDigits(dc, TemperatureText(Display.FrontFahrenheit), new Point(center.X + 8, center.Y - 11));
        DrawNativeDigits(dc, TemperatureText(Display.RearFahrenheit), new Point(center.X + 8, center.Y + 12));
        dc.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(92, 232, 235, 241)), 0.8),
            new Point(center.X - 27, center.Y),
            new Point(center.X + 28, center.Y));
    }

    private static void DrawNativeGearRing(DrawingContext dc, Point center)
    {
        const double assetSize = 92;
        var outer = new EllipseGeometry(center, 34, 34);
        var inner = new EllipseGeometry(center, 28, 28);
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner));
        dc.DrawImage(
            NativeAssetCache.Get(NativeGaugeMode.Analogue, "HUD_Dial_Analog_Gear_1.png"),
            new Rect(center.X - assetSize / 2, center.Y - assetSize / 2, assetSize, assetSize));
        dc.Pop();
    }

    private static void DrawNativeDigits(DrawingContext dc, string digits, Point center)
    {
        const double height = 16;
        const double width = 10.3;
        const double gap = -0.5;
        var left = center.X - ((width * digits.Length) + (gap * (digits.Length - 1))) / 2;
        foreach (var digit in digits)
        {
            var image = NativeAssetCache.GetTinted(
                NativeGaugeMode.Analogue,
                $"HUD_Dial_Speed_Analogue_{digit}.png",
                Color.FromArgb(242, 248, 250, 253));
            dc.DrawImage(image, new Rect(left, center.Y - height / 2, width, height));
            left += width + gap;
        }
    }

    private void DrawTick(
        DrawingContext dc,
        Point center,
        double radius,
        double angle,
        double length,
        double thickness,
        string? label)
    {
        var brush = new SolidColorBrush(Color.FromArgb(185, 190, 195, 205));
        dc.DrawLine(new Pen(brush, thickness), Polar(center, radius - length, angle), Polar(center, radius, angle));
        if (label is null)
        {
            return;
        }

        var text = Text(label, 6.2, brush, FontWeights.SemiBold);
        var point = Polar(center, radius - 14.5, angle);
        dc.DrawText(text, new Point(point.X - text.Width / 2, point.Y - text.Height / 2));
    }

    private static Point Polar(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }

    private static void DrawArc(
        DrawingContext dc,
        Point center,
        double radius,
        double start,
        double sweep,
        Pen pen) =>
        dc.DrawGeometry(null, pen, ArcGeometry(center, radius, start, sweep));

    private static Geometry ArcGeometry(Point center, double radius, double start, double sweep)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(Polar(center, radius, start), false, false);
        context.ArcTo(
            Polar(center, radius, start + sweep),
            new Size(radius, radius),
            0,
            sweep > 180,
            SweepDirection.Clockwise,
            true,
            false);
        geometry.Freeze();
        return geometry;
    }
}
