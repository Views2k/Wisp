using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Wisp.App;

// A fullscreen game can leave the pointer hidden or confined to its window when
// you switch away. Windows only refreshes the cursor image when the pointer moves,
// so when Wisp becomes the active app, release any confinement and ask the Wisp
// window under a still pointer for its cursor right away.
internal static class CursorRecovery
{
    private const int WmActivateApp = 0x001C, WmSetCursor = 0x0020, WmMouseMove = 0x0200, WmNcHitTest = 0x0084;
    private static bool _installed, _pending;

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));
    }

    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (PresentationSource.FromVisual((Window)sender) is not HwndSource source) return;
        source.RemoveHook(Hook);
        source.AddHook(Hook);
    }

    private static IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Every Wisp window receives this message; restore once per activation.
        if (message == WmActivateApp && wParam != IntPtr.Zero && !_pending)
        {
            _pending = true;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, Restore);
        }
        return IntPtr.Zero;
    }

    private static void Restore()
    {
        _pending = false;
        _ = ClipCursor(IntPtr.Zero);
        if (!GetCursorPos(out var point)) return;
        var window = WindowFromPoint(point);
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var process) == 0 || process != Environment.ProcessId) return;
        var hit = SendMessage(window, WmNcHitTest, IntPtr.Zero, (point.Y << 16) | (point.X & 0xffff));
        _ = SendMessage(window, WmSetCursor, window, (WmMouseMove << 16) | ((int)hit & 0xffff));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(IntPtr rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
