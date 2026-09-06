using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AmbientBackdropRasterizerTests
{
    [Fact]
    public void BackgroundMatchesWebsiteSideLightWithUnbiasedEightBitRounding()
    {
        var renderer = new AmbientBackdropRasterizer(256, 160);
        renderer.Render([], 1);
        var error = new double[3];
        for (var y = 0; y < renderer.Height; y++)
            for (var x = 0; x < renderer.Width; x++)
            {
                var expected = BackgroundAt(x, y, renderer.Width, renderer.Height);
                var index = (y * renderer.Width + x) * 4;
                Assert.Equal(255, renderer.Pixels[index + 3]);
                for (var channel = 0; channel < 3; channel++)
                {
                    var difference = renderer.Pixels[index + 2 - channel] - expected[channel];
                    Assert.InRange(difference, -1.001, 1.001);
                    error[channel] += difference;
                }
            }
        Assert.All(error, sum => Assert.InRange(sum / (256 * 160), -0.02, 0.02));

        // A nearly flat dark patch contains neighboring quantization levels,
        // rather than a wide, uniformly rounded contour band.
        var patch = new HashSet<byte>();
        for (var y = 70; y < 86; y++)
            for (var x = 120; x < 136; x++)
                patch.Add(renderer.Pixels[(y * 256 + x) * 4 + 1]);
        Assert.True(patch.Count >= 2);
    }

    [Fact]
    public void OverlappingFaintSpritesAreCompositedBeforeQuantization()
    {
        var renderer = new AmbientBackdropRasterizer(65, 65);
        var particle = new AmbientParticle(new AmbientPoint(32.5, 32.5), 8, 0.0035, 0.4, 0.54, 0.60, 0.60);
        renderer.Render([particle, particle, particle], 1);
        var expected = BackgroundAt(32, 32, 65, 65);
        double[] color = [0.54 * 255, 0.60 * 255, 0.60 * 255];
        for (var repeat = 0; repeat < 3; repeat++)
            for (var channel = 0; channel < 3; channel++)
                expected[channel] += (color[channel] - expected[channel]) * particle.Opacity;
        var index = (32 * 65 + 32) * 4;
        for (var channel = 0; channel < 3; channel++)
            Assert.InRange(renderer.Pixels[index + 2 - channel] - expected[channel], -1.001, 1.001);
    }

    [Fact]
    public void RadialSpriteFadesWithoutAVisibleRectangleAndDisabledIntensityRetainsBackground()
    {
        var renderer = new AmbientBackdropRasterizer(65, 65);
        renderer.Render([], 1);
        var background = renderer.Pixels.ToArray();
        var particle = new AmbientParticle(new AmbientPoint(32.5, 32.5), 12, 0.3, 0.82, 0.5, 0.6, 0.6);
        renderer.Render([particle], 1);
        var center = (32 * 65 + 32) * 4;
        var middle = (32 * 65 + 38) * 4;
        var corner = (22 * 65 + 22) * 4;
        Assert.True(renderer.Pixels[center] > renderer.Pixels[middle]);
        Assert.True(renderer.Pixels[middle] > background[middle]);
        Assert.Equal(background.AsSpan(corner, 4).ToArray(), renderer.Pixels.AsSpan(corner, 4).ToArray());
        renderer.Render([particle], 0);
        Assert.Equal(background, renderer.Pixels);
    }

    [Fact]
    public void FramesReuseStorageAndStableNoiseWithoutLeavingTrails()
    {
        var renderer = new AmbientBackdropRasterizer(128, 80);
        var storage = renderer.Pixels;
        var particle = new AmbientParticle(new AmbientPoint(32.5, 32.5), 8, 0.2, 0.4, 0.54, 0.6, 0.6);
        renderer.Render([], 1);
        var background = storage.ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10; index++)
            renderer.Render([particle], 1);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        renderer.Render([], 1);
        Assert.Same(storage, renderer.Pixels);
        Assert.Equal(background, storage);
    }

    [Fact]
    public void AllocationIsBoundedAndMalformedParticlesCannotCorruptOutput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AmbientBackdropRasterizer(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AmbientBackdropRasterizer(2000, 2000));
        var renderer = new AmbientBackdropRasterizer(32, 32);
        renderer.Render([], 1);
        var expected = renderer.Pixels.ToArray();
        renderer.Render([
            new AmbientParticle(new AmbientPoint(double.NaN, 0), 8, 0.2, 0.4, 1, 1, 1),
            new AmbientParticle(new AmbientPoint(16, 16), double.PositiveInfinity, 0.2, 0.4, 1, 1, 1),
            new AmbientParticle(new AmbientPoint(16, 16), 8, double.NaN, 0.4, 1, 1, 1)
        ], 1);
        Assert.Equal(expected, renderer.Pixels);
    }

    private static double[] BackgroundAt(int x, int y, int width, int height)
    {
        var dx = ((x + 0.5) / width - 1.2) * (width / (double)height) * 0.58;
        var dy = (1 - (y + 0.5) / height - 0.42) * 0.8;
        var light = Math.Exp(-(dx * dx + dy * dy) * 1.24) * 0.135;
        return [9 + 10 * light, 13 + 30 * light, 18 + 26 * light];
    }
}
