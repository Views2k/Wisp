using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

internal static class GForceHudLayerTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        RangeChangesShareTheForceTimelineAndReprojectHistory();
        TrailSamplingIsIndependentOfDisplayFrames();
        InvalidDataAndVehicleChangesResetMotion();
        OriginalGaugeArtworkIsCachedAndWorkerSafe();
    }

    private static void RangeChangesShareTheForceTimelineAndReprojectHistory()
    {
        var motion = new GForceHudMotion();
        motion.Observe(Input(0, .86, 0, 1), Ticks(1_000));
        motion.Observe(Input(16, .88, 0, 1.25), Ticks(1_016));
        motion.Observe(Input(32, .88, 0, 1.25), Ticks(1_032));
        var before = motion.Sample(Ticks(1_044));
        var after = motion.Sample(Ticks(1_048));
        Assert.Equal(1.0625, before.FullScaleG, 6);
        Assert.Equal(.865 / 1.0625 * 31, before.Offset.X, 6);
        Assert.Equal(1.125, after.FullScaleG, 6);
        Assert.Equal(.87 / 1.125 * 31, after.Offset.X, 6);
        Assert.InRange(before.Offset.X - after.Offset.X, 0, 1.5);
        var settled = motion.Sample(Ticks(1_100));
        Assert.Equal(.88 / 1.25 * 31, settled.Offset.X, 6);
        Assert.Equal(1, motion.Trail.Count);
        Assert.Equal(new Point(.86, 0), motion.Trail[0]);
        Assert.Equal(.86 / 1.25 * 31, GForceTrailHistory.Project(motion.Trail[0], settled.FullScaleG).X, 6);
        // A range change cannot manufacture movement in the stored trail.
        var history = new GForceTrailHistory();
        Assert.True(history.TryAddUnscaled(new(.86, 0), 1));
        Assert.False(history.TryAddUnscaled(new(.88, 0), 1.25));
        Assert.True(history.TryAddUnscaled(new(.90, 0), 1.25));
        var pinned = GForceTrailHistory.Project(new(double.MaxValue, double.MaxValue), 1);
        Assert.Equal(31, ((Vector)pinned).Length, 6);
    }

    private static void TrailSamplingIsIndependentOfDisplayFrames()
    {
        var slow = new GForceHudMotion();
        var fast = new GForceHudMotion();
        for (var elapsed = 0; elapsed <= 960; elapsed += 4)
        {
            var timestamp = Ticks(1_000 + elapsed);
            if (elapsed % 16 == 0)
            {
                var phase = elapsed / 100d;
                var input = Input(elapsed, Math.Sin(phase), Math.Cos(phase), elapsed < 480 ? 1 : 1.25);
                slow.Observe(input, timestamp);
                fast.Observe(input, timestamp);
                // A second publication of the same receive identity adds no trail point.
                fast.Observe(input, timestamp);
            }
            fast.Sample(timestamp);
            if (elapsed % 16 == 0) slow.Sample(timestamp);
        }
        var slowEnd = slow.Sample(Ticks(2_100));
        var fastEnd = fast.Sample(Ticks(2_100));
        Assert.Equal(slowEnd, fastEnd);
        Assert.Equal(GForceTrailHistory.Capacity, slow.Trail.Count);
        Assert.Equal(slow.Trail.Count, fast.Trail.Count);
        for (var index = 0; index < slow.Trail.Count; index++) Assert.Equal(slow.Trail[index], fast.Trail[index]);
    }

    private static void InvalidDataAndVehicleChangesResetMotion()
    {
        var motion = new GForceHudMotion();
        motion.Observe(Input(0, -.5, .25, 1), Ticks(1_000));
        Assert.True(motion.Active);
        Assert.Equal(-15.5, motion.Sample(Ticks(1_000)).Offset.X, 6);
        Assert.Equal(7.75, motion.Sample(Ticks(1_000)).Offset.Y, 6);
        motion.Observe(Input(16, .2, 0, 1) with { CarOrdinal = 2 }, Ticks(1_016));
        Assert.Equal(1, motion.Trail.Count);
        Assert.Equal(.2 * 31, motion.Sample(Ticks(1_016)).Offset.X, 6);
        motion.Observe(default, Ticks(1_032));
        Assert.False(motion.Active);
        Assert.Equal(0, motion.Trail.Count);
        Assert.Equal(default(Point), motion.Sample(Ticks(1_032)).Offset);
        motion.Observe(Input(48, double.NaN, 0, 1), Ticks(1_048));
        Assert.False(motion.Active);
    }

    private static void OriginalGaugeArtworkIsCachedAndWorkerSafe()
    {
        foreach (var gauge in new FrameworkElement[]
        {
            new NativeGForceMeterView(), new GForceMeterView { Width = 150, Height = 120 }
        })
        {
            gauge.Measure(new Size(gauge.Width, gauge.Height));
            gauge.Arrange(new Rect(0, 0, gauge.Width, gauge.Height));
            var snapshot = GForceHudLayer.Capture(gauge, null);
            Assert.Same(snapshot, GForceHudLayer.Capture(gauge, null));
            Assert.All(snapshot.Textures, texture =>
            {
                Assert.InRange(texture.Id, 60_000u, 69_999u);
                Assert.Equal(texture.Stride * texture.Height, texture.Pixels.Length);
            });
            Assert.Equal(snapshot.Textures.Count, snapshot.Textures.Select(texture => texture.Id).Distinct().Count());
            var playback = snapshot.CreatePlayback();
            playback.Update(snapshot, Ticks(1_000));
            var commands = Task.Run(() => playback.Build(Ticks(1_004))).GetAwaiter().GetResult();
            Assert.Equal(60_000u, commands[0].TextureId);
            Assert.Single(commands, command => command.TextureId == 60_001);
            Assert.All(commands, command => Assert.Equal(DirectCompositionShader.Image, command.Shader));
        }
    }

    private static NativeGForceInput Input(int elapsed, double x, double y, double scale) =>
        new(true, x, y, scale, 1, (uint)(1_000 + elapsed / 16 * 16), Ticks(1_000 + elapsed));
    private static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1_000);
}
