using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Wisp.LosslessPlaybackProbe;

// The child HWND has no separate overlay window and never requests focus.
internal sealed class PassiveVideoHost(FrameworkElement viewport) : HwndHost
{
    private Rect _lastClip = Rect.Empty;
    private int _loadedEvents;
    private int _clipUpdates;
    internal IntPtr VideoHandle { get; private set; }
    internal bool ClipVerified { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        const int childStyles = 0x40000000 | 0x10000000 | 0x08000000 | 0x04000000 | 0x02000000;
        VideoHandle = CreateWindowEx(0x08000000, "STATIC", "", childStyles,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (VideoHandle == IntPtr.Zero) throw new InvalidOperationException("child-window-creation-failed");
        Loaded += OnLoaded;
        viewport.Loaded += OnLoaded;
        LayoutUpdated += UpdateClip;
        return new HandleRef(this, VideoHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        LayoutUpdated -= UpdateClip;
        Loaded -= OnLoaded;
        viewport.Loaded -= OnLoaded;
        if (!DestroyWindow(hwnd.Handle)) throw new InvalidOperationException("child-window-destroy-failed");
        VideoHandle = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void UpdateClip(object? sender, EventArgs args)
    {
        _clipUpdates++;
        if (VideoHandle == IntPtr.Zero || !IsLoaded || !viewport.IsLoaded) return;
        var visible = viewport.TransformToVisual(this).TransformBounds(new Rect(viewport.RenderSize));
        visible.Intersect(new Rect(RenderSize));
        var dpi = VisualTreeHelper.GetDpi(this);
        var pixels = visible.IsEmpty ? new Rect(0, 0, 0, 0) : new Rect(
            Math.Ceiling(visible.Left * dpi.DpiScaleX), Math.Ceiling(visible.Top * dpi.DpiScaleY),
            Math.Max(0, Math.Floor(visible.Right * dpi.DpiScaleX) - Math.Ceiling(visible.Left * dpi.DpiScaleX)),
            Math.Max(0, Math.Floor(visible.Bottom * dpi.DpiScaleY) - Math.Ceiling(visible.Top * dpi.DpiScaleY)));
        if (_lastClip == pixels) return;
        var region = CreateRectRgn((int)pixels.Left, (int)pixels.Top, (int)pixels.Right, (int)pixels.Bottom);
        if (region == IntPtr.Zero) throw new InvalidOperationException("clip-region-creation-failed");
        if (SetWindowRgn(VideoHandle, region, true) == 0)
        {
            _ = DeleteObject(region);
            throw new InvalidOperationException("clip-region-apply-failed");
        }
        // SetWindowRgn owns region after success. Read back the region rather than assuming WPF clips HWND airspace.
        var actual = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (actual == IntPtr.Zero || GetWindowRgn(VideoHandle, actual) == 0 ||
                GetRgnBox(actual, out var bounds) == 0 || bounds.Left != (int)pixels.Left ||
                bounds.Top != (int)pixels.Top || bounds.Right != (int)pixels.Right || bounds.Bottom != (int)pixels.Bottom)
                throw new InvalidOperationException("clip-region-verification-failed");
            ClipVerified = true;
            _lastClip = pixels;
        }
        finally { if (actual != IntPtr.Zero) _ = DeleteObject(actual); }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _loadedEvents++;
        // LayoutUpdated may have run before Loaded. Loaded does not guarantee another layout pass.
        UpdateClip(sender, args);
    }

    internal Status GetStatus() => new(IsLoaded, viewport.IsLoaded, VideoHandle != IntPtr.Zero,
        _loadedEvents, _clipUpdates, ClipVerified);
    internal sealed record Status(bool HostLoaded, bool ViewportLoaded, bool HandleCreated,
        int LoadedEvents, int ClipUpdates, bool RegionVerified);

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out NativeRect rectangle);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
}
