using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeTachometerRecordedTraceTests
{
    private const double RpmTolerance = 0.01;
    private readonly ITestOutputHelper _output;

    public NativeTachometerRecordedTraceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(120, false)]
    [InlineData(60, true)]
    [InlineData(60, false)]
    public void NormalRecordedPlaybackFollowsTheKnownReceiveTimeCurve(int renderHz, bool observeBeforeSample)
    {
        var packets = ReadFixture();
        var replay = Replay(packets, renderHz, observeBeforeSample, includePause: false);

        Assert.Equal(renderHz == 120 ? 128 : 107, replay.Accepted.Count);
        Assert.Equal(renderHz == 120 ? 0 : 21, replay.CoalescedPackets);
        Assert.Equal(renderHz == 120 ? 14 : 9, replay.ChangedRpmWithSameGameTimestamp);
        Assert.InRange(replay.MaximumAcceptedGap, 1, TimestampMilliseconds(32));
        Assert.Null(replay.FirstAdaptiveObservation);

        foreach (var frame in replay.Frames)
        {
            var expected = ReferenceRpm(replay.Accepted, frame.AvailableSamples,
                frame.Timestamp - Stopwatch.Frequency * 0.040);
            AssertRpmNear(expected, frame.Rpm, frame.Timestamp);
        }

        AssertFiniteBoundedAndSettled(replay);
        _output.WriteLine(
            $"{renderHz}Hz/{(observeBeforeSample ? "observe-first" : "sample-first")}: " +
            $"accepted={replay.Accepted.Count}; coalesced={replay.CoalescedPackets}; " +
            $"same-game changed-RPM updates={replay.ChangedRpmWithSameGameTimestamp}; " +
            $"max receive gap={Milliseconds(replay.MaximumAcceptedGap):F4}ms; reference delay=40ms.");
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(120, false)]
    [InlineData(60, true)]
    [InlineData(60, false)]
    public void CoalescedPauseUsesABoundedPlaybackEnvelopeInsteadOfAssumingAFixedDelay(int renderHz, bool observeBeforeSample)
    {
        var replay = Replay(ReadFixture(), renderHz, observeBeforeSample, includePause: true);
        Assert.NotNull(replay.FirstAdaptiveObservation);
        Assert.True(replay.MaximumAcceptedGap > TimestampMilliseconds(32));
        Assert.True(replay.MaximumAcceptedGap < TimestampMilliseconds(250));

        foreach (var frame in replay.Frames)
        {
            if (frame.Timestamp < replay.FirstAdaptiveObservation!.Value)
            {
                var expected = ReferenceRpm(replay.Accepted, frame.AvailableSamples,
                    frame.Timestamp - Stopwatch.Frequency * 0.040);
                AssertRpmNear(expected, frame.Rpm, frame.Timestamp);
                continue;
            }

            // The pause can exhaust old history before the next callback accepts
            // queued data. Allow one render interval beyond the 75ms delay ceiling.
            var earliest = frame.Timestamp - Stopwatch.Frequency * (0.075 + 1d / renderHz);
            var latest = frame.Timestamp - Stopwatch.Frequency * 0.040;
            var range = ReferenceRange(replay.Accepted, frame.AvailableSamples, earliest, latest);
            Assert.InRange(frame.Rpm, range.Minimum - RpmTolerance, range.Maximum + RpmTolerance);
        }

        AssertFiniteBoundedAndSettled(replay);
        _output.WriteLine(
            $"{renderHz}Hz pause: max coalesced receive gap={Milliseconds(replay.MaximumAcceptedGap):F4}ms; " +
            $"accepted={replay.Accepted.Count}; coalesced={replay.CoalescedPackets}.");
    }

    [Theory]
    [InlineData(120, false)]
    [InlineData(120, true)]
    [InlineData(60, false)]
    [InlineData(60, true)]
    public void RecordedPlaybackIsIndependentOfSameTimestampCallbackOrder(int renderHz, bool includePause)
    {
        var packets = ReadFixture();
        var observeFirst = Replay(packets, renderHz, observeBeforeSample: true, includePause: includePause);
        var sampleFirst = Replay(packets, renderHz, observeBeforeSample: false, includePause: includePause);

        Assert.Equal(observeFirst.Frames.Count, sampleFirst.Frames.Count);
        for (var index = 0; index < observeFirst.Frames.Count; index++)
        {
            Assert.Equal(observeFirst.Frames[index].Timestamp, sampleFirst.Frames[index].Timestamp);
            Assert.Equal(observeFirst.Frames[index].Rpm, sampleFirst.Frames[index].Rpm, 6);
        }

        Assert.InRange(observeFirst.MaximumCallbackDifference, 0, 0.000001);
        Assert.InRange(sampleFirst.MaximumCallbackDifference, 0, 0.000001);
    }

    [Fact]
    public void RealChangedRpmSamplesSharingAGameTimestampRemainInTheReceiveTimeCurve()
    {
        var packets = ReadFixture();
        var pairs = packets.Zip(packets.Skip(1), (left, right) => (Left: left, Right: right))
            .Where(pair => pair.Left.GameMilliseconds == pair.Right.GameMilliseconds && pair.Left.Rpm != pair.Right.Rpm)
            .ToArray();
        Assert.Equal(14, pairs.Length);

        foreach (var pair in pairs)
        {
            var interpolator = new NativeTachometerInterpolator();
            interpolator.Observe(314, pair.Left.GameMilliseconds, pair.Left.Rpm,
                pair.Left.ReceivedTimestamp, pair.Left.ReceivedTimestamp);
            interpolator.Observe(314, pair.Right.GameMilliseconds, pair.Right.Rpm,
                pair.Right.ReceivedTimestamp, pair.Right.ReceivedTimestamp);

            var midpoint = (long)Math.Round((pair.Left.ReceivedTimestamp + pair.Right.ReceivedTimestamp) / 2d
                + Stopwatch.Frequency * 0.040);
            RecordedPacket[] knownPair = [pair.Left, pair.Right];
            var expected = ReferenceRpm(knownPair, knownPair.Length, midpoint - Stopwatch.Frequency * 0.040);
            AssertRpmNear(expected, interpolator.Sample(midpoint), midpoint);
            var endpoint = pair.Right.ReceivedTimestamp + TimestampMilliseconds(40);
            AssertRpmNear(pair.Right.Rpm, interpolator.Sample(endpoint), endpoint);
        }
    }

    private static ReplayResult Replay(
        IReadOnlyList<RecordedPacket> packets,
        int renderHz,
        bool observeBeforeSample,
        bool includePause)
    {
        var interpolator = new NativeTachometerInterpolator();
        var first = packets[0];
        interpolator.Observe(314, first.GameMilliseconds, first.Rpm,
            first.ReceivedTimestamp, first.ReceivedTimestamp);
        var accepted = new List<RecordedPacket> { first };
        var frames = new List<PlaybackFrame>();
        var nextPacketIndex = 1;
        var coalescedPackets = 0;
        var changedRpmWithSameGameTimestamp = 0;
        var maximumAcceptedGap = 0L;
        long? firstAdaptiveObservation = null;
        var lastObservation = first.ReceivedTimestamp;
        var maximumCallbackDifference = 0d;
        var endTimestamp = packets[^1].ReceivedTimestamp + TimestampMilliseconds(75 + 2_000d / renderHz);

        for (var frameIndex = 1; ; frameIndex++)
        {
            var timestamp = TimestampMilliseconds(frameIndex * 1_000d / renderHz);
            if (timestamp > endTimestamp)
            {
                break;
            }

            if (includePause && timestamp >= TimestampMilliseconds(600) && timestamp < TimestampMilliseconds(642))
            {
                continue;
            }

            var arrived = 0;
            var latest = accepted[^1];
            while (nextPacketIndex < packets.Count && packets[nextPacketIndex].ReceivedTimestamp <= timestamp)
            {
                latest = packets[nextPacketIndex++];
                arrived++;
            }

            if (arrived > 0)
            {
                var previous = accepted[^1];
                var gap = latest.ReceivedTimestamp - previous.ReceivedTimestamp;
                maximumAcceptedGap = Math.Max(maximumAcceptedGap, gap);
                if (gap > TimestampMilliseconds(32))
                {
                    firstAdaptiveObservation ??= timestamp;
                }

                if (latest.GameMilliseconds == previous.GameMilliseconds && latest.Rpm != previous.Rpm)
                {
                    changedRpmWithSameGameTimestamp++;
                }

                accepted.Add(latest);
                coalescedPackets += arrived - 1;
                lastObservation = timestamp;
            }

            double? observedRpm = null;
            if (arrived > 0 && observeBeforeSample)
            {
                observedRpm = interpolator.Observe(314, latest.GameMilliseconds, latest.Rpm,
                    timestamp, latest.ReceivedTimestamp);
            }

            var rpm = interpolator.Sample(timestamp);
            if (arrived > 0 && !observeBeforeSample)
            {
                observedRpm = interpolator.Observe(314, latest.GameMilliseconds, latest.Rpm,
                    timestamp, latest.ReceivedTimestamp);
            }

            if (observedRpm is double observed)
            {
                maximumCallbackDifference = Math.Max(maximumCallbackDifference, Math.Abs(observed - rpm));
            }

            frames.Add(new(timestamp, rpm, accepted.Count));
        }

        Assert.Equal(packets.Count, nextPacketIndex);
        return new(accepted, frames, coalescedPackets, changedRpmWithSameGameTimestamp,
            maximumAcceptedGap, firstAdaptiveObservation, lastObservation, maximumCallbackDifference);
    }

    private static double ReferenceRpm(IReadOnlyList<RecordedPacket> samples, int count, double playbackTimestamp)
    {
        if (playbackTimestamp <= samples[0].ReceivedTimestamp)
        {
            return samples[0].Rpm;
        }

        for (var index = 1; index < count; index++)
        {
            var right = samples[index];
            if (playbackTimestamp <= right.ReceivedTimestamp)
            {
                var left = samples[index - 1];
                var fraction = (playbackTimestamp - left.ReceivedTimestamp) /
                    (right.ReceivedTimestamp - left.ReceivedTimestamp);
                return left.Rpm + (right.Rpm - left.Rpm) * fraction;
            }
        }

        return samples[count - 1].Rpm;
    }

    private static (double Minimum, double Maximum) ReferenceRange(
        IReadOnlyList<RecordedPacket> samples,
        int count,
        double earliestPlayback,
        double latestPlayback)
    {
        var earliest = ReferenceRpm(samples, count, earliestPlayback);
        var latest = ReferenceRpm(samples, count, latestPlayback);
        var minimum = Math.Min(earliest, latest);
        var maximum = Math.Max(earliest, latest);
        for (var index = 0; index < count; index++)
        {
            var sample = samples[index];
            if (sample.ReceivedTimestamp >= earliestPlayback && sample.ReceivedTimestamp <= latestPlayback)
            {
                minimum = Math.Min(minimum, sample.Rpm);
                maximum = Math.Max(maximum, sample.Rpm);
            }
        }

        return (minimum, maximum);
    }

    private static void AssertFiniteBoundedAndSettled(ReplayResult replay)
    {
        Assert.Equal(128, replay.Accepted.Count + replay.CoalescedPackets);
        Assert.InRange(replay.MaximumCallbackDifference, 0, 0.000001);
        foreach (var frame in replay.Frames)
        {
            Assert.True(double.IsFinite(frame.Rpm));
            var known = replay.Accepted.Take(frame.AvailableSamples);
            Assert.InRange(frame.Rpm, known.Min(sample => sample.Rpm) - RpmTolerance,
                known.Max(sample => sample.Rpm) + RpmTolerance);
        }

        var latestRpm = replay.Accepted[^1].Rpm;
        var settled = replay.Frames.FirstOrDefault(frame => frame.Timestamp >= replay.LastObservation &&
            Math.Abs(frame.Rpm - latestRpm) <= RpmTolerance);
        Assert.True(settled.AvailableSamples > 0, "The recorded endpoint must be reached after its final observation.");
        Assert.InRange(settled.Timestamp - replay.LastObservation, 0, TimestampMilliseconds(75));
        Assert.All(replay.Frames.Where(frame => frame.Timestamp >= settled.Timestamp),
            frame => AssertRpmNear(latestRpm, frame.Rpm, frame.Timestamp));
    }

    private static void AssertRpmNear(double expected, double actual, long timestamp)
    {
        Assert.True(Math.Abs(expected - actual) <= RpmTolerance,
            $"At {Milliseconds(timestamp):F4}ms expected {expected:F6} RPM from known receive-time samples, got {actual:F6}.");
    }

    private static IReadOnlyList<RecordedPacket> ReadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TachMotion", "moving-rpm-128.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var packets = document.RootElement.EnumerateArray().Select(sample => new RecordedPacket(
            TimestampMilliseconds(sample.GetProperty("receiveMilliseconds").GetDouble()),
            sample.GetProperty("gameMilliseconds").GetUInt32(),
            sample.GetProperty("rpm").GetDouble())).ToArray();
        Assert.Equal(128, packets.Length);
        Assert.Equal(0, packets[0].ReceivedTimestamp);
        Assert.All(packets, packet => Assert.True(double.IsFinite(packet.Rpm) && packet.Rpm >= 0));
        for (var index = 1; index < packets.Length; index++)
        {
            Assert.True(packets[index].ReceivedTimestamp > packets[index - 1].ReceivedTimestamp);
        }

        return packets;
    }

    private sealed record ReplayResult(
        IReadOnlyList<RecordedPacket> Accepted,
        IReadOnlyList<PlaybackFrame> Frames,
        int CoalescedPackets,
        int ChangedRpmWithSameGameTimestamp,
        long MaximumAcceptedGap,
        long? FirstAdaptiveObservation,
        long LastObservation,
        double MaximumCallbackDifference);

    private readonly record struct RecordedPacket(long ReceivedTimestamp, uint GameMilliseconds, double Rpm);

    private readonly record struct PlaybackFrame(long Timestamp, double Rpm, int AvailableSamples);

    private static double Milliseconds(long timestamp) => timestamp * 1_000d / Stopwatch.Frequency;

    private static long TimestampMilliseconds(double milliseconds) =>
        (long)Math.Round(milliseconds * Stopwatch.Frequency / 1_000d);
}
