using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp.App.Runs;

public sealed class RunChartView : FrameworkElement
{
    private StreamGeometry? _gapGeometry;
    public static readonly DependencyProperty PanelProperty = DependencyProperty.Register(nameof(Panel), typeof(RunPlotPanel), typeof(RunChartView), new FrameworkPropertyMetadata(null, InvalidatePlot));
    public static readonly DependencyProperty StartSecondsProperty = DependencyProperty.Register(nameof(StartSeconds), typeof(double), typeof(RunChartView), new FrameworkPropertyMetadata(0d, InvalidatePlot));
    public static readonly DependencyProperty EndSecondsProperty = DependencyProperty.Register(nameof(EndSeconds), typeof(double), typeof(RunChartView), new FrameworkPropertyMetadata(1d, InvalidatePlot));
    public static readonly DependencyProperty CursorSecondsProperty = DependencyProperty.Register(nameof(CursorSeconds), typeof(double), typeof(RunChartView), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(nameof(SelectionStart), typeof(double), typeof(RunChartView), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(nameof(SelectionEnd), typeof(double), typeof(RunChartView), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(RunChartView), new FrameworkPropertyMetadata(Brushes.Turquoise, InvalidatePlot));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(RunChartView), new FrameworkPropertyMetadata(Brushes.WhiteSmoke, InvalidatePlot));
    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(nameof(MutedBrush), typeof(Brush), typeof(RunChartView), new FrameworkPropertyMetadata(Brushes.SlateGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WarningBrushProperty = DependencyProperty.Register(nameof(WarningBrush), typeof(Brush), typeof(RunChartView), new FrameworkPropertyMetadata(Brushes.Orange, InvalidatePlot));
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(nameof(Markers), typeof(IEnumerable<RunPlotMarker>), typeof(RunChartView), new FrameworkPropertyMetadata(Array.Empty<RunPlotMarker>(), FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly List<(Geometry Geometry, Pen Pen)> _paths = [];
    private Size _preparedSize;
    private bool _prepared;
    private double? _dragStart;
    private double? _dragEnd;
    public event Action<double, double>? IntervalSelected;
    public RunPlotPanel? Panel { get => (RunPlotPanel?)GetValue(PanelProperty); set => SetValue(PanelProperty, value); }
    public double StartSeconds { get => (double)GetValue(StartSecondsProperty); set => SetValue(StartSecondsProperty, value); }
    public double EndSeconds { get => (double)GetValue(EndSecondsProperty); set => SetValue(EndSecondsProperty, value); }
    public double CursorSeconds { get => (double)GetValue(CursorSecondsProperty); set => SetValue(CursorSecondsProperty, value); }
    public double SelectionStart { get => (double)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public double SelectionEnd { get => (double)GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public Brush WarningBrush { get => (Brush)GetValue(WarningBrushProperty); set => SetValue(WarningBrushProperty, value); }
    public IEnumerable<RunPlotMarker> Markers { get => (IEnumerable<RunPlotMarker>)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    internal bool RenderOffscreen { get; set; }
    private bool HasComparison => Panel?.Series.Any(series => series.Comparison) == true;
    public RunChartView()
    {
        Focusable = true; Cursor = Cursors.Cross; MinHeight = 180;
        // A chart measured while its pane is hidden may retain an empty drawing.
        IsVisibleChanged += (_, _) => { if (IsVisible) InvalidateVisual(); };
    }
    private Rect Plot => new(52, 30 + Math.Ceiling((Panel?.Series.Length ?? 0) / 2d) * 19,
        Math.Max(1, ActualWidth - 64), Math.Max(1, ActualHeight - 60 - Math.Ceiling((Panel?.Series.Length ?? 0) / 2d) * 19));
    private static void InvalidatePlot(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var chart = (RunChartView)target;
        chart._prepared = false;
        chart.InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        if ((!IsVisible && !RenderOffscreen) || Panel is null || ActualWidth < 100 || ActualHeight < 100) return;
        base.OnRender(drawing);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        Text(drawing, Panel.Title + " · " + Panel.Unit, 13, TextBrush, new Point(0, 0));
        for (int index = 0; index < Panel.Series.Length; index++)
        {
            var series = Panel.Series[index];
            var x = index % 2 * ActualWidth / 2;
            var y = 24 + index / 2 * 19;
            var pen = PenFor(series);
            drawing.DrawLine(pen, new Point(x, y + 7), new Point(x + 20, y + 7));
            Text(drawing, series.Name + ": " + ValueAt(series, CursorSeconds), 10.5, HasComparison ? pen.Brush : TextBrush, new Point(x + 26, y), ActualWidth / 2 - 30);
        }
        var plot = Plot;
        var gridPen = new Pen(MutedBrush, .4);
        for (int index = 0; index <= 4; index++)
        {
            double fraction = index / 4d;
            var y = plot.Bottom - fraction * plot.Height;
            drawing.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Text(drawing, (Panel.Minimum + fraction * (Panel.Maximum - Panel.Minimum)).ToString("0.#", CultureInfo.CurrentCulture),
                10, MutedBrush, new Point(0, y - 6), 48);
            Text(drawing, RunPresentation.Time(StartSeconds + fraction * Range), 10, MutedBrush,
                new Point(plot.Left + fraction * plot.Width - (index == 4 ? 30 : 0), plot.Bottom + 7), 58);
        }
        drawing.PushClip(new RectangleGeometry(plot));
        double selectionStart = _dragStart ?? SelectionStart, selectionEnd = _dragEnd ?? SelectionEnd;
        if (double.IsFinite(selectionStart) && double.IsFinite(selectionEnd))
        {
            var left = X(Math.Min(selectionStart, selectionEnd));
            var right = X(Math.Max(selectionStart, selectionEnd));
            drawing.PushOpacity(.13);
            drawing.DrawRectangle(AccentBrush, null, new Rect(left, plot.Top, Math.Max(0, right - left), plot.Height));
            drawing.Pop();
        }
        if (!_prepared || _preparedSize != RenderSize) Prepare();
        foreach (var (geometry, pen) in _paths) drawing.DrawGeometry(null, pen, geometry);
        if (_gapGeometry is not null)
        {
            drawing.PushOpacity(.15); drawing.DrawGeometry(MutedBrush, null, _gapGeometry); drawing.Pop();
        }
        DrawMarkers(drawing);
        if (CursorSeconds >= StartSeconds && CursorSeconds <= EndSeconds)
            drawing.DrawLine(new Pen(TextBrush, 1), new Point(X(CursorSeconds), plot.Top), new Point(X(CursorSeconds), plot.Bottom));
        drawing.Pop();
        if (Panel.Series.All(series => series.Points.Length == 0))
            Text(drawing, "This channel was not available in the recording.", 12, MutedBrush, new Point(plot.Left + 8, plot.Top + 24), plot.Width - 16);
    }
    private void DrawMarkers(DrawingContext drawing)
    {
        var markers = (Markers ?? []).Where(marker => double.IsFinite(marker.Seconds) && marker.Seconds >= StartSeconds && marker.Seconds <= EndSeconds)
            .OrderBy(marker => marker.Seconds).Take(256).ToArray();
        var labelEvery = Math.Max(1, (int)Math.Ceiling(markers.Length / 8d));
        for (var index = 0; index < markers.Length; index++)
        {
            var marker = markers[index]; var brush = marker.Comparison ? RunComparisonColors.RunB : HasComparison ? RunComparisonColors.RunA : AccentBrush;
            var pen = new Pen(brush, .8) { DashStyle = DashStyles.Dot };
            var x = X(marker.Seconds);
            drawing.DrawLine(pen, new Point(x, Plot.Top), new Point(x, Plot.Bottom));
            if (index % labelEvery == 0)
                Text(drawing, (marker.Comparison ? "B · " : "A · ") + marker.Label, 9, brush,
                    new Point(Math.Min(x + 3, Plot.Right - 86), Plot.Top + 3 + (index / labelEvery % 2) * 13), 86);
        }
    }
    private void Prepare()
    {
        _paths.Clear();
        if (Panel is null) return;
        _gapGeometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = _gapGeometry.Open())
        {
            foreach (var gap in Panel.Series.SelectMany(series => series.Gaps).Distinct())
            {
                if (gap.EndSeconds <= StartSeconds || gap.StartSeconds >= EndSeconds) continue;
                double left = X(Math.Max(StartSeconds, gap.StartSeconds)), right = X(Math.Min(EndSeconds, gap.EndSeconds));
                context.BeginFigure(new(left, Plot.Top), true, true);
                context.LineTo(new(right, Plot.Top), true, false); context.LineTo(new(right, Plot.Bottom), true, false);
                context.LineTo(new(left, Plot.Bottom), true, false);
            }
        }
        _gapGeometry.Freeze();
        foreach (var series in Panel.Series)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                bool started = false;
                foreach (var point in series.Points)
                {
                    var position = new Point(X(point.Seconds), Y(point.Value));
                    if (!started || point.BreakBefore) context.BeginFigure(position, false, false);
                    else context.LineTo(position, true, false);
                    started = true;
                }
            }
            geometry.Freeze();
            _paths.Add((geometry, PenFor(series)));
        }
        _prepared = true;
        _preparedSize = RenderSize;
    }
    private Pen PenFor(RunPlotSeries series)
    {
        var brush = HasComparison ? RunComparisonColors.Series(series.Comparison, series.ColorIndex)
            : series.ColorIndex switch { 1 => TextBrush, 2 => WarningBrush, _ => AccentBrush };
        var pen = new Pen(brush, series.Comparison ? 1.8 : 2) { DashStyle = series.Comparison ? DashStyles.Dash : DashStyles.Solid };
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }
    private static string ValueAt(RunPlotSeries series, double seconds)
    {
        return series.ReadRecordedValue?.Invoke(seconds) is double value ? value.ToString("0.#", CultureInfo.CurrentCulture) : "—";
    }
    private void Text(DrawingContext drawing, string value, double size, Brush brush, Point point, double width = double.PositiveInfinity)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (double.IsFinite(width)) { text.MaxTextWidth = Math.Max(1, width); text.MaxLineCount = 1; text.Trimming = TextTrimming.CharacterEllipsis; }
        drawing.DrawText(text, point);
    }
    private double Range => Math.Max(.01, EndSeconds - StartSeconds);
    private double X(double seconds) => Plot.Left + (seconds - StartSeconds) / Range * Plot.Width;
    private double Y(double value) => Plot.Bottom - (value - Panel!.Minimum) / Math.Max(.01, Panel.Maximum - Panel.Minimum) * Plot.Height;
    private double TimeAt(Point position) => StartSeconds + Math.Clamp((position.X - Plot.Left) / Plot.Width, 0, 1) * Range;
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var seconds = TimeAt(e.GetPosition(this));
        SetCurrentValue(CursorSecondsProperty, seconds);
        if (_dragStart is not null) { _dragEnd = seconds; InvalidateVisual(); }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus(); _dragStart = _dragEnd = TimeAt(e.GetPosition(this)); CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragStart is double start)
        {
            var end = TimeAt(e.GetPosition(this));
            _dragStart = _dragEnd = null; ReleaseMouseCapture();
            if (Math.Abs(end - start) >= .05) IntervalSelected?.Invoke(Math.Min(start, end), Math.Max(start, end));
            InvalidateVisual();
        }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Left or Key.Right or Key.Home)
        {
            SetCurrentValue(CursorSecondsProperty, e.Key == Key.Home ? StartSeconds : Math.Clamp(CursorSeconds + (e.Key == Key.Left ? -1 : 1) * Range / 100, StartSeconds, EndSeconds));
            e.Handled = true;
        }
        if (e.Key == Key.Escape) { _dragStart = _dragEnd = null; ReleaseMouseCapture(); InvalidateVisual(); }
    }
}
