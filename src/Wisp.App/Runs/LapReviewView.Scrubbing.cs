using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Wisp.App.Runs;

public partial class LapReviewView
{
    private bool _scrubbing, _scrubPending, _flushingScrub;
    internal int ScrubUpdates { get; private set; }

    private void InitializeScrubbing()
    {
        LapCursorSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => BeginScrub()));
        LapCursorSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => EndScrub()));
        Unloaded += (_, _) => EndScrub();
        DataContextChanged += (_, _) => CancelScrub();
    }

    private void LapCursorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_flushingScrub) return;
        if (!_scrubbing) { FlushScrub(); return; }
        if (_scrubPending) return;
        _scrubPending = true;
        CompositionTarget.Rendering += RenderScrub;
    }
    private void RenderScrub(object? sender, EventArgs e) => FlushScrub();
    internal void BeginScrub() => _scrubbing = true;
    internal void EndScrub() { if (_scrubPending) FlushScrub(); _scrubbing = false; }
    private void CancelScrub()
    {
        CompositionTarget.Rendering -= RenderScrub;
        _scrubPending = _scrubbing = false;
    }
    internal void FlushScrub()
    {
        CompositionTarget.Rendering -= RenderScrub;
        _scrubPending = false;
        if (_flushingScrub || LapCursorSlider is null) return;
        _flushingScrub = true;
        try
        {
            LapCursorSlider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateSource();
            ScrubUpdates++;
        }
        finally { _flushingScrub = false; }
    }
}
