using System.Runtime.InteropServices;
using Wisp.App.CrashDiagnostics;
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
            Assert.False(guard.Failed(UiPassKind.Timer));
            Assert.Equal(attempt, guard.ConsecutiveFailures(UiPassKind.Timer));
        }
        guard.Succeeded(UiPassKind.Timer);
        Assert.Equal(0, guard.ConsecutiveFailures(UiPassKind.Timer));
        for (var attempt = 1; attempt < UiUpdateGuard.MaximumConsecutiveFailures; attempt++) Assert.False(guard.Failed(UiPassKind.Timer));
        Assert.True(guard.Failed(UiPassKind.Timer));
        Assert.True(guard.Stopped);
        // Once stopped, nothing restarts the loop in this run, whichever pass reports.
        guard.Succeeded(UiPassKind.Timer);
        Assert.True(guard.Stopped);
        Assert.True(guard.Failed(UiPassKind.Packet));
    }

    [Fact]
    public void APassKindThatKeepsFailingStopsTheLoopWhileTheOtherKindsSucceed()
    {
        // 2.7.1 kept one count for every pass kind: a failure confined to the timer pass never
        // stopped the loop, because each clean packet or composition pass reset the count, and the
        // failure was reported once per tick instead.
        var guard = new UiUpdateGuard();
        for (var attempt = 1; attempt < UiUpdateGuard.MaximumConsecutiveFailures; attempt++)
        {
            Assert.False(guard.Failed(UiPassKind.Timer));
            guard.Succeeded(UiPassKind.Packet);
            guard.Succeeded(UiPassKind.Composition);
        }
        Assert.Equal(0, guard.ConsecutiveFailures(UiPassKind.Packet));
        Assert.Equal(UiUpdateGuard.MaximumConsecutiveFailures - 1, guard.ConsecutiveFailures(UiPassKind.Timer));
        Assert.True(guard.Failed(UiPassKind.Timer));
        Assert.True(guard.Stopped);
    }

    [Fact]
    public void OnlyMemoryStackExhaustionAndADeadRenderThreadStayFatal()
    {
        Assert.True(UiUpdateGuard.IsFatal(new OutOfMemoryException()));
        Assert.True(UiUpdateGuard.IsFatal(new StackOverflowException()));
        Assert.True(UiUpdateGuard.IsFatal(new COMException("PRIVATE", DashboardRenderingFallback.RenderThreadFailure)));
        Assert.True(UiUpdateGuard.IsFatal(new InvalidOperationException("PRIVATE",
            new COMException("PRIVATE", DashboardRenderingFallback.RenderThreadFailure))));
        Assert.False(UiUpdateGuard.IsFatal(new COMException("PRIVATE", unchecked((int)0x80004005))));
        Assert.False(UiUpdateGuard.IsFatal(new InvalidOperationException()));
        Assert.False(UiUpdateGuard.IsFatal(new System.IO.IOException()));
        Assert.False(UiUpdateGuard.IsFatal(new NullReferenceException()));
    }
}
