using System.Windows;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class GForceTrailHistoryTests
{
    [Fact]
    public void MovementThresholdRejectsJiggleButAcceptsRealMotion()
    {
        var history = new GForceTrailHistory();

        Assert.True(history.TryAdd(new Point(4, 7)));
        Assert.False(history.TryAdd(new Point(4.25, 7.25)));
        Assert.True(history.TryAdd(new Point(5, 7)));

        Assert.Equal(2, history.Count);
        Assert.Equal(new Point(4, 7), history[0]);
        Assert.Equal(new Point(5, 7), history[1]);
    }

    [Fact]
    public void CapacityRetainsOnlyTheNewestTrajectory()
    {
        var history = new GForceTrailHistory();
        var total = GForceTrailHistory.Capacity + 3;

        for (var index = 0; index < total; index++)
        {
            Assert.True(history.TryAdd(new Point(index * 2, -index)));
        }

        Assert.Equal(GForceTrailHistory.Capacity, history.Count);
        for (var index = 0; index < history.Count; index++)
        {
            var sourceIndex = index + 3;
            Assert.Equal(new Point(sourceIndex * 2, -sourceIndex), history[index]);
        }
    }

    [Fact]
    public void ClearDropsThePreviousTrajectory()
    {
        var history = new GForceTrailHistory();
        history.TryAdd(new Point(2, 3));
        history.TryAdd(new Point(4, 5));

        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.True(history.TryAdd(new Point(-1, -2)));
        Assert.Equal(new Point(-1, -2), history[0]);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NegativeInfinity)]
    public void NonFiniteSamplesAreIgnored(double x, double y)
    {
        var history = new GForceTrailHistory();

        Assert.False(history.TryAdd(new Point(x, y)));
        Assert.Equal(0, history.Count);
    }
}
