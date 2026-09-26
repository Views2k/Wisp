using Xunit;

namespace Wisp.App.Tests;

public sealed class WhatsNewBannerTests
{
    [Fact]
    public void ShowsUntilClosedAfterSetupOutsideDisplayMode()
    {
        Assert.True(ControlPanelWindow.ShouldShowWhatsNew(null, requiresSetup: false, displayMode: false));
        Assert.True(ControlPanelWindow.ShouldShowWhatsNew("wisp-2.4-earlier", requiresSetup: false, displayMode: false));
        Assert.False(ControlPanelWindow.ShouldShowWhatsNew(ControlPanelWindow.CurrentWhatsNewId, requiresSetup: false, displayMode: false));
        Assert.False(ControlPanelWindow.ShouldShowWhatsNew(null, requiresSetup: true, displayMode: false));
        Assert.False(ControlPanelWindow.ShouldShowWhatsNew(null, requiresSetup: false, displayMode: true));
    }
}
