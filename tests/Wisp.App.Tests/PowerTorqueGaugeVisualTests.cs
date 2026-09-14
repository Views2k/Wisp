using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Wisp.App.Tests;

internal static class PowerTorqueGaugeVisualTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var gauge = new PowerTorqueGaugeView { Width = 140, Height = 140, Maximum = 1_000 };
        gauge.Measure(new Size(140, 140));
        gauge.Arrange(new Rect(0, 0, 140, 140));
        foreach (var (value, angle) in new[] { (-100d, 135d), (0d, 135d), (500d, 270d), (1_000d, 405d), (1_500d, 405d) })
        {
            gauge.Display = new PowerTorqueDisplay(true, value, value, 1_500, 1_500);
            Assert.True(gauge.HasVisibleNeedle);
            Assert.Equal(value, gauge.DisplayedValue);
            Assert.Equal(angle, gauge.CurrentNeedleAngle, 8);
        }
        gauge.Display = PowerTorqueDisplay.Unavailable;
        Assert.False(gauge.HasVisibleNeedle);
        gauge.Display = new PowerTorqueDisplay(true, 427, 507, 612, 690);
        gauge.IsTorque = true;
        gauge.Maximum = 1_200;
        Assert.Equal(507, gauge.DisplayedValue);
        Assert.Equal(135 + 270 * 507d / 1_200, gauge.CurrentNeedleAngle, 8);
        gauge.TorqueUnit = TorqueUnit.PoundFeet;
        Assert.Equal(373.94400968357366, gauge.DisplayedValue, 8);
        Assert.Equal(135 + 270 * gauge.DisplayedValue / 1_200, gauge.CurrentNeedleAngle, 8);
        gauge.IsTorque = false;
        gauge.Maximum = 1_000;
        Assert.Equal(427, gauge.DisplayedValue);
        Assert.Equal(135 + 270 * 427d / 1_000, gauge.CurrentNeedleAngle, 8);

        foreach (var value in new[] { -999d, 0d, 427d, 1_500d, 12_000d })
        {
            gauge.Display = new PowerTorqueDisplay(true, value, 0, 0, 0);
            var drawing = RenderMethod(gauge, "DrawValue");
            var leaves = Flatten(drawing).ToArray();
            var digitCount = Math.Abs(value).ToString("0", System.Globalization.CultureInfo.InvariantCulture).Length;
            Assert.Equal(digitCount, leaves.OfType<ImageDrawing>().Count());
            Assert.Equal(value < 0 ? 1 : 0, leaves.OfType<GeometryDrawing>().Count(item => item.Geometry is LineGeometry));
            Assert.True(drawing.Bounds.Width <= 48.01);
        }

        gauge.Display = new PowerTorqueDisplay(true, 427, 507, 612, 690);
        RenderMethod(gauge, "OnRender");
        var field = typeof(PowerTorqueGaugeView).GetField("_dial", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalDial = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(originalDial.IsFrozen);
        gauge.Display = gauge.Display with { PowerBhp = 500, PeakPowerBhp = 650 };
        RenderMethod(gauge, "OnRender");
        Assert.Same(originalDial, field.GetValue(gauge));
        gauge.Maximum = 2_000;
        RenderMethod(gauge, "OnRender");
        Assert.NotSame(originalDial, field.GetValue(gauge));
        Assert.Equal(135 + 270 * 500d / 2_000, gauge.CurrentNeedleAngle, 8);

        foreach (var maximum in new[] { 0, -1d, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => gauge.Maximum = maximum);
        gauge.Display = new PowerTorqueDisplay(true, double.NaN, 0, 0, 0);
        Assert.False(gauge.HasVisibleNeedle);
        Assert.Equal(135, gauge.CurrentNeedleAngle);
    }

    private static DrawingGroup RenderMethod(PowerTorqueGaugeView gauge, string name)
    {
        var drawing = new DrawingGroup();
        using var dc = drawing.Open();
        typeof(PowerTorqueGaugeView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(gauge, [dc]);
        return drawing;
    }

    private static IEnumerable<Drawing> Flatten(Drawing drawing) => drawing is DrawingGroup group
        ? group.Children.SelectMany(Flatten) : [drawing];
}
