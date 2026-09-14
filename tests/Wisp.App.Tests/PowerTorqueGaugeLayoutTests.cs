using System.Windows;
using Xunit;

namespace Wisp.App.Tests;

public sealed class PowerTorqueGaugeLayoutTests
{
    [Fact]
    public void DisabledGaugesLeaveExistingBoundsUnchanged()
    {
        var size = new Size(416, 365.5);
        var layout = PowerTorqueGaugeLayout.Calculate(size, 76, false, false, 2);
        Assert.Equal(size, layout.Size);
        Assert.True(layout.PowerBounds.IsEmpty);
        Assert.True(layout.TorqueBounds.IsEmpty);
    }

    [Theory]
    [InlineData(416, 365.5, 76, 0.5)]
    [InlineData(416, 365.5, 76, 1)]
    [InlineData(416, 365.5, 76, 2)]
    [InlineData(327.5, 232, 76, 2)]
    [InlineData(345, 417, 76, 1)]
    [InlineData(160, 120, 4, 1)]
    [InlineData(390, 166, 4, 1)]
    public void AttachedPairStaysOutsideExistingArtworkAndInsideWindow(
        double width, double height, double top, double scale)
    {
        var layout = PowerTorqueGaugeLayout.Calculate(new Size(width, height), top, true, true, scale);
        var window = new Rect(layout.Size);
        Assert.True(layout.PowerBounds.Left > width);
        Assert.True(layout.TorqueBounds.Top > layout.PowerBounds.Bottom);
        Assert.True(window.Contains(layout.PowerBounds));
        Assert.True(window.Contains(layout.TorqueBounds));
        Assert.Equal(top, layout.PowerBounds.Top);
        Assert.Equal(140 * scale, layout.PowerBounds.Width);
        Assert.Equal(layout.PowerBounds.Width, layout.PowerBounds.Height);
    }

    [Fact]
    public void TorqueAloneUsesTheTopSlot()
    {
        var layout = PowerTorqueGaugeLayout.Calculate(new Size(416, 365.5), 76, false, true, 1);
        Assert.True(layout.PowerBounds.IsEmpty);
        Assert.Equal(76, layout.TorqueBounds.Top);
        Assert.Equal(365.5, layout.Size.Height);
    }

    [Theory]
    [InlineData(double.NaN, 140)]
    [InlineData(double.PositiveInfinity, 140)]
    [InlineData(0, 70)]
    [InlineData(5, 280)]
    public void InvalidScaleCannotBreakLayout(double scale, double diameter)
    {
        var layout = PowerTorqueGaugeLayout.Calculate(new Size(416, 365.5), 76, true, false, scale);
        Assert.Equal(diameter, layout.PowerBounds.Width);
        Assert.Equal(diameter, layout.PowerBounds.Height);
    }
}
