using System.Runtime.InteropServices;

namespace Wisp.App.CrashDiagnostics;

// A hidden top-level window on its own thread receives WM_QUERYENDSESSION and WM_ENDSESSION even
// while the dashboard thread is busy, so a Windows shutdown or sign-out is recorded as a close
// request before Windows ends the process. Without it, a run that Windows ended was reported as an
// unexplained exit.
internal sealed class SessionEndSentinel : IDisposable
{
    private const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;
    private const int WS_OVERLAPPED = 0x00000000;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private const string ClassName = "WispSessionEndSentinel";
    private readonly Action _sessionEnding;
    private readonly WindowProcedure _procedure; // Keeps the delegate alive for the window's lifetime.
    private readonly ManualResetEventSlim _started = new(false);
    private Thread? _thread;
    private IntPtr _window;
    private int _signalled;

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private SessionEndSentinel(Action sessionEnding)
    {
        _sessionEnding = sessionEnding;
        _procedure = Procedure;
    }

    internal static SessionEndSentinel? TryStart(Action sessionEnding)
    {
        var sentinel = new SessionEndSentinel(sessionEnding);
        try
        {
            sentinel._thread = new Thread(sentinel.Run) { IsBackground = true, Name = "Wisp session sentinel" };
            sentinel._thread.SetApartmentState(ApartmentState.STA);
            sentinel._thread.Start();
            if (sentinel._started.Wait(TimeSpan.FromSeconds(2)) && sentinel._window != IntPtr.Zero) return sentinel;
            sentinel.Dispose();
            return null;
        }
        catch (Exception)
        {
            sentinel.Dispose();
            return null;
        }
    }

    private void Run()
    {
        try
        {
            var instance = GetModuleHandleW(null);
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = _procedure,
                Instance = instance,
                ClassName = ClassName
            };
            if (RegisterClassExW(ref windowClass) == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS) return;
            _window = CreateWindowExW(0, ClassName, "Wisp session sentinel", WS_OVERLAPPED, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        }
        catch (Exception) { }
        finally { _started.Set(); }
        if (_window == IntPtr.Zero) return;
        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    private IntPtr Procedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_QUERYENDSESSION:
                Signal();
                return 1; // The session may end.
            case WM_ENDSESSION:
                if (wParam != IntPtr.Zero) Signal();
                return IntPtr.Zero;
            case WM_CLOSE:
                DestroyWindow(window);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    private void Signal()
    {
        if (Interlocked.Exchange(ref _signalled, 1) != 0) return;
        try { _sessionEnding(); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        var window = Interlocked.Exchange(ref _window, IntPtr.Zero);
        if (window != IntPtr.Zero) PostMessageW(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (_thread is { IsAlive: true } thread && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Message message, IntPtr window, uint filterMinimum, uint filterMaximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
