using Xunit;

namespace Wisp.Core.Tests;

public sealed class OverlayActivationPolicyTests
{
    [Fact]
    public void InteractiveOverlayAcceptsPointerInputWithoutBecomingActivatable()
    {
        var style = OverlayActivationPolicy.BuildExtendedStyle(
            currentStyle: 0,
            acceptsPointerInput: true);

        Assert.NotEqual(0, style & OverlayActivationPolicy.NoActivateExtendedStyle);
        Assert.NotEqual(0, style & OverlayActivationPolicy.ToolWindowExtendedStyle);
        Assert.Equal(0, style & OverlayActivationPolicy.TransparentExtendedStyle);
    }

    [Fact]
    public void LockedOverlayIsClickThroughAndNonActivating()
    {
        var style = OverlayActivationPolicy.BuildExtendedStyle(
            currentStyle: 0,
            acceptsPointerInput: false);

        Assert.NotEqual(0, style & OverlayActivationPolicy.NoActivateExtendedStyle);
        Assert.NotEqual(0, style & OverlayActivationPolicy.ToolWindowExtendedStyle);
        Assert.NotEqual(0, style & OverlayActivationPolicy.TransparentExtendedStyle);
    }

    [Fact]
    public void MouseActivationIsExplicitlyRejected()
    {
        var handled = OverlayActivationPolicy.TryHandleWindowMessage(
            OverlayActivationPolicy.MouseActivateMessage,
            out var result);

        Assert.True(handled);
        Assert.Equal(
            new IntPtr(OverlayActivationPolicy.MouseActivateNoActivateResult),
            result);
    }
}
