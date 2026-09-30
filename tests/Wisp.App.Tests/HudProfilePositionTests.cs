using System.Windows;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HudProfilePositionTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1080, 480, 300)]
    [InlineData(-1920, 0, 1920, 1080, 620, 180)]
    [InlineData(0, -1080, 1920, 1080, 380, 600)]
    [InlineData(-1280, 0, 1280, 720, 440, 340)]
    public void WholeGaugeStaysOnItsSelectedMonitorAtEveryEdge(double x, double y, double width, double height,
        double gaugeWidth, double gaugeHeight)
    {
        var monitor = new Rect(x, y, width, height);
        var footprint = new Size(gaugeWidth, gaugeHeight);
        foreach (var requested in new[]
        {
            new Point(monitor.Right - 1, monitor.Bottom - 1),
            new Point(monitor.Right + 5000, monitor.Bottom + 5000),
            new Point(monitor.Left - 5000, monitor.Top - 5000)
        })
        {
            var restored = AppController.ClampProfilePosition(new(requested.X, requested.Y, 1, 1), monitor, footprint);
            Assert.True(monitor.Contains(new Rect(restored, footprint)));
        }
        var edge = AppController.ClampProfilePosition(new(monitor.Right - 1, monitor.Bottom - 1, 1, 1), monitor, footprint);
        Assert.Equal(monitor.Right - gaugeWidth, edge.X);
        Assert.Equal(monitor.Bottom - gaugeHeight, edge.Y);
    }

    [Theory]
    [InlineData(0, 0, 120)]
    [InlineData(-1280, 0, 80)]
    [InlineData(0, -1080, 150)]
    public void NativeSavedInsetIsConvertedBeforeClampingTheFullWindow(double left, double top, double inset)
    {
        var monitor = new Rect(left, top, 1280, 1080);
        var footprint = new Size(640, 650);
        var offset = new Vector(0, inset);
        var savedTop = AppController.ClampProfilePosition(new(left, top, 1, 1), monitor, footprint, offset);
        Assert.Equal(top + inset, savedTop.Y);
        Assert.True(monitor.Contains(new Rect(savedTop - offset, footprint)));

        var savedBottom = AppController.ClampProfilePosition(new(left + 1279, top + 1079, 1, 1), monitor, footprint, offset);
        Assert.Equal(monitor.Right - footprint.Width, savedBottom.X);
        Assert.Equal(monitor.Bottom - footprint.Height + inset, savedBottom.Y);
        Assert.True(monitor.Contains(new Rect(savedBottom - offset, footprint)));
    }

    [Fact]
    public void ValidSavedPositionAndNativeInsetRoundTripWithoutMovement()
    {
        var placement = new OverlayPlacement(200, 360, 1, 1);
        var restored = AppController.ClampProfilePosition(placement, new Rect(0, 0, 1920, 1080), new Size(640, 650), new Vector(0, 120));
        Assert.Equal(new Point(200, 360), restored);
    }
}
