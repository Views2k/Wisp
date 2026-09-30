using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ForzaCaptureObservationTests
{
    private static readonly ForzaCaptureCandidate Window = new(42, 123, 456, 1920, 1080);

    [Fact]
    public void UnchangedWindowKeepsOneEpochWithoutDependingOnForegroundOrTelemetry()
    {
        var tracker = new ForzaCaptureObservationTracker();
        Assert.Null(tracker.Observe(Window));
        var first = Assert.IsType<RecorderTargetObservation>(tracker.Observe(Window));
        for (var index = 0; index < 100; index++) Assert.Same(first, tracker.Observe(Window));
        Assert.Equal(1, first.Epoch);
        Assert.Equal(Window.CreationFileTime, first.Target.CreationFileTime);
    }

    [Fact]
    public void ResizeMinimizeAndProcessReplacementRequireStableNewEpochs()
    {
        var tracker = new ForzaCaptureObservationTracker();
        tracker.Observe(Window);
        var first = tracker.Observe(Window)!;
        var resized = Window with { Width = 2560, Height = 1440 };
        Assert.Null(tracker.Observe(resized));
        var second = tracker.Observe(resized)!;
        Assert.True(second.Epoch > first.Epoch);
        Assert.Equal(first.Target, second.Target);
        Assert.Null(tracker.Observe(null));
        Assert.Null(tracker.Observe(resized));
        var restored = tracker.Observe(resized)!;
        Assert.True(restored.Epoch > second.Epoch);
        var replacement = resized with { CreationFileTime = 789 };
        Assert.Null(tracker.Observe(replacement));
        var restarted = tracker.Observe(replacement)!;
        Assert.True(restarted.Epoch > restored.Epoch);
        Assert.NotEqual(restored.Target, restarted.Target);
    }

    [Fact]
    public void ChangingSizeNeverArmsUntilTwoObservationsAgree()
    {
        var tracker = new ForzaCaptureObservationTracker();
        for (var width = 1000; width < 1010; width++) Assert.Null(tracker.Observe(Window with { Width = width }));
        Assert.NotNull(tracker.Observe(Window with { Width = 1009 }));
    }
}
