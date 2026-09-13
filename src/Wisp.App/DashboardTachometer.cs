using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

public sealed class DashboardTachometer : FrameworkElement
{
    private static readonly Typeface TickTypeface = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly RectangleGeometry _progressClip = new();
    private readonly RectangleGeometry _redlineClip = new();
    private Size _cachedSize;
    private double _cachedMaximumRpm = double.NaN;
    private double _cachedPixelsPerDip;
    private double? _cachedRedlineProgress;
    private StreamGeometry? _segments;
    private StreamGeometry? _segmentHighlights;
    private StreamGeometry? _redlineTicks;
    private StreamGeometry? _ticks;
    private Geometry[] _labels = [];
    private Pen? _tickPen, _redlinePen, _highlightPen;
    private Brush? _cachedForeground, _cachedRedline;

    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(NativeGaugeFrame), typeof(DashboardTachometer),
        new FrameworkPropertyMetadata(default(NativeGaugeFrame), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.Register(
        nameof(IsAvailable), typeof(bool), typeof(DashboardTachometer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(DashboardTachometer),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(DashboardTachometer),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(DashboardTachometer),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RedlineProperty = DependencyProperty.Register(
        nameof(Redline), typeof(Brush), typeof(DashboardTachometer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DashboardTachometer()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public NativeGaugeFrame Frame { get => (NativeGaugeFrame)GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public bool IsAvailable { get => (bool)GetValue(IsAvailableProperty); set => SetValue(IsAvailableProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush? Redline { get => (Brush?)GetValue(RedlineProperty); set => SetValue(RedlineProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0)
            return;

        var reading = ResolveReading(Frame, IsAvailable);
        EnsureGeometry(RenderSize, reading, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (!reading.IsAvailable)
            return;
        EnsurePens();

        if (reading.Progress > 0)
        {
            var current = PointOnArc(RenderSize, reading.Progress);
            var bounds = new Rect(-4, -4, current.X + 4, RenderSize.Height + 8);
            if (_progressClip.Rect != bounds)
                _progressClip.Rect = bounds;
            drawingContext.PushClip(_progressClip);
            drawingContext.DrawGeometry(Accent, null, _segments);
            if (reading.RedlineProgress.HasValue)
            {
                drawingContext.PushClip(_redlineClip);
                drawingContext.DrawGeometry(Redline ?? Accent, null, _segments);
                drawingContext.Pop();
            }
            drawingContext.PushOpacity(0.28);
            drawingContext.DrawGeometry(null, _highlightPen, _segmentHighlights);
            drawingContext.Pop();
            drawingContext.Pop();
        }

        drawingContext.PushOpacity(0.8);
        drawingContext.DrawGeometry(null, _tickPen, _ticks);
        drawingContext.Pop();
        drawingContext.DrawGeometry(null, _redlinePen, _redlineTicks);
        foreach (var label in _labels)
            drawingContext.DrawGeometry(Foreground, null, label);
    }

    private void EnsurePens()
    {
        if (_tickPen is null || !ReferenceEquals(_cachedForeground, Foreground))
        {
            _cachedForeground = Foreground;
            _tickPen = CreatePen(Foreground, 1);
            _highlightPen = CreatePen(Foreground, 0.7);
        }
        var redline = Redline ?? Accent;
        if (_redlinePen is null || !ReferenceEquals(_cachedRedline, redline))
        {
            _cachedRedline = redline;
            _redlinePen = CreatePen(redline, 1);
        }
    }

    private static Pen CreatePen(Brush brush, double thickness, PenLineCap cap = PenLineCap.Flat)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = cap, EndLineCap = cap };
        if (brush.IsFrozen)
            pen.Freeze();
        return pen;
    }

    private void EnsureGeometry(Size size, DashboardTachometerReading reading, double pixelsPerDip)
    {
        if (_segments is not null && _cachedSize == size && _cachedMaximumRpm == reading.ScaleMaximumRpm &&
            _cachedRedlineProgress == reading.RedlineProgress && _cachedPixelsPerDip == pixelsPerDip)
            return;

        _cachedSize = size;
        _cachedMaximumRpm = reading.ScaleMaximumRpm;
        _cachedRedlineProgress = reading.RedlineProgress;
        _cachedPixelsPerDip = pixelsPerDip;
        var thickness = 10 * Math.Min(1, size.Height / 100);
        var scaleValues = MajorTickValues(reading.ScaleMaximumRpm);
        var step = scaleValues.Length >= 2 ? scaleValues[1] / 10 : 1;
        var count = scaleValues.Length >= 2 ? (int)Math.Ceiling(reading.ScaleMaximumRpm / step) : 0;
        _segments = new StreamGeometry();
        _segmentHighlights = new StreamGeometry();
        using (var segments = _segments.Open())
        using (var highlights = _segmentHighlights.Open())
        {
            for (var index = 0; index < count; index++)
            {
                var startRpm = index * step;
                var spanRpm = Math.Min(step, reading.ScaleMaximumRpm - startRpm);
                var first = (startRpm + 0.06 * spanRpm) / reading.ScaleMaximumRpm;
                var last = (startRpm + 0.94 * spanRpm) / reading.ScaleMaximumRpm;
                AppendBandSegment(segments, size, first, last, thickness);
                var start = PointOnArc(size, first);
                var end = PointOnArc(size, last);
                var normal = new Vector(start.Y - end.Y, end.X - start.X);
                normal.Normalize();
                normal *= thickness / 2;
                highlights.BeginFigure(start - normal, false, false);
                highlights.LineTo(end - normal, true, false);
            }
        }
        _segments.Freeze();
        _segmentHighlights.Freeze();

        _ticks = new StreamGeometry();
        _redlineTicks = new StreamGeometry();
        var labels = new List<Geometry>(6);
        using (var ticks = _ticks.Open())
        using (var redlineTicks = _redlineTicks.Open())
        {
            var values = size.Height < 32 || size.Width < 100 ? [] : scaleValues;
            if (size.Width < 280 && values.Length > 2)
                values = [values[0], values[^1]];
            if (reading.IsAvailable && scaleValues.Length >= 2)
            {
                for (var index = 0; index <= count; index++)
                {
                    var value = Math.Min(index * step, reading.ScaleMaximumRpm);
                    var fraction = value / reading.ScaleMaximumRpm;
                    var major = Array.Exists(scaleValues, mark => Math.Abs(mark - value) < 0.001);
                    var warning = reading.RedlineProgress is { } redline && fraction >= redline;
                    AppendTick(warning ? redlineTicks : ticks, size, fraction,
                        thickness / 2, thickness / 2 + (major ? 7 : 4));
                }
            }
            foreach (var value in values)
            {
                var fraction = value / reading.ScaleMaximumRpm;
                var caption = value >= 1000 ? (value / 1000).ToString("0.##", CultureInfo.InvariantCulture) + "k" :
                    value.ToString("0", CultureInfo.InvariantCulture);
                var text = new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    TickTypeface, 16, Brushes.White, pixelsPerDip);
                var label = text.BuildGeometry(TickLabelOrigin(size, fraction,
                    new Size(text.WidthIncludingTrailingWhitespace, text.Height)));
                label.Freeze();
                labels.Add(label);
            }
            if (reading.RedlineProgress is { } exact)
            {
                var point = PointOnArc(size, exact);
                _redlineClip.Rect = new Rect(point.X, -4, Math.Max(0, size.Width - point.X + 4), size.Height + 8);
            }
        }
        _ticks.Freeze();
        _redlineTicks.Freeze();
        _labels = labels.ToArray();
    }

    private static void AppendTick(StreamGeometryContext context, Size size, double fraction, double first, double last)
    {
        var point = PointOnArc(size, fraction);
        var tangent = PointOnArc(size, Math.Min(1, fraction + 0.001)) -
            PointOnArc(size, Math.Max(0, fraction - 0.001));
        var normal = new Vector(-tangent.Y, tangent.X);
        if (normal.LengthSquared <= 0)
            return;
        normal.Normalize();
        context.BeginFigure(point + normal * first, false, false);
        context.LineTo(point + normal * last, true, false);
    }

    internal static Point TickLabelOrigin(Size size, double fraction, Size labelSize)
    {
        var point = PointOnArc(size, fraction);
        return new Point(
            Math.Clamp(point.X - labelSize.Width / 2, 0, Math.Max(0, size.Width - labelSize.Width)),
            Math.Clamp(point.Y + 15, 0, Math.Max(0, size.Height - labelSize.Height)));
    }

    private static void AppendBandSegment(StreamGeometryContext context, Size size, double first, double last, double thickness)
    {
        var start = PointOnArc(size, first);
        var end = PointOnArc(size, last);
        var normal = new Vector(start.Y - end.Y, end.X - start.X);
        if (normal.LengthSquared <= 0)
            return;
        normal.Normalize();
        normal *= thickness / 2;
        context.BeginFigure(start - normal, true, true);
        context.LineTo(end - normal, true, false);
        context.LineTo(end + normal, true, false);
        context.LineTo(start + normal, true, false);
    }

    internal static double[] MajorTickValues(double maximumRpm)
    {
        if (!double.IsFinite(maximumRpm) || maximumRpm <= 0)
            return [];
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(maximumRpm / 5)));
        var raw = maximumRpm / 5 / magnitude;
        var step = (raw <= 1 ? 1 : raw <= 2 ? 2 : raw <= 2.5 ? 2.5 : raw <= 5 ? 5 : 10) * magnitude;
        var values = new List<double>(6);
        for (var index = 0; index < 6 && index * step < maximumRpm; index++)
            values.Add(index * step);
        values.Add(maximumRpm);
        return values.ToArray();
    }

    internal static DashboardTachometerReading ResolveReading(NativeGaugeFrame frame, bool available)
    {
        if (!available || frame.IsElectric || frame.NativeGaugeSourceInvalidated ||
            !double.IsFinite(frame.EngineRpm) || !double.IsFinite(frame.TachometerMaximumRpm) || frame.TachometerMaximumRpm <= 0)
            return default;

        var maximumRpm = NativeGaugeGeometry.ScaleMaximumThousands(frame.TachometerMaximumRpm) * 1000d;
        var rpm = Math.Clamp(frame.EngineRpm, 0, maximumRpm);
        var redlineRpm = frame.ExactRedline.Rpm;
        double? redline = frame.ExactRedline.IsExact && double.IsFinite(redlineRpm) && redlineRpm > 0 && redlineRpm <= maximumRpm
            ? redlineRpm / maximumRpm : null;
        return new DashboardTachometerReading(true, rpm, maximumRpm, rpm / maximumRpm, redline);
    }

    internal static Point PointOnArc(Size size, double fraction)
    {
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
            return default;
        var progress = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        var inset = Math.Min(6, size.Width / 4);
        var verticalScale = Math.Min(1, size.Height / 100);
        return new Point(inset + (size.Width - 2 * inset) * progress,
            (60 - 10 * progress - 172 * progress * (1 - progress)) * verticalScale);
    }
}

internal readonly record struct DashboardTachometerReading(
    bool IsAvailable, double Rpm, double ScaleMaximumRpm, double Progress, double? RedlineProgress);
