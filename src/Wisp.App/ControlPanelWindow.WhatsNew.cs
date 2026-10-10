using System.Windows;

namespace Wisp.App;

public abstract partial class ControlPanelWindow
{
    // Introduces the features a release adds, once, until the banner is closed.
    internal const string CurrentWhatsNewId = "wisp-2.5-lap-delta";

    internal static bool ShouldShowWhatsNew(string? dismissed, bool requiresSetup, bool displayMode) =>
        !requiresSetup && !displayMode && dismissed != CurrentWhatsNewId;

    internal void RefreshWhatsNew() => RefreshDashboardBannerVisibility();

    // A failed save still hides the banner for this session; it returns next time Wisp opens.
    protected void WhatsNewClose_Click(object sender, RoutedEventArgs e)
    {
        _controller.TryDismissWhatsNew(CurrentWhatsNewId);
        RefreshDashboardBannerVisibility();
    }
}
