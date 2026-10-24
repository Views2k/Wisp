using System.IO;
using Wisp.App.CrashDiagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.App;

internal static class NativeAssetCache
{
    private static readonly Dictionary<string, BitmapSource> Images = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, BitmapSource> TintedImages = new(StringComparer.Ordinal);
    private static readonly object Sync = new();

    public static BitmapSource Get(NativeGaugeMode mode, string fileName)
        => Get(
            mode == NativeGaugeMode.Digital
                ? NativeAssetFamily.Digital
                : NativeAssetFamily.Analogue,
            fileName);

    public static BitmapSource Get(NativeAssetFamily family, string fileName)
    {
        var folder = family.ToString();
        var key = $"{folder}/{fileName}";
        lock (Sync)
        {
            if (Images.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var image = TryDecode(key);
            if (image is null) return Placeholder; // Not cached: the next request decodes again.
            Images.Add(key, image);
            return image;
        }
    }

    // A gauge asset is an embedded resource of Wisp's own program files, and a read can still fail
    // while another process holds that file. A failed decode is reported and the gauge draws a
    // transparent image instead of ending Wisp on the dashboard thread.
    internal const int DecodeAttempts = 3;
    private static readonly BitmapSource Placeholder = CreatePlaceholder();

    private static BitmapSource? TryDecode(string key)
    {
        Exception? failure = null;
        for (var attempt = 1; attempt <= DecodeAttempts; attempt++)
        {
            try { return Decode(key); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
            {
                failure = error;
                if (attempt < DecodeAttempts) Thread.Sleep(25 * attempt);
            }
        }
        if (failure is not null) Report(failure);
        return null;
    }

    // Naming the process that holds the file asks the Restart Manager, which can take seconds, so
    // that lookup and its report run off the dashboard thread.
    private static void Report(Exception failure)
    {
        if (!FileLockHolders.IsLockFailure(failure))
        {
            App.RecoveredExceptionSink?.Invoke(failure, null);
            return;
        }
        _ = Task.Run(() =>
        {
            try { App.RecoveredExceptionSink?.Invoke(failure, FileLockHolders.Note(FileLockHolders.DescribeProgramFiles())); }
            catch (Exception) { }
        });
    }

    private static BitmapSource CreatePlaceholder()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        return image;
    }

    private static BitmapSource Decode(string key)
    {
        var encoded = new BitmapImage();
        encoded.BeginInit();
        encoded.CacheOption = BitmapCacheOption.OnLoad;
        encoded.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        encoded.UriSource = new Uri(
            $"pack://application:,,,/Wisp;component/Assets/Native/{key}",
            UriKind.Absolute);
        encoded.EndInit();
        encoded.Freeze();

        // The original HiRes swatchbin metadata marks every Digital and
        // Analogue texture as premultiplied-alpha. The exported PNG bytes
        // preserve those RGB values, while WPF expects straight BGRA input
        // before it performs its own premultiplication for composition.
        // Decode every texture through the same one-time unpremultiply;
        // bypassing this for the analogue assists multiplies their RGB by
        // alpha a second time and destroys the native sectors and glow.
        var bgra = new FormatConvertedBitmap(encoded, PixelFormats.Bgra32, null, 0);
        var stride = checked(bgra.PixelWidth * 4);
        var pixels = new byte[checked(stride * bgra.PixelHeight)];
        bgra.CopyPixels(pixels, stride, 0);
        UnpremultiplyBgra(pixels);
        BitmapSource image = BitmapSource.Create(
            bgra.PixelWidth,
            bgra.PixelHeight,
            bgra.DpiX,
            bgra.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        image.Freeze();
        return image;
    }

    public static BitmapSource GetTinted(NativeGaugeMode mode, string fileName, Color tint)
        => GetTinted(
            mode == NativeGaugeMode.Digital
                ? NativeAssetFamily.Digital
                : NativeAssetFamily.Analogue,
            fileName,
            tint);

    public static BitmapSource GetTinted(NativeAssetFamily family, string fileName, Color tint)
    {
        var source = Get(family, fileName);
        if (ReferenceEquals(source, Placeholder)) return Placeholder;
        var folder = family.ToString();
        var key = $"{folder}/{fileName}|{tint.A:X2}{tint.R:X2}{tint.G:X2}{tint.B:X2}";
        lock (Sync)
        {
            if (TintedImages.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = checked(bgra.PixelWidth * 4);
            var pixels = new byte[checked(stride * bgra.PixelHeight)];
            bgra.CopyPixels(pixels, stride, 0);
            MultiplyStraightBgraByColor(pixels, tint);
            var image = BitmapSource.Create(
                bgra.PixelWidth,
                bgra.PixelHeight,
                bgra.DpiX,
                bgra.DpiY,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            image.Freeze();
            TintedImages.Add(key, image);
            return image;
        }
    }

    internal static void UnpremultiplyBgra(Span<byte> pixels)
    {
        if (pixels.Length % 4 != 0)
        {
            throw new ArgumentException("BGRA data must contain complete pixels.", nameof(pixels));
        }

        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            if (alpha == 0)
            {
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
                continue;
            }

            if (alpha == byte.MaxValue)
            {
                continue;
            }

            pixels[offset] = Unpremultiply(pixels[offset], alpha);
            pixels[offset + 1] = Unpremultiply(pixels[offset + 1], alpha);
            pixels[offset + 2] = Unpremultiply(pixels[offset + 2], alpha);
        }
    }

    internal static void MultiplyStraightBgraByColor(Span<byte> pixels, Color tint)
    {
        if (pixels.Length % 4 != 0)
        {
            throw new ArgumentException("BGRA data must contain complete pixels.", nameof(pixels));
        }

        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = Multiply(pixels[offset], tint.B);
            pixels[offset + 1] = Multiply(pixels[offset + 1], tint.G);
            pixels[offset + 2] = Multiply(pixels[offset + 2], tint.R);
            pixels[offset + 3] = Multiply(pixels[offset + 3], tint.A);
        }
    }

    private static byte Unpremultiply(byte component, byte alpha) =>
        (byte)Math.Min(byte.MaxValue, ((component * byte.MaxValue) + (alpha / 2)) / alpha);

    private static byte Multiply(byte component, byte tint) =>
        (byte)(((component * tint) + (byte.MaxValue / 2)) / byte.MaxValue);
}
