using System.Diagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeTachometerCadenceTests
{
    private const double RpmTolerance = 0.01;
    private const double MaximumTurnResponseMilliseconds = 75;
    private const double GameClockQuantumMilliseconds = 15.625;
    private static readonly double[] ReversalTimes = [1_000, 1_400, 1_700, 2_200];
    private readonly ITestOutputHelper _output;

    public NativeTachometerCadenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(82, RenderPattern.SmoothSwing, false)]
    [InlineData(82, RenderPattern.SmoothSwing, true)]
    [InlineData(82, RenderPattern.AlternatingExtremes, false)]
    [InlineData(82, RenderPattern.AlternatingExtremes, true)]
    [InlineData(82, RenderPattern.RateSteps, false)]
    [InlineData(82, RenderPattern.RateSteps, true)]
    [InlineData(60, RenderPattern.SmoothSwing, false)]
    [InlineData(60, RenderPattern.SmoothSwing, true)]
    [InlineData(60, RenderPattern.AlternatingExtremes, false)]
    [InlineData(60, RenderPattern.AlternatingExtremes, true)]
    [InlineData(60, RenderPattern.RateSteps, false)]
    [InlineData(60, RenderPattern.RateSteps, true)]
    public void CoalescedConstantRpmRampsStaySmoothAtVariableRenderCadence(
        int packetHz,
        RenderPattern pattern,
        bool falling)
    {
        var slope = falling ? -2d : 2d;
        var initialRpm = falling ? 7_000d : 1_000d;
        var results = new List<(ReplayResult Replay, MotionMetrics Metrics)>();

        foreach (var observeBeforeSample in new[] { true, false })
        {
            var replay = Replay(packetHz, pattern, at => initialRpm + slope * at, observeBeforeSample);
            var metrics = MeasureRamp(replay.Frames.Where(frame => frame.AtMilliseconds >= 500).ToArray(), slope);
            WriteMetrics(packetHz, pattern, falling, observeBeforeSample, replay, metrics);
            results.Add((replay, metrics));
        }

        foreach (var result in results)
        {
            AssertReplaySafetyAndCoalescing(result.Replay, packetHz);
            var metrics = result.Metrics;
            Assert.True(metrics.Frames >= 120);
            Assert.Equal(0, metrics.FlatFrames);

            // Synthetic smoothness budgets for a constant source-clock slope,
            // not measurements of FH6's native animation or shader behavior.
            Assert.True(metrics.P95VelocityChange <= 0.25,
                $"P95 adjacent velocity change was {metrics.P95VelocityChange:P1}; budget is 25% of source velocity.");
            Assert.True(metrics.MaximumVelocityChange <= 0.50,
                $"Maximum adjacent velocity change was {metrics.MaximumVelocityChange:P1}; budget is 50%.");
            Assert.True(metrics.P95VelocityError <= 0.25,
                $"P95 velocity error was {metrics.P95VelocityError:P1}; budget is 25%.");
            Assert.InRange(metrics.P95LagMilliseconds, 0, 50);
            Assert.InRange(metrics.MaximumLagMilliseconds, 0, 75);
        }
    }

    [Theory]
    [InlineData(82, RenderPattern.SmoothSwing)]
    [InlineData(82, RenderPattern.AlternatingExtremes)]
    [InlineData(82, RenderPattern.RateSteps)]
    [InlineData(60, RenderPattern.SmoothSwing)]
    [InlineData(60, RenderPattern.AlternatingExtremes)]
    [InlineData(60, RenderPattern.RateSteps)]
    public void QuickReversalsStayBoundedAndRespondWithoutLongWrongWayMotion(int packetHz, RenderPattern pattern)
    {
        foreach (var observeBeforeSample in new[] { true, false })
        {
            var replay = Replay(packetHz, pattern, ReversingRpm, observeBeforeSample);
            AssertReplaySafetyAndCoalescing(replay, packetHz);
            var movingFrames = replay.Frames.Where(frame => frame.AtMilliseconds >= 500 && frame.AtMilliseconds < 2_500).ToArray();
            var velocityChanges = movingFrames.Zip(movingFrames.Skip(1),
                (previous, current) => Math.Abs(current.VelocityRpmPerMillisecond - previous.VelocityRpmPerMillisecond)).ToArray();
            var maximumVelocity = movingFrames.Max(frame => Math.Abs(frame.VelocityRpmPerMillisecond));
            var steadyFrames = movingFrames.Where(frame => ReversalTimes.All(turn =>
                frame.AtMilliseconds < turn || frame.AtMilliseconds >= turn + 100)).ToArray();
            var flatFrames = steadyFrames.Count(frame => Math.Abs(frame.DeltaRpm) < RpmTolerance);
            _output.WriteLine(
                $"{packetHz}Hz/{pattern}/{OrderName(observeBeforeSample)} reversals: " +
                $"steady holds={flatFrames}/{steadyFrames.Length}; velocity-change p95/max=" +
                $"{Percentile(velocityChanges, 0.95) * 1_000:F0}/{velocityChanges.Max() * 1_000:F0} RPM/s; " +
                $"max speed={maximumVelocity * 1_000:F0} RPM/s; overshoot={replay.MaximumOvershootRpm:F6} RPM.");

            // Genuine slope reversals belong in the report, not the constant-ramp jitter assertion.
            for (var index = 0; index < ReversalTimes.Length; index++)
            {
                var turn = ReversalTimes[index];
                var nextTurn = index + 1 < ReversalTimes.Length ? ReversalTimes[index + 1] : 2_500;
                var direction = index % 2 == 0 ? -1 : 1;
                var observedAt = replay.Frames.First(frame => frame.LatestPacketMilliseconds >= turn).AtMilliseconds;
                var window = replay.Frames.Where(frame => frame.AtMilliseconds >= observedAt && frame.AtMilliseconds < nextTurn).ToArray();
                var response = SustainedDirectionResponse(window, direction, observedAt);
                _output.WriteLine($"Turn at {turn:F0}ms: accepted at {observedAt:F3}ms, sustained direction after {response:F3}ms.");
                Assert.InRange(response, 0, MaximumTurnResponseMilliseconds);
                Assert.All(window.Where(frame => frame.AtMilliseconds >= observedAt + MaximumTurnResponseMilliseconds),
                    frame => Assert.True(direction * frame.DeltaRpm >= -RpmTolerance,
                        $"Wrong-way motion at {frame.AtMilliseconds:F3}ms after turn at {turn:F0}ms."));
            }

            Assert.Equal(0, flatFrames);
            Assert.InRange(maximumVelocity, 0, 5 * 1.25);

            var stopObservedAt = replay.Frames.First(frame => frame.LatestPacketMilliseconds >= 2_500).AtMilliseconds;
            var settled = replay.Frames.Where(frame => frame.AtMilliseconds >= stopObservedAt + MaximumTurnResponseMilliseconds).ToArray();
            Assert.NotEmpty(settled);
            Assert.All(settled, frame => Assert.InRange(Math.Abs(frame.DisplayRpm - 3_600), 0, RpmTolerance));
        }
    }

    [Theory]
    [InlineData(82, RenderPattern.SmoothSwing)]
    [InlineData(82, RenderPattern.AlternatingExtremes)]
    [InlineData(82, RenderPattern.RateSteps)]
    [InlineData(60, RenderPattern.SmoothSwing)]
    [InlineData(60, RenderPattern.AlternatingExtremes)]
    [InlineData(60, RenderPattern.RateSteps)]
    public void CallbackOrderAtTheSameTimestampDoesNotChangeTheDisplayedMotion(int packetHz, RenderPattern pattern)
    {
        Func<double, double>[] signals = [at => 1_000 + 2 * at, at => 7_000 - 2 * at, ReversingRpm];
        foreach (var signal in signals)
        {
            var observeFirst = Replay(packetHz, pattern, signal, observeBeforeSample: true);
            var sampleFirst = Replay(packetHz, pattern, signal, observeBeforeSample: false);

            Assert.Equal(observeFirst.Frames.Count, sampleFirst.Frames.Count);
            for (var index = 0; index < observeFirst.Frames.Count; index++)
            {
                Assert.Equal(observeFirst.Frames[index].AtMilliseconds, sampleFirst.Frames[index].AtMilliseconds);
                Assert.Equal(observeFirst.Frames[index].DisplayRpm, sampleFirst.Frames[index].DisplayRpm, 6);
            }
        }
    }

    [Theory]
    [InlineData(1_000d, 3_000d)]
    [InlineData(6_000d, 1_200d)]
    public void DistinctReceiveTimesAcceptChangedRpmWithinOneCoarseGameClockTick(double initialRpm, double nextRpm)
    {
        var interpolator = new NativeTachometerInterpolator();
        var firstGameTimestamp = CoarseGameTimestampMilliseconds(0);
        var nextGameTimestamp = CoarseGameTimestampMilliseconds(12);
        Assert.Equal(firstGameTimestamp, nextGameTimestamp);
        interpolator.Observe(314, firstGameTimestamp, initialRpm, TimestampMilliseconds(0),
            receivedTimestamp: TimestampMilliseconds(0));
        var observedAt = TimestampMilliseconds(24);
        var before = interpolator.Sample(observedAt);

        var actual = interpolator.Observe(314, nextGameTimestamp, nextRpm, observedAt,
            receivedTimestamp: TimestampMilliseconds(12));

        Assert.Equal(before, actual, 6);
        for (var milliseconds = 28; milliseconds < 99; milliseconds += 4)
        {
            Assert.InRange(interpolator.Sample(TimestampMilliseconds(milliseconds)),
                Math.Min(initialRpm, nextRpm), Math.Max(initialRpm, nextRpm));
        }

        Assert.Equal(nextRpm, interpolator.Sample(TimestampMilliseconds(99)), 6);
    }

    [Theory]
    [InlineData(9_000d)]
    [InlineData(500d)]
    public void RebindingTheSameReceiveTimestampCannotReplaceTheAcceptedRpm(double reboundRpm)
    {
        var baseline = new NativeTachometerInterpolator();
        var rebound = new NativeTachometerInterpolator();
        foreach (var interpolator in new[] { baseline, rebound })
        {
            interpolator.Observe(314, CoarseGameTimestampMilliseconds(0), 1_000, TimestampMilliseconds(0),
                receivedTimestamp: TimestampMilliseconds(0));
            interpolator.Observe(314, CoarseGameTimestampMilliseconds(12), 3_000, TimestampMilliseconds(20),
                receivedTimestamp: TimestampMilliseconds(12));
            interpolator.Observe(314, CoarseGameTimestampMilliseconds(24), 5_000, TimestampMilliseconds(32),
                receivedTimestamp: TimestampMilliseconds(24));
        }

        foreach (var milliseconds in new[] { 40, 48, 56, 64, 72, 80, 100 })
        {
            var timestamp = TimestampMilliseconds(milliseconds);
            var expected = baseline.Sample(timestamp);
            var actual = rebound.Observe(314, CoarseGameTimestampMilliseconds(24), reboundRpm, timestamp,
                receivedTimestamp: TimestampMilliseconds(24));

            Assert.Equal(expected, actual, 6);
            Assert.Equal(expected, rebound.Sample(timestamp), 6);
        }

        Assert.Equal(5_000, rebound.Sample(TimestampMilliseconds(108)), 6);
    }

    private static ReplayResult Replay(
        int packetHz,
        RenderPattern pattern,
        Func<double, double> sourceRpm,
        bool observeBeforeSample)
    {
        var interpolator = new NativeTachometerInterpolator();
        var initialRpm = sourceRpm(0);
        interpolator.Observe(314, CoarseGameTimestampMilliseconds(0), initialRpm, 0, receivedTimestamp: 0);
        var frames = new List<FramePoint>();
        var packetInterval = 1_000d / packetHz;
        var packetPhase = packetInterval * 0.37;
        var nextPacketIndex = 1;
        var latestPacketMilliseconds = 0d;
        var latestPacketRpm = initialRpm;
        var deliveredPackets = 1;
        var acceptedPackets = 1;
        var coalescedPackets = 0;
        var noNewPacketFrames = 0;
        var sameGameTimestampObservations = 0;
        var previousObservedGameTimestamp = CoarseGameTimestampMilliseconds(0);
        var renderMilliseconds = 0d;
        var previousTimestamp = 0L;
        var previousRpm = initialRpm;
        var lowerBound = initialRpm;
        var upperBound = initialRpm;
        var maximumOvershootRpm = 0d;
        var maximumObserveSampleDifference = 0d;

        for (var frameIndex = 1; ; frameIndex++)
        {
            renderMilliseconds += RenderIntervalMilliseconds(pattern, frameIndex);
            if (renderMilliseconds > 3_000)
            {
                break;
            }

            var timestamp = TimestampMilliseconds(renderMilliseconds);
            var atMilliseconds = timestamp * 1_000d / Stopwatch.Frequency;
            var arrived = 0;
            while (TimestampMilliseconds(nextPacketIndex * packetInterval + packetPhase) <= timestamp)
            {
                latestPacketMilliseconds = nextPacketIndex * packetInterval + packetPhase;
                latestPacketRpm = sourceRpm(latestPacketMilliseconds);
                nextPacketIndex++;
                arrived++;
            }

            deliveredPackets += arrived;
            if (arrived == 0)
            {
                noNewPacketFrames++;
            }
            else
            {
                acceptedPackets++;
                coalescedPackets += arrived - 1;
                var gameTimestamp = CoarseGameTimestampMilliseconds(latestPacketMilliseconds);
                if (gameTimestamp == previousObservedGameTimestamp)
                {
                    sameGameTimestampObservations++;
                }

                previousObservedGameTimestamp = gameTimestamp;
                // Delayed playback of an observed peak is valid during reversal;
                // the separate turn/lag budgets prevent stale history from lingering.
                lowerBound = Math.Min(lowerBound, latestPacketRpm);
                upperBound = Math.Max(upperBound, latestPacketRpm);
            }

            // AppController consumes Latest once on a render callback, not
            // every queued packet. Receive time remains separate from render time.
            double? observedRpm = null;
            if (arrived > 0 && observeBeforeSample)
            {
                observedRpm = interpolator.Observe(314,
                    CoarseGameTimestampMilliseconds(latestPacketMilliseconds), latestPacketRpm, timestamp,
                    receivedTimestamp: TimestampMilliseconds(latestPacketMilliseconds));
                maximumOvershootRpm = Math.Max(maximumOvershootRpm, Outside(observedRpm.Value, lowerBound, upperBound));
            }

            var displayRpm = interpolator.Sample(timestamp);
            maximumOvershootRpm = Math.Max(maximumOvershootRpm, Outside(displayRpm, lowerBound, upperBound));
            if (arrived > 0 && !observeBeforeSample)
            {
                observedRpm = interpolator.Observe(314,
                    CoarseGameTimestampMilliseconds(latestPacketMilliseconds), latestPacketRpm, timestamp,
                    receivedTimestamp: TimestampMilliseconds(latestPacketMilliseconds));
                maximumOvershootRpm = Math.Max(maximumOvershootRpm, Outside(observedRpm.Value, lowerBound, upperBound));
            }

            if (observedRpm is double acceptedRpm)
            {
                maximumObserveSampleDifference = Math.Max(maximumObserveSampleDifference, Math.Abs(acceptedRpm - displayRpm));
            }

            var deltaRpm = displayRpm - previousRpm;
            var deltaMilliseconds = (timestamp - previousTimestamp) * 1_000d / Stopwatch.Frequency;
            frames.Add(new(atMilliseconds, displayRpm, sourceRpm(atMilliseconds), latestPacketMilliseconds,
                deltaRpm, deltaRpm / deltaMilliseconds));
            previousTimestamp = timestamp;
            previousRpm = displayRpm;
        }

        return new(frames, deliveredPackets, acceptedPackets, coalescedPackets, noNewPacketFrames, sameGameTimestampObservations,
            maximumOvershootRpm, maximumObserveSampleDifference);
    }

    private static double RenderIntervalMilliseconds(RenderPattern pattern, int frameIndex)
    {
        var framesPerSecond = pattern switch
        {
            RenderPattern.SmoothSwing => 90 + 30 * Math.Sin((frameIndex - 1) * Math.PI * 2 / 90),
            RenderPattern.AlternatingExtremes => frameIndex % 2 == 1 ? 60 : 120,
            RenderPattern.RateSteps => ((frameIndex - 1) / 30 % 4) switch
            {
                0 => 60,
                1 => 120,
                2 => 72,
                _ => 100
            },
            _ => throw new ArgumentOutOfRangeException(nameof(pattern))
        };
        return 1_000d / framesPerSecond;
    }

    private static double ReversingRpm(double atMilliseconds) => atMilliseconds switch
    {
        < 1_000 => 1_000 + 3 * atMilliseconds,
        < 1_400 => 4_000 - 4 * (atMilliseconds - 1_000),
        < 1_700 => 2_400 + 5 * (atMilliseconds - 1_400),
        < 2_200 => 3_900 - 3 * (atMilliseconds - 1_700),
        < 2_500 => 2_400 + 4 * (atMilliseconds - 2_200),
        _ => 3_600
    };

    private static MotionMetrics MeasureRamp(IReadOnlyList<FramePoint> frames, double sourceSlope)
    {
        var velocities = frames.Select(frame => frame.VelocityRpmPerMillisecond / sourceSlope).ToArray();
        var velocityChanges = velocities.Zip(velocities.Skip(1), (previous, current) => Math.Abs(current - previous)).ToArray();
        var velocityErrors = velocities.Select(velocity => Math.Abs(velocity - 1)).ToArray();
        var lag = frames.Select(frame => (frame.SourceRpm - frame.DisplayRpm) / sourceSlope).ToArray();
        return new(
            frames.Count,
            frames.Count(frame => Math.Abs(frame.DeltaRpm) < RpmTolerance),
            Percentile(velocities, 0.05),
            Percentile(velocities, 0.50),
            Percentile(velocities, 0.95),
            Percentile(velocityErrors, 0.95),
            Percentile(velocityChanges, 0.95),
            velocityChanges.Max(),
            Percentile(lag, 0.95),
            lag.Max());
    }

    private void WriteMetrics(
        int packetHz,
        RenderPattern pattern,
        bool falling,
        bool observeBeforeSample,
        ReplayResult replay,
        MotionMetrics metrics)
    {
        _output.WriteLine(
            $"{packetHz}Hz/{pattern}/{(falling ? "fall" : "rise")}/{OrderName(observeBeforeSample)}: " +
            $"received/accepted/coalesced={replay.DeliveredPackets}/{replay.AcceptedPackets}/{replay.CoalescedPackets}; " +
            $"new receives sharing a game timestamp={replay.SameGameTimestampObservations}; " +
            $"frames without new packet={replay.NoNewPacketFrames}; holds={metrics.FlatFrames}/{metrics.Frames}; " +
            $"normalized velocity p05/median/p95={metrics.P05Velocity:F3}/{metrics.MedianVelocity:F3}/{metrics.P95Velocity:F3}; " +
            $"velocity-error p95={metrics.P95VelocityError:P1}; " +
            $"velocity-change p95/max={metrics.P95VelocityChange:P1}/{metrics.MaximumVelocityChange:P1}; " +
            $"lag p95/max={metrics.P95LagMilliseconds:F3}/{metrics.MaximumLagMilliseconds:F3}ms; " +
            $"overshoot={replay.MaximumOvershootRpm:F6} RPM.");
    }

    private static void AssertReplaySafetyAndCoalescing(ReplayResult replay, int packetHz)
    {
        Assert.All(replay.Frames, frame => Assert.True(double.IsFinite(frame.DisplayRpm)));
        Assert.InRange(replay.MaximumOvershootRpm, 0, 0.000001);
        Assert.InRange(replay.MaximumObserveSampleDifference, 0, 0.000001);
        Assert.Equal(replay.DeliveredPackets, replay.AcceptedPackets + replay.CoalescedPackets);
        Assert.True(replay.NoNewPacketFrames > 0, "Replay must include render frames with no new packet.");
        if (packetHz == 82)
        {
            Assert.True(replay.CoalescedPackets > 0, "82Hz replay must actually discard intermediate packets.");
            Assert.True(replay.SameGameTimestampObservations > 0,
                "82Hz replay must observe distinct receive times sharing a coarse game timestamp.");
        }
        else
        {
            Assert.Equal(0, replay.CoalescedPackets);
            Assert.Equal(0, replay.SameGameTimestampObservations);
        }
    }

    private static double SustainedDirectionResponse(IReadOnlyList<FramePoint> frames, int direction, double observedAt)
    {
        for (var index = 0; index + 2 < frames.Count; index++)
        {
            if (direction * frames[index].DeltaRpm > RpmTolerance &&
                direction * frames[index + 1].DeltaRpm > RpmTolerance &&
                direction * frames[index + 2].DeltaRpm > RpmTolerance)
            {
                return frames[index].AtMilliseconds - observedAt;
            }
        }

        return double.PositiveInfinity;
    }

    private static double Percentile(IEnumerable<double> values, double quantile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered[(int)Math.Ceiling(quantile * ordered.Length) - 1];
    }

    private static string OrderName(bool observeBeforeSample) => observeBeforeSample ? "observe-first" : "sample-first";

    private sealed record ReplayResult(
        IReadOnlyList<FramePoint> Frames,
        int DeliveredPackets,
        int AcceptedPackets,
        int CoalescedPackets,
        int NoNewPacketFrames,
        int SameGameTimestampObservations,
        double MaximumOvershootRpm,
        double MaximumObserveSampleDifference);

    private readonly record struct FramePoint(
        double AtMilliseconds,
        double DisplayRpm,
        double SourceRpm,
        double LatestPacketMilliseconds,
        double DeltaRpm,
        double VelocityRpmPerMillisecond);

    private readonly record struct MotionMetrics(
        int Frames,
        int FlatFrames,
        double P05Velocity,
        double MedianVelocity,
        double P95Velocity,
        double P95VelocityError,
        double P95VelocityChange,
        double MaximumVelocityChange,
        double P95LagMilliseconds,
        double MaximumLagMilliseconds);

    private static double Outside(double value, double lowerBound, double upperBound) =>
        Math.Max(0, Math.Max(lowerBound - value, value - upperBound));

    private static uint CoarseGameTimestampMilliseconds(double receivedMilliseconds) =>
        1_000u + (uint)(Math.Floor(receivedMilliseconds / GameClockQuantumMilliseconds) * GameClockQuantumMilliseconds);

    private static long TimestampMilliseconds(double milliseconds) =>
        (long)Math.Round(milliseconds * Stopwatch.Frequency / 1_000d);

    public enum RenderPattern
    {
        SmoothSwing,
        AlternatingExtremes,
        RateSteps
    }
}
