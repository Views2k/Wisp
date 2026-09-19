using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.App.NativeRendering;

internal static class ElectricHudAssets
{
    internal const uint WhiteTextureId = 10_000;
    internal static readonly AnalogHudColor NumberTint = new(255, 255, 255, 102);
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
        var textures = new List<AnalogHudTexture>(Catalog.Count + 1)
        {
            new(WhiteTextureId, 1, 1, 4, new byte[] { 255, 255, 255, 255 })
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
        Add(NativeAssetFamily.Electric, "SpeedDial.png");
        for (var index = 0; index <= 4; index++) Add(NativeAssetFamily.Electric, $"GearGauge{index}.png");
        foreach (var value in Enumerable.Range(0, 9).Select(index => index * 30)
                     .Concat(Enumerable.Range(0, 9).Select(index => index * 50)).Distinct())
            Add(NativeAssetFamily.Electric, $"HUD_EV_Dial_Speed{value}.png", NumberTint);
        foreach (var token in new[] { "Reverse", "Drive", "1", "2", "3", "4" })
        {
            Add(NativeAssetFamily.Electric, $"HUD_EV_Gear_{token}.png");
            Add(NativeAssetFamily.Electric, $"HUD_EV_Gear_Small_{token}.png");
        }
        foreach (var file in new[] { "HUD_EV_Gear_Arc.png", "HUD_EV_RGN.png", "HUD_EV_PWR.png" })
            Add(NativeAssetFamily.Electric, file);
        for (var digit = 0; digit < 10; digit++)
        {
            Add(NativeAssetFamily.Electric, $"HUD_EV_Speed{digit}.png");
            Add(NativeAssetFamily.Electric, $"HUD_EV_Speed{digit}.png", mask: true);
        }
        Add(NativeAssetFamily.Analogue, "HUD_Dial_Unit_MPH.png");
        Add(NativeAssetFamily.Analogue, "HUD_Dial_Unit_KPH.png");
        foreach (var assist in new[] { "ABS", "TCR", "LC", "STM" })
            foreach (var state in new[] { "Off", "On", "On_glow" })
                Add(NativeAssetFamily.Analogue, $"HUD_Dial_Assist_Analogue_{assist}_{state}.png");
        return assets;
    }
}
