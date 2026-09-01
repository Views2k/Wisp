using System.Diagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeTachometerRecoveryTests
{
    private const int CarOrdinal = 314;
    private const int LastWarmupMilliseconds = 120;

    [Theory]
    [InlineData(110)]
    [InlineData(120)]
    public void OlderOrDuplicateDifferentCarReceiveCannotReplaceCurrentHistory(int receivedMilliseconds)
    {
        AssertRejectedFramePreservesHistory(3766, 9_000, receivedMilliseconds);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    public void OlderOrDuplicateInvalidRpmCannotClearCurrentHistory(double invalidRpm)
    {
        foreach (var receivedMilliseconds in new[] { 110, 120 })
        {
            AssertRejectedFramePreservesHistory(CarOrdinal, invalidRpm, receivedMilliseconds);
        }
    }

    [Theory]
    [InlineData(500d)]
    [InlineData(9_000d)]
    public void DuplicateReceiveIdentityCannotRetargetOrRefreshContinuity(double reboundRpm)
    {
        var baseline = CreateRunningHistory();
        var interpolator = CreateRunningHistory();
        var receivedTimestamp = TimestampMilliseconds(LastWarmupMilliseconds);

        foreach (var milliseconds in new[] { 140, 160, 180, 190 })
        {
            var now = TimestampMilliseconds(milliseconds);
            var expected = baseline.Sample(now);
            Assert.Equal(expected, interpolator.Observe(CarOrdinal, GameTimestamp(LastWarmupMilliseconds),
                reboundRpm, now, receivedTimestamp), 6);
            Assert.Equal(expected, interpolator.Sample(now), 6);
            Assert.Equal(expected, interpolator.Observe(CarOrdinal, GameTimestamp(LastWarmupMilliseconds),
                reboundRpm, now, receivedTimestamp), 6);
        }

        // The last real receive is still at 120 ms, not the rebound at 190 ms.
        var resumedAt = TimestampMilliseconds(196);
        Assert.Equal(5_000, interpolator.Observe(CarOrdinal, GameTimestamp(136), 5_000,
            resumedAt, resumedAt), 6);
        Assert.Equal(5_000, interpolator.Sample(resumedAt), 6);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    public void ActualClockRewindResetsEvenAfterANewerSampleCallback(int rewoundMilliseconds)
    {
        var interpolator = CreateRunningHistory();
        interpolator.Sample(TimestampMilliseconds(200));
        var rewoundAt = TimestampMilliseconds(rewoundMilliseconds);

        // 150 ms is newer than the last Observe (120 ms), but older than Sample (200 ms).
        Assert.Equal(900, interpolator.Observe(CarOrdinal, GameTimestamp(130), 900,
            rewoundAt, rewoundAt), 6);
        Assert.Equal(900, interpolator.Sample(rewoundAt), 6);

        var nextAt = TimestampMilliseconds(rewoundMilliseconds + 10);
        Assert.Equal(900, interpolator.Observe(CarOrdinal, GameTimestamp(140), 1_100,
            nextAt, nextAt), 6);
        Assert.Equal(1_000, interpolator.Sample(TimestampMilliseconds(rewoundMilliseconds + 45)), 6);
        Assert.Equal(1_100, interpolator.Sample(TimestampMilliseconds(rewoundMilliseconds + 50)), 6);
    }

    [Theory]
    [InlineData(75, false)]
    [InlineData(76, true)]
    public void ReceiveStarvationSnapsOnlyBeyondSeventyFiveMilliseconds(int gapMilliseconds, bool shouldSnap)
    {
        var interpolator = CreateRunningHistory();
        var resumedMilliseconds = LastWarmupMilliseconds + gapMilliseconds;
        var resumedAt = TimestampMilliseconds(resumedMilliseconds);
        var previous = interpolator.Sample(resumedAt);

        var actual = interpolator.Observe(CarOrdinal, GameTimestamp(136), 7_000,
            resumedAt, resumedAt);

        Assert.Equal(shouldSnap ? 7_000 : previous, actual, 6);
        Assert.Equal(actual, interpolator.Sample(resumedAt), 6);
        for (var offset = 5; offset <= 75; offset += 5)
        {
            Assert.InRange(interpolator.Sample(TimestampMilliseconds(resumedMilliseconds + offset)),
                previous, 7_000);
        }

        Assert.Equal(7_000, interpolator.Sample(TimestampMilliseconds(resumedMilliseconds + 75)), 6);
    }

    [Theory]
    [InlineData(250, false)]
    [InlineData(251, true)]
    public void GameDiscontinuityRetainsItsIndependentTwoHundredFiftyMillisecondBoundary(
        int gameGapMilliseconds, bool shouldSnap)
    {
        var interpolator = CreateRunningHistory();
        var now = TimestampMilliseconds(130);
        var previous = interpolator.Sample(now);

        var actual = interpolator.Observe(CarOrdinal,
            GameTimestamp(LastWarmupMilliseconds) + (uint)gameGapMilliseconds,
            7_000, now, now);

        Assert.Equal(shouldSnap ? 7_000 : previous, actual, 6);
        Assert.Equal(actual, interpolator.Sample(now), 6);
        for (var milliseconds = 135; milliseconds <= 230; milliseconds += 5)
        {
            Assert.InRange(interpolator.Sample(TimestampMilliseconds(milliseconds)), previous, 7_000);
        }

        Assert.Equal(7_000, interpolator.Sample(TimestampMilliseconds(230)), 6);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(100, true)]
    [InlineData(200, false)]
    [InlineData(200, true)]
    public void StarvedStreamRecoversWithinSeventyFiveMillisecondsOfHistoryImmediately(
        int gapMilliseconds, bool falling)
    {
        var interpolator = CreateRunningHistory(falling);
        var resumedMilliseconds = LastWarmupMilliseconds + gapMilliseconds;
        var previousLatestRpm = RampRpm(LastWarmupMilliseconds, falling);
        var initialRpm = RampRpm(0, falling);

        for (var milliseconds = 125; milliseconds < resumedMilliseconds; milliseconds += 5)
        {
            Assert.InRange(interpolator.Sample(TimestampMilliseconds(milliseconds)),
                Math.Min(initialRpm, previousLatestRpm), Math.Max(initialRpm, previousLatestRpm));
        }

        Assert.Equal(previousLatestRpm,
            interpolator.Sample(TimestampMilliseconds(resumedMilliseconds)), 6);

        var resumedRpm = RampRpm(resumedMilliseconds, falling);
        var latestAcceptedRpm = resumedRpm;
        var sourceSlope = falling ? -2d : 2d;
        for (var offset = 0; offset <= 400; offset += 5)
        {
            var milliseconds = resumedMilliseconds + offset;
            var now = TimestampMilliseconds(milliseconds);
            if (offset % 10 == 0)
            {
                latestAcceptedRpm = RampRpm(milliseconds, falling);
                var observed = interpolator.Observe(CarOrdinal, GameTimestamp(milliseconds),
                    latestAcceptedRpm, now, now);
                if (offset == 0)
                {
                    // This discontinuity is an explicit snap to received data, not extrapolation.
                    Assert.Equal(resumedRpm, observed, 6);
                }

                Assert.InRange(observed, Math.Min(resumedRpm, latestAcceptedRpm),
                    Math.Max(resumedRpm, latestAcceptedRpm));
            }

            var displayed = interpolator.Sample(now);
            Assert.True(double.IsFinite(displayed));
            Assert.InRange(displayed, Math.Min(resumedRpm, latestAcceptedRpm),
                Math.Max(resumedRpm, latestAcceptedRpm));
            var effectiveLagMilliseconds = (RampRpm(milliseconds, falling) - displayed) / sourceSlope;
            Assert.InRange(effectiveLagMilliseconds, 0, 75);
        }
    }

    private static void AssertRejectedFramePreservesHistory(int carOrdinal, double rpm, int receivedMilliseconds)
    {
        var baseline = CreateRunningHistory();
        var interpolator = CreateRunningHistory();
        var now = TimestampMilliseconds(140);
        var expected = baseline.Sample(now);

        Assert.Equal(expected, interpolator.Observe(carOrdinal, GameTimestamp(receivedMilliseconds), rpm,
            now, TimestampMilliseconds(receivedMilliseconds)), 6);
        Assert.Equal(expected, interpolator.Sample(now), 6);

        var nextAt = TimestampMilliseconds(150);
        Assert.Equal(
            baseline.Observe(CarOrdinal, GameTimestamp(150), RampRpm(150), nextAt, nextAt),
            interpolator.Observe(CarOrdinal, GameTimestamp(150), RampRpm(150), nextAt, nextAt), 6);
        for (var milliseconds = 155; milliseconds <= 210; milliseconds += 5)
        {
            var timestamp = TimestampMilliseconds(milliseconds);
            Assert.Equal(baseline.Sample(timestamp), interpolator.Sample(timestamp), 6);
        }
    }

    private static NativeTachometerInterpolator CreateRunningHistory(bool falling = false)
    {
        var interpolator = new NativeTachometerInterpolator();
        for (var milliseconds = 0; milliseconds <= LastWarmupMilliseconds; milliseconds += 10)
        {
            var timestamp = TimestampMilliseconds(milliseconds);
            interpolator.Observe(CarOrdinal, GameTimestamp(milliseconds), RampRpm(milliseconds, falling),
                timestamp, timestamp);
        }

        return interpolator;
    }

    private static double RampRpm(int milliseconds, bool falling = false) =>
        falling ? 7_000 - 2d * milliseconds : 1_000 + 2d * milliseconds;

    private static uint GameTimestamp(int milliseconds) => 1_000u + (uint)milliseconds;

    private static long TimestampMilliseconds(int milliseconds) =>
        (long)Math.Round(milliseconds * Stopwatch.Frequency / 1_000d);
}
