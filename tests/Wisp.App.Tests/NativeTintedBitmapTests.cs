using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Wisp.App.Tests;

internal static class NativeTintedBitmapTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        TintPreservesStraightChannelsShadowAlphaAndSourceGeometry();
        RepeatedTintsReuseTheImageWithoutCompoundingOrChangingTheSource();
        SeparateInstancesNeverRecolorEachOther();
        NativeDigitsMatchExistingTintPixelsAndScaledRendering();
    }

    private static void TintPreservesStraightChannelsShadowAlphaAndSourceGeometry()
    {
        var source = CreateSource();
        var original = Pixels(source);
        var image = new NativeTintedBitmap(source).GetImage(Color.FromArgb(205, 255, 0, 136));

        Assert.Equal(
            new byte[]
            {
                0, 0, 0, 16, 136, 0, 255, 103, 21, 0, 200, 164,
                43, 0, 200, 0, 136, 0, 64, 205, 7, 0, 91, 1
            },
            Pixels(image));
        Assert.Equal(source.PixelWidth, image.PixelWidth);
        Assert.Equal(source.PixelHeight, image.PixelHeight);
        Assert.Equal(source.DpiX, image.DpiX);
        Assert.Equal(source.DpiY, image.DpiY);
        Assert.Equal(PixelFormats.Bgra32, image.Format);
        Assert.Equal(original, Pixels(source));
    }

    private static void RepeatedTintsReuseTheImageWithoutCompoundingOrChangingTheSource()
    {
        var source = CreateSource();
        var original = Pixels(source);
        var tinting = new NativeTintedBitmap(source);
        var image = tinting.GetImage(Colors.WhiteSmoke);
        Color[] colors = [Colors.WhiteSmoke, Color.FromArgb(0, 255, 20, 100),
            Color.FromArgb(102, 255, 255, 255), Color.FromArgb(205, 255, 0, 136)];
        for (var pass = 0; pass < 8; pass++)
            foreach (var color in colors)
            {
                Assert.Same(image, tinting.GetImage(color));
                var expected = original.ToArray();
                NativeAssetCache.MultiplyStraightBgraByColor(expected, color);
                Assert.Equal(expected, Pixels(image));
            }

        var changes = 0;
        image.Changed += (_, _) => changes++;
        Assert.Same(image, tinting.GetImage(colors[^1]));
        Assert.Same(image, tinting.GetImage(colors[^1]));
        Assert.Equal(0, changes);
        Assert.Equal(original, Pixels(source));
    }

    private static void SeparateInstancesNeverRecolorEachOther()
    {
        var source = CreateSource();
        var first = new NativeTintedBitmap(source);
        var second = new NativeTintedBitmap(source);
        var firstImage = first.GetImage(Colors.Magenta);
        var secondImage = second.GetImage(Colors.Cyan);
        var secondPixels = Pixels(secondImage);

        Assert.NotSame(firstImage, secondImage);
        Assert.Same(firstImage, first.GetImage(Color.FromArgb(175, 255, 180, 0)));
        Assert.Equal(secondPixels, Pixels(secondImage));
        var firstPixels = Pixels(firstImage);
        Assert.Same(secondImage, second.GetImage(Colors.WhiteSmoke));
        Assert.Equal(firstPixels, Pixels(firstImage));
    }

    private static void NativeDigitsMatchExistingTintPixelsAndScaledRendering()
    {
        Color[] colors = [Colors.WhiteSmoke, Color.FromArgb(175, 36, 120, 245),
            Color.FromArgb(231, 226, 70, 113)];
        for (var digit = '0'; digit <= '9'; digit++)
        {
            var name = $"HUD_Dial_Speed_Analogue_{digit}.png";
            var source = NativeAssetCache.Get(NativeGaugeMode.Analogue, name);
            var sourcePixels = Pixels(source);
            var tinting = new NativeTintedBitmap(source);
            BitmapSource? firstImage = null;
            foreach (var color in colors)
            {
                var expected = NativeAssetCache.GetTinted(NativeGaugeMode.Analogue, name, color);
                var actual = tinting.GetImage(color);
                firstImage ??= actual;
                Assert.Same(firstImage, actual);
                Assert.Equal(expected.PixelWidth, actual.PixelWidth);
                Assert.Equal(expected.PixelHeight, actual.PixelHeight);
                Assert.Equal(expected.DpiX, actual.DpiX);
                Assert.Equal(expected.DpiY, actual.DpiY);
                Assert.Equal(Pixels(expected), Pixels(actual));
                Assert.Equal(RenderReadout(expected), RenderReadout(actual));
            }
            Assert.Equal(sourcePixels, Pixels(source));
        }
    }

    private static BitmapSource CreateSource()
    {
        byte[] pixels =
        [
            0, 0, 0, 20, 255, 255, 255, 128, 40, 120, 200, 204,
            80, 10, 200, 0, 255, 128, 64, 255, 13, 57, 91, 1
        ];
        var source = BitmapSource.Create(3, 2, 144, 120, PixelFormats.Bgra32, null, pixels, 12);
        source.Freeze();
        return source;
    }

    internal static byte[] Pixels(BitmapSource image)
    {
        var stride = checked(image.PixelWidth * 4);
        var pixels = new byte[checked(stride * image.PixelHeight)];
        image.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static byte[] RenderReadout(BitmapSource image)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawImage(image, new Rect(7.25, 3.5, 18, 28));
        var bitmap = new RenderTargetBitmap(48, 56, 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return Pixels(bitmap);
    }
}
