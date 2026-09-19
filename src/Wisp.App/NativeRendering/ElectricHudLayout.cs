using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wisp.App.NativeRendering;

internal sealed record ElectricHudLayout(
    AnalogHudRect Dial, AnalogHudRect DialNumber,
    AnalogHudRect Gear, AnalogHudRect GearArc, AnalogHudRect PreviousGear, AnalogHudRect NextGear,
    AnalogHudRect Needle, AnalogHudPoint NeedlePivot,
    AnalogHudAssistLayout Abs, AnalogHudAssistLayout Tcr, AnalogHudAssistLayout Lc, AnalogHudAssistLayout Stm,
    AnalogHudRect Unit, AnalogHudRect Hundreds, AnalogHudRect Tens, AnalogHudRect Ones,
    AnalogHudRect PowerBar, AnalogHudRect RegenLabel, AnalogHudRect PowerLabel,
    double DpiScaleX = 1, bool LayoutRounding = false)
{
    // The centered negative-margin root starts at (10,28) within the 345-DIP host.
    internal static ElectricHudLayout Authored { get; } = new(
        new(10, 30.5, 345, 320), new(172.5, 193.5, 20, 14),
        new(170, 168.5, 25, 30), new(153.5, 154, 58, 22),
        new(149.5, 180.5, 16, 24), new(199.5, 180.5, 16, 24),
        new(233, 110.5, 94, 180), new(182.5, 200.5),
        new(new(155.5, 115, 54, 33), new(182.5, 200.5)),
        new(new(155.5, 115, 54, 33), new(182.5, 200.5)),
        new(new(155.5, 115, 54, 33), new(182.5, 200.5)),
        new(new(155.5, 115, 54, 33), new(182.5, 200.5)),
        new(257, 279.25, 42, 17.25),
        new(103.25, 208.5, 60, 93), new(153.75, 208.5, 60, 93), new(204.25, 208.5, 60, 93),
        new(80.5, 310, 204, 16), new(41, 309.5, 33.5, 21), new(290.5, 309.5, 33.5, 21));

    internal static ElectricHudLayout Capture(NativeElectricAnalogSpeedometer control)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.Dispatcher.VerifyAccess();
        if (!control.IsArrangeValid)
            throw new InvalidOperationException("Arrange the electric gauge before capturing its layout.");
        var fallback = Authored;
        var needle = Element(control, "Needle");
        var needleBounds = Bounds(control, needle);
        var powerPanel = (StackPanel)Element(control, "PowerBarPanel");
        var powerBar = (FrameworkElement)powerPanel.Children[1];
        return new(
            BoundsOr(control, "DialImage", fallback.Dial),
            BoundsOr(control, "DialNumber4", fallback.DialNumber),
            BoundsOr(control, "GearImage", fallback.Gear),
            BoundsOr(control, "GearArcImage", fallback.GearArc),
            Adjacent(control, "PreviousGearImage", fallback.PreviousGear),
            Adjacent(control, "NextGearImage", fallback.NextGear),
            BoundsOr(control, "NeedleMaterial", fallback.Needle),
            needleBounds.Width > 0 && needleBounds.Height > 0
                ? new(needleBounds.X + needleBounds.Width / 2, needleBounds.Y + needleBounds.Height / 2)
                : fallback.NeedlePivot,
            Assist(control, "AbsImage", fallback.Abs), Assist(control, "TcrImage", fallback.Tcr),
            Assist(control, "LcImage", fallback.Lc), Assist(control, "StmImage", fallback.Stm),
            BoundsOr(control, "UnitImage", fallback.Unit),
            BoundsOr(control, "HundredsImage", fallback.Hundreds),
            BoundsOr(control, "TensImage", fallback.Tens),
            BoundsOr(control, "OnesImage", fallback.Ones),
            powerPanel.Visibility == Visibility.Collapsed || powerBar.RenderSize.Width <= 0
                ? fallback.PowerBar : Bounds(control, powerBar),
            BoundsOr(control, "RegenLabelImage", fallback.RegenLabel),
            BoundsOr(control, "PowerLabelImage", fallback.PowerLabel),
            VisualTreeHelper.GetDpi(control).DpiScaleX, control.UseLayoutRounding);
    }

    internal double RoundX(double value) => LayoutRounding ? Math.Round(value * DpiScaleX) / DpiScaleX : value;

    private static AnalogHudAssistLayout Assist(NativeElectricAnalogSpeedometer control, string name, AnalogHudAssistLayout fallback)
    {
        var image = Element(control, name);
        if (image.Visibility == Visibility.Collapsed || image.RenderSize.Width <= 0) return fallback;
        var decorator = (FrameworkElement)VisualTreeHelper.GetParent(image);
        var bounds = Bounds(control, decorator);
        return new(Bounds(control, image), new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
    }

    private static AnalogHudRect Adjacent(NativeElectricAnalogSpeedometer control, string name, AnalogHudRect fallback)
    {
        var element = Element(control, name);
        if (element.Visibility == Visibility.Collapsed || element.RenderSize.Width <= 0) return fallback;
        var bounds = Bounds(control, element);
        var translation = (TranslateTransform)element.RenderTransform;
        return bounds with { X = bounds.X + translation.X, Y = bounds.Y + translation.Y };
    }

    private static AnalogHudRect BoundsOr(NativeElectricAnalogSpeedometer control, string name, AnalogHudRect fallback)
    {
        var element = Element(control, name);
        var bounds = Bounds(control, element);
        return element.Visibility != Visibility.Collapsed && bounds.Width > 0 && bounds.Height > 0 ? bounds : fallback;
    }

    private static FrameworkElement Element(NativeElectricAnalogSpeedometer control, string name) =>
        (FrameworkElement)control.FindName(name);

    private static AnalogHudRect Bounds(NativeElectricAnalogSpeedometer control, FrameworkElement element)
    {
        double x = 0, y = 0;
        // Read layout offsets only. Native commands apply the original rotations/skew.
        for (Visual? current = element; current != control;)
        {
            if (current is null) throw new InvalidOperationException("The visual must belong to the electric gauge.");
            var offset = VisualTreeHelper.GetOffset(current);
            x += offset.X;
            y += offset.Y;
            current = VisualTreeHelper.GetParent(current) as Visual;
        }
        return new(x, y, element.RenderSize.Width, element.RenderSize.Height);
    }
}
