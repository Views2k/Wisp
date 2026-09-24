using System.Diagnostics;
using System.Runtime.InteropServices;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class CompositorNeedleCurveTests
{
    [Fact]
    public void PointLayoutMatchesThreePackedNativeDoubles()
    {
        Assert.Equal(24, Marshal.SizeOf<CompositorNeedlePoint>());
        Assert.Equal(0, Marshal.OffsetOf<CompositorNeedlePoint>(nameof(CompositorNeedlePoint.OffsetSeconds)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<CompositorNeedlePoint>(nameof(CompositorNeedlePoint.Angle)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<CompositorNeedlePoint>(nameof(CompositorNeedlePoint.Blur)).ToInt32());
    }

    [Fact]
    public void CurveContainsPairedSourceKnotsAndStopsAtNewestWithoutExtrapolation()
    {
        var playback = CreateSteady();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];

        Assert.True(playback.TryCopyCompositorCurve(Timestamp(120), points, out var curve));

        Assert.Equal(Timestamp(120), curve.StartTimestamp);
        Assert.Equal(Timestamp(120), curve.LatestObservationTimestamp);
        Assert.Equal(Timestamp(195), curve.FreshUntilTimestamp);
        Assert.Equal(20, curve.PlaybackTargetDelayMilliseconds);
        Assert.InRange(curve.Count, 2, CompositorNeedleCurve.MaximumPoints);
        Assert.InRange(curve.EndTimestamp, Timestamp(120), Timestamp(195));
        Assert.Equal(0, points[0].OffsetSeconds);
        Assert.Equal(curve.DurationSeconds, points[curve.Count - 1].OffsetSeconds);
        Assert.Equal(180, points[curve.Count - 1].Angle, 6);
        Assert.Equal(0.08, points[curve.Count - 1].Blur, 6);
        for (var index = 0; index < curve.Count; index++)
        {
            Assert.InRange(points[index].Angle, 120, 180);
            Assert.Equal(points[index].Angle * .004 - .64, points[index].Blur, 8);
            if (index > 0)
                Assert.InRange(points[index].OffsetSeconds - points[index - 1].OffsetSeconds,
                    1d / Stopwatch.Frequency, .001 + 1d / Stopwatch.Frequency);
        }
        // Include every future accepted corner rather than only a regular grid.
        for (var sourceTime = 108; sourceTime <= 120; sourceTime += 4)
            Assert.Contains(points.Take(curve.Count), point => Math.Abs(point.Angle - Angle(sourceTime)) < .0001);
    }

    [Fact]
    public void ExportDoesNotSampleFuturePruneHistoryOrChangeLaterLivePlayback()
    {
        var actual = CreateSteady();
        var expected = CreateSteady();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        var count = actual.BufferedSamples;
        var delay = actual.PlaybackDelayMilliseconds(Timestamp(120));
        var reseeds = actual.ReseedCount;

        Assert.True(actual.TryCopyCompositorCurve(Timestamp(120), points, out _));
        Assert.True(actual.TryCopyCompositorCurve(Timestamp(190), points, out _));
        Assert.False(actual.TryCopyCompositorCurve(Timestamp(196), points, out _));
        Assert.Equal(count, actual.BufferedSamples);
        Assert.Equal(delay, actual.PlaybackDelayMilliseconds(Timestamp(120)));
        Assert.Equal(reseeds, actual.ReseedCount);
        foreach (var time in new[] { 124, 132, 146 })
        {
            Assert.True(expected.Sample(Timestamp(time), out var expectedState));
            Assert.True(actual.Sample(Timestamp(time), out var actualState));
            Assert.Equal(expectedState, actualState);
            Assert.Equal(expected.BufferedSamples, actual.BufferedSamples);
            Assert.Equal(expected.PlaybackDelayMilliseconds(Timestamp(time)), actual.PlaybackDelayMilliseconds(Timestamp(time)));
        }
    }

    [Fact]
    public void FrozenAdaptiveProjectionMatchesIndependentSingleFutureSamples()
    {
        var playback = CreateAdaptive();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(176), points, out var curve));
        Assert.Equal(60, curve.PlaybackTargetDelayMilliseconds, 6);
        Assert.Equal(playback.PlaybackDelayMilliseconds(Timestamp(176)), curve.PlaybackDelayMilliseconds);

        for (var index = 0; index < curve.Count; index++)
        {
            var independent = CreateAdaptive();
            var timestamp = curve.StartTimestamp + (long)Math.Round(points[index].OffsetSeconds * Stopwatch.Frequency);
            Assert.True(independent.Sample(timestamp, out var expected));
            Assert.Equal(expected.Angle, points[index].Angle, 8);
            Assert.Equal(expected.Blur, points[index].Blur, 8);
        }
        // Between exported points the compositor interpolates the frozen
        // projection. Bound that approximation independently of live sampling.
        for (var milliseconds = .25; milliseconds < curve.DurationSeconds * 1_000; milliseconds += .25)
        {
            var independent = CreateAdaptive();
            Assert.True(independent.Sample(Timestamp(176 + milliseconds), out var expected));
            var actual = Evaluate(points, curve.Count, milliseconds / 1_000);
            Assert.InRange(Math.Abs(expected.Angle - actual.Angle), 0, .005);
            Assert.InRange(Math.Abs(expected.Blur - actual.Blur), 0, .00005);
        }
    }

    [Fact]
    public void QueuedHistoryCanBeProjectedBeforeSamplingWithoutChangingTheClock()
    {
        var actual = CreateSteady();
        var expected = CreateSteady();
        foreach (var playback in new[] { actual, expected })
            playback.ObserveQueued(314, 1_128, Angle(128), Blur(128), Timestamp(128), Timestamp(128), false);
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        Assert.True(actual.TryCopyCompositorCurve(Timestamp(128), points, out _));
        Assert.True(expected.Sample(Timestamp(128), out var expectedState));
        Assert.Equal(expectedState.Angle, points[0].Angle);
        Assert.Equal(expectedState.Blur, points[0].Blur);
        Assert.True(actual.Sample(Timestamp(128), out var actualState));
        Assert.Equal(expectedState, actualState);
    }

    [Fact]
    public void UnavailableDuplicateAndFuturePairsCannotExtendCurveFreshness()
    {
        var playback = CreateSteady();
        playback.ObserveQueued(314, 1_130, null, null, Timestamp(130), Timestamp(130), false);
        playback.ObserveQueued(314, 1_131, 250, .5, Timestamp(131), Timestamp(120), false);
        playback.ObserveQueued(314, 1_132, 250, .5, Timestamp(132), Timestamp(200), false);
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(132), points, out var curve));
        Assert.Equal(Timestamp(120), curve.LatestObservationTimestamp);
        Assert.Equal(Timestamp(195), curve.FreshUntilTimestamp);
        Assert.Equal(180, points[curve.Count - 1].Angle, 6);
        Assert.False(playback.TryCopyCompositorCurve(Timestamp(196), points, out _));
    }

    [Fact]
    public void SourceResetAndCarReturnCannotReuseAnEarlierCurveGeneration()
    {
        var playback = CreateSteady();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(120), points, out var original));
        playback.ObserveQueued(314, 1_124, null, null, Timestamp(124), Timestamp(124), true);
        Assert.False(playback.TryCopyCompositorCurve(Timestamp(124), points, out _));
        playback.ObserveQueued(3766, 1_128, 220, -.3, Timestamp(128), Timestamp(128), false);
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(128), points, out var other));
        Assert.Equal(3766, other.AcceptedCarOrdinal);
        Assert.True(other.ReseedCount > original.ReseedCount);
        playback.ObserveQueued(314, 1_132, 140, .2, Timestamp(132), Timestamp(132), false);
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(132), points, out var returned));
        Assert.Equal(314, returned.AcceptedCarOrdinal);
        Assert.True(returned.ReseedCount > other.ReseedCount);
        Assert.All(points.Take(returned.Count), point =>
        {
            Assert.Equal(140, point.Angle);
            Assert.Equal(.2, point.Blur);
        });
    }

    [Fact]
    public void EmptySmallAndBackwardClockRequestsFailWithoutMutatingPlayback()
    {
        var playback = CreateSteady();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        Assert.False(new NativeNeedlePlayback().TryCopyCompositorCurve(Timestamp(120), points, out _));
        Assert.False(playback.TryCopyCompositorCurve(Timestamp(120), Span<CompositorNeedlePoint>.Empty, out _));
        Assert.False(playback.TryCopyCompositorCurve(Timestamp(120), points.AsSpan(0, 1), out var failed));
        Assert.Equal(default, failed);
        Assert.False(playback.TryCopyCompositorCurve(Timestamp(119), points, out _));
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(120), points, out _));
    }

    [Fact]
    public void NewestValueIsAHoldWithAnOriginalFreshnessDeadline()
    {
        var playback = CreateSteady();
        Assert.True(playback.Sample(Timestamp(170), out var held));
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(170), points, out var curve));
        Assert.Equal(1, curve.Count);
        Assert.Equal(0, curve.DurationSeconds);
        Assert.Equal(held.Angle, points[0].Angle);
        Assert.Equal(held.Blur, points[0].Blur);
        Assert.Equal(Timestamp(195), curve.FreshUntilTimestamp);
    }

    [Fact]
    public void ExactExpiryCannotStartACompositorCurveButDoesNotChangeOrdinarySampling()
    {
        var playback = CreateSteady();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        var expiry = Timestamp(195);
        Assert.True(playback.TryCopyCompositorCurve(expiry - 1, points, out var before));
        Assert.Equal(expiry, before.FreshUntilTimestamp);
        Assert.False(playback.TryCopyCompositorCurve(expiry, points, out var expired));
        Assert.Equal(default, expired);
        Assert.True(playback.Sample(expiry, out var held));
        Assert.Equal(180, held.Angle);
        Assert.Equal(.08, held.Blur, 8);
        Assert.False(playback.Sample(expiry + 1, out _));
    }

    [Fact]
    public void ReusableCurveExportDoesNotAllocate()
    {
        var playback = CreateAdaptive();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        for (var index = 0; index < 64; index++)
            playback.TryCopyCompositorCurve(Timestamp(176), points, out _);
        var completed = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
            if (playback.TryCopyCompositorCurve(Timestamp(176), points, out var curve)) completed += curve.Count;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(completed > 1_000);
        Assert.Equal(0, allocated);
    }

    private static NativeNeedlePlayback CreateSteady()
    {
        var playback = new NativeNeedlePlayback();
        for (var time = 0; time <= 120; time += 4)
        {
            playback.ObserveQueued(314, (uint)(1_000 + time), Angle(time), Blur(time),
                Timestamp(time), Timestamp(time), false);
            playback.Sample(Timestamp(time), out _);
        }
        return playback;
    }

    private static NativeNeedlePlayback CreateAdaptive()
    {
        var playback = CreateSteady();
        foreach (var time in new[] { 168, 172, 176 })
            playback.ObserveQueued(314, (uint)(1_000 + time), Angle(time), Blur(time),
                Timestamp(176), Timestamp(time), false);
        playback.Sample(Timestamp(176), out _);
        return playback;
    }

    private static NativeNeedleRenderState Evaluate(CompositorNeedlePoint[] points, int count, double seconds)
    {
        var index = 0;
        while (index + 1 < count && points[index + 1].OffsetSeconds < seconds) index++;
        var left = points[index];
        var right = points[Math.Min(index + 1, count - 1)];
        var fraction = right.OffsetSeconds == left.OffsetSeconds ? 0 :
            (seconds - left.OffsetSeconds) / (right.OffsetSeconds - left.OffsetSeconds);
        return new(left.Angle + (right.Angle - left.Angle) * fraction,
            left.Blur + (right.Blur - left.Blur) * fraction);
    }

    private static double Angle(int milliseconds) => 120 + milliseconds * .5;
    private static double Blur(int milliseconds) => Angle(milliseconds) * .004 - .64;
    private static long Timestamp(double milliseconds) => Stopwatch.Frequency +
        (long)Math.Round(Stopwatch.Frequency * milliseconds / 1_000d);
}
