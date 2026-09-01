using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class FullscreenBoundsPolicyTests
{
    private static readonly PixelBounds Monitor = new(0, 0, 2560, 1440);

    [Fact]
    public void DetectsExactMonitorCoverage()
    {
        Assert.True(FullscreenBoundsPolicy.CoversMonitor(Monitor, Monitor));
    }

    [Fact]
    public void AllowsSmallBorderAndShadowDifferences()
    {
        var window = new PixelBounds(-6, -4, 2564, 1446);

        Assert.True(FullscreenBoundsPolicy.CoversMonitor(window, Monitor));
    }

    [Fact]
    public void RejectsAWindowThatLeavesTheTaskbarAreaVisible()
    {
        var maximizedWindow = new PixelBounds(0, 0, 2560, 1400);

        Assert.False(FullscreenBoundsPolicy.CoversMonitor(maximizedWindow, Monitor));
    }

    [Fact]
    public void RejectsInvalidBoundsOrTolerance()
    {
        Assert.False(FullscreenBoundsPolicy.CoversMonitor(default, Monitor));
        Assert.False(FullscreenBoundsPolicy.CoversMonitor(Monitor, default));
        Assert.False(FullscreenBoundsPolicy.CoversMonitor(Monitor, Monitor, -1));
    }
}
