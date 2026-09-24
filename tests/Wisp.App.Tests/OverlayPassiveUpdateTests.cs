using Xunit;

namespace Wisp.App.Tests;

public sealed class OverlayPassiveUpdateTests
{
    [Fact]
    public void RejectedRequestPreservesFailureWithoutRegisteringAnInvalidWindow()
    {
        var result = OverlayPassiveUpdate.Apply(IntPtr.Zero, native: true);
        Assert.True(result.RequestedEnabled);
        Assert.True(result.SetHResult < 0);
        Assert.True(result.CompletedTimestamp >= result.StartedTimestamp);
        Assert.DoesNotContain(OverlayPassiveUpdate.Snapshot(), state => state.WindowHandle == 0);
    }
}
