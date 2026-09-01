using System.Diagnostics;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeNeedlePlaybackTests
{
    [Fact]
    public void NativeAngleAndSignedBlurStayOnTheSamePlaybackTimeline()
    {
        var playback = new NativeNeedlePlayback();

        Assert.True(playback.Observe(314, 1_000, 120, -0.20, Timestamp(0), Timestamp(0), false, out var first));
        Assert.Equal(new NativeNeedleRenderState(120, -0.20), first);
        Assert.True(playback.Observe(314, 1_020, 240, 0.40, Timestamp(20), Timestamp(20), false, out var observed));
        Assert.Equal(first, observed);

        Assert.True(playback.Sample(Timestamp(50), out var sampled));
        Assert.Equal(180, sampled.Angle, 6);
        Assert.Equal(0.10, sampled.Blur, 6);
    }

    [Theory]
    [InlineData(null, 0d)]
    [InlineData(120d, null)]
    [InlineData(double.NaN, 0d)]
    [InlineData(-1d, 0d)]
    [InlineData(120d, double.PositiveInfinity)]
    [InlineData(120d, 0.650001)]
    [InlineData(120d, -0.650001)]
    public void PartialOrInvalidNativePairHoldsTheLastExactPairBriefly(double? angle, double? blur)
    {
        var playback = new NativeNeedlePlayback();
        Assert.True(playback.Observe(314, 1_000, 120, 0, Timestamp(0), Timestamp(0), false, out _));

        Assert.True(playback.Observe(314, 1_020, angle, blur, Timestamp(20), Timestamp(20), false, out var held));
        Assert.Equal(new NativeNeedleRenderState(120, 0), held);
        Assert.True(playback.Sample(Timestamp(30), out _));
        Assert.Equal(314, playback.AcceptedCarOrdinal);
    }

    [Fact]
    public void CarChangeSnapsBothNativeValuesWithoutCrossCarBlending()
    {
        var playback = new NativeNeedlePlayback();
        playback.Observe(314, 1_000, 120, -0.2, Timestamp(0), Timestamp(0), false, out _);
        playback.Observe(314, 1_020, 240, 0.4, Timestamp(20), Timestamp(20), false, out _);

        Assert.True(playback.Observe(3766, 1_040, 150, -0.1, Timestamp(40), Timestamp(40), false, out var changed));
        Assert.Equal(new NativeNeedleRenderState(150, -0.1), changed);
        Assert.Equal(3766, playback.AcceptedCarOrdinal);
    }

    [Theory]
    [InlineData(314, true)]
    [InlineData(3766, false)]
    public void SourceOrCarInvalidationClearsTheExactPairImmediately(int carOrdinal, bool sourceInvalidated)
    {
        var playback = new NativeNeedlePlayback();
        playback.Observe(314, 1_000, 120, -0.2, Timestamp(0), Timestamp(0), false, out _);

        Assert.False(playback.Observe(
            carOrdinal,
            1_020,
            null,
            null,
            Timestamp(20),
            Timestamp(20),
            sourceInvalidated,
            out _));
        Assert.False(playback.Sample(Timestamp(21), out _));
        Assert.Null(playback.AcceptedCarOrdinal);
    }

    [Fact]
    public void ValidPairAfterInvalidStateSnapsBothChannelsTogether()
    {
        var playback = new NativeNeedlePlayback();
        playback.Observe(314, 1_000, 120, -0.2, Timestamp(0), Timestamp(0), false, out _);
        playback.Observe(314, 1_020, 240, 0.4, Timestamp(20), Timestamp(20), false, out _);
        Assert.True(playback.Observe(314, 1_040, -1, 0, Timestamp(40), Timestamp(40), false, out _));

        Assert.True(playback.Observe(
            314,
            1_060,
            150,
            -0.1,
            Timestamp(60),
            Timestamp(60),
            false,
            out var recovered));
        Assert.True(playback.Sample(Timestamp(120), out recovered));
        Assert.Equal(new NativeNeedleRenderState(150, -0.1), recovered);
    }

    [Fact]
    public void DuplicateNativeObservationCannotRetargetEitherChannel()
    {
        var playback = new NativeNeedlePlayback();
        playback.Observe(314, 1_000, 120, -0.2, Timestamp(0), Timestamp(0), false, out _);

        Assert.True(playback.Observe(
            314,
            1_020,
            240,
            0.4,
            Timestamp(20),
            Timestamp(0),
            false,
            out var duplicate));
        Assert.Equal(new NativeNeedleRenderState(120, -0.2), duplicate);
        Assert.True(playback.Sample(Timestamp(50), out var held));
        Assert.Equal(new NativeNeedleRenderState(120, -0.2), held);
    }

    [Fact]
    public void PlaybackReachesBothExactEndpointsThenExpiresTheHeldPair()
    {
        var playback = new NativeNeedlePlayback();
        playback.Observe(314, 1_000, 120, -0.2, Timestamp(0), Timestamp(0), false, out _);
        playback.Observe(314, 1_020, 240, 0.4, Timestamp(20), Timestamp(20), false, out _);

        Assert.True(playback.Sample(Timestamp(60), out var endpoint));
        Assert.Equal(new NativeNeedleRenderState(240, 0.4), endpoint);
        Assert.False(playback.Sample(
            Timestamp(20 + NativeNeedlePlayback.NativeSampleFreshnessMilliseconds + 1),
            out _));
        Assert.Null(playback.AcceptedCarOrdinal);
    }

    [Fact]
    public void CompositorSamplingDoesNotAllocatePerFrame()
    {
        var playback = new NativeNeedlePlayback();
        playback.Observe(314, 1_000, 120, -0.2, Timestamp(0), Timestamp(0), false, out _);
        playback.Observe(314, 1_020, 240, 0.4, Timestamp(20), Timestamp(20), false, out _);
        playback.Sample(Timestamp(21), out _);

        var start = Timestamp(21);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var accepted = true;
        for (var frame = 0; frame < 10_000; frame++)
        {
            accepted &= playback.Sample(start + frame + 1, out _);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(accepted);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ExactPairObservationDoesNotAllocatePerCapture()
    {
        var playback = new NativeNeedlePlayback();
        playback.Observe(314, 1_000, 120, -0.2, Timestamp(0), Timestamp(0), false, out _);
        playback.Observe(314, 1_001, 121, -0.19, Timestamp(1), Timestamp(1), false, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var accepted = true;
        for (var sample = 2; sample < 10_002; sample++)
        {
            accepted &= playback.Observe(
                314,
                (uint)(1_000 + sample),
                120 + sample * 0.01,
                -0.2 + sample * 0.000001,
                Timestamp(sample),
                Timestamp(sample),
                false,
                out _);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(accepted);
        Assert.Equal(0, allocated);
    }

    private static long Timestamp(double milliseconds) =>
        (long)Math.Round(milliseconds * Stopwatch.Frequency / 1_000d);
}
