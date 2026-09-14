using System.Windows;

namespace Wisp.App;

internal readonly record struct PowerTorqueGaugeLayout(
    Size Size,
    Rect PowerBounds,
    Rect TorqueBounds)
{
    internal const double GaugeDiameter = 136;
    internal const double NativeSatelliteLeft = 276;
    internal const double NativeSecondColumnLeft = 410;
    internal const double RowGap = 2;

    internal static PowerTorqueGaugeLayout Calculate(
        Size existingSize,
        double top,
        bool showPower,
        bool showTorque,
        double scale,
        bool besideNativeAnalogSatellites = false)
    {
        if (!showPower && !showTorque)
            return new(existingSize, Rect.Empty, Rect.Empty);

        scale = double.IsFinite(scale) ? Math.Clamp(scale, 0.5, 2) : 1;
        var diameter = GaugeDiameter * scale;
        var gap = RowGap * scale;
        // Analogue dial artwork occupies .43 of its square from the center.
        // Share the transparent edge padding, retaining space between the actual rims.
        var left = besideNativeAnalogSatellites ? NativeSecondColumnLeft : existingSize.Width + 2;
        var power = showPower ? new Rect(left, top, diameter, diameter) : Rect.Empty;
        var torque = showTorque
            ? new Rect(left, top + (showPower ? diameter + gap : 0), diameter, diameter)
            : Rect.Empty;
        var bottom = showTorque ? torque.Bottom : power.Bottom;
        return new(new Size(left + diameter, Math.Max(existingSize.Height, bottom + 4)), power, torque);
    }
}

internal static class DetachedSupplementaryGaugeLayout
{
    // Slot order retains boost's default lower-right position. The remaining
    // default slots are reserved even while their gauges are attached or off.
    internal static Point Place(Rect workArea, Rect anchor, Size subject, Size cell, int slot)
    {
        cell = new Size(Math.Max(subject.Width, cell.Width), Math.Max(subject.Height, cell.Height));
        var gap = OverlayPlacementGeometry.DefaultGap;
        var groupSize = new Size(cell.Width * 2 + gap, cell.Height * 2 + gap);
        var group = OverlayPlacementGeometry.PlaceAbove(workArea, anchor, groupSize);
        var right = slot is 0 or 3;
        var lower = slot is 0 or 1;
        var position = new Point(group.X + (right ? cell.Width + gap : 0) + cell.Width - subject.Width,
            group.Y + (lower ? cell.Height + gap : 0) + cell.Height - subject.Height);
        return OverlayPlacementGeometry.ClampInside(workArea, subject, position);
    }
}
