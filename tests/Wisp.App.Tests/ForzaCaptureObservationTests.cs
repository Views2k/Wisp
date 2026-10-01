using Wisp.App.Clips;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ForzaCaptureObservationTests
{
    private static readonly ForzaCaptureCandidate Window = new(42, 123, 456, 1920, 1080);

    [Fact]
    public void UnchangedEligibleWindowKeepsOneEpochWithoutTelemetry()
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

    [Theory]
    [InlineData(123, 123, 0, 0, 1920, 1080, true)]
    [InlineData(123, 0, 0, 0, 1920, 1080, false)]
    [InlineData(123, 124, 0, 0, 1920, 1080, false)]
    [InlineData(0, 0, 0, 0, 1920, 1080, false)]
    [InlineData(123, 123, 1, 0, 1920, 1080, false)]
    [InlineData(123, 123, 0, 1, 1920, 1080, false)]
    [InlineData(123, 123, 0, 0, 1919, 1080, false)]
    [InlineData(123, 123, 0, 0, 1920, 1079, false)]
    [InlineData(123, 123, -1, 0, 1920, 1080, false)]
    [InlineData(123, 123, 0, 0, 3840, 1080, false)]
    public void CaptureRequiresExactForegroundAndPhysicalClientMonitorMatch(
        int window, int foreground, int left, int top, int right, int bottom, bool expected)
    {
        Assert.Equal(expected, ForzaCaptureEligibility.IsEligible(new(window), new(foreground),
            new(left, top, right, bottom), new(0, 0, 1920, 1080)));
    }

    [Fact]
    public void NegativeMonitorCoordinatesAreValidButDegenerateAndOverflowingBoundsAreNot()
    {
        var secondary = new PixelBounds(-3840, -2160, 0, 0);
        Assert.True(ForzaCaptureEligibility.IsEligible(new(123), new(123), secondary, secondary));
        Assert.False(ForzaCaptureEligibility.IsEligible(new(123), new(123), default, default));
        var overflow = new PixelBounds(int.MinValue, 0, int.MaxValue, 1080);
        Assert.False(ForzaCaptureEligibility.IsEligible(new(123), new(123), overflow, overflow));
    }

    [Fact]
    public void FocusLossDisarmsAndTheSameFullscreenWindowMustStabilizeAgain()
    {
        var tracker = new ForzaCaptureObservationTracker();
        tracker.Observe(Window);
        var first = tracker.Observe(Window)!;
        var bounds = new PixelBounds(0, 0, 1920, 1080);
        var eligible = ForzaCaptureEligibility.IsEligible(new(123), new(124), bounds, bounds);
        Assert.Null(tracker.Observe(eligible ? Window : null));
        Assert.Null(tracker.Observe(Window));
        Assert.True(tracker.Observe(Window)!.Epoch > first.Epoch);
    }
}
