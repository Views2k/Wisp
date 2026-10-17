using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeRendererRecoveryTests
{
    [Fact]
    public void TransientOutOfMemoryGetsThreeGrowingRetriesThenRetires()
    {
        var recovery = new NativeRendererRecovery();
        Assert.True(recovery.TryBeginRetry(NativeRendererException.OutOfMemory, out var first));
        Assert.True(recovery.TryBeginRetry(NativeRendererException.OutOfMemory, out var second));
        Assert.True(recovery.TryBeginRetry(NativeRendererException.OutOfMemory, out var third));
        Assert.False(recovery.TryBeginRetry(NativeRendererException.OutOfMemory, out var fourth));
        Assert.Equal(TimeSpan.FromSeconds(1), first);
        Assert.Equal(TimeSpan.FromSeconds(2), second);
        Assert.Equal(TimeSpan.FromSeconds(4), third);
        Assert.Equal(TimeSpan.Zero, fourth);
        Assert.Equal(NativeRendererRecovery.MaximumAttempts, recovery.Attempts);
    }

    [Theory]
    [InlineData(0x887A0006u)] // DXGI_ERROR_DEVICE_HUNG
    [InlineData(0x887A0004u)] // DXGI_ERROR_DEVICE_REMOVED
    [InlineData(0x80004005u)] // E_FAIL
    public void OtherFailuresKeepTheUnavailableState(uint hresult)
    {
        var recovery = new NativeRendererRecovery();
        Assert.False(NativeRendererRecovery.IsTransient(unchecked((int)hresult)));
        Assert.False(recovery.TryBeginRetry(unchecked((int)hresult), out var delay));
        Assert.Equal(TimeSpan.Zero, delay);
        Assert.Equal(0, recovery.Attempts);
    }

    [Fact]
    public void APresentedFrameRestoresTheRetryBudget()
    {
        var recovery = new NativeRendererRecovery();
        for (var attempt = 0; attempt < NativeRendererRecovery.MaximumAttempts; attempt++)
            Assert.True(recovery.TryBeginRetry(NativeRendererException.OutOfMemory, out _));
        Assert.False(recovery.TryBeginRetry(NativeRendererException.OutOfMemory, out _));
        recovery.Reset();
        Assert.Equal(0, recovery.Attempts);
        Assert.True(recovery.TryBeginRetry(NativeRendererException.OutOfMemory, out var delay));
        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }
}
