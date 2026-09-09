using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.App.NativeRendering;

internal readonly record struct AnalogHudColor(byte R, byte G, byte B, byte A = 255);

internal sealed record AnalogHudTexture(
    uint Id, int Width, int Height, int Stride, ReadOnlyMemory<byte> Pixels);

internal sealed record AnalogHudAsset(
    uint Id, NativeAssetFamily Family, string FileName,
    AnalogHudColor? Tint = null, bool AlphaMask = false);

internal static class AnalogHudAssets
{
    internal static readonly AnalogHudColor NumberTint = new(255, 255, 255, 102);
    internal static readonly AnalogHudColor RedlineNumberTint = new(255, 0, 136);
    internal static readonly AnalogHudColor LitRedlineNumberTint = new(255, 0, 136, 205);
    private static readonly ReadOnlyCollection<AnalogHudAsset> Catalog = CreateCatalog().AsReadOnly();
    private static readonly Dictionary<(NativeAssetFamily, string, AnalogHudColor?, bool), uint> Ids =
        Catalog.ToDictionary(asset => (asset.Family, asset.FileName, asset.Tint, asset.AlphaMask), asset => asset.Id);
    private static IReadOnlyList<AnalogHudTexture>? _textures;

    internal static IReadOnlyList<AnalogHudAsset> Definitions => Catalog;

    internal static uint Id(
        NativeAssetFamily family, string fileName, AnalogHudColor? tint = null, bool alphaMask = false) =>
        Ids[(family, fileName, tint, alphaMask)];

    // The existing cache repairs the extracted assets' alpha exactly as the WPF
    // HUD does. Convert its result, rather than premultiplying the raw PNG again.
    internal static IReadOnlyList<AnalogHudTexture> LoadOnUiThread()
    {
        Application.Current?.Dispatcher.VerifyAccess();
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Native HUD assets must be decoded on the UI thread.");
        if (_textures is not null)
            return _textures;

        var textures = new List<AnalogHudTexture>(Catalog.Count);
        foreach (var asset in Catalog)
        {
            var source = asset.Tint is { } tint
                ? NativeAssetCache.GetTinted(asset.Family, asset.FileName, Color.FromArgb(tint.A, tint.R, tint.G, tint.B))
                : NativeAssetCache.Get(asset.Family, asset.FileName);
            var premultiplied = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            premultiplied.Freeze();
            var stride = checked(premultiplied.PixelWidth * 4);
            var pixels = new byte[checked(stride * premultiplied.PixelHeight)];
            premultiplied.CopyPixels(pixels, stride, 0);
            if (asset.AlphaMask)
                MakeWhiteAlphaMask(pixels);
            textures.Add(new AnalogHudTexture(asset.Id, premultiplied.PixelWidth, premultiplied.PixelHeight, stride, pixels));
        }

        _textures = textures.AsReadOnly();
        return _textures;
    }

    internal static void MakeWhiteAlphaMask(Span<byte> pixels)
    {
        if (pixels.Length % 4 != 0)
            throw new ArgumentException("BGRA data must contain complete pixels.", nameof(pixels));
        for (var offset = 0; offset < pixels.Length; offset += 4)
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3];
    }

    private static List<AnalogHudAsset> CreateCatalog()
    {
        var assets = new List<AnalogHudAsset>();
        void Add(NativeAssetFamily family, string file, AnalogHudColor? tint = null, bool mask = false) =>
            assets.Add(new AnalogHudAsset((uint)assets.Count + 1, family, file, tint, mask));

        for (var digit = 0; digit < 10; digit++)
        {
            Add(NativeAssetFamily.Analogue, $"HUD_Dial_Speed_Analogue_{digit}.png");
            Add(NativeAssetFamily.Analogue, $"HUD_Dial_Speed_Analogue_{digit}.png", mask: true);
        }
        Add(NativeAssetFamily.Analogue, "HUD_Dial_Unit_MPH.png");
        Add(NativeAssetFamily.Analogue, "HUD_Dial_Unit_KPH.png");
        for (var number = 0; number <= 30; number++)
        {
            Add(NativeAssetFamily.Analogue, $"HUD_Dial_RevNumbers_{number}.png", NumberTint);
            Add(NativeAssetFamily.Analogue, $"HUD_Dial_RevNumbers_{number}.png", RedlineNumberTint);
            Add(NativeAssetFamily.Analogue, $"HUD_Dial_RevNumbers_{number}.png", LitRedlineNumberTint);
        }
        foreach (var neutral in new[] { "N", "R" })
            Add(NativeAssetFamily.Analogue, $"HUD_Dial_Analog_Gear_{neutral}.png");
        for (var gear = 1; gear <= 10; gear++)
            foreach (var state in new[] { "", "Redline_", "Redline_glow_" })
                Add(NativeAssetFamily.Analogue, $"HUD_Dial_Analog_Gear_{state}{gear}.png");
        foreach (var state in new[] { "", "Redline_", "Redline_glow_" })
            Add(NativeAssetFamily.Digital, $"HUD_Dial_Digital_Gear_{state}Drive.png");
        foreach (var assist in new[] { "ABS", "TCR", "LC", "STM" })
            foreach (var state in new[] { "Off", "On", "On_glow" })
                Add(NativeAssetFamily.Analogue, $"HUD_Dial_Assist_Analogue_{assist}_{state}.png");
        return assets;
    }
}
