using System.Diagnostics;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AnalogHudPlaybackTests
{
    [Fact]
    public void EmptyAndResetPlaybackCannotShowAnOldNeedle()
    {
        var playback = new AnalogHudPlayback();
        Assert.False(playback.Sample(Timestamp(0)).NeedleVisible);
        playback.Observe(Frame(0, 1_000), Timestamp(0));
        Assert.True(playback.Sample(Timestamp(1)).NeedleVisible);

        playback.Reset();
        Assert.Equal(default, playback.Sample(Timestamp(2)));
        playback.Observe(Frame(10, 6_000), Timestamp(10));
        var restarted = playback.Sample(Timestamp(11));
        Assert.Equal(6_000, restarted.AppliedRpm);
        Assert.Equal(0, restarted.Blur);
    }

    [Fact]
    public void FallbackUsesTheExistingFortyMillisecondTimelineAndKeepsRawRpm()
    {
        var playback = new AnalogHudPlayback();
        playback.Observe(Frame(0, 1_000), Timestamp(0));
        var latest = Frame(20, 5_000);
        playback.Observe(latest, Timestamp(20));

        var middle = playback.Sample(Timestamp(50));
        Assert.False(middle.Native);
        Assert.Equal(3_000, middle.AppliedRpm!.Value, 6);
        Assert.Equal(210, middle.Angle, 6);
        Assert.Equal(latest, middle.Frame);
        Assert.Equal(5_000, middle.Frame.EngineRpm);
        Assert.Equal(40, middle.PlaybackTargetDelayMilliseconds, 6);

        var endpoint = playback.Sample(Timestamp(60));
        Assert.Equal(5_000, endpoint.AppliedRpm!.Value, 6);
        Assert.Equal(270, endpoint.Angle, 6);
    }

    [Fact]
    public void FallbackBlurUsesTheExistingSignedCombustionExposure()
    {
        var playback = new AnalogHudPlayback();
        playback.Observe(Frame(0, 1_000), Timestamp(0));
        playback.Observe(Frame(20, 5_000), Timestamp(20));
        var before = playback.Sample(Timestamp(40));
        var after = playback.Sample(Timestamp(50));

        Assert.Equal(0, before.Blur);
        Assert.Equal(NativeGaugeGeometry.CombustionNeedleBlurRadians(after.Angle - before.Angle, .01), after.Blur, 12);
        Assert.True(after.Blur < 0);
        Assert.Equal(0, playback.Sample(Timestamp(400)).Blur);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueuedRpmFrameCrossingThePreviousSampleDoesNotReseedOrShiftItsSourceTime(bool hasReceivedTimestamp)
    {
        var playback = new AnalogHudPlayback();
        for (int ms = 0; ms <= 40; ms += 10)
            playback.Observe(Frame(ms, 1_000 + ms * 100), Timestamp(ms));
        var before = playback.Sample(Timestamp(50));
        var queued = Frame(49, 5_900) with { ReceivedTimestamp = hasReceivedTimestamp ? Timestamp(49) : null };

        playback.ObserveQueued(queued, Timestamp(49), Timestamp(52));
        var after = playback.Sample(Timestamp(52));

        Assert.Equal(before.ReseedCount, after.ReseedCount);
        Assert.Equal(before.StarvationReseedCount, after.StarvationReseedCount);
        Assert.Equal(2_200, after.AppliedRpm!.Value, 6);
        Assert.Equal(5_900, after.Frame.EngineRpm);
        Assert.Equal(40, after.PlaybackTargetDelayMilliseconds, 6);
        Assert.Equal(5_500, playback.Sample(Timestamp(85)).AppliedRpm!.Value, 6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueuedNativePairCrossingThePreviousSampleRetainsBothOriginalChannels(bool hasNativeTimestamp)
    {
        var playback = new AnalogHudPlayback();
        for (int ms = 0; ms <= 40; ms += 10)
            playback.Observe(Frame(ms, 4_000) with
            {
                NativeNeedleAngleDegrees = 120 + ms,
                NativeNeedleBlurAmount = -.2 + ms * .005
            }, Timestamp(ms));
        var before = playback.Sample(Timestamp(50));
        var queued = Frame(49, 4_000) with
        {
            NativeNeedleAngleDegrees = 169,
            NativeNeedleBlurAmount = .045,
            NativeGaugeObservedTimestamp = hasNativeTimestamp ? Timestamp(49) : 0
        };

        playback.ObserveQueued(queued, Timestamp(49), Timestamp(52));
        var after = playback.Sample(Timestamp(52));

        Assert.True(after.Native);
        Assert.Equal(before.ReseedCount, after.ReseedCount);
        Assert.Equal(132, after.Angle, 6);
        Assert.Equal(-.14, after.Blur, 6);
        var later = playback.Sample(Timestamp(85));
        Assert.True(later.Native);
        Assert.Equal(165, later.Angle, 6);
        Assert.Equal(.025, later.Blur, 6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WaitingInTheQueueCannotRefreshAnExpiredNativePair(bool hasNativeTimestamp)
    {
        var playback = new AnalogHudPlayback();
        var queued = Frame(0, 4_000) with
        {
            NativeNeedleAngleDegrees = 320,
            NativeNeedleBlurAmount = -.4,
            NativeGaugeObservedTimestamp = hasNativeTimestamp ? Timestamp(0) : 0
        };

        playback.ObserveQueued(queued, Timestamp(10), Timestamp(100));
        var after = playback.Sample(Timestamp(100));

        Assert.False(after.Native);
        Assert.Equal(4_000, after.AppliedRpm);
        Assert.Equal(240, after.Angle);
        Assert.Equal(0, after.Blur);
    }

    [Fact]
    public void OlderQueuedRpmCannotRetargetOrReseedPlayback()
    {
        var playback = new AnalogHudPlayback();
        for (int ms = 0; ms <= 40; ms += 10)
            playback.Observe(Frame(ms, 1_000 + ms * 100), Timestamp(ms));
        var before = playback.Sample(Timestamp(50));

        playback.ObserveQueued(Frame(20, 7_000), Timestamp(49), Timestamp(52));
        var after = playback.Sample(Timestamp(52));

        Assert.Equal(before.ReseedCount, after.ReseedCount);
        Assert.Equal(2_200, after.AppliedRpm!.Value, 6);
        Assert.Equal(5_000, playback.Sample(Timestamp(85)).AppliedRpm!.Value, 6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATrueConsumptionClockRewindStillReseedsTheSelectedSource(bool native)
    {
        var playback = new AnalogHudPlayback();
        NativeGaugeFrame Source(int ms, double value) => native
            ? Frame(ms, 4_000) with { NativeNeedleAngleDegrees = value, NativeNeedleBlurAmount = -.1 }
            : Frame(ms, value);
        playback.Observe(Source(0, native ? 150 : 1_000), Timestamp(0));
        playback.Observe(Source(40, native ? 300 : 7_000), Timestamp(40));
        var before = playback.Sample(Timestamp(50));

        playback.ObserveQueued(Source(10, native ? 240 : 900), Timestamp(10), Timestamp(11));
        var after = playback.Sample(Timestamp(11));

        Assert.Equal(before.ReseedCount + 1, after.ReseedCount);
        Assert.Equal(native, after.Native);
        if (native) { Assert.Equal(240, after.Angle); Assert.Equal(-.1, after.Blur); }
        else { Assert.Equal(900, after.AppliedRpm); Assert.Equal(0, after.Blur); }
    }

    [Fact]
    public void QueuedCarChangeStillSnapsWithoutBlendingTheOldEngine()
    {
        var playback = new AnalogHudPlayback();
        playback.Observe(Frame(0, 1_000), Timestamp(0));
        playback.Observe(Frame(40, 7_000), Timestamp(40));
        var before = playback.Sample(Timestamp(50));
        var queued = Frame(49, 900) with { CarOrdinal = 3766 };

        playback.ObserveQueued(queued, Timestamp(49), Timestamp(52));
        var after = playback.Sample(Timestamp(52));

        Assert.Equal(before.ReseedCount + 1, after.ReseedCount);
        Assert.Equal(queued.CarOrdinal, after.Frame.CarOrdinal);
        Assert.Equal(900, after.AppliedRpm);
        Assert.Equal(0, after.Blur);
    }

    [Fact]
    public void NativeAngleAndSignedBlurRemainTheExactPairedPlaybackChannels()
    {
        var playback = new AnalogHudPlayback();
        playback.Observe(Frame(0, 1_000) with { NativeNeedleAngleDegrees = 120, NativeNeedleBlurAmount = -.20 }, Timestamp(0));
        playback.Observe(Frame(20, 5_000) with { NativeNeedleAngleDegrees = 240, NativeNeedleBlurAmount = .40 }, Timestamp(20));

        var sample = playback.Sample(Timestamp(50));
        Assert.True(sample.Native);
        Assert.True(sample.NeedleVisible);
        Assert.Null(sample.AppliedRpm);
        Assert.Equal(180, sample.Angle, 6);
        Assert.Equal(.10, sample.Blur, 6);
        Assert.Equal(5_000, sample.Frame.EngineRpm);
        Assert.Equal(40, sample.PlaybackTargetDelayMilliseconds, 6);
    }

    [Fact]
    public void NativeAvailabilityChecksDoNotAdvanceOrResetEitherPlaybackChannel()
    {
        var actual = new AnalogHudPlayback();
        var reference = new AnalogHudPlayback();
        foreach (var playback in new[] { actual, reference })
        {
            playback.Observe(Frame(0, 1_000) with { NativeNeedleAngleDegrees = 120, NativeNeedleBlurAmount = -.2 }, Timestamp(0));
            playback.Observe(Frame(20, 5_000) with { NativeNeedleAngleDegrees = 240, NativeNeedleBlurAmount = .4 }, Timestamp(20));
            Assert.True(playback.Sample(Timestamp(50)).Native);
        }

        Assert.True(actual.HasNativeNeedle(Timestamp(60)));
        Assert.False(actual.HasNativeNeedle(Timestamp(200)));
        Assert.True(actual.HasNativeNeedle(Timestamp(50)));

        var afterChecks = actual.Sample(Timestamp(55));
        Assert.Equal(reference.Sample(Timestamp(55)), afterChecks);
        Assert.Equal(210, afterChecks.Angle, 6);
        Assert.Equal(.25, afterChecks.Blur, 6);
    }

    [Fact]
    public void NativeAvailabilityUsesExactObservationAgeAndRejectsFallbackAndResetState()
    {
        var playback = new AnalogHudPlayback();
        Assert.False(playback.HasNativeNeedle(Timestamp(0)));
        playback.Observe(Frame(0, 4_000), Timestamp(0));
        Assert.False(playback.HasNativeNeedle(Timestamp(1)));
        playback.Observe(Frame(20, 4_000) with
        {
            NativeNeedleAngleDegrees = 240,
            NativeNeedleBlurAmount = -.1
        }, Timestamp(20));
        var observedAt = Timestamp(20);
        var lastFresh = Timestamp(20 + NativeNeedlePlayback.NativeSampleFreshnessMilliseconds);

        Assert.False(playback.HasNativeNeedle(observedAt - 1));
        Assert.True(playback.HasNativeNeedle(observedAt));
        Assert.True(playback.HasNativeNeedle(lastFresh));
        Assert.False(playback.HasNativeNeedle(lastFresh + 1));
        Assert.True(playback.HasNativeNeedle(observedAt));
        playback.Reset();
        Assert.False(playback.HasNativeNeedle(observedAt));
    }

    [Fact]
    public void DelayedNativeObservationUsesItsOwnClockInsteadOfTheUdpClock()
    {
        var playback = new AnalogHudPlayback();
        var native = new NativeNeedlePlayback();
        foreach (var milliseconds in new[] { 0, 13, 25, 41, 55 })
        {
            var frame = Frame(milliseconds, 2_000 + milliseconds * 20) with
            {
                NativeNeedleAngleDegrees = 130 + milliseconds,
                NativeNeedleBlurAmount = -.25 + milliseconds * .002,
                NativeGaugeObservedTimestamp = Timestamp(milliseconds - 3)
            };
            var now = Timestamp(milliseconds + 2);
            playback.Observe(frame, now);
            Assert.True(native.Observe(frame.CarOrdinal, frame.GameTimestampMilliseconds,
                frame.NativeNeedleAngleDegrees, frame.NativeNeedleBlurAmount, now,
                frame.NativeGaugeObservedTimestamp, false, out _));
            var sampledAt = Timestamp(milliseconds + 7);
            var actual = playback.Sample(sampledAt);
            Assert.True(native.Sample(sampledAt, out var expected));
            Assert.True(actual.Native);
            Assert.Equal(expected.Angle, actual.Angle, 12);
            Assert.Equal(expected.Blur, actual.Blur, 12);
        }
    }

    [Fact]
    public void DuplicateNativePairCannotRetargetTheNeedleEvenWhenUdpIsNew()
    {
        var playback = new AnalogHudPlayback();
        playback.Observe(Frame(0, 1_000) with { NativeNeedleAngleDegrees = 150, NativeNeedleBlurAmount = -.1 }, Timestamp(0));
        playback.Observe(Frame(20, 5_000) with
        {
            NativeNeedleAngleDegrees = 300,
            NativeNeedleBlurAmount = .5,
            NativeGaugeObservedTimestamp = Timestamp(0)
        }, Timestamp(20));

        var sample = playback.Sample(Timestamp(50));
        Assert.True(sample.Native);
        Assert.Equal(150, sample.Angle);
        Assert.Equal(-.1, sample.Blur);
        Assert.Equal(5_000, sample.Frame.EngineRpm);
    }

    [Fact]
    public void ExpiredNativePairFallsBackToRpmWithoutReusingNativeBlur()
    {
        var playback = new AnalogHudPlayback();
        playback.Observe(Frame(0, 4_000) with { NativeNeedleAngleDegrees = 320, NativeNeedleBlurAmount = -.4 }, Timestamp(0));
        Assert.True(playback.Sample(Timestamp(40)).Native);

        var expired = playback.Sample(Timestamp(NativeNeedlePlayback.NativeSampleFreshnessMilliseconds + 1));
        Assert.False(expired.Native);
        Assert.True(expired.NeedleVisible);
        Assert.Equal(4_000, expired.AppliedRpm);
        Assert.Equal(240, expired.Angle);
        Assert.Equal(0, expired.Blur);
    }

    [Fact]
    public void ExplicitNativeInvalidationSwitchesImmediatelyToIndependentRpm()
    {
        var playback = new AnalogHudPlayback();
        playback.Observe(Frame(0, 4_000) with { NativeNeedleAngleDegrees = 320, NativeNeedleBlurAmount = -.4 }, Timestamp(0));
        playback.Observe(Frame(20, 4_000) with { NativeGaugeSourceInvalidated = true }, Timestamp(20));

        var sample = playback.Sample(Timestamp(21));
        Assert.False(sample.Native);
        Assert.Equal(240, sample.Angle);
        Assert.Equal(0, sample.Blur);
    }

    [Fact]
    public void CarChangeCannotBlendThePreviousEngineOrCreateAnArtificialBlur()
    {
        var playback = new AnalogHudPlayback();
        playback.Observe(Frame(0, 1_000), Timestamp(0));
        playback.Observe(Frame(20, 7_000), Timestamp(20));
        playback.Sample(Timestamp(50));
        var nextCar = Frame(60, 900) with { CarOrdinal = 3766 };
        playback.Observe(nextCar, Timestamp(60));

        var sample = playback.Sample(Timestamp(61));
        Assert.Equal(nextCar.CarOrdinal, sample.Frame.CarOrdinal);
        Assert.Equal(900, sample.AppliedRpm);
        Assert.Equal(147, sample.Angle, 6);
        Assert.Equal(0, sample.Blur);
    }

    [Fact]
    public void MissingExactScaleHidesOnlyDerivedNeedleAndDoesNotInventData()
    {
        var playback = new AnalogHudPlayback();
        var frame = Frame(0, 4_000) with { ExactRedline = ExactRedlineResult.Unavailable() };
        playback.Observe(frame, Timestamp(0));
        Assert.False(playback.Sample(Timestamp(1)).NeedleVisible);

        playback.Observe(frame with
        {
            NativeNeedleAngleDegrees = 240,
            NativeNeedleBlurAmount = -.1,
            NativeGaugeObservedTimestamp = Timestamp(10)
        }, Timestamp(10));
        var native = playback.Sample(Timestamp(11));
        Assert.True(native.Native);
        Assert.True(native.NeedleVisible);
        Assert.Equal(240, native.Angle);
    }

    private static NativeGaugeFrame Frame(int milliseconds, double rpm) => new(
        true, 100, rpm, 8_000, TransmissionGear.Neutral, SpeedUnit.MilesPerHour,
        ExactRedlineResult.Exact(7_000 * 2 * Math.PI / 60), CarOrdinal: 314,
        GameTimestampMilliseconds: (uint)(1_000 + milliseconds), ReceivedTimestamp: Timestamp(milliseconds),
        NativeGaugeObservedTimestamp: Timestamp(milliseconds));

    private static long Timestamp(double milliseconds) =>
        (long)Math.Round((1_000 + milliseconds) * Stopwatch.Frequency / 1_000d);
}
