using System.Runtime.InteropServices;

namespace Wisp.App.CrashDiagnostics;

// What Windows told the sentinel window: the session is ending or going on after all, the PC is
// about to sleep or has resumed, and the session was locked or unlocked.
internal enum SessionNotice { Ending, Continuing, Suspending, Resumed, Locked, Unlocked }

// A hidden top-level window on its own thread receives WM_QUERYENDSESSION and WM_ENDSESSION even
// while the dashboard thread is busy, so a Windows shutdown or sign-out is recorded as a close
// request before Windows ends the process. Without it, a run that Windows ended was reported as an
// unexplained exit. The same window receives the sleep, resume, lock and unlock notices, which the
// run marker keeps so the next start can tell a run that ended on a sleeping or locked PC apart.
internal sealed class SessionEndSentinel : IDisposable
{
    private const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016,
        WM_POWERBROADCAST = 0x0218, WM_WTSSESSION_CHANGE = 0x02B1;
    private const long PBT_APMSUSPEND = 0x0004, PBT_APMRESUMESUSPEND = 0x0007, PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const long WTS_SESSION_LOCK = 0x7, WTS_SESSION_UNLOCK = 0x8;
    private const uint NOTIFY_FOR_THIS_SESSION = 0;
    private const int WS_OVERLAPPED = 0x00000000;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private const string ClassName = "WispSessionEndSentinel";
    private readonly Action<SessionNotice> _notice;
    private readonly WindowProcedure _procedure; // Keeps the delegate alive for the window's lifetime.
    private readonly ManualResetEventSlim _started = new(false);
    private Thread? _thread;
    private IntPtr _window;
    private int _signalled;
    private bool _sessionNotifications;

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private SessionEndSentinel(Action<SessionNotice> notice)
    {
        _notice = notice;
        _procedure = Procedure;
    }

    internal static SessionEndSentinel? TryStart(Action<SessionNotice> notice)
    {
        var sentinel = new SessionEndSentinel(notice);
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
        // Lock and unlock notices need a registration, which can wait for the Terminal Services service;
        // without one the sentinel still records shutdowns and sleep.
        try { _sessionNotifications = WTSRegisterSessionNotification(_window, NOTIFY_FOR_THIS_SESSION); }
        catch (Exception) { }
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
                else Continue();
                return IntPtr.Zero;
            case WM_POWERBROADCAST:
                if ((long)wParam == PBT_APMSUSPEND) Notify(SessionNotice.Suspending);
                else if ((long)wParam is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND) Notify(SessionNotice.Resumed);
                return 1;
            case WM_WTSSESSION_CHANGE:
                if ((long)wParam == WTS_SESSION_LOCK) Notify(SessionNotice.Locked);
                else if ((long)wParam == WTS_SESSION_UNLOCK) Notify(SessionNotice.Unlocked);
                return IntPtr.Zero;
            case WM_CLOSE:
                if (_sessionNotifications)
                {
                    _sessionNotifications = false;
                    try { WTSUnRegisterSessionNotification(window); }
                    catch (Exception) { }
                }
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
        Notify(SessionNotice.Ending);
    }

    // Windows asked every window whether the session may end and a program vetoed it: the close
    // request recorded on the query is withdrawn, and a later shutdown is recorded again.
    private void Continue()
    {
        if (Interlocked.Exchange(ref _signalled, 0) == 0) return;
        Notify(SessionNotice.Continuing);
    }

    private void Notify(SessionNotice notice)
    {
        try { _notice(notice); }
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

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
}
