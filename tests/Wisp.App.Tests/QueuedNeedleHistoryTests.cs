using System.Diagnostics;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class QueuedNeedleHistoryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueuedRpmHistoryAfterRendererWaitKeepsTheOriginalTimeline(bool hasSourceTimestamp)
    {
        var continuous = new AnalogHudPlayback();
        var queued = new AnalogHudPlayback();
        var before = Prime(continuous, queued, native: false, hasSourceTimestamp);

        DrainAfterWait(continuous, queued, native: false, hasSourceTimestamp);
        var expected = continuous.Sample(Timestamp(263));
        var actual = queued.Sample(Timestamp(263));

        Assert.False(actual.Native);
        Assert.Equal(before.ReseedCount, actual.ReseedCount);
        Assert.Equal(before.StarvationReseedCount, actual.StarvationReseedCount);
        Assert.Equal(20, expected.PlaybackDelayMilliseconds, 6);
        Assert.Equal(20, actual.PlaybackTargetDelayMilliseconds, 6);
        Assert.InRange(actual.PlaybackDelayMilliseconds, 19.999, 20.001);
        Assert.Equal(3_430, expected.AppliedRpm!.Value, 6);
        Assert.Equal(expected.AppliedRpm.Value, actual.AppliedRpm!.Value, 6);
        Assert.Equal(expected.Angle, actual.Angle, 6);
        Assert.InRange(actual.AppliedRpm.Value, Frame(240, false, hasSourceTimestamp).EngineRpm,
            Frame(250, false, hasSourceTimestamp).EngineRpm);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueuedNativeHistoryAfterRendererWaitKeepsAngleAndBlurOnTheOriginalTimeline(bool hasSourceTimestamp)
    {
        var continuous = new AnalogHudPlayback();
        var queued = new AnalogHudPlayback();
        var before = Prime(continuous, queued, native: true, hasSourceTimestamp);

        DrainAfterWait(continuous, queued, native: true, hasSourceTimestamp);
        var expected = continuous.Sample(Timestamp(263));
        var actual = queued.Sample(Timestamp(263));

        Assert.True(actual.Native);
        Assert.Null(actual.AppliedRpm);
        Assert.Equal(before.ReseedCount, actual.ReseedCount);
        Assert.Equal(before.StarvationReseedCount, actual.StarvationReseedCount);
        Assert.Equal(20, expected.PlaybackDelayMilliseconds, 6);
        Assert.Equal(20, actual.PlaybackTargetDelayMilliseconds, 6);
        Assert.InRange(actual.PlaybackDelayMilliseconds, 19.999, 20.001);
        Assert.Equal(241.5, expected.Angle, 6);
        Assert.Equal(.043, expected.Blur, 6);
        Assert.Equal(expected.Angle, actual.Angle, 6);
        Assert.Equal(expected.Blur, actual.Blur, 6);
        Assert.InRange(actual.Angle, 240, 245);
        Assert.InRange(actual.Blur, .04, .05);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialQueuedBatchSamplesAvailableHistoryAtOneConsumptionTimestamp(bool native)
    {
        var playback = new AnalogHudPlayback();
        for (var milliseconds = 0; milliseconds <= 40; milliseconds += 10)
            playback.ObserveQueued(Frame(milliseconds, native, true), Timestamp(milliseconds), Timestamp(50));

        var actual = playback.Sample(Timestamp(50));

        Assert.Equal(native, actual.Native);
        Assert.Equal(1L, actual.ReseedCount);
        Assert.Equal(0L, actual.StarvationReseedCount);
        Assert.Equal(20, actual.PlaybackDelayMilliseconds, 6);
        Assert.Equal(20, actual.PlaybackTargetDelayMilliseconds, 6);
        if (native)
        {
            Assert.Equal(135, actual.Angle, 6);
            Assert.Equal(-.17, actual.Blur, 6);
        }
        else Assert.Equal(1_300, actual.AppliedRpm!.Value, 6);
        Assert.Equal(actual, playback.Sample(Timestamp(50)));
    }

    [Theory]
    [InlineData(false, "duplicate")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "future")]
    [InlineData(true, "future")]
    [InlineData(true, "partial")]
    public void RejectedQueuedSamplesCannotAdvanceTheTimelineBeforeRemainingHistoryArrives(bool native, string rejection)
    {
        var continuous = new AnalogHudPlayback();
        var queued = new AnalogHudPlayback();
        var before = Prime(continuous, queued, native, hasSourceTimestamp: true);
        var rejectedAt = rejection == "future" ? 300 : rejection == "partial" ? 210 : 190;
        var rejected = Frame(rejectedAt, native, true) with
        {
            EngineRpm = 7_999,
            NativeNeedleAngleDegrees = native ? 359 : double.NaN,
            NativeNeedleBlurAmount = native && rejection != "partial" ? .6 : double.NaN
        };

        queued.ObserveQueued(rejected, Timestamp(rejectedAt), Timestamp(263));
        DrainAfterWait(continuous, queued, native, hasSourceTimestamp: true);
        var expected = continuous.Sample(Timestamp(263));
        var actual = queued.Sample(Timestamp(263));

        Assert.Equal(native, actual.Native);
        Assert.Equal(before.ReseedCount, actual.ReseedCount);
        Assert.Equal(before.StarvationReseedCount, actual.StarvationReseedCount);
        const int delay = 20;
        Assert.Equal(delay, actual.PlaybackTargetDelayMilliseconds, 6);
        Assert.InRange(actual.PlaybackDelayMilliseconds, delay - .001, delay + .001);
        Assert.Equal(expected.Angle, actual.Angle, 6);
        if (native) Assert.Equal(expected.Blur, actual.Blur, 6);
        else Assert.Equal(expected.AppliedRpm!.Value, actual.AppliedRpm!.Value, 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedQueuedRpmStillDetectsALaterConsumptionClockRewind(bool future)
    {
        var queued = new AnalogHudPlayback();
        var before = Prime(new AnalogHudPlayback(), queued, native: false, hasSourceTimestamp: true);
        var rejectedAt = future ? 500 : 190;

        queued.ObserveQueued(Frame(rejectedAt, false, true), Timestamp(rejectedAt), Timestamp(300));
        queued.ObserveQueued(Frame(270, false, true), Timestamp(270), Timestamp(280));
        var actual = queued.Sample(Timestamp(280));

        Assert.False(actual.Native);
        Assert.Equal(before.ReseedCount + 1, actual.ReseedCount);
        Assert.Equal(before.StarvationReseedCount, actual.StarvationReseedCount);
        Assert.Equal(3_700, actual.AppliedRpm!.Value, 6);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("future")]
    [InlineData("partial")]
    public void RetainedQueuedNativePairStillDetectsALaterConsumptionClockRewind(string rejection)
    {
        var queued = new AnalogHudPlayback();
        var before = Prime(new AnalogHudPlayback(), queued, native: true, hasSourceTimestamp: true);
        var rejectedAt = rejection == "future" ? 300 : rejection == "partial" ? 210 : 190;
        var rejected = Frame(rejectedAt, true, true);
        if (rejection == "partial") rejected = rejected with { NativeNeedleBlurAmount = double.NaN };

        queued.ObserveQueued(rejected, Timestamp(rejectedAt), Timestamp(263));
        queued.ObserveQueued(Frame(250, true, true), Timestamp(250), Timestamp(260));
        var actual = queued.Sample(Timestamp(260));

        Assert.True(actual.Native);
        Assert.Equal(before.ReseedCount + 1, actual.ReseedCount);
        Assert.Equal(before.StarvationReseedCount, actual.StarvationReseedCount);
        Assert.Equal(245, actual.Angle, 6);
        Assert.Equal(.05, actual.Blur, 6);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SamplingBeforeTheLastQueuedConsumptionCannotAdvancePlayback(bool native, bool future)
    {
        var queued = new AnalogHudPlayback();
        var before = Prime(new AnalogHudPlayback(), queued, native, hasSourceTimestamp: true);
        var rejectedAt = future ? 300 : 190;

        queued.ObserveQueued(Frame(rejectedAt, native, true), Timestamp(rejectedAt), Timestamp(263));
        var earlier = queued.Sample(Timestamp(260));

        Assert.Equal(before.Angle, earlier.Angle, 6);
        if (native) Assert.Equal(before.Blur, earlier.Blur, 6);
        else Assert.Equal(before.AppliedRpm!.Value, earlier.AppliedRpm!.Value, 6);

        queued.ObserveQueued(Frame(250, native, true), Timestamp(250), Timestamp(260));
        var restarted = queued.Sample(Timestamp(260));
        Assert.Equal(before.ReseedCount + 1, restarted.ReseedCount);
        Assert.Equal(before.StarvationReseedCount, restarted.StarvationReseedCount);
        if (native)
        {
            Assert.Equal(245, restarted.Angle, 6);
            Assert.Equal(.05, restarted.Blur, 6);
        }
        else Assert.Equal(3_500, restarted.AppliedRpm!.Value, 6);
    }

    private static AnalogHudSample Prime(AnalogHudPlayback continuous, AnalogHudPlayback queued,
        bool native, bool hasSourceTimestamp)
    {
        for (var milliseconds = 0; milliseconds <= 200; milliseconds += 10)
        {
            var frame = Frame(milliseconds, native, hasSourceTimestamp);
            continuous.Observe(frame, Timestamp(milliseconds));
            queued.Observe(frame, Timestamp(milliseconds));
        }

        var before = queued.Sample(Timestamp(205));
        Assert.Equal(continuous.Sample(Timestamp(205)), before);
        Assert.Equal(20, before.PlaybackDelayMilliseconds, 6);
        return before;
    }

    private static void DrainAfterWait(AnalogHudPlayback continuous, AnalogHudPlayback queued,
        bool native, bool hasSourceTimestamp)
    {
        // Source observations continue every 10 ms while rendering waits 58 ms.
        // The worker then consumes the complete queue before its next draw.
        for (var milliseconds = 210; milliseconds <= 260; milliseconds += 10)
        {
            var frame = Frame(milliseconds, native, hasSourceTimestamp);
            continuous.Observe(frame, Timestamp(milliseconds));
            queued.ObserveQueued(frame, Timestamp(milliseconds), Timestamp(263));
        }
    }

    private static NativeGaugeFrame Frame(int milliseconds, bool native, bool hasSourceTimestamp) => new(
        true, 100, 1_000 + milliseconds * 10, 8_000, TransmissionGear.Neutral, SpeedUnit.MilesPerHour,
        ExactRedlineResult.Exact(7_000 * 2 * Math.PI / 60), CarOrdinal: 314,
        GameTimestampMilliseconds: (uint)(1_000 + milliseconds),
        ReceivedTimestamp: hasSourceTimestamp ? Timestamp(milliseconds) : null,
        NativeNeedleAngleDegrees: native ? 120 + milliseconds * .5 : double.NaN,
        NativeNeedleBlurAmount: native ? -.2 + milliseconds * .001 : double.NaN,
        NativeGaugeObservedTimestamp: native && hasSourceTimestamp ? Timestamp(milliseconds) : 0);

    private static long Timestamp(double milliseconds) =>
        (long)Math.Round((1_000 + milliseconds) * Stopwatch.Frequency / 1_000d);
}
