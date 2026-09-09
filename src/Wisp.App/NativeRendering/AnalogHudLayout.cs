using System.Windows;
using System.Windows.Media;

namespace Wisp.App.NativeRendering;

internal readonly record struct AnalogHudPoint(double X, double Y);
internal readonly record struct AnalogHudRect(double X, double Y, double Width, double Height);
internal readonly record struct AnalogHudAssistLayout(AnalogHudRect Bounds, AnalogHudPoint Pivot);

internal sealed record AnalogHudLayout(
    AnalogHudRect Material,
    AnalogHudRect Overlay,
    AnalogHudRect Needle,
    AnalogHudPoint NeedlePivot,
    AnalogHudPoint GearCenter,
    AnalogHudAssistLayout Abs,
    AnalogHudAssistLayout Tcr,
    AnalogHudAssistLayout Lc,
    AnalogHudAssistLayout Stm,
    AnalogHudRect Hundreds,
    AnalogHudRect Tens,
    AnalogHudRect Ones,
    double DpiScaleX = 1,
    double DpiScaleY = 1,
    bool LayoutRounding = false)
{
    // Unrounded authored coordinates are useful for offscreen material checks.
    // The live HUD always supplies Capture after WPF has arranged the control.
    internal static AnalogHudLayout Authored { get; } = new(
        new(0, 0, 288, 288), new(0, 0, 288, 285),
        new(178.5, 54, 110, 180), new(144, 144), new(144, 142.5),
        new(new(117, 60, 54, 33), new(144, 142.5)),
        new(new(117, 60, 54, 33), new(144, 142.5)),
        new(new(117, 60, 54, 33), new(144, 142.5)),
        new(new(117, 60, 54, 33), new(144, 142.5)),
        new(104, 191, 60, 93), new(160, 191, 60, 93), new(216, 191, 60, 93));

    internal static AnalogHudLayout Capture(NativeAnalogSpeedometer control)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.Dispatcher.VerifyAccess();
        if (!control.IsArrangeValid)
            throw new InvalidOperationException("Arrange the native gauge before capturing its layout.");
        var overlay = Element(control, "OverlayGrid");
        var needle = Element(control, "Needle");
        var gear = Element(control, "GearImage");
        var dpi = VisualTreeHelper.GetDpi(control);
        var overlayBounds = Bounds(control, overlay);
        var coordinateBounds = Bounds(control, Element(control, "NativeCoordinateGrid"));
        double X(double value) => control.UseLayoutRounding ? Math.Round(value * dpi.DpiScaleX) / dpi.DpiScaleX : value;
        double Y(double value) => control.UseLayoutRounding ? Math.Round(value * dpi.DpiScaleY) / dpi.DpiScaleY : value;
        if (coordinateBounds.Width <= 0 || coordinateBounds.Height <= 0)
            coordinateBounds = new(0, 0, X(288), Y(288));
        if (overlayBounds.Width <= 0 || overlayBounds.Height <= 0)
            overlayBounds = new(coordinateBounds.X, coordinateBounds.Y, coordinateBounds.Width, coordinateBounds.Height - Y(3));
        var fallback = Canonical(overlayBounds, coordinateBounds, dpi, control.UseLayoutRounding);
        var needleBounds = Bounds(control, Element(control, "NeedleMaterial"));
        var needleArranged = needle.RenderSize.Width > 0 && needle.RenderSize.Height > 0;
        return new AnalogHudLayout(
            BoundsOr(control, "MaterialVisual", fallback.Material),
            overlayBounds,
            needleArranged ? needleBounds : fallback.Needle,
            needleArranged ? Center(control, needle) : fallback.NeedlePivot,
            gear.RenderSize.Width > 0 ? Center(control, gear) : fallback.GearCenter,
            Assist(control, "AbsImage", fallback.Abs), Assist(control, "TcrImage", fallback.Tcr),
            Assist(control, "LcImage", fallback.Lc), Assist(control, "StmImage", fallback.Stm),
            BoundsOr(control, "HundredsImage", fallback.Hundreds),
            BoundsOr(control, "TensImage", fallback.Tens),
            BoundsOr(control, "OnesImage", fallback.Ones),
            dpi.DpiScaleX, dpi.DpiScaleY, control.UseLayoutRounding);
    }

    internal AnalogHudRect Gear(bool drive)
    {
        var size = drive ? 68d : 100d;
        var width = Round(size, DpiScaleX);
        var height = Round(size, DpiScaleY);
        return new(GearCenter.X - width / 2, GearCenter.Y - height / 2, width, height);
    }

    internal AnalogHudRect Unit(bool milesPerHour)
    {
        var width = Round(milesPerHour ? 52 : 62, DpiScaleX);
        var height = Round(23, DpiScaleY);
        return new(
            Overlay.X + Overlay.Width - width - Round(milesPerHour ? 2 : 6, DpiScaleX),
            Overlay.Y + Overlay.Height - height - Round(104, DpiScaleY), width, height);
    }

    private double Round(double value, double dpi) => LayoutRounding ? Math.Round(value * dpi) / dpi : value;

    private static AnalogHudAssistLayout Assist(NativeAnalogSpeedometer control, string name, AnalogHudAssistLayout fallback)
    {
        var image = Element(control, name);
        if (image.Visibility == Visibility.Collapsed || image.RenderSize.Width <= 0 || image.RenderSize.Height <= 0)
            return fallback;
        var decorator = (FrameworkElement)VisualTreeHelper.GetParent(image);
        return new(Bounds(control, image), Center(control, decorator));
    }

    private static AnalogHudRect BoundsOr(NativeAnalogSpeedometer control, string name, AnalogHudRect fallback)
    {
        var bounds = Bounds(control, Element(control, name));
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : fallback;
    }

    private static AnalogHudLayout Canonical(AnalogHudRect overlay, AnalogHudRect coordinates, DpiScale dpi, bool round)
    {
        double X(double value) => round ? Math.Round(value * dpi.DpiScaleX) / dpi.DpiScaleX : value;
        double Y(double value) => round ? Math.Round(value * dpi.DpiScaleY) / dpi.DpiScaleY : value;
        var assistWidth = X(54);
        var assistHeight = Y(33);
        var orbitHeight = assistHeight + Y(132);
        var assist = new AnalogHudAssistLayout(
            new(overlay.X + X((overlay.Width - assistWidth) / 2), overlay.Y + Y((overlay.Height - orbitHeight) / 2), assistWidth, assistHeight),
            default);
        assist = assist with { Pivot = new(assist.Bounds.X + assistWidth / 2, assist.Bounds.Y + orbitHeight / 2) };
        var gearCenter = new AnalogHudPoint(overlay.X + X((overlay.Width - X(100)) / 2) + X(100) / 2,
            overlay.Y + Y((overlay.Height - Y(100)) / 2) + Y(100) / 2);
        var digitTop = overlay.Y + overlay.Height - Y(1) - Y(93);
        var digitRight = overlay.X + overlay.Width - X(12);
        return new AnalogHudLayout(
            new(0, 0, X(288), Y(288)), overlay,
            new(coordinates.X + X(178.5), coordinates.Y + Y(54), X(110), Y(180)),
            new(coordinates.X + X(288) / 2, coordinates.Y + Y(288) / 2), gearCenter,
            assist, assist, assist, assist,
            new(digitRight - X(60) - 2 * (X(60) - X(4)), digitTop, X(60), Y(93)),
            new(digitRight - X(60) - (X(60) - X(4)), digitTop, X(60), Y(93)),
            new(digitRight - X(60), digitTop, X(60), Y(93)),
            dpi.DpiScaleX, dpi.DpiScaleY, round);
    }

    private static FrameworkElement Element(NativeAnalogSpeedometer control, string name) =>
        (FrameworkElement)control.FindName(name);

    private static AnalogHudPoint Center(NativeAnalogSpeedometer control, FrameworkElement element)
    {
        var bounds = Bounds(control, element);
        return new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
    }

    private static AnalogHudRect Bounds(NativeAnalogSpeedometer control, FrameworkElement element)
    {
        double x = 0, y = 0;
        // Layout offsets exclude RenderTransform. This recovers the authored
        // needle and assist quads without modifying their live rotations.
        for (Visual? current = element; current != control;)
        {
            if (current is null)
                throw new InvalidOperationException("The visual must belong to the analogue gauge.");
            var offset = VisualTreeHelper.GetOffset(current);
            x += offset.X;
            y += offset.Y;
            current = VisualTreeHelper.GetParent(current) as Visual;
        }
        return new(x, y, element.RenderSize.Width, element.RenderSize.Height);
    }
}
