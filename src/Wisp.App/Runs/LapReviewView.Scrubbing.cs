using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Wisp.App.Runs;

public partial class LapReviewView
{
    private bool _scrubbing, _scrubPending, _flushingScrub;
    private int _scrubContextDepth;
    private LapReviewViewModel? _scrubModel;
    internal int ScrubUpdates { get; private set; }

    private void InitializeScrubbing()
    {
        LapCursorSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => BeginScrub()));
        LapCursorSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => EndScrub()));
        Unloaded += (_, _) => { EndScrub(); AttachScrubModel(null); };
        Loaded += (_, _) => AttachScrubModel(DataContext as LapReviewViewModel);
        DataContextChanged += (_, _) =>
        {
            CancelScrub();
            AttachScrubModel(DataContext as LapReviewViewModel);
            RefreshScrubBindings();
        };
    }

    private void LapCursorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_flushingScrub || _scrubContextDepth > 0) return;
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
    private void AttachScrubModel(LapReviewViewModel? model)
    {
        if (ReferenceEquals(_scrubModel, model)) return;
        if (_scrubModel is not null)
        {
            _scrubModel.ScrubContextChanging -= BeginScrubContextChange;
            _scrubModel.ScrubContextChanged -= EndScrubContextChange;
        }
        _scrubModel = model;
        _scrubContextDepth = 0;
        if (_scrubModel is not null)
        {
            _scrubModel.ScrubContextChanging += BeginScrubContextChange;
            _scrubModel.ScrubContextChanged += EndScrubContextChange;
        }
    }
    private void BeginScrubContextChange()
    {
        if (_scrubContextDepth++ > 0) return;
        // Commit to the old target before its meaning or sample count changes.
        EndScrub();
        CancelScrub();
    }
    private void EndScrubContextChange()
    {
        if (_scrubContextDepth <= 0 || --_scrubContextDepth > 0) return;
        RefreshScrubBindings();
    }
    private void RefreshScrubBindings()
    {
        if (LapCursorSlider is null || _flushingScrub) return;
        _flushingScrub = true;
        try
        {
            LapCursorSlider.GetBindingExpression(RangeBase.MaximumProperty)?.UpdateTarget();
            LapCursorSlider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateTarget();
        }
        finally { _flushingScrub = false; }
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
