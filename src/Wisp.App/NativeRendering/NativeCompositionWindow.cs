using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Wisp.App.NativeRendering;

internal readonly record struct NativeHostBounds(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
}

internal readonly record struct NativeHostPlacement(NativeHostBounds Bounds, int OffsetX, int OffsetY)
{
    internal static NativeHostPlacement Create(NativeHostBounds layout, NativeHostBounds monitor, bool fillMonitor)
    {
        // Preserve layouts spanning display edges; one monitor-sized target
        // would otherwise clip artwork that the previous host could display.
        var contained = layout.Width > 0 && layout.Height > 0 && monitor.Width > 0 && monitor.Height > 0 &&
            layout.Left >= monitor.Left && layout.Top >= monitor.Top &&
            layout.Right <= monitor.Right && layout.Bottom <= monitor.Bottom;
        return fillMonitor && contained
            ? new(monitor, layout.Left - monitor.Left, layout.Top - monitor.Top)
            : new(layout, 0, 0);
    }
}

// A Win32-only target: no HwndSource/HwndTarget, GDI bitmap, or WPF pixels.
// The WPF window remains the layout/editing model, outside the visible HUD path.
internal sealed class NativeCompositionWindow : IDisposable
{
    private const string ClassName = "Wisp.NativeCompositionHost";
    private const int NoRedirection = 0x00200000;
    private const int Transparent = 0x00000020;
    private const int ToolWindow = 0x00000080;
    private const int NoActivate = 0x08000000;
    private static readonly WindowProcedure Procedure = WindowProc;
    private static readonly Lazy<ushort> WindowClass = new(RegisterWindowClass);
    private readonly HwndSource _layoutSource;
    private readonly Action _changed;
    private readonly Action<int> _failed;
    private readonly bool _fillMonitor;
    private bool _visible, _cloaked, _disposed, _synchronizing, _detached;
    private IntPtr _owner;
    private Rectangle _bounds;
    private bool _topmost;

    internal IntPtr Handle { get; private set; }
    internal int OffsetX { get; private set; }
    internal int OffsetY { get; private set; }

    internal NativeCompositionWindow(HwndSource layoutSource, Action changed, Action<int> failed, bool fillMonitor = false)
    {
        _layoutSource = layoutSource;
        _changed = changed;
        _failed = failed;
        _fillMonitor = fillMonitor;
        _ = WindowClass.Value;
        // LAYERED + TRANSPARENT gives system-managed cross-thread click-through.
        // Do not call SetLayeredWindowAttributes/UpdateLayeredWindow: all pixels
        // belong to DComp, and NOREDIRECTIONBITMAP removes the normal surface.
        Handle = CreateWindowEx(NoRedirection | Transparent | ToolWindow | NoActivate | 0x00080000,
            ClassName, "Wisp native HUD", 0x80000000, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _ = OverlayPassiveUpdate.Apply(Handle, native: true);
        _layoutSource.AddHook(LayoutWindowProcedure);
    }

    internal void Synchronize(bool visible, bool raise = false)
    {
        if (_disposed || _detached || _synchronizing) return;
        _synchronizing = true;
        try
        {
            var layout = _layoutSource.Handle;
            var origin = new Point();
            if (!GetClientRect(layout, out var client) || !ClientToScreen(layout, ref origin))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var layoutBounds = new NativeHostBounds(origin.X, origin.Y,
                origin.X + client.Right, origin.Y + client.Bottom);
            var monitorBounds = layoutBounds;
            if (_fillMonitor)
            {
                var monitor = MonitorFromWindow(layout, 2); // MONITOR_DEFAULTTONEAREST
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                monitorBounds = new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
            }
            var placement = NativeHostPlacement.Create(layoutBounds, monitorBounds, _fillMonitor);
            var bounds = new Rectangle
            {
                Left = placement.Bounds.Left,
                Top = placement.Bounds.Top,
                Right = placement.Bounds.Right,
                Bottom = placement.Bounds.Bottom
            };
            // Owning this popup from the cloaked WPF window would inherit its
            // cloak. Instead mirror the existing game ownership and Z-order.
            var owner = GetWindow(layout, 4); // GW_OWNER
            if (owner != _owner)
            {
                Marshal.SetLastPInvokeError(0);
                _ = SetWindowLongPtr(Handle, -8, owner);
                var error = Marshal.GetLastPInvokeError();
                if (error != 0) throw new Win32Exception(error);
                _owner = owner;
                raise = true;
            }
            var topmost = (GetWindowLong(layout, -20) & 8) != 0;
            if (visible && (raise || !_visible || !_bounds.Equals(bounds) || _topmost != topmost))
            {
                var order = topmost ? new IntPtr(-1) : _topmost ? new IntPtr(-2) : IntPtr.Zero;
                uint flags = 0x0010 | 0x0200 | 0x0040; // NOACTIVATE | NOOWNERZORDER | SHOWWINDOW
                if (!raise && _visible && _topmost == topmost) flags |= 0x0004; // NOZORDER
                if (!SetWindowPos(Handle, order, bounds.Left, bounds.Top,
                    bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, flags))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                _bounds = bounds;
                _topmost = topmost;
            }
            if (!visible && _visible) _ = ShowWindow(Handle, 0);
            OffsetX = placement.OffsetX;
            OffsetY = placement.OffsetY;
            _visible = visible;
        }
        finally { _synchronizing = false; }
    }

    internal void SetLayoutCloaked(bool cloaked)
    {
        if (_disposed || _cloaked == cloaked) return;
        var value = cloaked ? 1 : 0;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(_layoutSource.Handle, 13, ref value, sizeof(int)));
        _cloaked = cloaked;
    }

    private IntPtr LayoutWindowProcedure(IntPtr hwnd, int message, IntPtr word, IntPtr data, ref bool handled)
    {
        if (_disposed || _synchronizing) return IntPtr.Zero;
        // Geometry, Z-order, ownership, DPI and desktop-mode changes are events,
        // not a second polling loop. Never let an interop error escape WndProc.
        if (message is 0x0047 or 0x007D or 0x02E0 or 0x007E) // WINDOWPOSCHANGED, STYLECHANGED, DPI, DISPLAY
        {
            try
            {
                bool raise = false;
                if (message == 0x0047)
                    raise = (Marshal.PtrToStructure<WindowPosition>(data).Flags & 0x0004) == 0;
                Synchronize(_visible && IsWindowVisible(hwnd) && !IsIconic(hwnd), raise);
                _changed();
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            { _failed(error.HResult); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _disposed = true;
        _ = DestroyWindow(Handle);
        Handle = IntPtr.Zero;
    }

    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        if (!_layoutSource.IsDisposed) _layoutSource.RemoveHook(LayoutWindowProcedure);
        if (_cloaked && !_layoutSource.IsDisposed && IsWindow(_layoutSource.Handle))
        {
            var value = 0;
            _ = DwmSetWindowAttribute(_layoutSource.Handle, 13, ref value, sizeof(int));
        }
        _ = ShowWindow(Handle, 0);
        _visible = false;
        _cloaked = false;
    }

    private static ushort RegisterWindowClass()
    {
        var windowClass = new WindowClassInfo
        {
            Size = (uint)Marshal.SizeOf<WindowClassInfo>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = GetModuleHandle(null),
            ClassName = ClassName
        };
        var atom = RegisterClassEx(ref windowClass);
        if (atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return atom;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr word, IntPtr data)
    {
        if (message == 0x0082) OverlayPassiveUpdate.Forget(hwnd); // WM_NCDESTROY
        if (message == 0x031E) _ = OverlayPassiveUpdate.Apply(hwnd, native: true); // WM_DWMCOMPOSITIONCHANGED
        return message switch
        {
            0x0084 => new IntPtr(-1), // HTTRANSPARENT; the window is always input-transparent.
            0x0021 => new IntPtr(3), // MA_NOACTIVATE
            0x0014 => new IntPtr(1), // no GDI background
            _ => DefWindowProc(hwnd, message, word, data)
        };
    }

    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr word, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public Rectangle Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public IntPtr Window, After; public int X, Y, Width, Height; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassInfo
    {
        public uint Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background, MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr SmallIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassInfo windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr word, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rectangle rectangle);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
