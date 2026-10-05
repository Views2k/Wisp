using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed record LapReviewPlotData(LapReviewLap? Lap, LapReviewLap? Reference,
    LapReviewComparison? Comparison, LapReviewChannel Channel, SpeedUnit SpeedUnit, int Cursor, int SectionStart, int SectionEnd,
    int Wheel = 0, TireTemperatureUnit TemperatureUnit = TireTemperatureUnit.Fahrenheit);

public sealed class LapReviewPlot : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(LapReviewPlotData), typeof(LapReviewPlot), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsMapProperty = DependencyProperty.Register(nameof(IsMap), typeof(bool), typeof(LapReviewPlot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public LapReviewPlotData? Data { get => (LapReviewPlotData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public bool IsMap { get => (bool)GetValue(IsMapProperty); set => SetValue(IsMapProperty, value); }
    public event Action<int>? PointChosen;
    private readonly List<(Point Position, int Index)> _hitPoints = [];
    private DrawingGroup? _preparedDrawing;
    private LapReviewPlotData? _preparedData;
    private Size _preparedSize;
    private double _preparedDpi;
    private bool _preparedMap;
    private Brush? _preparedText, _preparedMuted, _preparedBackground;
    private static readonly Brush[] Heat = Enumerable.Range(0, 64).Select(i =>
    {
        var t = i / 63d;
        var brush = new SolidColorBrush(Color.FromRgb((byte)(60 + 190 * t), (byte)(200 - 60 * t), (byte)(245 - 150 * t)));
        brush.Freeze(); return (Brush)brush;
    }).ToArray();
    public LapReviewPlot() { Focusable = true; ClipToBounds = true; Cursor = Cursors.Cross; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var text = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        var muted = TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;
        var background = TryFindResource("InputBrush") as Brush ?? Brushes.Black;
        if (ActualWidth < 60 || ActualHeight < 50 || Data is not { Lap.Points.Length: > 1 } data)
        {
            _preparedDrawing = null; _preparedData = null; _hitPoints.Clear();
            dc.DrawRectangle(background, null, new(0, 0, ActualWidth, ActualHeight));
            Label(dc, "No recorded lap positions", new(12, 12), muted); return;
        }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_preparedDrawing is null || _preparedSize != RenderSize || _preparedDpi != dpi || _preparedMap != IsMap ||
            !ReferenceEquals(_preparedText, text) || !ReferenceEquals(_preparedMuted, muted) || !ReferenceEquals(_preparedBackground, background) ||
            !SamePlot(_preparedData, data))
        {
            _preparedDrawing = new DrawingGroup();
            using (var drawing = _preparedDrawing.Open()) Prepare(drawing, data, muted, background);
            _preparedData = data; _preparedSize = RenderSize; _preparedDpi = dpi; _preparedMap = IsMap;
            _preparedText = text; _preparedMuted = muted; _preparedBackground = background;
        }
        dc.DrawDrawing(_preparedDrawing);
        var cursor = _hitPoints[Math.Clamp(data.Cursor, 0, _hitPoints.Count - 1)].Position;
        if (IsMap) dc.DrawEllipse(text, new(Brushes.Black, 1), cursor, 5, 5);
        else dc.DrawLine(new(text, 1), new(cursor.X, 28), new(cursor.X, ActualHeight - 28));
        if (IsKeyboardFocused && FocusCues.GetShowKeyboardFocus(this)) dc.DrawRectangle(null, new(text, 1), new Rect(1, 1, ActualWidth - 2, ActualHeight - 2));
    }
    private static bool SamePlot(LapReviewPlotData? a, LapReviewPlotData b) => a is not null &&
        ReferenceEquals(a.Lap, b.Lap) && ReferenceEquals(a.Reference, b.Reference) && ReferenceEquals(a.Comparison, b.Comparison) &&
        a.Channel == b.Channel && a.SpeedUnit == b.SpeedUnit && a.SectionStart == b.SectionStart && a.SectionEnd == b.SectionEnd &&
        a.Wheel == b.Wheel && a.TemperatureUnit == b.TemperatureUnit;
    private void Prepare(DrawingContext dc, LapReviewPlotData data, Brush muted, Brush background)
    {
        _hitPoints.Clear();
        dc.DrawRectangle(background, null, new(0, 0, ActualWidth, ActualHeight));
        var points = data.Lap!.Points;
        var area = new Rect(18, 28, ActualWidth - 36, ActualHeight - 56);
        var values = points.Select((point, index) => Value(point, data, index));
        if (!IsMap) values = values.Concat(Enumerable.Range(0, points.Length).Select(index => ReferenceValue(data, index)));
        var finite = values.Where(value => value is { } number && double.IsFinite(number)).Select(value => value!.Value).ToArray();
        var minimum = finite.Length == 0 ? 0 : finite.Min(); var maximum = finite.Length == 0 ? 1 : finite.Max();
        if (maximum - minimum < .001) maximum = minimum + 1;
        Func<LapReviewPoint, Point> project;
        if (IsMap)
        {
            var all = points.AsEnumerable();
            if (data.Comparison?.CanCompare == true && data.Reference is { } reference) all = all.Concat(reference.Points);
            var array = all.ToArray();
            var left = array.Min(point => point.Position.X); var right = array.Max(point => point.Position.X);
            var bottom = array.Min(point => point.Position.Z); var top = array.Max(point => point.Position.Z);
            var scale = Math.Min(area.Width / Math.Max(1, right - left), area.Height / Math.Max(1, top - bottom));
            var ox = area.Left + (area.Width - (right - left) * scale) / 2; var oy = area.Top + (area.Height - (top - bottom) * scale) / 2;
            // Same handedness as the HUD: world +X right, +Z up.
            project = point => new(ox + (point.Position.X - left) * scale, oy + (top - point.Position.Z) * scale);
        }
        else project = point => new(area.Left + point.DistanceMeters / Math.Max(1, data.Lap.RecordedDistanceMeters) * area.Width, area.Bottom);
        if (IsMap && data.Comparison?.CanCompare == true && data.Reference is { } mapReference)
            DrawPath(dc, mapReference.Points, project, new(RunComparisonColors.RunB, 1.5));
        var stride = Math.Max(1, (int)Math.Ceiling(points.Length / 3000d));
        Point? prior = null; var broken = false;
        for (var i = 0; i < points.Length; i++)
        {
            broken |= points[i].BreakBefore;
            var value = Value(points[i], data, i);
            var p = project(points[i]);
            if (!IsMap) p.Y = area.Bottom - ((value ?? minimum) - minimum) / (maximum - minimum) * area.Height;
            var valid = value is { } number && double.IsFinite(number);
            broken |= !IsMap && !valid;
            _hitPoints.Add((p, i));
            if (i % stride != 0 && i != points.Length - 1) continue;
            if (prior is { } previous && !broken && (IsMap || valid))
            {
                var brush = IsMap && valid ? Heat[Math.Clamp((int)((value!.Value - minimum) / (maximum - minimum) * 63), 0, 63)] : IsMap ? muted : RunComparisonColors.RunA;
                var selected = i >= data.SectionStart && i <= data.SectionEnd;
                dc.DrawLine(new(brush, selected ? 3 : 1.2), previous, p);
            }
            prior = IsMap || valid ? p : null; broken = false;
        }
        if (!IsMap && data.Comparison?.CanCompare == true && data.Reference is { } chartReference && data.Channel != LapReviewChannel.Delta)
        {
            Point? previous = null;
            for (var i = 0; i < points.Length; i++)
            {
                if (points[i].BreakBefore) previous = null;
                var value = ReferenceValue(data, i);
                if (value is not { } v || !double.IsFinite(v)) { previous = null; continue; }
                if (i % stride != 0 && i != points.Length - 1) continue;
                var point = new Point(project(points[i]).X, area.Bottom - (v - minimum) / (maximum - minimum) * area.Height);
                if (previous is { } p) dc.DrawLine(new(RunComparisonColors.RunB, 1.5), p, point);
                previous = point;
            }
        }
        if (IsMap) dc.DrawEllipse(null, new(RunComparisonColors.RunA, 2), project(points[0]), 6, 6);
        var unit = Unit(data);
        Label(dc, finite.Length == 0 ? "No comparable values for this channel" : $"{minimum:0.##} → {maximum:0.##} {unit}" + (IsMap ? " · low = blue, high = amber" : " · lap = cyan, reference = amber"), new(10, 5), muted);
        Label(dc, IsMap ? "Recorded line · start ring · click or use arrow keys" : $"Distance from lap start · 0–{data.Lap.RecordedDistanceMeters:0} m", new(10, ActualHeight - 21), muted);
    }
    private static void DrawPath(DrawingContext dc, LapReviewPoint[] points, Func<LapReviewPoint, Point> project, Pen pen)
    {
        Point? previous = null; var broken = false;
        var stride = Math.Max(1, (int)Math.Ceiling(points.Length / 3000d));
        for (var i = 0; i < points.Length; i++) { broken |= points[i].BreakBefore; if (i % stride != 0 && i != points.Length - 1) continue; var p = project(points[i]); if (previous is { } prior && !broken) dc.DrawLine(pen, prior, p); previous = p; broken = false; }
    }
    internal static double? ReferenceValue(LapReviewPlotData data, int index)
    {
        if (data.Channel == LapReviewChannel.Delta || data.Comparison?.CanCompare != true || data.Reference is not { } reference) return null;
        var match = data.Comparison.Points.ElementAtOrDefault(index);
        if (match?.ReferencePointIndex is not { } lower || match.ReferenceLapSeconds is not { } seconds || lower < 0 || lower >= reference.Points.Length) return null;
        var a = reference.Points[lower]; var value = Value(a, data, index);
        if (lower + 1 == reference.Points.Length) return value;
        var b = reference.Points[lower + 1];
        if (data.Channel == LapReviewChannel.Gear)
            return !b.BreakBefore && b.LapSeconds > a.LapSeconds && seconds >= b.LapSeconds ? Value(b, data, index) : value;
        if (b.BreakBefore || b.LapSeconds <= a.LapSeconds) return null;
        var next = Value(b, data, index);
        return value + (next - value) * Math.Clamp((seconds - a.LapSeconds) / (b.LapSeconds - a.LapSeconds), 0, 1);
    }
    private void Label(DrawingContext dc, string value, Point at, Brush brush) => dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), at);
    internal static double? Value(LapReviewPoint point, LapReviewPlotData data, int index)
    {
        var state = point.Sample.State;
        return data.Channel switch
        {
            LapReviewChannel.Speed => state.GroundSpeedMetersPerSecond * RunPresentation.SpeedFactor(data.SpeedUnit),
            LapReviewChannel.Delta => data.Comparison?.CanCompare == true ? data.Comparison.Points.ElementAtOrDefault(index)?.DeltaSeconds : null,
            LapReviewChannel.Throttle => state.Accelerator / 2.55,
            LapReviewChannel.Brake => state.Brake / 2.55,
            LapReviewChannel.Steering => Math.Clamp(state.Steering / 127d * 100, -100, 100),
            LapReviewChannel.LateralG => state.LateralAccelerationMetersPerSecondSquared / 9.80665,
            LapReviewChannel.LongitudinalG => state.LongitudinalAccelerationMetersPerSecondSquared / 9.80665,
            LapReviewChannel.CombinedG => Math.Sqrt(Math.Pow(state.LateralAccelerationMetersPerSecondSquared, 2) + Math.Pow(state.LongitudinalAccelerationMetersPerSecondSquared, 2)) / 9.80665,
            LapReviewChannel.Rpm => state.EngineRpm,
            LapReviewChannel.Gear => (int)state.Gear >= 0 ? (int)state.Gear : null,
            LapReviewChannel.TireTemperature => data.TemperatureUnit == TireTemperatureUnit.Celsius ? (WheelValue(state.TireTemperatureFahrenheit, data.Wheel) - 32) * 5 / 9 : WheelValue(state.TireTemperatureFahrenheit, data.Wheel),
            LapReviewChannel.SlipRatio => WheelValue(state.TireSlipRatio, data.Wheel),
            LapReviewChannel.SlipAngle => WheelValue(state.TireSlipAngle, data.Wheel),
            LapReviewChannel.Suspension => WheelValue(state.NormalizedSuspensionTravel, data.Wheel),
            _ => null
        };
    }
    private static double WheelValue(WheelValues values, int wheel) => wheel switch { 1 => values.FrontRight, 2 => values.RearLeft, 3 => values.RearRight, _ => values.FrontLeft };
    private static string Unit(LapReviewPlotData data) => data.Channel switch { LapReviewChannel.Speed => RunPresentation.SpeedLabel(data.SpeedUnit), LapReviewChannel.Delta => "s (lap − reference)", LapReviewChannel.Throttle or LapReviewChannel.Brake or LapReviewChannel.Steering => "%", LapReviewChannel.Rpm => "rpm", LapReviewChannel.Gear => "gear", LapReviewChannel.TireTemperature => data.TemperatureUnit == TireTemperatureUnit.Celsius ? "°C" : "°F", LapReviewChannel.SlipRatio or LapReviewChannel.SlipAngle => "raw", LapReviewChannel.Suspension => "normalized", _ => "g" };
    protected override void OnMouseDown(MouseButtonEventArgs e) { base.OnMouseDown(e); if (e.ChangedButton != MouseButton.Left) return; Focus(); CaptureMouse(); Choose(e.GetPosition(this)); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Choose(e.GetPosition(this)); }
    protected override void OnMouseUp(MouseButtonEventArgs e) { base.OnMouseUp(e); if (IsMouseCaptured) ReleaseMouseCapture(); }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e) { base.OnPropertyChanged(e); if (e.Property == FocusCues.ShowKeyboardFocusProperty && IsKeyboardFocused) InvalidateVisual(); }
    private void Choose(Point p) { if (_hitPoints.Count == 0) return; var chosen = _hitPoints.MinBy(item => IsMap ? (item.Position - p).LengthSquared : Math.Abs(item.Position.X - p.X)); PointChosen?.Invoke(chosen.Index); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (Data?.Lap is not { Points.Length: > 0 } lap) return;
        var next = e.Key switch { Key.Left or Key.Down => Data.Cursor - 1, Key.Right or Key.Up => Data.Cursor + 1, Key.Home => 0, Key.End => lap.Points.Length - 1, _ => -1 };
        if (next < 0 && e.Key is not Key.Left and not Key.Down) return;
        PointChosen?.Invoke(Math.Clamp(next, 0, lap.Points.Length - 1)); e.Handled = true;
    }
}
