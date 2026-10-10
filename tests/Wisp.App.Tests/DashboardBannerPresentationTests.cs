using Xunit;

namespace Wisp.App.Tests;

public sealed class DashboardBannerPresentationTests
{
    [Theory]
    [InlineData(true, true, true, true, (int)DashboardBannerKind.Custom)]
    [InlineData(false, true, true, true, (int)DashboardBannerKind.Update)]
    [InlineData(false, false, true, true, (int)DashboardBannerKind.WhatsNew)]
    [InlineData(false, false, false, true, (int)DashboardBannerKind.FeatureTour)]
    [InlineData(false, false, false, false, (int)DashboardBannerKind.None)]
    public void PriorityChoosesOneEligibleSurface(bool custom, bool update, bool whatsNew, bool featureTour, int expected) =>
        Assert.Equal((DashboardBannerKind)expected, DashboardBannerPresentation.Select(custom, update, whatsNew, featureTour, tourOpen: false));

    [Fact]
    public void OpenTourSuppressesEveryCombinationWithoutConsumingEligibility()
    {
        for (var flags = 0; flags < 16; flags++)
            Assert.Equal(DashboardBannerKind.None, DashboardBannerPresentation.Select(
                (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0, tourOpen: true));
        Assert.Equal(DashboardBannerKind.Custom, DashboardBannerPresentation.Select(true, true, true, true, tourOpen: false));
    }

    [Fact]
    public void ExpiryUpdateAndDismissalTransitionsRestoreNextEligibleBanner()
    {
        Assert.Equal(DashboardBannerKind.WhatsNew, DashboardBannerPresentation.Select(false, false, true, true, false));
        Assert.Equal(DashboardBannerKind.Update, DashboardBannerPresentation.Select(false, true, true, true, false));
        Assert.Equal(DashboardBannerKind.Custom, DashboardBannerPresentation.Select(true, true, true, true, false));
        Assert.Equal(DashboardBannerKind.Update, DashboardBannerPresentation.Select(false, true, true, true, false));
        Assert.Equal(DashboardBannerKind.WhatsNew, DashboardBannerPresentation.Select(false, false, true, true, false));
        Assert.Equal(DashboardBannerKind.FeatureTour, DashboardBannerPresentation.Select(false, false, false, true, false));
        Assert.Equal(DashboardBannerKind.None, DashboardBannerPresentation.Select(false, false, false, false, false));
    }
}
