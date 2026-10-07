using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Wisp.App.Runs;
using Wisp.UiReview;
using Xunit;

namespace Wisp.App.Tests;

internal static class LapReviewScrubbingTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { AssertScrubbing(); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void AssertScrubbing()
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        model.SetRuns(LapReviewFixtures.Create(), LapReviewFixtures.Create(reference: true));
        Await(LapReviewComparisonTestSupport.Ready(model));
        var view = new LapReviewView { DataContext = model };
        var slider = Assert.IsType<Slider>(view.FindName("LapCursorSlider"));
        view.Measure(new Size(1000, 800)); view.Arrange(new Rect(0, 0, 1000, 800));
        ReadyBindings(view, slider);
        Assert.Equal((double)model.MaximumCursor, slider.Maximum);
        Assert.Equal((double)model.Cursor, slider.Value);
        try
        {
            var updates = view.ScrubUpdates;
            slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            for (var value = 1; value <= 20; value++) slider.SetCurrentValue(RangeBase.ValueProperty, (double)value);
            Assert.Equal(0, model.Cursor);
            Assert.Equal(updates, view.ScrubUpdates);
            view.FlushScrub();
            Assert.Equal(20, model.Cursor);
            Assert.Equal(updates + 1, view.ScrubUpdates);
            for (var value = 21; value <= 30; value++) slider.SetCurrentValue(RangeBase.ValueProperty, (double)value);
            Assert.Equal(20, model.Cursor);
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Assert.Equal(30, model.Cursor);
            Assert.Equal(updates + 2, view.ScrubUpdates);

            slider.SetCurrentValue(RangeBase.ValueProperty, 40d);
            Assert.Equal(40, model.Cursor);
            Slider.IncreaseSmall.Execute(null, slider);
            Assert.Equal(41, model.Cursor);
            model.Cursor = 45;
            Assert.Equal(45d, slider.Value);

            AssertTargetSwitching(view, slider, model);
            model.Is3D = false;
            model.Cursor = 45;

            view.BeginScrub();
            slider.SetCurrentValue(RangeBase.ValueProperty, 55d);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Equal(55, model.Cursor);

            using var next = new LapReviewComparisonTestSupport();
            next.Model.SetRuns(LapReviewFixtures.Create(), null);
            Await(LapReviewComparisonTestSupport.Ready(next.Model));
            view.BeginScrub();
            slider.SetCurrentValue(RangeBase.ValueProperty, 75d);
            view.DataContext = next.Model;
            ReadyBindings(view, slider);
            view.FlushScrub();
            Assert.Equal(55, model.Cursor);
            Assert.Equal(0, next.Model.Cursor);
            Assert.Equal(0d, slider.Value);
            Assert.Null(PresentationSource.FromVisual(view));
        }
        finally { view.EndScrub(); view.DataContext = null; }
    }

    private static void AssertTargetSwitching(LapReviewView view, Slider slider, LapReviewViewModel model)
    {
        model.Is3D = true; model.SharedSpace = true;
        model.ScrubA = true;
        var b = model.MapPlotB.Cursor;
        var updates = view.ScrubUpdates;
        view.BeginScrub();
        slider.SetCurrentValue(RangeBase.ValueProperty, 75d);
        Assert.Equal(45, model.Cursor);
        model.ScrubB = true;
        Assert.Equal(75, model.Cursor);
        Assert.Equal(b, model.MapPlotB.Cursor);
        Assert.Equal((double)b, slider.Value);
        Assert.Equal(updates + 1, view.ScrubUpdates);
        view.BeginScrub();
        slider.SetCurrentValue(RangeBase.ValueProperty, 120d);
        model.ScrubA = true;
        Assert.Equal(75, model.Cursor); Assert.Equal(120, model.MapPlotB.Cursor);
        Assert.Equal(75d, slider.Value);

        // A shorter replacement must not clamp A through B's slider maximum.
        model.Cursor = 300;
        model.Reference = model.Reference! with { Points = model.Reference.Points.Take(90).ToArray() };
        Await(LapReviewComparisonTestSupport.Ready(model));
        model.ScrubB = true;
        Assert.Equal(89d, slider.Maximum);
        Assert.Equal(300, model.Cursor);
        Assert.Equal(0d, slider.Value);
        view.BeginScrub();
        slider.SetCurrentValue(RangeBase.ValueProperty, 70d);
        model.Is3D = false;
        Assert.Equal(300, model.Cursor);
        Assert.Equal(300d, slider.Value);
        Assert.Equal((double)model.MaximumCursor, slider.Maximum);
        model.Is3D = true;
        Assert.Equal(70d, slider.Value);
        Assert.Equal(70, model.MapPlotB.Cursor);
        view.BeginScrub();
        slider.SetCurrentValue(RangeBase.ValueProperty, 80d);
        model.Reference = null;
        Await(LapReviewComparisonTestSupport.Ready(model));
        Assert.Equal(300, model.Cursor);
        Assert.Equal(300d, slider.Value);
        Assert.False(model.IsMapComparison);
        Assert.Equal((double)model.MaximumCursor, slider.Maximum);
    }

    private static void ReadyBindings(LapReviewView view, Slider slider)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        slider.GetBindingExpression(RangeBase.MaximumProperty)?.UpdateTarget();
        slider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateTarget();
        view.UpdateLayout();
    }

    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
}
