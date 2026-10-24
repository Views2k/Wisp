using Xunit;

namespace Wisp.App.Tests;

public sealed class UiUpdateGuardTests
{
    [Fact]
    public void FiveFailuresInARowStopTheLoopAndACleanPassResetsTheCount()
    {
        var guard = new UiUpdateGuard();
        for (var attempt = 1; attempt < UiUpdateGuard.MaximumConsecutiveFailures; attempt++)
        {
            Assert.False(guard.Failed());
            Assert.Equal(attempt, guard.ConsecutiveFailures);
        }
        guard.Succeeded();
        Assert.Equal(0, guard.ConsecutiveFailures);
        for (var attempt = 1; attempt < UiUpdateGuard.MaximumConsecutiveFailures; attempt++) Assert.False(guard.Failed());
        Assert.True(guard.Failed());
        Assert.True(guard.Stopped);
        // Once stopped, nothing restarts the loop in this run.
        guard.Succeeded();
        Assert.True(guard.Stopped);
        Assert.True(guard.Failed());
    }

    [Fact]
    public void OnlyMemoryAndStackExhaustionStayFatal()
    {
        Assert.True(UiUpdateGuard.IsFatal(new OutOfMemoryException()));
        Assert.True(UiUpdateGuard.IsFatal(new StackOverflowException()));
        Assert.False(UiUpdateGuard.IsFatal(new InvalidOperationException()));
        Assert.False(UiUpdateGuard.IsFatal(new System.IO.IOException()));
        Assert.False(UiUpdateGuard.IsFatal(new NullReferenceException()));
    }
}
