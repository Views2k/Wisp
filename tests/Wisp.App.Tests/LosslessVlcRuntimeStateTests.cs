using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LosslessVlcRuntimeStateTests
{
    [Fact]
    public void DelayedOwnerClearsPendingOnlyAfterActualRelease()
    {
        var state = new LosslessVlcRuntime.CleanupOwnership();
        var owner = new object();
        state.Begin(owner);
        state.SetStage(owner, "create-playback-engine");
        Assert.True(state.MarkPending(owner));
        Assert.True(state.Pending);
        Assert.Equal("create-playback-engine", state.Stage);
        Assert.Equal("cleanup-pending-restart-may-be-required", state.Status);
        state.Complete(owner);
        Assert.False(state.Pending);
        Assert.Equal("idle", state.Stage);
        Assert.Equal("complete", state.Status);
    }

    [Fact]
    public void TimeoutAfterReleaseCannotMarkIdleOrNewOwnerPending()
    {
        var state = new LosslessVlcRuntime.CleanupOwnership();
        var previous = new object(); var current = new object();
        state.Begin(previous); state.Complete(previous);
        Assert.False(state.MarkPending(previous));
        state.Begin(current); state.SetStage(current, "engine-ready");
        Assert.False(state.MarkPending(previous));
        state.Complete(previous);
        Assert.False(state.Pending);
        Assert.Equal("engine-ready", state.Stage);
        Assert.True(state.MarkPending(current));
    }

    [Fact]
    public void WaitingOperationCannotMarkAnotherOwnerPendingOrReleaseIt()
    {
        var state = new LosslessVlcRuntime.CleanupOwnership();
        var owner = new object(); var waiter = new object();
        state.Begin(owner);
        Assert.False(state.MarkPending(waiter));
        state.Complete(waiter);
        Assert.Throws<InvalidOperationException>(() => state.Begin(waiter));
        Assert.True(state.MarkPending(owner));
    }
}
