using System.Diagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeTachometerInterpolatorTests
{
    [Fact]
    public void InterpolatesBetweenConsecutiveTelemetrySamplesAtDisplayRate()
    {
        var interpolator = new NativeTachometerInterpolator();
        var start = TimestampMilliseconds(0);

        Assert.Equal(1_000, interpolator.Observe(314, 1_000, 1_000, start));
        Assert.Equal(1_000, interpolator.Observe(
            314,
            1_016,
            3_000,
            start + TimestampMilliseconds(16)));

        Assert.Equal(2_000, interpolator.Sample(start + TimestampMilliseconds(48)), 3);
    }

    [Fact]
    public void ReachesTheExactSampleAfterThePlaybackDelayAndHoldsWithoutExtrapolation()
    {
        var interpolator = new NativeTachometerInterpolator();
        var start = TimestampMilliseconds(0);

        interpolator.Observe(314, 2_000, 2_000, start);
        interpolator.Observe(314, 2_020, 6_000, start + TimestampMilliseconds(20));

        Assert.Equal(6_000, interpolator.Sample(start + TimestampMilliseconds(60)), 6);
        Assert.Equal(6_000, interpolator.Sample(start + TimestampMilliseconds(200)), 6);
    }

    [Fact]
    public void ResetAndCarChangesSnapInsteadOfBlendingUnrelatedEngines()
    {
        var interpolator = new NativeTachometerInterpolator();
        var start = TimestampMilliseconds(0);

        interpolator.Observe(314, 3_000, 2_000, start);
        interpolator.Observe(314, 3_020, 6_000, start + TimestampMilliseconds(20));
        Assert.Equal(4_000, interpolator.Sample(start + TimestampMilliseconds(50)), 3);

        Assert.Equal(900, interpolator.Observe(
            3766,
            3_060,
            900,
            start + TimestampMilliseconds(60)));
        Assert.Equal(900, interpolator.Sample(start + TimestampMilliseconds(65)), 6);

        interpolator.Reset();
        Assert.Equal(1_500, interpolator.Observe(
            3766,
            3_080,
            1_500,
            start + TimestampMilliseconds(80)));
    }

    [Fact]
    public void TelemetryGapsAndReversedGameTimestampsSnapToTheNewSample()
    {
        var interpolator = new NativeTachometerInterpolator();
        var start = TimestampMilliseconds(0);

        interpolator.Observe(314, 4_000, 2_000, start);
        Assert.Equal(7_000, interpolator.Observe(
            314,
            5_000,
            7_000,
            start + TimestampMilliseconds(1_000)));

        Assert.Equal(1_200, interpolator.Observe(
            314,
            4_900,
            1_200,
            start + TimestampMilliseconds(1_010)));
        Assert.Equal(1_200, interpolator.Sample(start + TimestampMilliseconds(1_015)), 6);
    }

    [Theory]
    [InlineData(120, 30)]
    [InlineData(120, 60)]
    [InlineData(144, 30)]
    [InlineData(144, 60)]
    [InlineData(240, 30)]
    [InlineData(240, 60)]
    public void SteadyTelemetryMovesEveryCompositorFrameWithBoundedLag(int renderHz, int telemetryHz)
    {
        var cadenceMilliseconds = 1_000d / telemetryHz;
        var samples = RegularSamples(cadenceMilliseconds);

        var replay = Replay(renderHz, samples, 250, 2_000);

        AssertSmoothReplay(replay, renderHz, cadenceMilliseconds, allowJitter: false);
    }

    [Theory]
    [InlineData(120, 100d / 3)]
    [InlineData(120, 50d)]
    [InlineData(144, 100d / 3)]
    [InlineData(144, 50d)]
    [InlineData(240, 100d / 3)]
    [InlineData(240, 50d)]
    public void LocalArrivalCadenceControlsMotionWhenGameTimestampsAdvanceFaster(
        int renderHz,
        double arrivalMilliseconds)
    {
        var samples = RegularSamples(arrivalMilliseconds, gameStepMilliseconds: 16);

        var replay = Replay(renderHz, samples, 250, 2_000);

        AssertSmoothReplay(replay, renderHz, arrivalMilliseconds, allowJitter: false);
    }

    [Theory]
    [InlineData(100d / 3)]
    [InlineData(50d)]
    public void FirstTransitionUsesTheObservedArrivalInterval(double arrivalMilliseconds)
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, 1_000, 1_000, TimestampMilliseconds(0));

        Assert.Equal(1_000, interpolator.Observe(
            314,
            1_016,
            3_000,
            TimestampMilliseconds(arrivalMilliseconds)));

        Assert.InRange(
            interpolator.Sample(TimestampMilliseconds(arrivalMilliseconds * 1.5)),
            1_500,
            2_500);
        Assert.Equal(3_000, interpolator.Sample(TimestampMilliseconds(arrivalMilliseconds * 3 + 1)), 6);
    }

    [Theory]
    [InlineData(120, 30)]
    [InlineData(120, 60)]
    [InlineData(144, 30)]
    [InlineData(144, 60)]
    [InlineData(240, 30)]
    [InlineData(240, 60)]
    public void AlternatingArrivalJitterKeepsMotionContinuousWithoutExtrapolating(
        int renderHz,
        int telemetryHz)
    {
        var cadenceMilliseconds = 1_000d / telemetryHz;
        var samples = RegularSamples(cadenceMilliseconds, jitterMilliseconds: 3);

        var replay = Replay(renderHz, samples, 250, 2_000);

        AssertSmoothReplay(replay, renderHz, cadenceMilliseconds + 3, allowJitter: true);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(240)]
    public void OngoingArrivalRateChangesRecoverSmoothMotionAndCurrentRateLatency(int renderHz)
    {
        double[] cadences = [1_000d / 60, 1_000d / 30, 50, 1_000d / 60];
        int[] sampleCounts = [45, 24, 16, 45];
        var samples = new List<ReplaySample> { new(0, 1_000) };
        var windows = new List<(double Start, double End, double Cadence)>();
        var atMilliseconds = 0d;
        var gameTimestamp = 1_000u;

        for (var phase = 0; phase < cadences.Length; phase++)
        {
            var phaseStart = atMilliseconds;
            for (var index = 0; index < sampleCounts[phase]; index++)
            {
                atMilliseconds += cadences[phase];
                gameTimestamp += 16;
                samples.Add(new(atMilliseconds, gameTimestamp));
            }

            var previousCadence = phase == 0 ? cadences[phase] : cadences[phase - 1];
            var settlingMilliseconds = Math.Max(250, 8 * Math.Max(previousCadence, cadences[phase]));
            windows.Add((phaseStart + settlingMilliseconds, atMilliseconds, cadences[phase]));
        }

        foreach (var window in windows)
        {
            var replay = Replay(renderHz, samples, window.Start, window.End);

            AssertSmoothReplay(replay, renderHz, window.Cadence, allowJitter: false);
        }
    }

    [Fact]
    public void RepeatedReceiveTimestampDoesNotRestartOrRetargetPlayback()
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, 1_000, 1_000, TimestampMilliseconds(0));
        interpolator.Observe(314, 1_016, 3_000, TimestampMilliseconds(16));

        Assert.Equal(2_000, interpolator.Observe(
            314, 1_016, 9_000, TimestampMilliseconds(48), TimestampMilliseconds(16)), 3);
        Assert.Equal(2_500, interpolator.Sample(TimestampMilliseconds(52)), 3);
        Assert.Equal(3_000, interpolator.Sample(TimestampMilliseconds(56)), 6);
        Assert.Equal(3_000, interpolator.Observe(
            314, 1_016, 9_000, TimestampMilliseconds(60), TimestampMilliseconds(16)), 6);
    }

    [Fact]
    public void RepeatedReceiveTimestampsCannotHideARealAcceptedSampleGap()
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, 1_000, 1_000, TimestampMilliseconds(0));
        interpolator.Observe(314, 1_016, 3_000, TimestampMilliseconds(16));

        foreach (var milliseconds in new[] { 60, 100, 140, 180, 220, 260, 280 })
        {
            Assert.Equal(3_000, interpolator.Observe(
                314,
                1_016,
                9_000,
                TimestampMilliseconds(milliseconds),
                TimestampMilliseconds(16)), 6);
        }

        Assert.Equal(7_000, interpolator.Observe(314, 1_032, 7_000, TimestampMilliseconds(300)), 6);
        Assert.Equal(7_000, interpolator.Sample(TimestampMilliseconds(308)), 6);
    }

    [Theory]
    [InlineData(75, false)]
    [InlineData(76, true)]
    [InlineData(249, true)]
    [InlineData(250, true)]
    [InlineData(251, true)]
    public void AcceptedWallClockGapOnlySnapsBeyondTheStaleThreshold(int gapMilliseconds, bool shouldSnap)
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, 1_000, 1_000, TimestampMilliseconds(0));
        interpolator.Observe(314, 1_016, 3_000, TimestampMilliseconds(16));

        var actual = interpolator.Observe(314, 1_032, 7_000, TimestampMilliseconds(16 + gapMilliseconds));

        Assert.Equal(shouldSnap ? 7_000 : 3_000, actual, 6);
    }

    [Theory]
    [InlineData(249, false)]
    [InlineData(250, false)]
    [InlineData(251, true)]
    public void GameTimestampGapStillRejectsStaleSamplesWithFastLocalArrival(int gapMilliseconds, bool shouldSnap)
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, 1_000, 1_000, TimestampMilliseconds(0));

        var actual = interpolator.Observe(314, 1_000u + (uint)gapMilliseconds, 3_000, TimestampMilliseconds(16));

        Assert.Equal(shouldSnap ? 3_000 : 1_000, actual, 6);
    }

    [Fact]
    public void BackwardsLocalClockSnapsAndStartsANewCadenceHistory()
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, 1_000, 1_000, TimestampMilliseconds(100));
        interpolator.Observe(314, 1_020, 5_000, TimestampMilliseconds(120));

        Assert.Equal(900, interpolator.Observe(314, 1_040, 900, TimestampMilliseconds(110)), 6);
        Assert.Equal(900, interpolator.Sample(TimestampMilliseconds(115)), 6);
        Assert.Equal(900, interpolator.Observe(314, 1_056, 2_500, TimestampMilliseconds(126)), 6);
        Assert.Equal(1_700, interpolator.Sample(TimestampMilliseconds(158)), 3);
    }

    [Fact]
    public void UnsignedGameTimestampWrapPreservesAForwardTransition()
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, uint.MaxValue - 7, 1_000, TimestampMilliseconds(0));

        Assert.Equal(1_000, interpolator.Observe(314, 8, 3_000, TimestampMilliseconds(16)), 6);
        Assert.Equal(2_000, interpolator.Sample(TimestampMilliseconds(48)), 3);
        Assert.Equal(3_000, interpolator.Sample(TimestampMilliseconds(56)), 6);
    }

    [Fact]
    public void CarChangeDuringATransitionDoesNotBlendThePreviousEngine()
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, 1_000, 1_000, TimestampMilliseconds(0));
        interpolator.Observe(314, 1_016, 7_000, TimestampMilliseconds(16));
        Assert.Equal(4_000, interpolator.Sample(TimestampMilliseconds(48)), 3);

        Assert.Equal(900, interpolator.Observe(3766, 1_048, 900, TimestampMilliseconds(48)), 6);
        Assert.Equal(900, interpolator.Sample(TimestampMilliseconds(56)), 6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    public void InvalidTargetClearsMotionAndNextValidSampleSnaps(double invalidRpm)
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, 1_000, 1_000, TimestampMilliseconds(0));
        interpolator.Observe(314, 1_016, 7_000, TimestampMilliseconds(16));

        Assert.Equal(0, interpolator.Observe(314, 1_024, invalidRpm, TimestampMilliseconds(24)), 6);
        Assert.Equal(0, interpolator.Sample(TimestampMilliseconds(25)), 6);
        Assert.Equal(1_500, interpolator.Observe(314, 1_025, 1_500, TimestampMilliseconds(25)), 6);
    }

    [Fact]
    public void ZeroRpmRemainsAValidTargetInsteadOfClearingMotion()
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, 1_000, 4_000, TimestampMilliseconds(0));

        Assert.Equal(4_000, interpolator.Observe(314, 1_016, 0, TimestampMilliseconds(16)), 6);
        Assert.Equal(2_000, interpolator.Sample(TimestampMilliseconds(48)), 3);
        Assert.Equal(0, interpolator.Sample(TimestampMilliseconds(56)), 6);
    }

    [Fact]
    public void SamplingBeforeTransitionStartDoesNotExtrapolateBackwards()
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, 1_000, 1_000, TimestampMilliseconds(0));
        interpolator.Observe(314, 1_016, 3_000, TimestampMilliseconds(16));

        Assert.Equal(1_000, interpolator.Sample(TimestampMilliseconds(8)), 6);
        Assert.Equal(1_000, interpolator.Sample(TimestampMilliseconds(16)), 6);
    }

    [Fact]
    public void RapidRpmChangesPreserveTheReceivedCurveAndReachTheFinalSamplePromptly()
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, 1_000, 1_000, TimestampMilliseconds(0));
        (int Milliseconds, uint GameTimestamp, double Rpm)[] targets =
        [
            (0, 1_000, 1_000),
            (20, 1_020, 7_000),
            (27, 1_040, 400),
            (31, 1_060, 5_500),
            (40, 1_080, 0),
            (46, 1_100, 6_500)
        ];

        var nextTarget = 1;
        for (var milliseconds = 1; milliseconds <= 95; milliseconds++)
        {
            var atTimestamp = TimestampMilliseconds(milliseconds);
            var before = interpolator.Sample(atTimestamp);
            if (nextTarget < targets.Length && targets[nextTarget].Milliseconds == milliseconds)
            {
                var target = targets[nextTarget++];
                Assert.Equal(before, interpolator.Observe(
                    314, target.GameTimestamp, target.Rpm, atTimestamp), 6);
            }

            var playbackMilliseconds = milliseconds - 40;
            var expected = targets[0].Rpm;
            for (var index = 1; index < targets.Length && playbackMilliseconds > 0; index++)
            {
                var left = targets[index - 1];
                var right = targets[index];
                var fraction = Math.Clamp(
                    (double)(playbackMilliseconds - left.Milliseconds) / (right.Milliseconds - left.Milliseconds),
                    0, 1);
                expected = left.Rpm + (right.Rpm - left.Rpm) * fraction;
                if (playbackMilliseconds <= right.Milliseconds)
                    break;
            }

            var actual = interpolator.Sample(atTimestamp);
            Assert.InRange(actual, 0, 7_000);
            Assert.Equal(expected, actual, 6);
        }

        Assert.Equal(6_500, interpolator.Sample(TimestampMilliseconds(95)), 6);
    }

    private static IReadOnlyList<ReplaySample> RegularSamples(
        double cadenceMilliseconds,
        double jitterMilliseconds = 0,
        uint? gameStepMilliseconds = null)
    {
        var samples = new List<ReplaySample> { new(0, 1_000) };
        for (var index = 1; ; index++)
        {
            var atMilliseconds = index * cadenceMilliseconds - (index % 2 == 0 ? 0 : jitterMilliseconds);
            if (atMilliseconds > 2_000)
            {
                return samples;
            }

            var gameTimestamp = gameStepMilliseconds is uint step
                ? 1_000u + (uint)index * step
                : 1_000u + (uint)Math.Round(index * cadenceMilliseconds);
            samples.Add(new(atMilliseconds, gameTimestamp));
        }
    }

    private static ReplaySummary Replay(
        int renderHz,
        IReadOnlyList<ReplaySample> samples,
        double measurementStartMilliseconds,
        double measurementEndMilliseconds)
    {
        const double initialRpm = 1_000;
        const double rpmPerMillisecond = 2;
        const double epsilon = 0.00001;
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, samples[0].GameTimestampMilliseconds, initialRpm, TimestampMilliseconds(0));
        var nextSampleIndex = 1;
        var latestAcceptedRpm = initialRpm;
        var previousRpm = initialRpm;
        var frames = 0;
        var movingFrames = 0;
        var consecutiveHoldFrames = 0;
        var longestHoldFrames = 0;
        var maximumLagMilliseconds = 0d;

        for (var frame = 1; frame <= (int)Math.Floor(measurementEndMilliseconds * renderHz / 1_000); frame++)
        {
            var renderMilliseconds = frame * 1_000d / renderHz;
            var renderTimestamp = TimestampMilliseconds(renderMilliseconds);
            while (nextSampleIndex < samples.Count &&
                   TimestampMilliseconds(samples[nextSampleIndex].AtMilliseconds) <= renderTimestamp)
            {
                var sample = samples[nextSampleIndex++];
                var sampleTimestamp = TimestampMilliseconds(sample.AtMilliseconds);
                var before = interpolator.Sample(sampleTimestamp);
                latestAcceptedRpm = initialRpm + rpmPerMillisecond * sample.AtMilliseconds;
                var actual = interpolator.Observe(314, sample.GameTimestampMilliseconds, latestAcceptedRpm, sampleTimestamp);
                Assert.Equal(before, actual, 6);
            }

            var rpm = interpolator.Sample(renderTimestamp);
            Assert.True(double.IsFinite(rpm));
            Assert.InRange(rpm, initialRpm - epsilon, latestAcceptedRpm + epsilon);
            Assert.True(rpm >= previousRpm - epsilon, "An increasing RPM replay must not move backwards.");

            if (renderMilliseconds >= measurementStartMilliseconds)
            {
                frames++;
                if (rpm > previousRpm + epsilon)
                {
                    movingFrames++;
                    consecutiveHoldFrames = 0;
                }
                else
                {
                    consecutiveHoldFrames++;
                    longestHoldFrames = Math.Max(longestHoldFrames, consecutiveHoldFrames);
                }

                var sourceRpm = initialRpm + rpmPerMillisecond * renderMilliseconds;
                maximumLagMilliseconds = Math.Max(maximumLagMilliseconds, (sourceRpm - rpm) / rpmPerMillisecond);
            }

            previousRpm = rpm;
        }

        return new(frames, movingFrames, longestHoldFrames, maximumLagMilliseconds);
    }

    private static void AssertSmoothReplay(
        ReplaySummary replay,
        int renderHz,
        double cadenceMilliseconds,
        bool allowJitter)
    {
        Assert.True(replay.Frames >= renderHz / 4, "The replay must measure at least a quarter-second of rendering.");
        if (allowJitter)
        {
            Assert.True(
                replay.MovingFrames >= replay.Frames * 0.95,
                $"Only {replay.MovingFrames}/{replay.Frames} jittered replay frames moved.");
            Assert.InRange(replay.LongestHoldFrames, 0, 1);
        }
        else
        {
            Assert.Equal(replay.Frames, replay.MovingFrames);
            Assert.Equal(0, replay.LongestHoldFrames);
        }

        var maximumAllowedLagMilliseconds = Math.Max(40, 2 * cadenceMilliseconds) + 1_000d / renderHz + 0.001;
        Assert.True(
            replay.MaximumLagMilliseconds <= maximumAllowedLagMilliseconds,
            $"Replay lag {replay.MaximumLagMilliseconds:F3}ms exceeds {maximumAllowedLagMilliseconds:F3}ms.");
    }

    private readonly record struct ReplaySample(double AtMilliseconds, uint GameTimestampMilliseconds);

    private readonly record struct ReplaySummary(
        int Frames,
        int MovingFrames,
        int LongestHoldFrames,
        double MaximumLagMilliseconds);

    private static long TimestampMilliseconds(double milliseconds) =>
        (long)Math.Round(milliseconds * Stopwatch.Frequency / 1_000d);
}
