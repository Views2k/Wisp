namespace Wisp.App;

internal enum DashboardBannerKind { None, Custom, Update, WhatsNew, FeatureTour }

internal static class DashboardBannerPresentation
{
    internal static DashboardBannerKind Select(bool custom, bool update, bool whatsNew, bool featureTour, bool tourOpen) =>
        tourOpen ? DashboardBannerKind.None :
        custom ? DashboardBannerKind.Custom :
        update ? DashboardBannerKind.Update :
        whatsNew ? DashboardBannerKind.WhatsNew :
        featureTour ? DashboardBannerKind.FeatureTour : DashboardBannerKind.None;
}
