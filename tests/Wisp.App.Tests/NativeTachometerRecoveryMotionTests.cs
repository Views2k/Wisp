using System.Diagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeTachometerRecoveryMotionTests
{
    private const int CarOrdinal = 314;
    private const int WarmupEndMilliseconds = 120;

    [Theory]
    [InlineData(76, false)]
    [InlineData(76, true)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    [InlineData(150, false)]
    [InlineData(150, true)]
    public void StarvationRecoveryStartsMovingAfterTheFirstNewSample(int gapMilliseconds, bool falling)
    {
        var interpolator = CreateRunningHistory(0, falling);
        var resumedAt = WarmupEndMilliseconds + gapMilliseconds;
        var resumedRpm = Rpm(resumedAt, falling);
        interpolator.Sample(Timestamp(resumedAt));
        Assert.Equal(resumedRpm, Observe(interpolator, resumedAt, 0, falling), 6);

        Assert.Equal(resumedRpm, Observe(interpolator, resumedAt + 10, 0, falling), 6);
        var displayed = interpolator.Sample(Timestamp(resumedAt + 15));
        var direction = falling ? -1 : 1;

        Assert.True(direction * (displayed - resumedRpm) > 0.01,
            "A new RPM sample must end the recovery hold; do not prime another 40 ms buffer.");
        Assert.InRange(displayed,
            Math.Min(resumedRpm, Rpm(resumedAt + 10, falling)),
            Math.Max(resumedRpm, Rpm(resumedAt + 10, falling)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(12, false)]
    [InlineData(12, true)]
    [InlineData(25, false)]
    [InlineData(25, true)]
    [InlineData(60, false)]
    [InlineData(60, true)]
    public void RepeatedStarvationKeepsMovingWhileDelayRebuilds(int observationAgeMilliseconds, bool falling)
    {
        var interpolator = CreateRunningHistory(observationAgeMilliseconds, falling);
        var previousReceive = WarmupEndMilliseconds;
        var slope = falling ? -2d : 2d;

        foreach (var gap in new[] { 76, 100, 150 })
        {
            var resumedAt = previousReceive + gap;
            for (var now = previousReceive + observationAgeMilliseconds + 5;
                 now < resumedAt + observationAgeMilliseconds; now += 5)
                interpolator.Sample(Timestamp(now));

            var resumedRpm = Rpm(resumedAt, falling);
            var latestRpm = resumedRpm;
            var previousRpm = resumedRpm;
            for (var offset = 0; offset <= 200; offset += 5)
            {
                var receivedAt = resumedAt + offset;
                var now = receivedAt + observationAgeMilliseconds;
                if (offset % 10 == 0)
                {
                    latestRpm = Rpm(receivedAt, falling);
                    var observed = Observe(interpolator, receivedAt, observationAgeMilliseconds, falling);
                    if (offset == 0)
                        Assert.Equal(resumedRpm, observed, 6);
                }

                var displayed = interpolator.Sample(Timestamp(now));
                Assert.True(double.IsFinite(displayed));
                Assert.InRange(displayed, Math.Min(resumedRpm, latestRpm), Math.Max(resumedRpm, latestRpm));
                // One fresh interval is needed before interpolation is possible.
                // Thereafter phase correction may change speed by at most 25%.
                if (offset >= 15)
                {
                    var normalizedVelocity = (displayed - previousRpm) / (5 * slope);
                    Assert.InRange(normalizedVelocity, 0.749999, 1.250001);
                }
                var additionalLag = (Rpm(receivedAt, falling) - displayed) / slope;
                Assert.InRange(additionalLag, 0, 75);
                previousRpm = displayed;
            }
            previousReceive = resumedAt + 200;
        }
    }

    [Theory]
    [InlineData(30, 60)]
    [InlineData(30, 120)]
    [InlineData(60, 60)]
    [InlineData(60, 120)]
    public void RepeatedRecoveryKeepsMotionAtPacketAndRenderCadenceInEitherCallbackOrder(int packetHz, int renderHz)
    {
        var observeFirst = ReplayRecoveryCadence(packetHz, renderHz, true);
        var sampleFirst = ReplayRecoveryCadence(packetHz, renderHz, false);

        Assert.Equal(observeFirst.Count, sampleFirst.Count);
        for (var index = 0; index < observeFirst.Count; index++)
            Assert.Equal(observeFirst[index], sampleFirst[index], 6);
    }

    [Fact]
    public void RejectedReboundFramesCannotReprimeStarvationRecovery()
    {
        var interpolator = CreateRunningHistory(0, false);
        const int resumedAt = 220;
        var resumedRpm = Observe(interpolator, resumedAt, 0, false);

        interpolator.Observe(CarOrdinal, GameTimestamp(resumedAt), 9_000,
            Timestamp(225), Timestamp(resumedAt));
        interpolator.Observe(CarOrdinal + 1, GameTimestamp(210), double.NaN,
            Timestamp(226), Timestamp(210));
        Observe(interpolator, 230, 0, false);

        Assert.Equal(CarOrdinal, interpolator.AcceptedCarOrdinal);
        Assert.True(interpolator.Sample(Timestamp(235)) > resumedRpm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetAndCarChangesStillUseNormalInitialPriming(bool reset)
    {
        var interpolator = CreateRunningHistory(0, false);
        Observe(interpolator, 220, 0, false);
        Observe(interpolator, 230, 0, false);
        interpolator.Sample(Timestamp(235));
        if (reset)
            interpolator.Reset();
        var nextCar = reset ? CarOrdinal : CarOrdinal + 1;
        interpolator.Observe(nextCar, GameTimestamp(400), 1_000, Timestamp(400), Timestamp(400));
        interpolator.Observe(nextCar, GameTimestamp(410), 1_020, Timestamp(410), Timestamp(410));

        Assert.Equal(1_000, interpolator.Sample(Timestamp(440)), 6);
        Assert.Equal(1_010, interpolator.Sample(Timestamp(445)), 6);
        Assert.Equal(1_020, interpolator.Sample(Timestamp(450)), 6);
    }

    [Fact]
    public void GameDiscontinuityIsNotTreatedAsContinuousStarvationRecovery()
    {
        var interpolator = CreateRunningHistory(0, false);
        interpolator.Observe(CarOrdinal, GameTimestamp(500), 2_000, Timestamp(220), Timestamp(220));
        interpolator.Observe(CarOrdinal, GameTimestamp(510), 2_020, Timestamp(230), Timestamp(230));

        Assert.Equal(2_000, interpolator.Sample(Timestamp(255)), 6);
        Assert.Equal(2_010, interpolator.Sample(Timestamp(265)), 6);
    }

    private static List<double> ReplayRecoveryCadence(int packetHz, int renderHz, bool observeBeforeSample)
    {
        var interpolator = CreateRunningHistory(0, false);
        var rendered = new List<double>();
        var packetInterval = 1_000d / packetHz;
        var renderInterval = 1_000d / renderHz;
        var packetPhase = packetInterval * 0.37;
        var previousReceive = (double)WarmupEndMilliseconds;

        foreach (var gap in new[] { 76, 100, 150 })
        {
            var resumedAt = previousReceive + gap;
            interpolator.Sample(Timestamp(resumedAt));
            var resumedRpm = Observe(interpolator, resumedAt, 0, false);
            var latestReceivedAt = resumedAt;
            var latestAcceptedRpm = resumedRpm;
            var previousRpm = resumedRpm;
            var nextPacket = 1;
            int? firstNewPacketFrame = null;

            for (var frame = 1; frame <= renderHz * 0.4; frame++)
            {
                var now = Timestamp(resumedAt + frame * renderInterval);
                var hasNewPacket = false;
                while (Timestamp(resumedAt + nextPacket * packetInterval + packetPhase) <= now)
                {
                    latestReceivedAt = resumedAt + nextPacket * packetInterval + packetPhase;
                    nextPacket++;
                    hasNewPacket = true;
                }
                if (hasNewPacket)
                    firstNewPacketFrame ??= frame;

                double? observed = null;
                if (hasNewPacket && observeBeforeSample)
                {
                    latestAcceptedRpm = Rpm(latestReceivedAt, false);
                    observed = interpolator.Observe(CarOrdinal, GameTimestamp(latestReceivedAt), latestAcceptedRpm,
                        now, Timestamp(latestReceivedAt));
                }
                var displayed = interpolator.Sample(now);
                Assert.InRange(displayed, resumedRpm, latestAcceptedRpm);
                if (hasNewPacket && !observeBeforeSample)
                {
                    latestAcceptedRpm = Rpm(latestReceivedAt, false);
                    observed = interpolator.Observe(CarOrdinal, GameTimestamp(latestReceivedAt), latestAcceptedRpm,
                        now, Timestamp(latestReceivedAt));
                }
                if (observed is double value)
                    Assert.Equal(displayed, value, 6);
                if (firstNewPacketFrame is int first && frame > first)
                {
                    var normalizedVelocity = (displayed - previousRpm) / (2 * renderInterval);
                    Assert.InRange(normalizedVelocity, 0.749999, 1.250001);
                }
                rendered.Add(displayed);
                previousRpm = displayed;
            }
            Assert.NotNull(firstNewPacketFrame);
            previousReceive = latestReceivedAt;
        }
        return rendered;
    }

    private static NativeTachometerInterpolator CreateRunningHistory(int observationAgeMilliseconds, bool falling)
    {
        var interpolator = new NativeTachometerInterpolator();
        for (var receivedAt = 0; receivedAt <= WarmupEndMilliseconds; receivedAt += 10)
            Observe(interpolator, receivedAt, observationAgeMilliseconds, falling);
        return interpolator;
    }

    private static double Observe(NativeTachometerInterpolator interpolator, double receivedAt,
        int observationAgeMilliseconds, bool falling) =>
        interpolator.Observe(CarOrdinal, GameTimestamp(receivedAt), Rpm(receivedAt, falling),
            Timestamp(receivedAt + observationAgeMilliseconds), Timestamp(receivedAt));

    private static double Rpm(double milliseconds, bool falling) =>
        falling ? 7_000 - 2d * milliseconds : 1_000 + 2d * milliseconds;

    private static uint GameTimestamp(double milliseconds) => 1_000u + (uint)milliseconds;
    private static long Timestamp(double milliseconds) => (long)Math.Round(milliseconds * Stopwatch.Frequency / 1_000d);
}
