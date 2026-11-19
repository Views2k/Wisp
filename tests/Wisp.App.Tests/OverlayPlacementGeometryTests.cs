using System.Windows;
using Xunit;

namespace Wisp.App.Tests;

public sealed class OverlayPlacementGeometryTests
{
    // A second monitor to the left of the primary one, offset downward.
    private static readonly Rect Monitor = new(-1920, 40, 1920, 1080);
    private static readonly Size Gauge = new(620, 108);

    [Theory]
    [InlineData(-1920, 40)]
    [InlineData(-620, 40)]
    [InlineData(-1920, 1012)]
    [InlineData(-620, 1012)]
    [InlineData(-1270, 40)]
    [InlineData(-1000, 500)]
    public void AChosenPositionMayTouchAnyEdgeOfItsMonitor(double left, double top)
    {
        var chosen = new Point(left, top);

        Assert.Equal(chosen, OverlayPlacementGeometry.ClampOnScreen(Monitor, Gauge, chosen));
    }

    [Theory]
    [InlineData(-5000, -5000, -1920, 40)]
    [InlineData(5000, 5000, -620, 1012)]
    [InlineData(-1930, 30, -1920, 40)]
    [InlineData(-610, 1020, -620, 1012)]
    public void APositionOffTheMonitorComesBackToItsEdgeWithoutAMargin(double left, double top, double expectedLeft, double expectedTop)
    {
        var position = OverlayPlacementGeometry.ClampOnScreen(Monitor, Gauge, new Point(left, top));

        Assert.Equal(new Point(expectedLeft, expectedTop), position);
        Assert.True(Monitor.Contains(new Rect(position, Gauge)));
    }

    [Fact]
    public void AnElementLargerThanItsMonitorStartsAtTheTopLeftCorner()
    {
        var position = OverlayPlacementGeometry.ClampOnScreen(Monitor, new Size(4000, 3000), new Point(-100, 300));

        Assert.Equal(Monitor.TopLeft, position);
    }

    [Fact]
    public void DefaultPlacementsKeepTheirMargin()
    {
        var workArea = new Rect(0, 0, 1920, 1040);

        Assert.Equal(new Point(24, 24), OverlayPlacementGeometry.ClampInside(workArea, Gauge, new Point(0, 0)));
        Assert.Equal(new Point(1920 - 24 - 620, 24), OverlayPlacementGeometry.PlaceTopRight(workArea, Gauge));
    }

    // In order: at the default margin it keeps the margin; against the edge it stays against it; ten away
    // it stays ten away; far away but growing past the edge it gets the default margin; still inside
    // after growing it does not move.
    [Theory]
    [InlineData(-214, 24, 190, 390, -414)]
    [InlineData(-190, 0, 190, 390, -390)]
    [InlineData(-200, 10, 190, 390, -400)]
    [InlineData(-400, 210, 190, 500, -524)]
    [InlineData(-1000, 810, 190, 390, -1000)]
    public void AnElementWispResizesKeepsItsGapToTheEdgeItWouldCross(double left, double gap, double width, double nextWidth, double expectedLeft)
    {
        var previous = new Rect(left, 64, width, 100);
        Assert.Equal(gap, Monitor.Right - previous.Right);

        var position = OverlayPlacementGeometry.KeepEdgeGaps(Monitor, previous, new Size(nextWidth, 100), previous.TopLeft);

        Assert.Equal(new Point(expectedLeft, 64), position);
    }

    [Fact]
    public void AnElementWispResizesKeepsItsGapToTheBottomEdgeAndIgnoresAnUnknownPosition()
    {
        var previous = new Rect(-1000, 996, 300, 100);
        Assert.Equal(new Point(-1000, 896), OverlayPlacementGeometry.KeepEdgeGaps(Monitor, previous, new Size(300, 200), previous.TopLeft));
        var unknown = new Point(double.NaN, double.NaN);
        var kept = OverlayPlacementGeometry.KeepEdgeGaps(Monitor, Rect.Empty, new Size(300, 200), unknown);
        Assert.True(double.IsNaN(kept.X) && double.IsNaN(kept.Y));
    }

    [Fact]
    public void TheNativeLayoutUsesTheSameRule()
    {
        foreach (var requested in new[] { new Point(-1920, 40), new Point(-5000, 5000), new Point(-700, 600) })
            Assert.Equal(
                OverlayPlacementGeometry.ClampOnScreen(Monitor, Gauge, requested),
                OverlayPlacementGeometry.ClampNativeInside(Monitor, Gauge, requested));
    }
}
