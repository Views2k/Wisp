using System.Windows;
using Wisp.App.Drift;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DriftGaugePlacementTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1080, 1)]
    [InlineData(-1920, 0, 1920, 1080, 1.5)]
    [InlineData(1920, 40, 1280, 720, 2)]
    [InlineData(0, 0, 640, 360, 2)]
    public void TopCenterPlacementFitsTheChosenMonitorAtEveryAllowedScale(double x, double y, double width, double height, double scale)
    {
        var area = new Rect(x, y, width, height);
        var size = DriftGaugeWindow.FitSize(area, scale);
        var position = DriftGaugeWindow.DefaultPosition(area, size);
        Assert.Equal(area.Top + 24, position.Y, 6);
        Assert.Equal(area.Left + area.Width / 2, position.X + size.Width / 2, 6);
        Assert.True(new Rect(position, size).Left >= area.Left + 24 - .001);
        Assert.True(new Rect(position, size).Right <= area.Right - 24 + .001);
        Assert.True(new Rect(position, size).Bottom <= area.Bottom - 24 + .001);
        Assert.Equal(DriftGaugeWindow.BaseWidth / DriftGaugeWindow.BaseHeight, size.Width / size.Height, 6);
    }

    [Fact]
    public void NormalMonitorKeepsRequestedSizeAndNonfiniteScaleUsesTheDefault()
    {
        var area = new Rect(0, 0, 3840, 2160);
        Assert.Equal(new Size(1240, 216), DriftGaugeWindow.FitSize(area, 2));
        Assert.Equal(new Size(620, 108), DriftGaugeWindow.FitSize(area, double.NaN));
        Assert.Equal(new Size(310, 54), DriftGaugeWindow.FitSize(area, .1));
    }
}
