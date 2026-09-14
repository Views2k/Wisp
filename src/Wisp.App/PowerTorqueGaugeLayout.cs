using System.Windows;

namespace Wisp.App;

internal readonly record struct PowerTorqueGaugeLayout(
    Size Size,
    Rect PowerBounds,
    Rect TorqueBounds)
{
    internal static PowerTorqueGaugeLayout Calculate(
        Size existingSize,
        double top,
        bool showPower,
        bool showTorque,
        double scale)
    {
        if (!showPower && !showTorque)
            return new(existingSize, Rect.Empty, Rect.Empty);

        scale = double.IsFinite(scale) ? Math.Clamp(scale, 0.5, 2) : 1;
        var diameter = 140 * scale;
        var gap = 8 * scale;
        var left = existingSize.Width + 8;
        var power = showPower ? new Rect(left, top, diameter, diameter) : Rect.Empty;
        var torque = showTorque
            ? new Rect(left, top + (showPower ? diameter + gap : 0), diameter, diameter)
            : Rect.Empty;
        var bottom = showTorque ? torque.Bottom : power.Bottom;
        return new(new Size(left + diameter, Math.Max(existingSize.Height, bottom + 4)), power, torque);
    }
}
