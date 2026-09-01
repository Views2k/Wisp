using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Wisp.App;

internal static class WindowZOrder
{
    private static readonly IntPtr Top = IntPtr.Zero;

    public static bool IsWindowAvailable(IntPtr windowHandle) =>
        windowHandle != IntPtr.Zero && IsWindow(windowHandle);

    public static bool IsAttachedToGame(Window? window, IntPtr gameWindow)
    {
        if (window is null || !window.IsVisible || !IsWindowAvailable(gameWindow))
        {
            return false;
        }

        var helper = new WindowInteropHelper(window);
        return helper.Handle != IntPtr.Zero && helper.Owner == gameWindow;
    }

    public static bool AttachAboveGame(Window? window, IntPtr gameWindow, bool raise)
    {
        if (window is null || !window.IsVisible || !IsWindowAvailable(gameWindow))
        {
            return false;
        }

        var helper = new WindowInteropHelper(window);
        var handle = helper.Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        // A non-topmost owned window stays immediately above its game window,
        // but naturally falls behind Discord or any other app selected by the
        // player.  This preserves the HUD across alt-tab without leaking it
        // over unrelated applications.
        window.Topmost = false;
        if (helper.Owner != gameWindow)
        {
            helper.Owner = gameWindow;
        }

        if (raise)
        {
            _ = SetWindowPos(
                handle,
                Top,
                0,
                0,
                0,
                0,
                NoMove | NoSize | NoActivate | ShowWindow);
        }

        return true;
    }

    public static void DetachFromGame(Window? window)
    {
        if (window is null)
        {
            return;
        }

        var helper = new WindowInteropHelper(window);
        if (helper.Handle != IntPtr.Zero && helper.Owner != IntPtr.Zero)
        {
            helper.Owner = IntPtr.Zero;
        }
    }

    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoActivate = 0x0010;
    private const uint ShowWindow = 0x0040;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
