namespace Wisp.Core;

public static class OverlayActivationPolicy
{
    public const int MouseActivateMessage = 0x0021;
    public const int MouseActivateNoActivateResult = 3;
    public const int TransparentExtendedStyle = 0x00000020;
    public const int ToolWindowExtendedStyle = 0x00000080;
    public const int NoActivateExtendedStyle = 0x08000000;

    public static int BuildExtendedStyle(int currentStyle, bool acceptsPointerInput)
    {
        var style = currentStyle | ToolWindowExtendedStyle | NoActivateExtendedStyle;
        return acceptsPointerInput
            ? style & ~TransparentExtendedStyle
            : style | TransparentExtendedStyle;
    }

    public static bool TryHandleWindowMessage(int message, out IntPtr result)
    {
        if (message == MouseActivateMessage)
        {
            result = new IntPtr(MouseActivateNoActivateResult);
            return true;
        }

        result = IntPtr.Zero;
        return false;
    }
}
