using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wisp.App.Clips;

internal sealed class LosslessVideoHost(FrameworkElement viewport) : HwndHost
{
    private readonly TaskCompletionSource<IntPtr> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Rect _lastClip = Rect.Empty;
    private Task _cleanup = Task.CompletedTask;
    private IntPtr _window;
    internal Task<IntPtr> Ready => _ready.Task;
    internal event EventHandler? Closed;
    internal event EventHandler? Failed;
    internal void RetireAfter(Task cleanup) => _cleanup = cleanup;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        const int styles = 0x40000000 | 0x10000000 | 0x08000000 | 0x04000000 | 0x02000000;
        _window = CreateWindowEx(0x08000000, "STATIC", "", styles, 0, 0, 1, 1,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new InvalidOperationException("The lossless video surface could not be created.");
        Loaded += OnLoaded; viewport.Loaded += OnLoaded; LayoutUpdated += UpdateClip;
        return new HandleRef(this, _window);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Loaded -= OnLoaded; viewport.Loaded -= OnLoaded; LayoutUpdated -= UpdateClip;
        _ready.TrySetCanceled();
        Closed?.Invoke(this, EventArgs.Empty);
        _window = IntPtr.Zero;
        if (_cleanup.IsCompleted) { _ = DestroyWindow(hwnd.Handle); return; }
        // Native Stop runs off the UI thread. Retire the exact child without leaving a
        // visible overlay or destroying its handle while VLC may still be using it.
        _ = ShowWindow(hwnd.Handle, 0);
        _ = SetParent(hwnd.Handle, new IntPtr(-3)); // HWND_MESSAGE: hidden, never activated.
        _ = _cleanup.ContinueWith(completed =>
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.InvokeAsync(() => DestroyWindow(hwnd.Handle), DispatcherPriority.Send);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new IntPtr(3); }
        return base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => UpdateClip(sender, args);
    private void UpdateClip(object? sender, EventArgs args)
    {
        if (_window == IntPtr.Zero || !IsLoaded || !viewport.IsLoaded) return;
        try
        {
            var visible = viewport.TransformToVisual(this).TransformBounds(new Rect(viewport.RenderSize));
            visible.Intersect(new Rect(RenderSize));
            var dpi = VisualTreeHelper.GetDpi(this);
            var pixels = visible.IsEmpty ? new Rect(0, 0, 0, 0) : new Rect(
                Math.Ceiling(visible.Left * dpi.DpiScaleX), Math.Ceiling(visible.Top * dpi.DpiScaleY),
                Math.Max(0, Math.Floor(visible.Right * dpi.DpiScaleX) - Math.Ceiling(visible.Left * dpi.DpiScaleX)),
                Math.Max(0, Math.Floor(visible.Bottom * dpi.DpiScaleY) - Math.Ceiling(visible.Top * dpi.DpiScaleY)));
            if (_lastClip == pixels) return;
            var region = CreateRectRgn((int)pixels.Left, (int)pixels.Top, (int)pixels.Right, (int)pixels.Bottom);
            if (region == IntPtr.Zero) throw new InvalidOperationException("The lossless video viewport could not be prepared.");
            if (SetWindowRgn(_window, region, true) == 0)
            { _ = DeleteObject(region); throw new InvalidOperationException("The lossless video viewport could not be applied."); }
            var actual = CreateRectRgn(0, 0, 0, 0);
            try
            {
                if (actual == IntPtr.Zero || GetWindowRgn(_window, actual) == 0 || GetRgnBox(actual, out var bounds) == 0 ||
                    bounds.Left != (int)pixels.Left || bounds.Top != (int)pixels.Top ||
                    bounds.Right != (int)pixels.Right || bounds.Bottom != (int)pixels.Bottom)
                    throw new InvalidOperationException("The lossless video viewport could not be verified.");
            }
            finally { if (actual != IntPtr.Zero) _ = DeleteObject(actual); }
            _lastClip = pixels;
            _ready.TrySetResult(_window);
        }
        catch (InvalidOperationException error)
        {
            _ = ShowWindow(_window, 0);
            _ready.TrySetException(error);
            Failed?.Invoke(this, EventArgs.Empty);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className,
        string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out NativeRect rectangle);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
}
