using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AmbientParticlesTests
{
    [Fact]
    public void TransparentSpritesLeaveEveryUntouchedPixelTransparent()
    {
        var renderer = new AmbientBackdropRasterizer(65, 65, particlesOnly: true);
        var particle = new AmbientParticle(new AmbientPoint(32.5, 32.5), 12, 0.3, 0.82, 0.5, 0.6, 0.6);
        renderer.Render([particle], 1, Color.FromRgb(230, 60, 100));

        Assert.Equal(new byte[4], renderer.Pixels.AsSpan(0, 4).ToArray());
        var center = (32 * 65 + 32) * 4;
        Assert.InRange(renderer.Pixels[center + 3], 75, 77);
        Assert.InRange(renderer.Pixels[center + 2], 67, 70);
        Assert.InRange(renderer.Pixels[center + 1], 17, 19);
        Assert.InRange(renderer.Pixels[center], 29, 31);
        for (var index = 0; index < renderer.Pixels.Length; index += 4)
        {
            var alpha = renderer.Pixels[index + 3];
            Assert.InRange(alpha, 0, 77);
            Assert.InRange(renderer.Pixels[index], 0, alpha);
            Assert.InRange(renderer.Pixels[index + 1], 0, alpha);
            Assert.InRange(renderer.Pixels[index + 2], 0, alpha);
        }

        renderer.Render([], 1, Colors.White);
        Assert.All(renderer.Pixels, value => Assert.Equal(0, value));
        renderer.Render([particle], 0, Colors.White);
        Assert.All(renderer.Pixels, value => Assert.Equal(0, value));
        renderer.Render([particle], 1, Colors.Transparent);
        Assert.All(renderer.Pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ColorChangesOnlyTheSpriteColorAndDoesNotAccumulateOldFrames()
    {
        var renderer = new AmbientBackdropRasterizer(65, 65, particlesOnly: true);
        var particle = new AmbientParticle(new AmbientPoint(32.5, 32.5), 8, 0.1, 0.4, 0.54, 0.60, 0.60);
        renderer.Render([particle, particle, particle], 1, Colors.Red);
        var red = renderer.Pixels.ToArray();
        renderer.Render([particle, particle, particle], 1, Colors.Blue);
        for (var index = 0; index < renderer.Pixels.Length; index += 4)
        {
            Assert.Equal(red[index + 3], renderer.Pixels[index + 3]);
            Assert.Equal(red[index + 2], renderer.Pixels[index]);
            Assert.Equal(0, renderer.Pixels[index + 2]);
            Assert.Equal(0, renderer.Pixels[index + 1]);
        }
        var center = (32 * 65 + 32) * 4;
        Assert.InRange(renderer.Pixels[center + 3] / 255d, 0.267, 0.275);
    }

    [Fact]
    public void TransparentFramesHaveABoundedBufferAndReuseItWithoutAllocating()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AmbientBackdropRasterizer(1000, 401, particlesOnly: true));
        var renderer = new AmbientBackdropRasterizer(128, 80, particlesOnly: true);
        var particle = new AmbientParticle(new AmbientPoint(32.5, 32.5), 8, 0.2, 0.4, 1, 1, 1);
        var buffer = renderer.Pixels;
        renderer.Render([particle], 1, Colors.Cyan);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10; index++)
            renderer.Render([particle], 1, Colors.Cyan);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Assert.Same(buffer, renderer.Pixels);
    }

    [Fact]
    public void ParticleControlCannotSubscribeToPointerAndDetachesWithTheWindow() => OnSta(() =>
    {
        var control = new AmbientBackdrop { ParticlesOnly = true, IsWinterModeEnabled = false, ParticleColor = Colors.Cyan };
        var host = new Window { Content = control, ShowActivated = false, ShowInTaskbar = false };
        control.Measure(new Size(3840, 2160));
        control.Arrange(new Rect(0, 0, 3840, 2160));
        try
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.True(control.HasEnvironmentSubscriptions);
            Assert.True(control.HasHostWindow);
            Assert.False(control.HasPointerSubscriptions);
            Assert.False(control.HasAnimationTickSubscription);
            Assert.False(control.IsHitTestVisible);
            Assert.False(control.Focusable);
            var visual = Assert.IsType<DrawingVisual>(VisualTreeHelper.GetChild(control, 0));
            Assert.NotEmpty(visual.Drawing.Children);
            foreach (var child in visual.Drawing.Children)
            {
                var sprite = Assert.IsType<ImageDrawing>(child);
                var image = Assert.IsAssignableFrom<BitmapSource>(sprite.ImageSource);
                Assert.True(image.IsFrozen);
                Assert.InRange(image.PixelWidth, 16, AmbientParticleSprites.MaximumSpritePixels);
                Assert.InRange(sprite.Rect.Width, 0, 48);
            }

            control.IsPointerInteractionEnabled = false;
            control.IsPointerInteractionEnabled = true;
            Assert.False(control.HasPointerSubscriptions);
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.False(control.HasHostWindow);
            Assert.False(control.HasEnvironmentSubscriptions);
            Assert.False(control.HasAnimationTickSubscription);
        }
        finally
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            host.Content = null;
            host.Close();
        }
    });

    [Fact]
    public void ChangingParticleModeKeepsInstallerDefaultsAndRebuildsAlphaCorrectly() => OnSta(() =>
    {
        var control = new AmbientBackdrop();
        var host = new Window { Content = control, ShowActivated = false, ShowInTaskbar = false };
        control.Measure(new Size(128, 80));
        control.Arrange(new Rect(0, 0, 128, 80));
        try
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.False(control.ParticlesOnly);
            Assert.True(control.IsPointerInteractionEnabled);
            Assert.True(control.HasPointerSubscriptions);
            var opaque = CopyFrame(control);
            Assert.All(opaque.Where((_, index) => index % 4 == 3), alpha => Assert.Equal(255, alpha));

            control.ParticlesOnly = true;
            Assert.False(control.HasPointerSubscriptions);
            Assert.Contains((byte)0, CopyFrame(control).Where((_, index) => index % 4 == 3));
            control.ParticlesOnly = false;
            Assert.True(control.HasPointerSubscriptions);
            Assert.Equal(opaque, CopyFrame(control));
        }
        finally
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            host.Content = null;
            host.Close();
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WinterModeSwitchesImmediatelyAndRestoresBothStillFrames(bool particlesOnly) => OnSta(() =>
    {
        var control = new AmbientBackdrop { ParticlesOnly = particlesOnly, IsAnimationEnabled = false };
        control.Measure(new Size(512, 320));
        control.Arrange(new Rect(0, 0, 512, 320));
        Assert.True(control.IsWinterModeEnabled);
        var snow = CopyFrame(control);
        control.IsWinterModeEnabled = false;
        var wisps = CopyFrame(control);
        Assert.False(wisps.SequenceEqual(snow));
        control.IsWinterModeEnabled = true;
        Assert.Equal(snow, CopyFrame(control));
        control.IsWinterModeEnabled = false;
        Assert.Equal(wisps, CopyFrame(control));
        Assert.False(control.HasAnimationTickSubscription);
        Assert.Equal(0, control.SceneTimeSeconds);
    });

    [Fact]
    public void SnowUsesItsOwnBlueDefaultAndRespectsTransparentAndCustomColors() => OnSta(() =>
    {
        var control = new AmbientBackdrop { ParticlesOnly = true, IsAnimationEnabled = false, ParticleColor = Colors.Red };
        control.Measure(new Size(512, 320));
        control.Arrange(new Rect(0, 0, 512, 320));
        Assert.Equal(ColorCustomization.DefaultWinterParticleColor, control.WinterParticleColor);
        var blue = CopyFrame(control);
        Assert.True(ChannelTotal(blue, 0) > ChannelTotal(blue, 2));
        control.WinterParticleColor = Colors.Transparent;
        Assert.Equal(0, ChannelTotal(CopyFrame(control), 3));
        control.IsWinterModeEnabled = false;
        var classic = CopyFrame(control);
        Assert.True(ChannelTotal(classic, 2) > 0);
        Assert.Equal(0, ChannelTotal(classic, 0));
        control.WinterParticleColor = Colors.Lime;
        control.IsWinterModeEnabled = true;
        var green = CopyFrame(control);
        Assert.True(ChannelTotal(green, 1) > 0);
        Assert.Equal(0, ChannelTotal(green, 0));
        Assert.Equal(0, ChannelTotal(green, 2));

        static long ChannelTotal(byte[] pixels, int channel)
        {
            long total = 0;
            for (var i = channel; i < pixels.Length; i += 4)
                total += pixels[i];
            return total;
        }
    });

    [Fact]
    public void SnowFallsAtDifferentSpeedsAndReusesFrozenCrystalDrawings() => OnSta(() =>
    {
        var snow = new AmbientSnowfall(7919);
        var first = SnowFrame(snow, 0, Colors.White);
        var same = SnowFrame(snow, 0, Colors.White);
        var later = SnowFrame(snow, 0.25, Colors.White).ToDictionary(flake => flake.Rect.Width);
        Assert.NotEmpty(first);
        Assert.Equal(first, same);
        Assert.All(first, image => Assert.InRange(image.Rect.Width, 3.8, 16));
        Assert.True(first.Count(image => image.Rect.Width <= 11) > first.Length / 2);
        Assert.True(first.Select(image => Math.Round(image.Rect.Width)).Distinct().Count() > 3);
        var speeds = new HashSet<double>();
        var rotated = 0;
        foreach (var image in first)
        {
            Assert.True(image.Source.IsFrozen);
            if (!later.TryGetValue(image.Rect.Width, out var next)) continue;
            var vertical = next.Rect.Y - image.Rect.Y;
            Assert.True(vertical > 0);
            speeds.Add(Math.Round(vertical));
            if (!ReferenceEquals(image.Source, next.Source))
                rotated++;
        }
        Assert.True(speeds.Count > 3 && rotated > 3);
        Assert.Equal(first, SnowFrame(snow, 0, Colors.White));
        Assert.Empty(SnowFrame(snow, 0, Colors.Transparent));
        Assert.NotSame(first[0].Source, SnowFrame(snow, 0, Colors.Coral)[0].Source);
    });

    [Fact]
    public void SnowEmissionsAreSparseAndFadeIndependentlyAtDifferentHeights() => OnSta(() =>
    {
        var snow = new AmbientSnowfall(2711);
        var previous = SnowFrame(snow, 0, Colors.White).ToDictionary(flake => flake.Rect.Width);
        var meltHeights = new List<double>();
        var births = 0;
        var lowerFlakes = new HashSet<double>();
        var activeTotal = 0;
        var framesWithOpposingFades = 0;
        for (var step = 1; step <= 160; step++)
        {
            var current = SnowFrame(snow, step * 0.25, Colors.White).ToDictionary(flake => flake.Rect.Width);
            activeTotal += current.Count;
            bool fadingIn = false, fadingOut = false;
            foreach (var (width, flake) in current)
            {
                if (flake.Rect.Top > 730 && flake.Opacity > 0.08) lowerFlakes.Add(width);
                if (previous.TryGetValue(width, out var last))
                {
                    var delta = flake.Opacity - last.Opacity;
                    Assert.InRange(Math.Abs(delta), 0, 0.55);
                    fadingIn |= delta > 0.02;
                    fadingOut |= delta < -0.02;
                }
                else if (Interior(flake.Rect))
                {
                    Assert.InRange(flake.Opacity, 0, 0.35);
                    births++;
                }
            }
            foreach (var (width, flake) in previous)
            {
                if (current.ContainsKey(width) || !Interior(flake.Rect)) continue;
                Assert.InRange(flake.Opacity, 0, 0.18);
                meltHeights.Add(flake.Rect.Top);
            }
            if (fadingIn && fadingOut) framesWithOpposingFades++;
            previous = current;
        }

        // The previous full-height renderer submitted about 125 crystals in this viewport.
        Assert.InRange(activeTotal / 160d, 32, 48);
        Assert.True(births > 20 && meltHeights.Count > 20);
        Assert.True(meltHeights.Max() - meltHeights.Min() > 240);
        Assert.True(lowerFlakes.Count >= 2);
        Assert.True(framesWithOpposingFades > 80);

        static bool Interior(Rect rect) => rect.Left > 12 && rect.Right < 1268 && rect.Top > 12 && rect.Bottom < 780;
    });

    private static (Rect Rect, ImageSource Source, double Opacity)[] SnowFrame(AmbientSnowfall snow, double seconds, Color color)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            snow.Draw(drawing, new Size(1280, 800), seconds, color, 1);
        if (visual.Drawing is not { } frame) return [];
        return frame.Children.Select(command =>
        {
            var opacity = 1d;
            if (command is DrawingGroup group)
            {
                opacity = group.Opacity;
                command = Assert.Single(group.Children);
            }
            var image = Assert.IsType<ImageDrawing>(command);
            return (image.Rect, image.ImageSource, opacity);
        }).ToArray();
    }

    private static byte[] CopyFrame(AmbientBackdrop control)
    {
        var image = new RenderTargetBitmap((int)control.ActualWidth, (int)control.ActualHeight,
            96, 96, PixelFormats.Pbgra32);
        image.Render(control);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }

    [Theory]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(240)]
    public void FrameGateCarriesRefreshRemaindersWithoutDuplicateOrCatchUpFrames(int refreshRate)
    {
        var gate = new AmbientParticleFrameGate();
        var draws = 0;
        for (var frame = 0; frame < refreshRate * 10; frame++)
        {
            var timestamp = TimeSpan.FromTicks((long)Math.Round(frame * (double)TimeSpan.TicksPerSecond / refreshRate));
            if (gate.ShouldDraw(timestamp)) draws++;
            Assert.False(gate.ShouldDraw(timestamp));
        }
        Assert.InRange(draws, 599, 600);
        Assert.True(gate.ShouldDraw(TimeSpan.FromHours(1)));
        Assert.False(gate.ShouldDraw(TimeSpan.FromHours(1)));
        gate.Reset();
        Assert.True(gate.ShouldDraw(TimeSpan.Zero));
    }

    [Theory]
    [InlineData(59.94, 0, 0, 16.6832, 16.6835)]
    [InlineData(59.94, 0.1, 0, 16.4832, 16.8835)]
    [InlineData(59.94, 0, 1, 16, 17)]
    [InlineData(60, 0, 0, 16.6665, 16.667)]
    [InlineData(60, 0.1, 0, 16.4665, 16.867)]
    [InlineData(90, 0.1, 0, 10.911, 22.223)]
    [InlineData(120, 0.1, 0, 16.6665, 16.667)]
    [InlineData(144, 0.1, 0, 13.8887, 21.034)]
    [InlineData(165, 0.1, 0, 12.1211, 18.382)]
    [InlineData(240, 0.1, 0, 16.6665, 16.667)]
    public void FrameGateToleratesTimingVariationWithoutLosingItsBudgetDuringLongSessions(
        double refreshRate, double jitterMilliseconds, double quantumMilliseconds,
        double minimumIntervalMilliseconds, double maximumIntervalMilliseconds)
    {
        const int durationSeconds = 90 * 60;
        foreach (var originSeconds in new[] { 0, durationSeconds })
        {
            var gate = new AmbientParticleFrameGate();
            var callbacks = (int)Math.Ceiling(refreshRate * durationSeconds);
            var quantumTicks = (long)Math.Round(quantumMilliseconds * TimeSpan.TicksPerMillisecond);
            var draws = 0;
            long? previousDraw = null;
            var minimumInterval = double.PositiveInfinity;
            var maximumInterval = 0d;
            for (var frame = 0; frame < callbacks; frame++)
            {
                var jitter = (frame % 2 == 0 ? jitterMilliseconds : -jitterMilliseconds) / 1000;
                var ticks = (long)Math.Round((originSeconds + 0.01 + frame / refreshRate + jitter) *
                    TimeSpan.TicksPerSecond);
                if (quantumTicks > 0)
                    ticks = (long)Math.Round(ticks / (double)quantumTicks) * quantumTicks;
                if (!gate.ShouldDraw(TimeSpan.FromTicks(ticks)))
                    continue;
                draws++;
                if (previousDraw is { } previous)
                {
                    var interval = (ticks - previous) / (double)TimeSpan.TicksPerMillisecond;
                    minimumInterval = Math.Min(minimumInterval, interval);
                    maximumInterval = Math.Max(maximumInterval, interval);
                }
                previousDraw = ticks;
            }
            var expectedDraws = (int)Math.Round(Math.Min(refreshRate, 60) * durationSeconds);
            Assert.InRange(draws, expectedDraws - 1, expectedDraws + 1);
            Assert.InRange(minimumInterval, minimumIntervalMilliseconds, maximumIntervalMilliseconds);
            Assert.InRange(maximumInterval, minimumIntervalMilliseconds, maximumIntervalMilliseconds);
        }
    }

    [Fact]
    public void FrameGateRejectsDuplicateAndBackwardTimesAndDoesNotCatchUpAfterStalls()
    {
        var gate = new AmbientParticleFrameGate();
        Assert.False(gate.ShouldDraw(TimeSpan.FromTicks(-1)));
        Assert.True(gate.ShouldDraw(TimeSpan.Zero));
        Assert.False(gate.ShouldDraw(TimeSpan.Zero));
        Assert.False(gate.ShouldDraw(TimeSpan.FromMilliseconds(1)));
        Assert.False(gate.ShouldDraw(TimeSpan.FromMilliseconds(0.5)));
        Assert.True(gate.ShouldDraw(TimeSpan.FromMilliseconds(30)));
        Assert.False(gate.ShouldDraw(TimeSpan.FromMilliseconds(30.0001)));
        Assert.False(gate.ShouldDraw(TimeSpan.FromMilliseconds(35)));

        var resumed = TimeSpan.FromMinutes(90);
        Assert.True(gate.ShouldDraw(resumed));
        Assert.False(gate.ShouldDraw(resumed));
        Assert.False(gate.ShouldDraw(resumed - TimeSpan.FromMilliseconds(1)));
        Assert.False(gate.ShouldDraw(resumed + TimeSpan.FromTicks(1)));
        Assert.False(gate.ShouldDraw(resumed + TimeSpan.FromMilliseconds(8)));
        Assert.True(gate.ShouldDraw(resumed + TimeSpan.FromMilliseconds(16.667)));
        gate.Reset();
        Assert.True(gate.ShouldDraw(TimeSpan.Zero));
    }

    [Fact]
    public void DisablingParticleAnimationAndUnloadingDetachCompositionHandler() => OnSta(() =>
    {
        var control = new AmbientBackdrop { ParticlesOnly = true };
        var start = typeof(AmbientBackdrop).GetMethod("SetAnimationRunning",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        start.Invoke(control, [true]);
        Assert.True(control.HasAnimationTickSubscription);
        control.IsAnimationEnabled = false;
        Assert.False(control.HasAnimationTickSubscription);
        Assert.False(control.IsAnimationRunning);
        start.Invoke(control, [true]);
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.False(control.HasAnimationTickSubscription);
        Assert.False(control.IsAnimationRunning);
    });

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Particle backdrop STA check timed out.");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
