using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

internal sealed class GForceTrailHistory
{
    internal const int Capacity = 8;
    internal const double MinimumMovementPixels = 0.75;
    private const double MinimumMovementSquared = MinimumMovementPixels * MinimumMovementPixels;

    private readonly Point[] _samples = new Point[Capacity];
    private int _count;
    private int _next;

    internal int Count => _count;

    internal Point this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            var first = _count == Capacity ? _next : 0;
            return _samples[(first + index) % Capacity];
        }
    }

    internal bool TryAdd(Point sample)
    {
        if (!double.IsFinite(sample.X) || !double.IsFinite(sample.Y))
        {
            return false;
        }

        if (_count > 0)
        {
            var newest = _samples[(_next + Capacity - 1) % Capacity];
            var deltaX = sample.X - newest.X;
            var deltaY = sample.Y - newest.Y;
            if ((deltaX * deltaX) + (deltaY * deltaY) < MinimumMovementSquared)
            {
                return false;
            }
        }

        _samples[_next] = sample;
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
        return true;
    }

    internal void Clear()
    {
        _count = 0;
        _next = 0;
    }
}

public sealed class GForceTrailView : FrameworkElement
{
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position),
        typeof(Point),
        typeof(GForceTrailView),
        new FrameworkPropertyMetadata(default(Point), OnPositionChanged));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(GForceTrailView),
        new FrameworkPropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty TrailBrushProperty = DependencyProperty.Register(
        nameof(TrailBrush),
        typeof(Brush),
        typeof(GForceTrailView),
        new FrameworkPropertyMetadata(
            Brushes.Transparent,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnTrailBrushChanged));

    private readonly GForceTrailHistory _history = new();
    private readonly Pen?[] _segmentPens = new Pen?[GForceTrailHistory.Capacity - 1];

    public GForceTrailView() => RebuildSegmentPens();

    public Point Position
    {
        get => (Point)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Brush TrailBrush
    {
        get => (Brush)GetValue(TrailBrushProperty);
        set => SetValue(TrailBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var sampleCount = _history.Count;
        if (sampleCount < 2 || TrailBrush is null || RenderSize.Width <= 0 || RenderSize.Height <= 0)
        {
            return;
        }

        var centerX = RenderSize.Width / 2;
        var centerY = RenderSize.Height / 2;
        var previousSample = _history[0];
        var previous = new Point(centerX + previousSample.X, centerY + previousSample.Y);
        for (var index = 1; index < sampleCount; index++)
        {
            var sample = _history[index];
            var current = new Point(centerX + sample.X, centerY + sample.Y);
            var penIndex = GForceTrailHistory.Capacity - sampleCount + index - 1;
            if (_segmentPens[penIndex] is { } pen)
            {
                drawingContext.DrawLine(pen, previous, current);
            }

            previous = current;
        }

        for (var index = 0; index < sampleCount - 1; index++)
        {
            var freshness = (index + 1d) / sampleCount;
            var sample = _history[index];
            var radius = 1 + (freshness * 0.65);
            drawingContext.PushOpacity(0.10 + (freshness * 0.22));
            drawingContext.DrawEllipse(
                TrailBrush,
                null,
                new Point(centerX + sample.X, centerY + sample.Y),
                radius,
                radius);
            drawingContext.Pop();
        }
    }

    private void RebuildSegmentPens()
    {
        if (TrailBrush is null)
        {
            Array.Clear(_segmentPens);
            return;
        }

        for (var index = 0; index < _segmentPens.Length; index++)
        {
            var freshness = (index + 1d) / _segmentPens.Length;
            var brush = (Brush)TrailBrush.CloneCurrentValue();
            brush.Opacity *= 0.10 + (freshness * 0.42);
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            var pen = new Pen(brush, 0.8 + (freshness * 1.2))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            if (pen.CanFreeze)
            {
                pen.Freeze();
            }

            _segmentPens[index] = pen;
        }
    }

    private static void OnPositionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var view = (GForceTrailView)dependencyObject;
        if (view.IsActive && view._history.TryAdd((Point)args.NewValue))
        {
            view.InvalidateVisual();
        }
    }

    private static void OnIsActiveChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var view = (GForceTrailView)dependencyObject;
        if ((bool)args.NewValue)
        {
            return;
        }

        view._history.Clear();
        view.InvalidateVisual();
    }

    private static void OnTrailBrushChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var view = (GForceTrailView)dependencyObject;
        view.RebuildSegmentPens();
    }
}
