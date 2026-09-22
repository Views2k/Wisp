using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCaptureCanaryStartGateTests
{
    [Fact]
    public void RecordedOpacityFadeCannotStartCanaryBeforeFinalTargetSettles()
    {
        var gate = new ShiftCaptureCanaryStartGate(10_000_000);
        // Test7 events 665/673/676/679: same rectangle/RGB, alpha changes during
        // the ordinary show animation. Relative QPC and effective ARGB retained.
        (long Qpc, uint Argb)[] targets =
        [
            (45_349_391, 1761563492),
            (45_950_966, 4227814244),
            (45_987_150, 4278145892),
            (46_085_317, 4294923108)
        ];
        Assert.Equal(4, targets.Select(target => target.Argb).Distinct().Count());
        Assert.Single(targets.Select(target => target.Argb & 0x00FFFFFF).Distinct());
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(targets[0].Qpc, 1, true));
        // Old code starts its OFF phase at event 670, then aborts 5.4591 ms later.
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(45_896_301, 1, true));
        for (var index = 1; index < targets.Length; index++)
            Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(targets[index].Qpc, index + 1, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(48_585_316, 4, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.Ready, gate.Observe(48_585_317, 4, true));
    }

    [Fact]
    public void ReturningToForzaStartsDeadlineRatherThanTheRequestClick()
    {
        var gate = new ShiftCaptureCanaryStartGate(1_000);
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(1_000, 1, false));
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(61_000, 1, false));
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(62_000, 1, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.Ready, gate.Observe(62_250, 1, true));
    }

    [Fact]
    public void MissingTargetTimesOutAfterEligibleWaitWithoutStartingCanary()
    {
        var gate = new ShiftCaptureCanaryStartGate(1_000);
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(1_000, 0, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(10_999, 0, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.TimedOut, gate.Observe(11_000, 0, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.TimedOut, gate.Observe(12_000, 1, true));
    }

    [Fact]
    public void LostEligibilityRequiresAnotherContinuousStableInterval()
    {
        var gate = new ShiftCaptureCanaryStartGate(1_000);
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(1_000, 1, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(1_200, 1, false));
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(1_300, 1, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(1_549, 1, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.Ready, gate.Observe(1_550, 1, true));
    }

    [Fact]
    public void NewTargetCannotReusePreviousTargetSettlingTime()
    {
        var gate = new ShiftCaptureCanaryStartGate(1_000);
        gate.Observe(1_000, 1, true);
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(1_249, 2, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(1_250, 2, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.Ready, gate.Observe(1_499, 2, true));
    }

    [Fact]
    public void ContinuousTargetChangesReachBoundedTimeoutWithoutAutomaticRearming()
    {
        var gate = new ShiftCaptureCanaryStartGate(1_000);
        for (var now = 1_000; now < 11_000; now += 100)
            Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(now, now, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.TimedOut, gate.Observe(11_000, 11_000, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.TimedOut, gate.Observe(12_000, 11_000, true));
        gate.Reset();
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(13_000, 11_000, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.Ready, gate.Observe(13_250, 11_000, true));
    }

    [Fact]
    public void LostEligibilityDoesNotExtendAnAlreadyStartedDeadline()
    {
        var gate = new ShiftCaptureCanaryStartGate(1_000);
        gate.Observe(1_000, 1, true);
        gate.Observe(1_100, 1, false);
        Assert.Equal(ShiftCaptureCanaryReadiness.TimedOut, gate.Observe(11_000, 1, false));
        Assert.Equal(ShiftCaptureCanaryReadiness.TimedOut, gate.Observe(12_000, 1, true));
    }

    [Fact]
    public void ManualRetryDiscardsPriorSettlingTime()
    {
        var gate = new ShiftCaptureCanaryStartGate(1_000);
        gate.Observe(1_000, 1, true);
        Assert.Equal(ShiftCaptureCanaryReadiness.Ready, gate.Observe(1_250, 1, true));
        gate.Reset();
        Assert.Equal(ShiftCaptureCanaryReadiness.Waiting, gate.Observe(1_300, 1, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.Ready, gate.Observe(1_550, 1, true));
    }

    [Fact]
    public void BackwardClockCannotCreateStableTime()
    {
        var gate = new ShiftCaptureCanaryStartGate(1_000);
        gate.Observe(1_000, 1, true);
        Assert.Equal(ShiftCaptureCanaryReadiness.TimedOut, gate.Observe(999, 1, true));
        Assert.Equal(ShiftCaptureCanaryReadiness.TimedOut, gate.Observe(1_300, 1, true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidClockFrequencyIsRejected(long frequency)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShiftCaptureCanaryStartGate(frequency));
    }
}
