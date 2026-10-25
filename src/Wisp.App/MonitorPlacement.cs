using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Wisp.App;

// A saved HUD placement holds the window's position in the device-independent pixels of the monitor
// it was saved on, and its key names that monitor. With monitors at different scaling, restoring it
// while the window sits on another monitor converts those pixels with the wrong scale and can land
// the HUD on the wrong screen. Moving the window to the saved monitor first (in physical pixels, so
// Windows updates the window's DPI) makes the saved coordinates mean what they meant.
internal static class MonitorPlacement
{
    private const uint MonitorDefaultToNearest = 2;

    // The device name is the first part of a placement key: "\\.\DISPLAY2-2560x1440-SpeedV4-…".
    internal static string? DeviceOf(string? placementKey)
    {
        if (string.IsNullOrEmpty(placementKey)) return null;
        var separator = placementKey.IndexOf('-', StringComparison.Ordinal);
        return separator > 0 ? placementKey[..separator] : null;
    }

    // True when the window is on the key's monitor afterwards; false leaves the window where it is,
    // which is also the answer when the key names no connected monitor.
    internal static bool TryMoveToMonitor(Window window, string? placementKey)
    {
        if (DeviceOf(placementKey) is not { } device) return false;
        var target = IntPtr.Zero;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var candidate = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref candidate) || !string.Equals(candidate.Device, device, StringComparison.OrdinalIgnoreCase)) return true;
            target = monitor;
            info = candidate;
            return false;
        }, IntPtr.Zero);
        if (target == IntPtr.Zero) return false;

        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (MonitorFromWindow(handle, MonitorDefaultToNearest) == target) return true;
        if (!GetWindowRect(handle, out var bounds)) return false;
        const uint flags = 0x0001 | 0x0004 | 0x0010 | 0x0200; // no size, z-order, activation or owner-order change
        var x = info.Monitor.Left + ((info.Monitor.Right - info.Monitor.Left) - (bounds.Right - bounds.Left)) / 2;
        var y = info.Monitor.Top + ((info.Monitor.Bottom - info.Monitor.Top) - (bounds.Bottom - bounds.Top)) / 2;
        return SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, flags) &&
               MonitorFromWindow(handle, MonitorDefaultToNearest) == target;
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorEnumProcedure(IntPtr monitor, IntPtr deviceContext, IntPtr bounds, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProcedure callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
