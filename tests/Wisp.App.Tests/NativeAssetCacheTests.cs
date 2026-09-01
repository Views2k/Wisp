using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeAssetCacheTests
{
    [Fact]
    public void NativePremultipliedSamplesAreRestoredWithoutChangingAlpha()
    {
        byte[] pixel = [90, 90, 90, 128];

        NativeAssetCache.UnpremultiplyBgra(pixel);

        Assert.Equal([179, 179, 179, 128], pixel);
    }

    [Fact]
    public void NativeColorTintPreservesBlackShadowRgbAndMultipliesAlpha()
    {
        byte[] pixels =
        [
            0,
            0,
            0,
            20,
            255,
            255,
            255,
            128
        ];

        NativeAssetCache.MultiplyStraightBgraByColor(
            pixels,
            System.Windows.Media.Color.FromArgb(102, 255, 255, 255));

        Assert.Equal(
            [
                0,
                0,
                0,
                8,
                255,
                255,
                255,
                51
            ],
            pixels);
    }

    [Fact]
    public void NativeMagentaTintPreservesShadowAndMapsWhiteGlyphRgb()
    {
        byte[] pixels =
        [
            0,
            0,
            0,
            20,
            255,
            255,
            255,
            128
        ];

        NativeAssetCache.MultiplyStraightBgraByColor(
            pixels,
            System.Windows.Media.Color.FromArgb(205, 255, 0, 136));

        Assert.Equal(
            [
                0,
                0,
                0,
                16,
                136,
                0,
                255,
                103
            ],
            pixels);
    }
}
