using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeHostPlacementTests
{
    [Theory]
    [InlineData(0, 0, 3840, 2160, 3000, 1500, 3600, 2100)]
    [InlineData(-2560, -1440, 0, 0, -2400, -1300, -1800, -700)]
    [InlineData(3840, -600, 6400, 840, 4000, -500, 4700, 300)]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1920, 1080)]
    public void ContainedMainHudUsesMonitorBoundsAndPreservesScreenPosition(
        int monitorLeft, int monitorTop, int monitorRight, int monitorBottom,
        int left, int top, int right, int bottom)
    {
        var monitor = new NativeHostBounds(monitorLeft, monitorTop, monitorRight, monitorBottom);
        var layout = new NativeHostBounds(left, top, right, bottom);

        var result = NativeHostPlacement.Create(layout, monitor, true);

        Assert.Equal(monitor, result.Bounds);
        Assert.Equal(left, result.Bounds.Left + result.OffsetX);
        Assert.Equal(top, result.Bounds.Top + result.OffsetY);
        Assert.Equal(right - left, layout.Width);
        Assert.Equal(bottom - top, layout.Height);
    }

    [Theory]
    [InlineData(-1, 100, 500, 700)]
    [InlineData(1500, 100, 1921, 700)]
    [InlineData(100, -1, 700, 500)]
    [InlineData(100, 500, 700, 1081)]
    [InlineData(-500, -500, 2400, 1500)]
    [InlineData(2000, 100, 2600, 700)]
    [InlineData(100, 100, 100, 700)]
    [InlineData(100, 100, 700, 100)]
    public void StraddlingOffscreenOrEmptyHudKeepsItsOwnBounds(int left, int top, int right, int bottom)
    {
        var layout = new NativeHostBounds(left, top, right, bottom);

        var result = NativeHostPlacement.Create(layout, new(0, 0, 1920, 1080), true);

        Assert.Equal(new NativeHostPlacement(layout, 0, 0), result);
    }

    [Fact]
    public void AuxiliaryHudKeepsItsOwnBounds()
    {
        var layout = new NativeHostBounds(1500, 700, 1750, 950);

        var result = NativeHostPlacement.Create(layout, new(0, 0, 1920, 1080), false);

        Assert.Equal(new NativeHostPlacement(layout, 0, 0), result);
    }

    [Fact]
    public void MonitorMoveRecalculatesOffsetsWithoutChangingHudSize()
    {
        var beforeLayout = new NativeHostBounds(100, 200, 700, 800);
        var afterLayout = new NativeHostBounds(-1800, 250, -1200, 850);
        var before = NativeHostPlacement.Create(beforeLayout, new(0, 0, 3840, 2160), true);
        var after = NativeHostPlacement.Create(afterLayout, new(-1920, 0, 0, 1080), true);

        Assert.Equal(beforeLayout.Width, afterLayout.Width);
        Assert.Equal(beforeLayout.Height, afterLayout.Height);
        Assert.Equal(100, before.OffsetX);
        Assert.Equal(120, after.OffsetX);
        Assert.Equal(250, after.OffsetY);
        Assert.Equal(afterLayout.Left, after.Bounds.Left + after.OffsetX);
    }
}
