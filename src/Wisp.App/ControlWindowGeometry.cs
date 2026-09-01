using System.Windows;

namespace Wisp.App;

public static class ControlWindowGeometry
{
    public const double WorkAreaInset = 12;

    public static Size FitToWorkArea(Size desiredSize, Size workAreaSize)
    {
        var availableWidth = Math.Max(320, workAreaSize.Width - (WorkAreaInset * 2));
        var availableHeight = Math.Max(320, workAreaSize.Height - (WorkAreaInset * 2));
        return new Size(
            Math.Min(desiredSize.Width, availableWidth),
            Math.Min(desiredSize.Height, availableHeight));
    }

    public static Size FitToPhysicalWorkArea(
        Size desiredSize,
        Size physicalWorkAreaSize,
        double dpiScaleX,
        double dpiScaleY)
    {
        var scaleX = double.IsFinite(dpiScaleX) && dpiScaleX > 0 ? dpiScaleX : 1;
        var scaleY = double.IsFinite(dpiScaleY) && dpiScaleY > 0 ? dpiScaleY : 1;
        return FitToWorkArea(
            desiredSize,
            new Size(
                physicalWorkAreaSize.Width / scaleX,
                physicalWorkAreaSize.Height / scaleY));
    }
}
