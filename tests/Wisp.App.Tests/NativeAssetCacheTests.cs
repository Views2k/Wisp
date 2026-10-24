using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeAssetCacheTests
{
    // Runs inside the WPF application check: pack URIs resolve only with an application present.
    internal static void AssertMissingResourceOnCurrentDispatcher()
    {
        // A tire readout below freezing asked for "HUD_Dial_Speed_Analogue_-.png", which is not a
        // resource; the cache answers with its placeholder and never throws on the dashboard thread.
        const string name = "HUD_Dial_Speed_Analogue_-.png";
        Assert.True(NativeAssetCache.IsPlaceholder(NativeAssetCache.Get(NativeGaugeMode.Analogue, name)));
        Assert.True(NativeAssetCache.IsPlaceholder(NativeAssetCache.Get(NativeGaugeMode.Analogue, name)));
        Assert.True(NativeAssetCache.IsPlaceholder(NativeAssetCache.GetTinted(NativeGaugeMode.Analogue, name, System.Windows.Media.Colors.White)));
    }

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
