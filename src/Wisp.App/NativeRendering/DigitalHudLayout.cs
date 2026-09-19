using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisp.Core;

namespace Wisp.App.NativeRendering;

// Digital assists use opposing parent/child skews. Capture the final affine quad
// so layout rounding, collapsed rows and the original margins remain intact.
internal readonly record struct DigitalHudQuad(AnalogHudPoint Origin, AnalogHudPoint XAxis, AnalogHudPoint YAxis)
{
    internal static DigitalHudQuad Rect(double x, double y, double width, double height) =>
        new(new(x, y), new(width, 0), new(0, height));

    internal DirectCompositionDrawCommand Command(uint textureId, double opacity = 1,
        AnalogHudColor? color = null, DirectCompositionShader shader = DirectCompositionShader.Image)
    {
        var command = AnalogHudScene.Quad(textureId, default, opacity: opacity, color: color, shader: shader);
        command.OriginX = (float)Origin.X;
        command.OriginY = (float)Origin.Y;
        command.AxisXX = (float)XAxis.X;
        command.AxisXY = (float)XAxis.Y;
        command.AxisYX = (float)YAxis.X;
        command.AxisYY = (float)YAxis.Y;
        return command;
    }
}

internal sealed record DigitalHudLayout(
    DigitalHudQuad Gear, DigitalHudQuad GearGauge, DigitalHudQuad NextGear,
    DigitalHudQuad Hundreds, DigitalHudQuad Tens, DigitalHudQuad Ones, DigitalHudQuad Unit,
    DigitalHudQuad Stm, DigitalHudQuad Abs, DigitalHudQuad Lc, DigitalHudQuad Tcr,
    DigitalHudQuad Gauge, DigitalHudQuad PowerBar, DigitalHudQuad RegenLabel, DigitalHudQuad PowerLabel,
    double DpiScaleX = 1, bool LayoutRounding = false)
{
    internal static DigitalHudLayout Capture(NativeDigitalSpeedometer control) => Capture(control, control.Frame);
    internal static DigitalHudLayout Capture(NativeElectricDigitalSpeedometer control) => Capture(control, control.Frame);

    private static DigitalHudLayout Capture(UserControl control, NativeGaugeFrame frame)
    {
        control.Dispatcher.VerifyAccess();
        if (!control.IsArrangeValid)
            throw new InvalidOperationException("Arrange the digital gauge before capturing its layout.");
        var fallback = Authored(frame);
        DigitalHudQuad Read(string name, DigitalHudQuad otherwise)
        {
            if (control.FindName(name) is not FrameworkElement element ||
                element.Visibility == Visibility.Collapsed || element.RenderSize.Width <= 0 || element.RenderSize.Height <= 0)
                return otherwise;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null && parent != control; parent = VisualTreeHelper.GetParent(parent))
                if (parent is UIElement { Visibility: Visibility.Collapsed }) return otherwise;
            var transform = element.TransformToAncestor(control);
            var origin = transform.Transform(new Point());
            var x = transform.Transform(new Point(element.RenderSize.Width, 0));
            var y = transform.Transform(new Point(0, element.RenderSize.Height));
            return new(new(origin.X, origin.Y), new(x.X - origin.X, x.Y - origin.Y), new(y.X - origin.X, y.Y - origin.Y));
        }
        return new(Read("GearImage", fallback.Gear), Read("GearGaugeImage", fallback.GearGauge), Read("NextGearImage", fallback.NextGear),
            Read("HundredsImage", fallback.Hundreds), Read("TensImage", fallback.Tens), Read("OnesImage", fallback.Ones),
            Read("UnitImage", fallback.Unit), Read("StmImage", fallback.Stm), Read("AbsImage", fallback.Abs),
            Read("LcImage", fallback.Lc), Read("TcrImage", fallback.Tcr), Read("GaugeVisual", fallback.Gauge),
            Read("PowerBarGrid", fallback.PowerBar), Read("RegenLabelImage", fallback.RegenLabel), Read("PowerLabelImage", fallback.PowerLabel),
            VisualTreeHelper.GetDpi(control).DpiScaleX, control.UseLayoutRounding);
    }

    internal double RoundX(double value) => LayoutRounding ? Math.Round(value * DpiScaleX) / DpiScaleX : value;

    // Authored, unrounded coordinates for offscreen scenes. Live rendering uses Capture.
    internal static DigitalHudLayout Authored(NativeGaugeFrame frame)
    {
        var multi = frame.IsElectric && NativeElectricGearModel.IsMultiGear(frame.ElectricGearState);
        var offset = multi ? 19d : 0;
        var assists = frame.NativeAssists;
        var available = new[] { assists.IsSTMAvailable, assists.IsABSAvailable, assists.IsLCAvailable, assists.IsTCRAvailable };
        var count = assists.Available ? available.Count(value => value) : 0;
        var rows = new DigitalHudQuad[4];
        var row = 0;
        var skew = Math.Tan(11 * Math.PI / 180);
        for (var index = 0; index < rows.Length; index++)
        {
            rows[index] = DigitalHudQuad.Rect(264.5 + offset + skew * (count * 20 - row * 20 - 26),
                134.5 - count * 20 + row * 20, 54, 32);
            if (assists.Available && available[index]) row++;
        }
        var barWidth = multi ? 234 : 215;
        var bar = DigitalHudQuad.Rect(59.5, 144, barWidth, 16);
        bar = bar with { YAxis = new(Math.Tan(-8 * Math.PI / 180) * 16, 16) };
        return new(DigitalHudQuad.Rect(26 + offset, 69.5, 68, 68),
            DigitalHudQuad.Rect(25.5, 41, 34, 98), DigitalHudQuad.Rect(55.5 + offset, 40.5, 25, 30),
            DigitalHudQuad.Rect(91.5 + offset, 35.5, 70, 106), DigitalHudQuad.Rect(151.5 + offset, 35.5, 70, 106),
            DigitalHudQuad.Rect(211.5 + offset, 35.5, 70, 106),
            DigitalHudQuad.Rect(269.5 + offset + (count > 0 ? skew * (4 + count * 20) : 0),
                count > 0 ? 114.5 - count * 20 : 118.5, 44, 23),
            rows[0], rows[1], rows[2], rows[3], DigitalHudQuad.Rect(11, 136.5, 302, 24),
            bar, DigitalHudQuad.Rect(20, 143.5, 33.5, 21), DigitalHudQuad.Rect(65.5 + barWidth, 143.5, 33.5, 21));
    }
}
