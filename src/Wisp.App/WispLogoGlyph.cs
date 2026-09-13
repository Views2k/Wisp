using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.App;

public static class WispLogoGlyph
{
    public static BitmapSource Mask { get; } = CreateMask();

    private static BitmapSource CreateMask()
    {
        var original = new BitmapImage(new Uri("pack://application:,,,/Wisp;component/Assets/Wisp-logo.png"));
        var source = new FormatConvertedBitmap(original, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            // The original artwork has a bright green W on a dark circular backing.
            // Use its green-channel coverage once; accent changes only replace the fill brush.
            var coverage = Math.Clamp((pixels[offset + 1] - 32) * 255 / 168, 0, 255);
            pixels[offset + 3] = (byte)(pixels[offset + 3] * coverage / 255);
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 255;
        }
        var mask = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        mask.Freeze();
        return mask;
    }
}
