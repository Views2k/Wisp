using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App;
using Wisp.App.NativeRendering;

namespace Wisp.UiReview;

// Review only: RenderTargetBitmap does not execute WPF's PS3 needle effect.
// Read back the existing D3D11 shader body through WARP, then temporarily use
// those pixels in the actual gauge's existing needle canvas and rotation.
internal static class PowerTorqueShaderCapture
{
    private static readonly Dictionary<(int Width, int Height, double Blur), BitmapSource> Needles = new();

    internal static BitmapSource RenderGauge(PowerTorqueGaugeView gauge, int dpi)
    {
        ArgumentNullException.ThrowIfNull(gauge);
        if (dpi is < 96 or > 384) throw new ArgumentOutOfRangeException(nameof(dpi));
        var size = new Size(
            double.IsFinite(gauge.Width) && gauge.Width > 0 ? gauge.Width : 140,
            double.IsFinite(gauge.Height) && gauge.Height > 0 ? gauge.Height : 140);
        return RenderSurface(gauge, size, dpi);
    }

    internal static BitmapSource RenderSurface(FrameworkElement surface, Size size, int dpi)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (dpi is < 96 or > 384) throw new ArgumentOutOfRangeException(nameof(dpi));
        var pixelWidth = checked((int)Math.Ceiling(size.Width * dpi / 96d));
        var pixelHeight = checked((int)Math.Ceiling(size.Height * dpi / 96d));
        if (pixelWidth > 4096 || pixelHeight > 4096)
            throw new ArgumentOutOfRangeException(nameof(surface), "Review surface is too large.");

        surface.Measure(size);
        surface.Arrange(new Rect(size));
        surface.UpdateLayout();
        var originals = new List<(Canvas Parent, int Index, NativeAnalogNeedleVisual Original, Image Replacement)>();
        try
        {
            foreach (var needle in Descendants(surface).OfType<NativeAnalogNeedleVisual>().ToArray())
            {
                if (!IsVisibleWithin(needle, surface)) continue;
                if (needle.IsElectricMaterial)
                    throw new NotSupportedException("The existing D3D capture shader is combustion-only; it cannot verify the EV needle material.");
                if (VisualTreeHelper.GetParent(needle) is not Canvas parent)
                    throw new InvalidOperationException("The gauge needle's authored canvas changed; review the capture adapter.");

                var transform = needle.TransformToAncestor(surface);
                var origin = transform.Transform(new Point());
                var xScale = (transform.Transform(new Point(1, 0)) - origin).Length;
                var yScale = (transform.Transform(new Point(0, 1)) - origin).Length;
                var width = Math.Max(1, checked((int)Math.Ceiling(needle.ActualWidth * xScale * dpi / 96d)));
                var height = Math.Max(1, checked((int)Math.Ceiling(needle.ActualHeight * yScale * dpi / 96d)));
                var image = new Image
                {
                    Source = CaptureNeedle(width, height, needle.BlurAmount),
                    Width = needle.Width,
                    Height = needle.Height,
                    Margin = needle.Margin,
                    Opacity = needle.Opacity,
                    Visibility = needle.Visibility,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                    RenderTransform = needle.RenderTransform.CloneCurrentValue(),
                    RenderTransformOrigin = needle.RenderTransformOrigin,
                    LayoutTransform = needle.LayoutTransform.CloneCurrentValue()
                };
                Canvas.SetLeft(image, Canvas.GetLeft(needle));
                Canvas.SetTop(image, Canvas.GetTop(needle));
                Canvas.SetRight(image, Canvas.GetRight(needle));
                Canvas.SetBottom(image, Canvas.GetBottom(needle));
                Panel.SetZIndex(image, Panel.GetZIndex(needle));
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var index = parent.Children.IndexOf(needle);
                parent.Children.RemoveAt(index);
                parent.Children.Insert(index, image);
                originals.Add((parent, index, needle, image));
            }

            surface.Measure(size);
            surface.Arrange(new Rect(size));
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            foreach (var entry in originals)
            {
                entry.Parent.Children.Remove(entry.Replacement);
                entry.Parent.Children.Insert(entry.Index, entry.Original);
            }
            surface.UpdateLayout();
        }
    }

    private static bool IsVisibleWithin(DependencyObject child, FrameworkElement surface)
    {
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element && element.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(current, surface)) return true;
        }
        return false;
    }

    private static BitmapSource CaptureNeedle(int width, int height, double blur)
    {
        if (!double.IsFinite(blur)) throw new ArgumentOutOfRangeException(nameof(blur));
        var key = (width, height, blur);
        if (Needles.TryGetValue(key, out var cached)) return cached;

        // Same hidden, nonactivating HWND contract as NativeContractTests.cpp.
        // Never call ShowWindow or the presentation APIs on this review target.
        var window = CreateWindowEx(0x08200080, "STATIC", "Wisp needle pixel review", 0x80000000,
            -32000, -32000, width, height, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            using var device = DirectCompositionDevice.Create(window, width, height, cpuRendering: true);
            var command = AnalogHudScene.Quad(0, new AnalogHudRect(0, 0, width, height),
                shader: DirectCompositionShader.Needle);
            command.ParameterX = (float)blur;
            device.RenderForCapture([command], 1);
            var pixels = device.CaptureBgra();
            var nontransparent = 0;
            for (var index = 3; index < pixels.Length; index += 4)
                if (pixels[index] != 0) nontransparent++;
            if (nontransparent == 0 || nontransparent == width * height)
                throw new InvalidOperationException("Needle shader readback was blank or opaque; this is not a valid visual review.");
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
            bitmap.Freeze();
            if (Needles.Count < 24) Needles.Add(key, bitmap);
            return bitmap;
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
