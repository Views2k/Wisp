using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.LosslessPlaybackProbe;

internal static class SyntheticRgb
{
    internal const int Width = 1280, Height = 720;

    internal static Comparison CompareFirstFrame(string png)
    {
        using var file = new FileStream(png, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException("snapshot-size-bound");
        var decoder = new PngBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != Width || decoder.Frames[0].PixelHeight != Height)
            throw new InvalidDataException("snapshot-dimensions-mismatch");
        // Format conversion only reorders/unpacks channels. No ICC/gamma transform or tolerance is applied.
        var rgb = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Rgb24, null, 0);
        var actual = new byte[Width * Height * 3];
        rgb.CopyPixels(actual, Width * 3, 0);
        var expected = new byte[actual.Length];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var index = (y * Width + x) * 3;
                if (y < Height / 4)
                {
                    expected[index] = (byte)(x % 32);
                    expected[index + 1] = (byte)(x / 2 % 64);
                    expected[index + 2] = (byte)(x / 4 % 128);
                }
                else if (y < Height / 2)
                {
                    expected[index] = (byte)(x & 255);
                    expected[index + 1] = (byte)(y & 255);
                    expected[index + 2] = (byte)((x + y) & 255);
                }
                else if (y < Height * 3 / 4)
                {
                    expected[index] = (byte)((x & 1) * 255);
                    expected[index + 1] = (byte)((y & 1) * 255);
                    expected[index + 2] = (byte)(((x + y) & 1) * 255);
                }
                else
                {
                    var noise = unchecked((uint)x * 0x45d9f3bu ^ (uint)y * 0x119de1f3u);
                    noise ^= noise >> 16;
                    expected[index] = (byte)(noise & 255);
                    expected[index + 1] = (byte)((noise >> 8) & 255);
                    expected[index + 2] = (byte)((noise >> 16) & 255);
                }
            }
        var differing = 0;
        var maximum = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            var difference = Math.Abs(expected[i] - actual[i]);
            if (difference != 0) differing++;
            maximum = Math.Max(maximum, difference);
        }
        return new Comparison(expected.Length, differing, maximum,
            Convert.ToHexString(SHA256.HashData(expected)), Convert.ToHexString(SHA256.HashData(actual)));
    }

    internal sealed record Comparison(int ComponentSamples, int DifferingSamples, int MaximumByteError,
        string ReferenceSha256, string SnapshotRgbSha256);
}
