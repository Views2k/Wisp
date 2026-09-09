using System.Diagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativePlaybackDiagnosticsTests
{
    [Fact]
    public void ReadingDiagnosticsDoesNotAdvanceOrChangePlayback()
    {
        var observed = new NativeTachometerInterpolator();
        var control = new NativeTachometerInterpolator();
        (int At, uint GameTime, double Rpm)[] samples =
        [
            (0, 1_000, 1_000),
            (16, 1_016, 7_000),
            (24, 1_016, 2_000),
            (72, 1_064, 6_500),
            (160, 1_144, 900),
            (176, 1_160, 4_000)
        ];
        var next = 0;
        for (var at = 0; at <= 240; at++)
        {
            var timestamp = Timestamp(at);
            if (next < samples.Length && samples[next].At == at)
            {
                var sample = samples[next++];
                Assert.Equal(
                    control.Observe(314, sample.GameTime, sample.Rpm, timestamp),
                    observed.Observe(314, sample.GameTime, sample.Rpm, timestamp));
            }

            ReadDiagnostics(observed, Timestamp(at + 100));
            Assert.Equal(control.Sample(timestamp), observed.Sample(timestamp));
            Assert.Equal(control.BufferedSamples, observed.BufferedSamples);
            Assert.Equal(control.PlaybackAtNewest, observed.PlaybackAtNewest);
            Assert.Equal(control.PlaybackDelayMilliseconds(timestamp), observed.PlaybackDelayMilliseconds(timestamp));
            Assert.Equal(control.ReseedCount, observed.ReseedCount);
            Assert.Equal(control.StarvationReseedCount, observed.StarvationReseedCount);
        }
        Assert.Equal(samples.Length, next);
    }

    [Fact]
    public void ActualHeldDelayIsDistinctFromTheTargetAndEmptyStateIsExplicit()
    {
        var playback = new NativeTachometerInterpolator();
        Assert.Equal(0, playback.BufferedSamples);
        Assert.False(playback.PlaybackAtNewest);
        Assert.Equal(0, playback.PlaybackDelayMilliseconds(Timestamp(100)));

        playback.Observe(314, 1_000, 1_000, Timestamp(0));
        playback.Observe(314, 1_020, 6_000, Timestamp(20));
        Assert.Equal(2, playback.BufferedSamples);
        Assert.False(playback.PlaybackAtNewest);
        Assert.Equal(40, playback.PlaybackTargetDelayMilliseconds, 6);
        Assert.Equal(6_000, playback.Sample(Timestamp(200)));
        Assert.Equal(1, playback.BufferedSamples);
        Assert.True(playback.PlaybackAtNewest);
        Assert.Equal(180, playback.PlaybackDelayMilliseconds(Timestamp(200)), 6);
        Assert.Equal(40, playback.PlaybackTargetDelayMilliseconds, 6);

        playback.Reset();
        Assert.Equal(0, playback.BufferedSamples);
        Assert.False(playback.PlaybackAtNewest);
        Assert.Equal(0, playback.PlaybackDelayMilliseconds(Timestamp(200)));
    }

    [Fact]
    public void CumulativeReseedsDistinguishDeliveryStarvationFromCarAndGameTimeChanges()
    {
        var playback = new NativeTachometerInterpolator();
        playback.Observe(314, 1_000, 1_000, Timestamp(0));
        playback.Observe(314, 1_016, 6_000, Timestamp(16));
        Assert.Equal(1, playback.ReseedCount);
        Assert.Equal(0, playback.StarvationReseedCount);

        playback.Observe(314, 1_032, 2_000, Timestamp(100));
        Assert.Equal(2, playback.ReseedCount);
        Assert.Equal(1, playback.StarvationReseedCount);
        Assert.True(playback.PlaybackAtNewest);
        Assert.Equal(0, playback.PlaybackDelayMilliseconds(Timestamp(100)));

        playback.Observe(314, 2_000, 3_000, Timestamp(120));
        playback.Observe(3766, 2_016, 900, Timestamp(136));
        Assert.Equal(4, playback.ReseedCount);
        Assert.Equal(1, playback.StarvationReseedCount);
        playback.Reset();
        Assert.Equal(4, playback.ReseedCount);
        Assert.Equal(1, playback.StarvationReseedCount);
        playback.Observe(3766, 2_032, 1_200, Timestamp(152));
        Assert.Equal(5, playback.ReseedCount);
        Assert.Equal(1, playback.StarvationReseedCount);
    }

    [Fact]
    public void NativePairDiagnosticsTrackTheSharedTimelineAndClearWhenItExpires()
    {
        var playback = new NativeNeedlePlayback();
        Assert.True(playback.Observe(314, 1_000, 120, -0.2, Timestamp(0), Timestamp(0), false, out _));
        Assert.True(playback.Observe(314, 1_020, 240, 0.4, Timestamp(20), Timestamp(20), false, out _));
        Assert.Equal(2, playback.BufferedSamples);
        Assert.Equal(1, playback.ReseedCount);
        Assert.Equal(0, playback.StarvationReseedCount);
        Assert.Equal(40, playback.PlaybackTargetDelayMilliseconds, 6);
        Assert.False(playback.PlaybackAtNewest);
        Assert.Equal(50, playback.PlaybackDelayMilliseconds(Timestamp(30)), 6);
        Assert.True(playback.Sample(Timestamp(50), out var midpoint));
        Assert.Equal(180, midpoint.Angle, 6);
        Assert.Equal(0.1, midpoint.Blur, 6);
        Assert.True(playback.Sample(Timestamp(60), out var endpoint));
        Assert.Equal(new NativeNeedleRenderState(240, 0.4), endpoint);
        Assert.True(playback.PlaybackAtNewest);

        Assert.False(playback.Sample(Timestamp(96), out _));
        Assert.Equal(0, playback.BufferedSamples);
        Assert.False(playback.PlaybackAtNewest);
        Assert.Equal(0, playback.PlaybackDelayMilliseconds(Timestamp(96)));
        Assert.Equal(1, playback.ReseedCount);
        Assert.Equal(0, playback.StarvationReseedCount);
    }

    [Fact]
    public void ReadingDiagnosticPropertiesDoesNotAllocate()
    {
        var playback = new NativeTachometerInterpolator();
        playback.Observe(314, 1_000, 1_000, Timestamp(0));
        ReadDiagnostics(playback, Timestamp(20));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
            ReadDiagnostics(playback, index);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void ReadDiagnostics(NativeTachometerInterpolator playback, long timestamp)
    {
        _ = playback.BufferedSamples;
        _ = playback.PlaybackAtNewest;
        _ = playback.PlaybackDelayMilliseconds(timestamp);
        _ = playback.PlaybackTargetDelayMilliseconds;
        _ = playback.ReseedCount;
        _ = playback.StarvationReseedCount;
    }

    private static long Timestamp(double milliseconds) =>
        (long)Math.Round(milliseconds * Stopwatch.Frequency / 1_000d);
}
