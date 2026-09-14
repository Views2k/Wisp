using System.Windows;
using System.Windows.Controls;

namespace Wisp.App;

public partial class FeatureTourOverlay : UserControl
{
    private FrameworkElement? _target;
    public FeatureTourOverlay()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Reposition();
        TourCard.SizeChanged += (_, _) => Reposition();
    }

    internal void SetTarget(FrameworkElement? target)
    {
        if (_target is not null) _target.LayoutUpdated -= TargetLayoutUpdated;
        _target = target;
        if (_target is not null) _target.LayoutUpdated += TargetLayoutUpdated;
        Reposition();
    }

    private void TargetLayoutUpdated(object? sender, EventArgs e) => Reposition();

    internal void Reposition()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        TourCard.Width = Math.Min(392, Math.Max(220, width - 24));
        TourCardScroll.MaxHeight = Math.Max(100, height - 68);
        var cardHeight = Math.Min(TourCard.ActualHeight, height - 24);
        var x = Math.Max(12, width - TourCard.Width - 20);
        var y = Math.Max(12, height - cardHeight - 20);
        var highlightVisible = false;
        if (_target is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 })
        {
            try
            {
                var bounds = _target.TransformToVisual(this).TransformBounds(new Rect(_target.RenderSize));
                bounds.Intersect(new Rect(8, 8, Math.Max(0, width - 16), Math.Max(0, height - 16)));
                if (!bounds.IsEmpty && bounds.Width > 1 && bounds.Height > 1)
                {
                    Canvas.SetLeft(TourHighlight, bounds.Left - 3);
                    Canvas.SetTop(TourHighlight, bounds.Top - 3);
                    TourHighlight.Width = bounds.Width + 6;
                    TourHighlight.Height = bounds.Height + 6;
                    highlightVisible = true;
                    // Prefer below the real control; fall back above or alongside it.
                    if (bounds.Bottom + cardHeight + 24 <= height) y = bounds.Bottom + 12;
                    else if (bounds.Top >= cardHeight + 24) y = bounds.Top - cardHeight - 12;
                    else if (bounds.Left >= TourCard.Width + 24) x = bounds.Left - TourCard.Width - 12;
                    else if (width - bounds.Right >= TourCard.Width + 24) x = bounds.Right + 12;
                }
            }
            catch (InvalidOperationException) { /* Navigation can detach the prior anchor. */ }
        }
        var visibility = highlightVisible ? Visibility.Visible : Visibility.Collapsed;
        if (TourHighlight.Visibility != visibility) TourHighlight.Visibility = visibility;
        Canvas.SetLeft(TourCard, x);
        Canvas.SetTop(TourCard, y);
    }
}
