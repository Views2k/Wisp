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
        bool besideNativeAnalogSatellites = false) =>
        Calculate(existingSize, top, showPower, showTorque, scale, scale, besideNativeAnalogSatellites);

    internal static PowerTorqueGaugeLayout Calculate(
        Size existingSize,
        double top,
        bool showPower,
        bool showTorque,
        double powerScale,
        double torqueScale,
        bool besideNativeAnalogSatellites = false)
    {
        if (!showPower && !showTorque)
            return new(existingSize, Rect.Empty, Rect.Empty);

        var powerDiameter = Diameter(powerScale);
        var torqueDiameter = Diameter(torqueScale);
        var columnWidth = Math.Max(showPower ? powerDiameter : 0, showTorque ? torqueDiameter : 0);
        var gap = RowGap * Math.Max(NormalizeScale(powerScale), NormalizeScale(torqueScale));
        // Analogue dial artwork occupies .43 of its square from the center.
        // Share the transparent edge padding, retaining space between the actual rims.
        var left = besideNativeAnalogSatellites ? NativeSecondColumnLeft : existingSize.Width + 2;
        var power = showPower ? new Rect(left + (columnWidth - powerDiameter) / 2, top,
            powerDiameter, powerDiameter) : Rect.Empty;
        var torque = showTorque
            ? new Rect(left + (columnWidth - torqueDiameter) / 2,
                top + (showPower ? powerDiameter + gap : 0), torqueDiameter, torqueDiameter)
            : Rect.Empty;
        var bottom = showTorque ? torque.Bottom : power.Bottom;
        return new(new Size(Math.Max(existingSize.Width, left + columnWidth),
            Math.Max(existingSize.Height, bottom + 4)), power, torque);
    }

    internal static double NormalizeScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, .5, 2) : 1;
    internal static double Diameter(double scale) => GaugeDiameter * NormalizeScale(scale);
}

internal readonly record struct AnalogSupplementaryGaugeLayout(
    Size Size, Rect BoostBounds, Rect TireBounds, Rect PowerBounds, Rect TorqueBounds)
{
    internal static AnalogSupplementaryGaugeLayout Calculate(
        Size existingSize, double top,
        bool showBoost, bool showTire, bool showPower, bool showTorque,
        double boostScale, double tireScale, double powerScale, double torqueScale,
        double firstColumnLeft = PowerTorqueGaugeLayout.NativeSatelliteLeft)
    {
        if (!showBoost && !showTire && !showPower && !showTorque)
            return new(existingSize, Rect.Empty, Rect.Empty, Rect.Empty, Rect.Empty);

        var boost = PowerTorqueGaugeLayout.Diameter(boostScale);
        var tire = PowerTorqueGaugeLayout.Diameter(tireScale);
        var power = PowerTorqueGaugeLayout.Diameter(powerScale);
        var torque = PowerTorqueGaugeLayout.Diameter(torqueScale);
        var hasLeft = showBoost || showTire;
        var hasRight = showPower || showTorque;
        var hasUpper = showBoost || showPower;
        var hasLower = showTire || showTorque;
        var leftWidth = hasLeft ? Math.Max(PowerTorqueGaugeLayout.GaugeDiameter,
            Math.Max(showBoost ? boost : 0, showTire ? tire : 0)) : 0;
        var rightWidth = hasRight ? Math.Max(PowerTorqueGaugeLayout.GaugeDiameter,
            Math.Max(showPower ? power : 0, showTorque ? torque : 0)) : 0;
        var upperHeight = hasUpper ? Math.Max(PowerTorqueGaugeLayout.GaugeDiameter,
            Math.Max(showBoost ? boost : 0, showPower ? power : 0)) : 0;
        var lowerHeight = hasLower ? Math.Max(PowerTorqueGaugeLayout.GaugeDiameter,
            Math.Max(showTire ? tire : 0, showTorque ? torque : 0)) : 0;

        // Rims occupy .43 of each square. Sharing two DIPs of transparent side
        // padding keeps the original compact grid, even for unequal dial sizes.
        var rightLeft = firstColumnLeft + (hasLeft ? leftWidth - 2 : 0);
        var lowerTop = top + (hasUpper ? upperHeight + PowerTorqueGaugeLayout.RowGap : 0);
        var boostBounds = Centered(showBoost, firstColumnLeft, top, leftWidth, upperHeight, boost);
        var tireBounds = Centered(showTire, firstColumnLeft, lowerTop, leftWidth, lowerHeight, tire);
        var powerBounds = Centered(showPower, rightLeft, top, rightWidth, upperHeight, power);
        var torqueBounds = Centered(showTorque, rightLeft, lowerTop, rightWidth, lowerHeight, torque);
        var right = hasRight ? rightLeft + rightWidth : firstColumnLeft + leftWidth;
        var bottom = hasLower ? lowerTop + lowerHeight : top + upperHeight;
        return new(new Size(Math.Max(existingSize.Width, right), Math.Max(existingSize.Height, bottom + 4)),
            boostBounds, tireBounds, powerBounds, torqueBounds);
    }

    private static Rect Centered(bool visible, double left, double top, double width, double height, double diameter) =>
        visible ? new Rect(left + (width - diameter) / 2, top + (height - diameter) / 2, diameter, diameter) : Rect.Empty;
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
