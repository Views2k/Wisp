using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace Wisp.App;

public abstract partial class ControlPanelWindow
{
    private void DashboardUpdateAvailabilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or nameof(DiagnosticsViewModel.IsApplicationUpdateAvailable))) return;
        if (Dispatcher.CheckAccess()) RefreshDashboardBannerVisibility();
        else Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(RefreshDashboardBannerVisibility));
    }

    private void RefreshDashboardBannerVisibility()
    {
        if (_contentSlotsClosed) return;
        var update = FindName("DashboardUpdateBanner") as FrameworkElement;
        var whatsNew = FindName("WhatsNewBanner") as FrameworkElement;
        var tour = FindName("FeatureTourWelcomeBanner") as FrameworkElement;
        var displayMode = this is MainWindow { IsDashboardDisplayMode: true };
        var selection = DashboardBannerPresentation.Select(_appliedBanner is not null,
            _controller.ViewModel.IsApplicationUpdateAvailable,
            ShouldShowWhatsNew(_controller.Settings.DismissedWhatsNewId, _controller.Settings.RequiresSetup, displayMode),
            ShouldOfferFeatureTour(), FeatureTour.IsOpen);

        // Collapse the previous surface first so no transition can expose two banners.
        if (update is not null) update.Visibility = Visibility.Collapsed;
        if (whatsNew is not null) whatsNew.Visibility = Visibility.Collapsed;
        if (tour is not null) tour.Visibility = Visibility.Collapsed;
        var visible = selection switch
        {
            DashboardBannerKind.Custom or DashboardBannerKind.Update => update,
            DashboardBannerKind.WhatsNew => whatsNew,
            DashboardBannerKind.FeatureTour => tour,
            _ => null
        };
        if (visible is not null) visible.Visibility = Visibility.Visible;
    }
}
