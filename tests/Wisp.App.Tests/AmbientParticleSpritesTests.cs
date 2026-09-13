using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AmbientParticleSpritesTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    [InlineData(3)]
    public void DrawsNativeResolutionSpritesWithoutAnUpscaledCanvasOrOpacityLayers(double dpi) => OnSta(() =>
    {
        var renderer = new AmbientParticleSprites();
        AmbientParticle[] particles =
        [
            Particle(64.125, 80.625, 1.3, 0.32),
            Particle(500.625, 620.375, 9, 0.18),
            Particle(2200.375, 1200.125, 21, 0.08)
        ];
        var visual = Draw(renderer, particles, new Size(3840, 2160), Colors.Cyan, 1, dpi);
        Assert.Equal(particles.Length, visual.Drawing.Children.Count);
        for (var index = 0; index < particles.Length; index++)
        {
            var drawing = Assert.IsType<ImageDrawing>(visual.Drawing.Children[index]);
            var source = Assert.IsAssignableFrom<BitmapSource>(drawing.ImageSource);
            Assert.True(source.IsFrozen);
            Assert.True(source.PixelWidth >= drawing.Rect.Width * dpi);
            Assert.InRange(source.PixelWidth, 16, AmbientParticleSprites.MaximumSpritePixels);
            Assert.Equal(particles[index].Position.X, drawing.Rect.X + drawing.Rect.Width / 2, 8);
            Assert.Equal(particles[index].Position.Y, drawing.Rect.Y + drawing.Rect.Height / 2, 8);
            Assert.Equal(particles[index].Radius * 2,
                drawing.Rect.Width * (source.PixelWidth - 2) / source.PixelWidth, 8);
        }
    });

    [Fact]
    public void TextureResolutionChangesDoNotChangeTheProjectedParticleRadius() => OnSta(() =>
    {
        var renderer = new AmbientParticleSprites();
        var smaller = Assert.IsType<ImageDrawing>(Assert.Single(Draw(renderer,
            [Particle(40.125, 40.25, 6.99, 0.3)], new Size(100, 100), Colors.Cyan).Drawing.Children));
        var larger = Assert.IsType<ImageDrawing>(Assert.Single(Draw(renderer,
            [Particle(40.125, 40.25, 7.01, 0.3)], new Size(100, 100), Colors.Cyan).Drawing.Children));
        var smallerSource = (BitmapSource)smaller.ImageSource;
        var largerSource = (BitmapSource)larger.ImageSource;
        Assert.Equal(16, smallerSource.PixelWidth);
        Assert.Equal(32, largerSource.PixelWidth);
        var smallerVisibleDiameter = smaller.Rect.Width * (smallerSource.PixelWidth - 2) / smallerSource.PixelWidth;
        var largerVisibleDiameter = larger.Rect.Width * (largerSource.PixelWidth - 2) / largerSource.PixelWidth;
        Assert.Equal(6.99 * 2, smallerVisibleDiameter, 8);
        Assert.Equal(7.01 * 2, largerVisibleDiameter, 8);
        Assert.Equal(0.04, largerVisibleDiameter - smallerVisibleDiameter, 8);
    });

    [Fact]
    public void ReusesFrozenSpritesWhileKeepingFractionalMotionAndFineOpacityChanges() => OnSta(() =>
    {
        var renderer = new AmbientParticleSprites();
        var particle = Particle(40.125, 40.25, 8, 0.10);
        var first = Assert.IsType<ImageDrawing>(Assert.Single(
            Draw(renderer, [particle], new Size(100, 100), Colors.White).Drawing.Children));
        var moved = Assert.IsType<ImageDrawing>(Assert.Single(Draw(renderer,
            [particle with { Position = new AmbientPoint(40.375, 40.5) }],
            new Size(100, 100), Colors.White).Drawing.Children));
        Assert.Same(first.ImageSource, moved.ImageSource);
        Assert.Equal(0.25, moved.Rect.X - first.Rect.X, 8);
        Assert.Equal(0.25, moved.Rect.Y - first.Rect.Y, 8);
        Assert.Equal(1, renderer.CreatedSpriteCount);

        var faded = Assert.IsType<ImageDrawing>(Assert.Single(Draw(renderer,
            [particle with { Opacity = 0.104 }], new Size(100, 100), Colors.White).Drawing.Children));
        Assert.NotSame(first.ImageSource, faded.ImageSource);
        Assert.NotEqual(Pixels((BitmapSource)first.ImageSource), Pixels((BitmapSource)faded.ImageSource));
        Assert.Equal(2, renderer.CreatedSpriteCount);
    });

    [Fact]
    public void PreservesTransparentEdgesAndPremultipliedColorWithoutAWindowBackgroundFill() => OnSta(() =>
    {
        var renderer = new AmbientParticleSprites();
        var particle = Particle(32, 32, 12, 0.3);
        var first = Assert.IsType<ImageDrawing>(Assert.Single(Draw(renderer,
            [particle], new Size(64, 64), Color.FromRgb(230, 60, 100)).Drawing.Children));
        var source = (BitmapSource)first.ImageSource;
        var pixels = Pixels(source);
        Assert.Equal(PixelFormats.Pbgra32, source.Format);
        Assert.All(pixels.Take(source.PixelWidth * 4), value => Assert.Equal(0, value));
        Assert.All(pixels.TakeLast(source.PixelWidth * 4), value => Assert.Equal(0, value));
        Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];
            Assert.InRange(alpha, 0, 77);
            Assert.Equal((230 * alpha + 127) / 255, pixels[index + 2]);
            Assert.Equal((60 * alpha + 127) / 255, pixels[index + 1]);
            Assert.Equal((100 * alpha + 127) / 255, pixels[index]);
        }

        var coverageBytes = renderer.CachedCoverageBytes;
        var changed = Assert.IsType<ImageDrawing>(Assert.Single(Draw(renderer,
            [particle], new Size(64, 64), Colors.Blue).Drawing.Children));
        Assert.NotSame(source, changed.ImageSource);
        Assert.Equal(coverageBytes, renderer.CachedCoverageBytes);
        Assert.Equal(1, renderer.CachedSpriteCount);
        Assert.Null(Draw(renderer, [particle], new Size(64, 64), Colors.Transparent).Drawing);
        Assert.Null(Draw(renderer, [particle], new Size(64, 64), Colors.Blue, 0).Drawing);
    });

    [Fact]
    public void IgnoresInvisibleOrMalformedParticlesBeforeCreatingTextures() => OnSta(() =>
    {
        var renderer = new AmbientParticleSprites();
        var particle = Particle(32, 32, 4, 0.3);
        var visual = Draw(renderer,
        [
            particle with { Position = new AmbientPoint(-20, -20) },
            particle with { Position = new AmbientPoint(double.NaN, 20) },
            particle with { Radius = 20000 },
            particle with { Radius = double.PositiveInfinity },
            particle with { Opacity = double.NaN },
            particle with { Opacity = 0 },
            particle with { Softness = double.NaN }
        ], new Size(64, 64), Colors.Cyan);
        Assert.Null(visual.Drawing);
        Assert.Equal(0, renderer.CreatedSpriteCount);
        Assert.Null(Draw(renderer, [particle], new Size(64, 64), Colors.Cyan, double.NaN).Drawing);
    });

    [Fact]
    public void CacheStaysBoundedAcrossLargeDpiSpritesAndManyFadingParticles() => OnSta(() =>
    {
        var renderer = new AmbientParticleSprites();
        for (var index = 1; index <= 40; index++)
        {
            Draw(renderer, [Particle(32, 32, 21, index / 255d)], new Size(64, 64), Colors.Cyan, 1, 4);
            Assert.InRange(renderer.CachedPixelBytes, 0, AmbientParticleSprites.MaximumCachedPixelBytes);
        }
        Assert.True(renderer.CachedSpriteCount < renderer.CreatedSpriteCount);
        for (var index = 1; index <= 1100; index++)
        {
            var particle = Particle(32, 32, 2, (index % 255 + 1) / 255d) with { Softness = index / 255 / 7d };
            Draw(renderer, [particle], new Size(64, 64), Colors.Cyan);
            Assert.InRange(renderer.CachedSpriteCount, 0, AmbientParticleSprites.MaximumCachedSprites);
            Assert.InRange(renderer.CachedPixelBytes, 0, AmbientParticleSprites.MaximumCachedPixelBytes);
            Assert.InRange(renderer.CachedCoverageBytes, 0, 1024 * 1024);
        }
        Assert.Equal(AmbientParticleSprites.MaximumCachedSprites, renderer.CachedSpriteCount);
    });

    private static AmbientParticle Particle(double x, double y, double radius, double opacity) =>
        new(new AmbientPoint(x, y), radius, opacity, 0.4, 1, 1, 1);

    private static DrawingVisual Draw(AmbientParticleSprites renderer, AmbientParticle[] particles,
        Size viewport, Color color, double intensity = 1, double dpi = 1)
    {
        var visual = new DrawingVisual();
        using var drawing = visual.RenderOpen();
        renderer.Draw(drawing, particles, viewport, color, intensity, new DpiScale(dpi, dpi));
        return visual;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Particle sprite STA check timed out.");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
