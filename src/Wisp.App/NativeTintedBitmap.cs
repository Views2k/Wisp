using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.App;

internal sealed class NativeTintedBitmap
{
    private readonly WriteableBitmap _image;
    private readonly byte[] _sourcePixels;
    private readonly byte[] _tintedPixels;
    private readonly int _stride;
    private readonly Int32Rect _bounds;
    private Color _tint;
    private bool _hasTint;

    internal NativeTintedBitmap(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        _stride = checked(bgra.PixelWidth * 4);
        _sourcePixels = new byte[checked(_stride * bgra.PixelHeight)];
        _tintedPixels = new byte[_sourcePixels.Length];
        bgra.CopyPixels(_sourcePixels, _stride, 0);
        _image = new WriteableBitmap(bgra.PixelWidth, bgra.PixelHeight,
            bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null);
        _bounds = new Int32Rect(0, 0, bgra.PixelWidth, bgra.PixelHeight);
    }

    internal BitmapSource GetImage(Color tint)
    {
        if (_hasTint && _tint == tint) return _image;

        // Each gauge owns its glyphs; a color change reuses the same bitmap.
        _sourcePixels.CopyTo(_tintedPixels, 0);
        NativeAssetCache.MultiplyStraightBgraByColor(_tintedPixels, tint);
        _image.WritePixels(_bounds, _tintedPixels, _stride, 0);
        _tint = tint;
        _hasTint = true;
        return _image;
    }
}
