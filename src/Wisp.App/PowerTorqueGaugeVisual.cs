using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wisp.App;

public sealed class PowerTorqueGaugeView : Grid
{
    internal const double StartAngle = 135;
    internal const double SweepAngle = 270;
    private const double DesignSize = 140;
    private static readonly Point Center = new(70, 70);
    private static readonly Brush ScaleBrush = FrozenBrush(Color.FromArgb(185, 190, 195, 205));
    private static readonly Brush TrackBrush = FrozenBrush(Color.FromArgb(145, 142, 147, 156));
    private static readonly Brush LabelBrush = FrozenBrush(Color.FromArgb(215, 210, 215, 225));
    private readonly RotateTransform _needleRotation = new(StartAngle);
    private readonly NativeAnalogNeedleVisual _needleMaterial;
    private readonly Viewbox _needleView;
    private DrawingGroup? _dial;
    private double _dialPixelsPerDip;

    public static readonly DependencyProperty DisplayProperty = DependencyProperty.Register(
        nameof(Display), typeof(PowerTorqueDisplay), typeof(PowerTorqueGaugeView),
        new FrameworkPropertyMetadata(PowerTorqueDisplay.Unavailable, OnDisplayChanged));
    public static readonly DependencyProperty IsTorqueProperty = DependencyProperty.Register(
        nameof(IsTorque), typeof(bool), typeof(PowerTorqueGaugeView),
        new FrameworkPropertyMetadata(false, OnDialChanged));
    public static readonly DependencyProperty TorqueUnitProperty = DependencyProperty.Register(
        nameof(TorqueUnit), typeof(TorqueUnit), typeof(PowerTorqueGaugeView),
        new FrameworkPropertyMetadata(TorqueUnit.NewtonMeters, OnDialChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(PowerTorqueGaugeView),
        new FrameworkPropertyMetadata(1_000d, OnDialChanged),
        value => value is double maximum && double.IsFinite(maximum) && maximum > 0);
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(PowerTorqueGaugeView),
        new FrameworkPropertyMetadata(FrozenBrush(Color.FromRgb(162, 221, 245)),
            FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsElectricMaterialProperty = DependencyProperty.Register(
        nameof(IsElectricMaterial), typeof(bool), typeof(PowerTorqueGaugeView),
        new FrameworkPropertyMetadata(false, OnMaterialChanged));

    public PowerTorqueGaugeView()
    {
        ClipToBounds = false;
        IsHitTestVisible = false;
        var needle = new Canvas
        {
            Width = 288,
            Height = 288,
            ClipToBounds = false,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _needleRotation
        };
        _needleMaterial = new NativeAnalogNeedleVisual { Width = 110, Height = 180 };
        Canvas.SetLeft(_needleMaterial, 178.5);
        Canvas.SetTop(_needleMaterial, 54);
        needle.Children.Add(_needleMaterial);
        _needleView = new Viewbox
        {
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            Visibility = Visibility.Hidden,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.8, 0.8),
            Child = needle
        };
        Children.Add(_needleView);
    }

    public PowerTorqueDisplay Display
    {
        get => (PowerTorqueDisplay)GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }
    public bool IsTorque
    {
        get => (bool)GetValue(IsTorqueProperty);
        set => SetValue(IsTorqueProperty, value);
    }
    public TorqueUnit TorqueUnit
    {
        get => (TorqueUnit)GetValue(TorqueUnitProperty);
        set => SetValue(TorqueUnitProperty, value);
    }
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }
    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }
    public bool IsElectricMaterial
    {
        get => (bool)GetValue(IsElectricMaterialProperty);
        set => SetValue(IsElectricMaterialProperty, value);
    }

    internal double CurrentNeedleAngle => _needleRotation.Angle;
    internal bool HasVisibleNeedle => _needleView.Visibility == Visibility.Visible;
    internal double DisplayedValue => Value(Display);
    internal double DisplayedPeak => Peak(Display);

    private double Value(PowerTorqueDisplay display) => IsTorque
        ? PowerTorqueDisplay.ConvertTorque(display.TorqueNm, TorqueUnit) : display.PowerBhp;
    private double Peak(PowerTorqueDisplay display) => IsTorque
        ? PowerTorqueDisplay.ConvertTorque(display.PeakTorqueNm, TorqueUnit) : display.PeakPowerBhp;
    private bool HasReading => Display.Available && double.IsFinite(DisplayedValue);
    private static double Whole(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    private static void OnDisplayChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var gauge = (PowerTorqueGaugeView)sender;
        gauge.UpdateNeedle();
        var old = (PowerTorqueDisplay)args.OldValue;
        var current = (PowerTorqueDisplay)args.NewValue;
        if (old.Available != current.Available ||
            Whole(gauge.Value(old)) != Whole(gauge.Value(current)) ||
            Whole(gauge.Peak(old)) != Whole(gauge.Peak(current)))
            gauge.InvalidateVisual();
    }

    private static void OnDialChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var gauge = (PowerTorqueGaugeView)sender;
        gauge._dial = null;
        gauge.UpdateNeedle();
        gauge.InvalidateVisual();
    }

    private static void OnMaterialChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((PowerTorqueGaugeView)sender)._needleMaterial.IsElectricMaterial = (bool)args.NewValue;

    private void UpdateNeedle()
    {
        _needleView.Visibility = HasReading ? Visibility.Visible : Visibility.Hidden;
        _needleRotation.Angle = NeedleAngle(DisplayedValue, Maximum);
    }

    internal static double NeedleAngle(double value, double maximum) => StartAngle + SweepAngle *
        (double.IsFinite(value) && double.IsFinite(maximum) && maximum > 0
            ? Math.Clamp(value / maximum, 0, 1) : 0);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.PushTransform(new ScaleTransform(ActualWidth / DesignSize, ActualHeight / DesignSize));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_dial is null || _dialPixelsPerDip != dpi)
        {
            _dial = BuildDial();
            _dialPixelsPerDip = dpi;
        }
        dc.DrawDrawing(_dial);
        var peak = DisplayedPeak;
        if (double.IsFinite(peak) && peak > 0)
        {
            var angle = NeedleAngle(peak, Maximum);
            dc.DrawLine(new Pen(AccentBrush, 2), Polar(58, angle), Polar(64, angle));
        }
        DrawValue(dc);
        var peakText = double.IsFinite(peak) && peak > 0
            ? $"PEAK {Whole(peak).ToString("0", CultureInfo.InvariantCulture)}" : "PEAK —";
        DrawCentered(dc, Text(peakText, 8.5, LabelBrush), 126);
        dc.Pop();
    }

    private DrawingGroup BuildDial()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            var arc = new StreamGeometry();
            using (var path = arc.Open())
            {
                path.BeginFigure(Polar(60.2, StartAngle), false, false);
                path.ArcTo(Polar(60.2, StartAngle + SweepAngle), new Size(60.2, 60.2),
                    0, true, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, new Pen(TrackBrush, 2.2), arc);
            for (var index = 0; index <= 20; index++)
            {
                var angle = StartAngle + SweepAngle * index / 20;
                var major = index % 4 == 0;
                dc.DrawLine(new Pen(ScaleBrush, major ? 1.2 : 0.7),
                    Polar(major ? 53.2 : 56.7, angle), Polar(60.2, angle));
                if (!major) continue;
                var label = Text((Maximum * index / 20).ToString("0.#", CultureInfo.InvariantCulture), 7, ScaleBrush);
                var point = Polar(45, angle);
                dc.DrawText(label, new Point(point.X - label.Width / 2, point.Y - label.Height / 2));
            }
            var outer = new EllipseGeometry(Center, 29, 29);
            var inner = new EllipseGeometry(Center, 22.5, 22.5);
            dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner));
            dc.PushOpacity(0.35);
            dc.DrawImage(NativeAssetCache.Get(NativeGaugeMode.Analogue, "HUD_Dial_Analog_Gear_1.png"),
                new Rect(28, 28, 84, 84));
            dc.Pop();
            dc.Pop();
            DrawCentered(dc, Text(IsTorque ? TorqueUnit == TorqueUnit.PoundFeet ? "lb-ft" : "Nm" : "BHP",
                9, LabelBrush), 101);
            DrawCentered(dc, Text(IsTorque ? "TORQUE" : "POWER", 10.5, LabelBrush, FontStyles.Italic), 112);
        }
        drawing.Freeze();
        return drawing;
    }

    private void DrawValue(DrawingContext dc)
    {
        if (!HasReading)
        {
            DrawCentered(dc, Text("—", 28, Brushes.WhiteSmoke), 50);
            return;
        }
        var number = Whole(DisplayedValue).ToString("0", CultureInfo.InvariantCulture);
        const double width = 18;
        const double height = 28;
        const double gap = -1;
        var totalWidth = number.Sum(character => character == '-' ? 8 : width) + gap * (number.Length - 1);
        var scale = Math.Min(1, 48 / totalWidth);
        dc.PushTransform(new ScaleTransform(scale, scale, Center.X, Center.Y));
        var left = Center.X - totalWidth / 2;
        foreach (var digit in number)
        {
            if (digit == '-')
            {
                dc.DrawLine(new Pen(Brushes.WhiteSmoke, 2), new Point(left + 1, Center.Y), new Point(left + 7, Center.Y));
                left += 8 + gap;
                continue;
            }
            dc.DrawImage(NativeAssetCache.Get(NativeGaugeMode.Analogue, $"HUD_Dial_Speed_Analogue_{digit}.png"),
                new Rect(left, Center.Y - height / 2, width, height));
            left += width + gap;
        }
        dc.Pop();
    }

    private FormattedText Text(string text, double size, Brush brush, FontStyle? style = null) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Bahnschrift SemiCondensed, Bahnschrift, Segoe UI"),
            style ?? FontStyles.Normal, FontWeights.SemiBold, FontStretches.Condensed),
        size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static void DrawCentered(DrawingContext dc, FormattedText text, double top) =>
        dc.DrawText(text, new Point(Center.X - text.Width / 2, top));

    private static Point Polar(double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(Center.X + Math.Cos(radians) * radius, Center.Y + Math.Sin(radians) * radius);
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
