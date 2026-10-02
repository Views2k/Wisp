using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Wisp.App.Tests;

internal sealed class NativeArtworkBackplate : IDisposable
{
    private static readonly WindowProcedure Procedure = WindowProc;
    private readonly uint _threadId = GetCurrentThreadId();
    private readonly string _className = "Wisp.NativeArtworkBackplate." + Guid.NewGuid().ToString("N");
    private readonly IntPtr _instance;
    private IntPtr _brush;
    private bool _registered;

    internal IntPtr Handle { get; private set; }

    internal NativeArtworkBackplate(int left, int top, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _instance = GetModuleHandle(null);
        if (_instance == IntPtr.Zero) throw Failure("module lookup");
        try
        {
            _brush = CreateSolidBrush(18u | (52u << 8) | (86u << 16));
            if (_brush == IntPtr.Zero) throw Failure("brush creation");
            var windowClass = new WindowClassInfo
            {
                Size = (uint)Marshal.SizeOf<WindowClassInfo>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                Instance = _instance,
                Background = _brush,
                ClassName = _className
            };
            if (RegisterClassEx(ref windowClass) == 0) throw Failure("class registration");
            _registered = true;
            // Ordinary opaque GDI backplate; only the HUD under test uses DirectComposition.
            Handle = CreateWindowEx(0x08000088, _className, "Wisp test backplate", 0x80000000,
                left, top, width, height, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw Failure("window creation");
        }
        catch (Exception creationError)
        {
            try { Dispose(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Native test backplate creation and cleanup failed.", creationError, cleanupError);
            }
            throw;
        }
    }

    internal void ShowBehind(IntPtr native)
    {
        VerifyThread();
        if (Handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeArtworkBackplate));
        if (native == IntPtr.Zero) throw new ArgumentException("The native test host is required.", nameof(native));
        // NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW | NOOWNERZORDER.
        if (!SetWindowPos(Handle, native, 0, 0, 0, 0, 0x0253)) throw Failure("passive placement");
        // DefWindowProc erases with the private class brush. Paint this HWND synchronously.
        if (!RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0145)) throw Failure("background painting");
        if (!GdiFlush()) throw new InvalidOperationException("Native test backplate drawing did not flush.");
    }

    public void Dispose()
    {
        VerifyThread();
        if (Handle != IntPtr.Zero)
        {
            if (!DestroyWindow(Handle)) throw Failure("window destruction");
            Handle = IntPtr.Zero;
        }
        if (_registered)
        {
            if (!UnregisterClass(_className, _instance)) throw Failure("class removal");
            _registered = false;
            // Windows deletes the registered class background brush when unregistering it.
            _brush = IntPtr.Zero;
        }
        if (_brush != IntPtr.Zero)
        {
            if (!DeleteObject(_brush)) throw Failure("unregistered brush deletion");
            _brush = IntPtr.Zero;
        }
    }

    private void VerifyThread()
    {
        if (GetCurrentThreadId() != _threadId)
            throw new InvalidOperationException("The native test backplate must stay on its creating thread.");
    }

    private static Win32Exception Failure(string operation) =>
        new(Marshal.GetLastWin32Error(), $"Native test backplate {operation} failed.");

    private static IntPtr WindowProc(IntPtr window, uint message, IntPtr word, IntPtr data) =>
        message == 0x0021 ? new IntPtr(3) : DefWindowProc(window, message, word, data);

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr word, IntPtr data);

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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassInfo windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string title, uint style,
        int left, int top, int width, int height, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int left, int top, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr window, IntPtr updateRectangle, IntPtr updateRegion, uint flags);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr word, IntPtr data);
}
