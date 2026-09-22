using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.App.NativeRendering;

internal static class DigitalHudAssets
{
    internal const uint WhiteTextureId = 50_000;
    private static readonly ReadOnlyCollection<AnalogHudAsset> Catalog = CreateCatalog().AsReadOnly();
    private static readonly Dictionary<(NativeAssetFamily, string, AnalogHudColor?, bool), uint> Ids =
        Catalog.ToDictionary(asset => (asset.Family, asset.FileName, asset.Tint, asset.AlphaMask), asset => asset.Id);
    private static IReadOnlyList<AnalogHudTexture>? _textures;

    internal static IReadOnlyList<AnalogHudAsset> Definitions => Catalog;
    internal static uint Id(NativeAssetFamily family, string fileName, AnalogHudColor? tint = null, bool alphaMask = false) =>
        Ids[(family, fileName, tint, alphaMask)];

    internal static IReadOnlyList<AnalogHudTexture> LoadOnUiThread()
    {
        Application.Current?.Dispatcher.VerifyAccess();
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Native HUD assets must be decoded on the UI thread.");
        if (_textures is not null) return _textures;
        var textures = new List<AnalogHudTexture>(Catalog.Count + 2)
        {
            new(WhiteTextureId, 1, 1, 4, new byte[] { 255, 255, 255, 255 }),
            ShiftCueArtwork.Texture(digital: true)
        };
        foreach (var asset in Catalog)
        {
            var source = asset.Tint is { } tint
                ? NativeAssetCache.GetTinted(asset.Family, asset.FileName, Color.FromArgb(tint.A, tint.R, tint.G, tint.B))
                : NativeAssetCache.Get(asset.Family, asset.FileName);
            var bitmap = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            bitmap.Freeze();
            var stride = checked(bitmap.PixelWidth * 4);
            var pixels = new byte[checked(stride * bitmap.PixelHeight)];
            bitmap.CopyPixels(pixels, stride, 0);
            if (asset.AlphaMask) AnalogHudAssets.MakeWhiteAlphaMask(pixels);
            textures.Add(new(asset.Id, bitmap.PixelWidth, bitmap.PixelHeight, stride, pixels));
        }
        return _textures = textures.AsReadOnly();
    }

    private static List<AnalogHudAsset> CreateCatalog()
    {
        var assets = new List<AnalogHudAsset>();
        void Add(NativeAssetFamily family, string file, AnalogHudColor? tint = null, bool mask = false) =>
            assets.Add(new((uint)assets.Count + WhiteTextureId + 1, family, file, tint, mask));
        foreach (var gear in new[] { "N", "R" })
            Add(NativeAssetFamily.Digital, $"HUD_Dial_Digital_Gear_{gear}.png");
        foreach (var gear in Enumerable.Range(1, 10).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("Drive"))
            foreach (var state in new[] { "", "Redline_", "Redline_glow_" })
                Add(NativeAssetFamily.Digital, $"HUD_Dial_Digital_Gear_{state}{gear}.png");
        for (var digit = 0; digit < 10; digit++)
        {
            Add(NativeAssetFamily.Digital, $"HUD_Dial_Speed_Digital_{digit}.png");
            Add(NativeAssetFamily.Digital, $"HUD_Dial_Speed_Digital_{digit}.png", mask: true);
        }
        foreach (var unit in new[] { "MPH", "KPH" })
            Add(NativeAssetFamily.Digital, $"HUD_Dial_Unit_Digital_{unit}.png");
        foreach (var assist in new[] { "ABS", "TCR", "LC", "STM" })
            foreach (var state in new[] { "Off", "On", "On_glow" })
                Add(NativeAssetFamily.Digital, $"HUD_Dial_Assist_Digital_{assist}_{state}.png");
        foreach (var token in new[] { "Reverse", "Drive", "1", "2", "3", "4" })
            Add(NativeAssetFamily.Electric, $"HUD_EV_Gear_{token}.png");
        foreach (var state in new[] { "0bar", "1bar", "2bar", "3bar", "max" })
            Add(NativeAssetFamily.Electric, $"HUD_EV_Digital_Bar_{state}.png");
        Add(NativeAssetFamily.Electric, "HUD_EV_RGN.png");
        Add(NativeAssetFamily.Electric, "HUD_EV_PWR.png");
        return assets;
    }
}
