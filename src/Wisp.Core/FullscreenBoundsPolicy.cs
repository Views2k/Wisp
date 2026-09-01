namespace Wisp.Core;

public readonly record struct PixelBounds(int Left, int Top, int Right, int Bottom)
{
    public bool IsValid => Right > Left && Bottom > Top;
}

public static class FullscreenBoundsPolicy
{
    public static bool CoversMonitor(PixelBounds window, PixelBounds monitor, int tolerancePixels = 8)
    {
        if (!window.IsValid || !monitor.IsValid || tolerancePixels < 0)
        {
            return false;
        }

        return window.Left <= monitor.Left + tolerancePixels &&
               window.Top <= monitor.Top + tolerancePixels &&
               window.Right >= monitor.Right - tolerancePixels &&
               window.Bottom >= monitor.Bottom - tolerancePixels;
    }
}
