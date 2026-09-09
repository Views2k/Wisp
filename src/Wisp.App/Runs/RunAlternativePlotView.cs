using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp.App.Runs;

public sealed class RunAlternativePlotView : FrameworkElement
{
    public static readonly DependencyProperty PanelProperty = DependencyProperty.Register(nameof(Panel), typeof(RunAlternativePlotPanel), typeof(RunAlternativePlotView), new FrameworkPropertyMetadata(null, Changed));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(RunAlternativePlotView), new FrameworkPropertyMetadata(Brushes.Turquoise, Changed));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(RunAlternativePlotView), new FrameworkPropertyMetadata(Brushes.WhiteSmoke, Changed));
    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(nameof(MutedBrush), typeof(Brush), typeof(RunAlternativePlotView), new FrameworkPropertyMetadata(Brushes.SlateGray, Changed));
    public RunAlternativePlotPanel? Panel { get => (RunAlternativePlotPanel?)GetValue(PanelProperty); set => SetValue(PanelProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public event Action<RunAlternativeSelection>? PointSelected;
    internal bool ShowInteractionHints { get; set; } = true;
    private readonly List<(Geometry Geometry, bool Comparison)> _geometry = [];
    private readonly List<Hit> _hits = [];
    private Size _size;
    private bool _prepared;
    private int _selected = -1;
    private sealed record Hit(Point Position, bool Comparison, double Seconds, int SampleIndex, string Detail);

    public RunAlternativePlotView()
    {
        MinHeight = 280; Focusable = true; Cursor = Cursors.Cross;
        AutomationProperties.SetHelpText(this, "Use left and right arrows to inspect recorded points. Press Enter to select a point in the run.");
    }

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var view = (RunAlternativePlotView)target;
        view._prepared = false; view._selected = -1;
        if (args.Property == PanelProperty)
        {
            AutomationProperties.SetName(view, view.Panel?.Title ?? "Run plot");
            view.SetCurrentValue(MinHeightProperty, view.Panel?.EqualAxes == true ? 340d : 280d);
        }
        view.InvalidateVisual();
    }

    private Rect Plot
    {
        get
        {
            double width = Math.Max(1, ActualWidth - 82), height = Math.Max(1, ActualHeight - 154);
            if (Panel?.EqualAxes == true)
            {
                var side = Math.Min(width, height);
                return new(64 + (width - side) / 2, 88 + (height - side) / 2, side, side);
            }
            return new(64, 88, width, height);
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (Panel is null || ActualWidth < 180 || ActualHeight < 180) return;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        Text(drawing, Panel.Title, 14, TextBrush, new(0, 0), ActualWidth);
        Text(drawing, Panel.Description, 11, MutedBrush, new(0, 23), ActualWidth);
        for (var i = 0; i < Panel.Series.Length; i++)
        {
            var series = Panel.Series[i];
            var x = i * ActualWidth / Math.Max(1, Panel.Series.Length);
            var color = series.Comparison ? RunComparisonColors.RunB : AccentBrush;
            drawing.DrawRectangle(series.Comparison ? null : color, new Pen(color, 1.6), new Rect(x, 49, 10, 10));
            var count = Panel.Kind == RunAlternativePlotKind.Scatter ? $" · {series.Points.Length:N0} of {series.SourcePointCount:N0} readings" : "";
            Text(drawing, series.Name + count, 11, color, new(x + 17, 45), ActualWidth / Math.Max(1, Panel.Series.Length) - 20);
        }
        var plot = Plot;
        Text(drawing, Panel.YLabel + " (" + Panel.YUnit + ")", 10.5, MutedBrush, new(0, 68), ActualWidth);
        var grid = new Pen(MutedBrush, .45);
        for (var i = 0; i <= 4; i++)
        {
            var fraction = i / 4d;
            var y = plot.Bottom - fraction * plot.Height;
            drawing.DrawLine(grid, new(plot.Left, y), new(plot.Right, y));
            Text(drawing, Format(Panel.YMinimum + fraction * (Panel.YMaximum - Panel.YMinimum)), 10, MutedBrush, new(plot.Left - 61, y - 7), 56);
            if (Panel.Kind == RunAlternativePlotKind.Scatter)
            {
                var x = plot.Left + fraction * plot.Width;
                drawing.DrawLine(grid, new(x, plot.Top), new(x, plot.Bottom));
                var labelWidth = Math.Min(70, plot.Width / 4);
                Text(drawing, Format(Panel.XMinimum + fraction * (Panel.XMaximum - Panel.XMinimum)), 10, MutedBrush,
                    new(x - labelWidth / 2, plot.Bottom + 7), labelWidth, TextAlignment.Center);
            }
        }
        if (Panel.Kind == RunAlternativePlotKind.Bars)
        {
            for (var i = 0; i < Panel.Categories.Length; i++)
                Text(drawing, Panel.Categories[i], 10, MutedBrush, new(X(i) - plot.Width / 8 + 2, plot.Bottom + 7), plot.Width / 4 - 4);
        }
        else Text(drawing, Panel.XLabel + " (" + Panel.XUnit + ")", 10.5, MutedBrush, new(plot.Left, plot.Bottom + 25), plot.Width);

        if (!_prepared || _size != RenderSize) Prepare();
        drawing.PushClip(new RectangleGeometry(plot));
        if (Panel.XMinimum < 0 && Panel.XMaximum > 0)
            drawing.DrawLine(new Pen(MutedBrush, 1), new(X(0), plot.Top), new(X(0), plot.Bottom));
        if (Panel.YMinimum < 0 && Panel.YMaximum > 0)
            drawing.DrawLine(new Pen(MutedBrush, 1), new(plot.Left, Y(0)), new(plot.Right, Y(0)));
        foreach (var (geometry, comparison) in _geometry)
        {
            var brush = comparison ? RunComparisonColors.RunB : AccentBrush;
            drawing.PushOpacity(comparison ? .8 : .7);
            drawing.DrawGeometry(comparison ? null : brush, comparison ? new Pen(brush, 1.3) : null, geometry);
            drawing.Pop();
        }
        if (_selected >= 0 && _selected < _hits.Count)
            drawing.DrawEllipse(null, new Pen(TextBrush, 1.5), _hits[_selected].Position, 5, 5);
        drawing.Pop();
        if (Panel.Kind == RunAlternativePlotKind.Bars)
        {
            foreach (var series in Panel.Series.Take(2))
                for (var category = 0; category < Math.Min(4, Panel.Categories.Length); category++)
                {
                    var bar = Panel.Bars.FirstOrDefault(value => value.Comparison == series.Comparison && value.Category == category);
                    var x = X(category) + (Panel.Series.Length == 1 ? 0 : (series.Comparison ? .17 : -.17) * plot.Width / 4);
                    var y = bar is null ? plot.Bottom - 15 : Math.Clamp(Y(bar.Value) - 16, plot.Top, plot.Bottom - 15);
                    Text(drawing, bar is null ? "—" : Format(bar.Value), 10, series.Comparison ? RunComparisonColors.RunB : AccentBrush, new(x - 18, y), 40);
                }
        }
        if (_hits.Count == 0)
            Text(drawing, Panel.Kind == RunAlternativePlotKind.Bars ? "Tire temperatures were unavailable for this selection." : Panel.XUnit == "RPM"
                    ? "No matching RPM readings. Adjust the gear/throttle filters or select another section." : "No recorded driving points were available in this selection.",
                12, MutedBrush, new(plot.Left + 8, plot.Top + 15), plot.Width - 16, lines: 3);
        var detail = _selected >= 0 && _selected < _hits.Count ? _hits[_selected].Detail :
            Panel.Kind == RunAlternativePlotKind.Bars ? "Hover a bar, or focus the plot and use arrow keys, to read its value." : "Hover a dot, or focus the plot and use arrow keys, to inspect its recorded value.";
        if (ShowInteractionHints) Text(drawing, detail, 11, TextBrush, new(0, ActualHeight - 20), ActualWidth);
    }

    private void Prepare()
    {
        _geometry.Clear(); _hits.Clear();
        if (Panel is null) return;
        foreach (var series in Panel.Series.Take(2))
        {
            var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var context = geometry.Open())
            {
                if (Panel.Kind == RunAlternativePlotKind.Scatter)
                {
                    foreach (var point in series.Points.Take(RunAlternativePlots.MaximumSeriesPoints))
                    {
                        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) continue;
                        var position = new Point(X(point.X), Y(point.Y));
                        if (!Plot.Contains(position)) continue;
                        Square(context, new Rect(position.X - 2, position.Y - 2, 4, 4));
                        _hits.Add(new(position, series.Comparison, point.SourceSeconds, point.SampleIndex,
                            $"{series.Name} · {point.SourceSeconds:0.000}s · gear {(int)point.Gear} · {Format(point.X)} {Panel.XUnit} / {Format(point.Y)} {Panel.YUnit}"));
                    }
                }
                else
                {
                    foreach (var bar in Panel.Bars.Where(bar => bar.Comparison == series.Comparison).Take(4))
                    {
                        if (!double.IsFinite(bar.Value) || bar.Category < 0 || bar.Category >= Panel.Categories.Length) continue;
                        var groupWidth = Plot.Width / 4;
                        var width = groupWidth * (Panel.Series.Length == 1 ? .46 : .28);
                        var offset = Panel.Series.Length == 1 ? 0 : (series.Comparison ? .17 : -.17) * groupWidth;
                        var x = X(bar.Category) + offset;
                        var y = Y(bar.Value); var zero = Y(0);
                        Square(context, new Rect(x - width / 2, Math.Min(y, zero), width, Math.Max(.8, Math.Abs(zero - y))));
                        _hits.Add(new(new(x, y), series.Comparison, bar.SourceSeconds, -1,
                            $"{series.Name} · {Panel.Categories[bar.Category]} · {Format(bar.Value)} {Panel.YUnit} · boundary {bar.SourceSeconds:0.000}s"));
                    }
                }
            }
            geometry.Freeze(); _geometry.Add((geometry, series.Comparison));
        }
        _prepared = true; _size = RenderSize;
        if (_selected >= _hits.Count) _selected = -1;
    }

    private static void Square(StreamGeometryContext context, Rect rectangle)
    {
        context.BeginFigure(rectangle.TopLeft, true, true); context.LineTo(rectangle.TopRight, true, false);
        context.LineTo(rectangle.BottomRight, true, false); context.LineTo(rectangle.BottomLeft, true, false);
    }
    private double X(double value) => Plot.Left + (value - Panel!.XMinimum) / Math.Max(.0001, Panel.XMaximum - Panel.XMinimum) * Plot.Width;
    private double Y(double value) => Plot.Bottom - (value - Panel!.YMinimum) / Math.Max(.0001, Panel.YMaximum - Panel.YMinimum) * Plot.Height;
    private static string Format(double value) => value.ToString(Math.Abs(value) >= 100 ? "0" : "0.##", CultureInfo.CurrentCulture);
    private void Text(DrawingContext drawing, string value, double size, Brush brush, Point position, double width,
        TextAlignment alignment = TextAlignment.Left, int lines = 1)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, width), MaxLineCount = lines, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = alignment };
        drawing.DrawText(text, position);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var mouse = e.GetPosition(this); var closest = -1; var distance = Panel?.Kind == RunAlternativePlotKind.Bars ? double.MaxValue : 225d;
        for (var i = 0; i < _hits.Count; i++)
        {
            var d = (_hits[i].Position - mouse).LengthSquared;
            if (d < distance && Plot.Contains(mouse)) { closest = i; distance = d; }
        }
        Select(closest);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus(); PublishSelection(); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_hits.Count == 0) return;
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End)
        {
            var selected = e.Key switch { Key.Home => 0, Key.End => _hits.Count - 1, Key.Left => Math.Max(0, _selected - 1), _ => Math.Min(_hits.Count - 1, _selected + 1) };
            Select(selected); e.Handled = true;
        }
        else if (e.Key == Key.Enter) { PublishSelection(); e.Handled = true; }
    }
    private void Select(int selected)
    {
        if (_selected == selected) return;
        _selected = selected;
        AutomationProperties.SetItemStatus(this, selected >= 0 && selected < _hits.Count ? _hits[selected].Detail : "");
        InvalidateVisual();
    }
    private void PublishSelection()
    {
        if (_selected >= 0 && _selected < _hits.Count && _hits[_selected].SampleIndex >= 0)
        {
            var hit = _hits[_selected]; PointSelected?.Invoke(new(hit.Comparison, hit.Seconds, hit.SampleIndex));
        }
    }
}
