using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace Wisp.App;

internal static class OverlayPresentation
{
    private const int ExtendedStyleIndex = -20;
    private const int StyleChangingMessage = 0x007C;
    private const int CompositionChangedMessage = 0x031E;
    private const int LayeredExtendedStyle = 0x00080000;
    private const uint LayeredAlpha = 0x00000002;

    public static void Initialize(HwndSource source)
    {
        // Keep WPF's normal GPU present path. DWM blends the transparent surface;
        // AllowsTransparency would instead read every frame back to system memory.
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        SetWindowLong(source.Handle, ExtendedStyleIndex,
            GetWindowLong(source.Handle, ExtendedStyleIndex) | LayeredExtendedStyle);
        ApplyComposition(source.Handle);
    }

    public static bool TryHandleWindowMessage(IntPtr handle, int message, IntPtr wordParameter, IntPtr longParameter)
    {
        if (message == StyleChangingMessage && wordParameter.ToInt64() == ExtendedStyleIndex)
        {
            // System-managed layering preserves cross-process click-through. WPF
            // otherwise removes this bit when its own per-pixel opacity is disabled.
            const int newStyleOffset = sizeof(int);
            Marshal.WriteInt32(longParameter, newStyleOffset,
                Marshal.ReadInt32(longParameter, newStyleOffset) | LayeredExtendedStyle);
            return true;
        }

        if (message == CompositionChangedMessage)
        {
            ApplyComposition(handle);
        }

        return false;
    }

    private static void ApplyComposition(IntPtr handle)
    {
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(handle, ref margins));
        if (!SetLayeredWindowAttributes(handle, 0, byte.MaxValue, LayeredAlpha))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr handle, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr handle, uint colorKey, byte alpha, uint flags);
}
