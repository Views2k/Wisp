using Xunit;

namespace Wisp.App.Tests;

public sealed class FeatureTourSessionTests
{
    [Fact]
    public void FourStepsSupportBackAndStopAtBothEndsWithoutClosingEarly()
    {
        var tour = new FeatureTourSession();
        Assert.False(tour.IsOpen);
        Assert.False(tour.Next());
        tour.Back();
        Assert.Equal(0, tour.StepIndex);

        tour.Start();
        tour.Back();
        Assert.Equal(0, tour.StepIndex);
        Assert.True(tour.Next());
        Assert.Equal(1, tour.StepIndex);
        tour.Back();
        Assert.Equal(0, tour.StepIndex);
        Assert.True(tour.Next());
        Assert.True(tour.Next());
        Assert.True(tour.Next());
        Assert.Equal(3, tour.StepIndex);
        Assert.False(tour.Next());
        Assert.True(tour.IsOpen);
    }

    [Theory]
    [InlineData(null, true, false, false, true)]
    [InlineData("older-tour", true, false, false, true)]
    [InlineData(FeatureTourSession.CurrentTourId, true, false, false, false)]
    [InlineData(null, false, false, false, false)]
    [InlineData(null, true, true, false, false)]
    [InlineData(null, true, false, true, false)]
    public void OfferRequiresManualDiscoveryCompletedSetupAndRegularWindow(
        string? receipt, bool manualDiscovery, bool requiresSetup, bool displayMode, bool expected) =>
        Assert.Equal(expected, FeatureTourSession.ShouldOffer(receipt, manualDiscovery, requiresSetup, displayMode));

    [Fact]
    public void DismissAndFinishPersistOnceWhileReplayStartsAtTheBeginning()
    {
        var tour = new FeatureTourSession();
        var saves = 0;
        tour.Start();
        tour.Next();
        Assert.True(tour.PersistReceipt(() => { saves++; return true; }));
        Assert.False(tour.IsOpen);
        Assert.False(tour.HasPendingReceipt);
        Assert.Equal(1, saves);

        tour.Start();
        Assert.True(tour.IsOpen);
        Assert.Equal(0, tour.StepIndex);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void SaveFailureClosesTheTourButKeepsARecoverableReceipt()
    {
        var tour = new FeatureTourSession();
        tour.Start();
        Assert.False(tour.PersistReceipt(() => false));
        Assert.False(tour.IsOpen);
        Assert.True(tour.HasPendingReceipt);

        Assert.True(tour.PersistReceipt(() => true));
        Assert.False(tour.HasPendingReceipt);
        Assert.False(tour.IsOpen);
    }

    [Fact]
    public void TemporarilyHidingOrClosingTheTourDoesNotSilentlyWriteAReceipt()
    {
        var tour = new FeatureTourSession();
        tour.Start();
        tour.Next();
        tour.Close();
        Assert.False(tour.IsOpen);
        Assert.False(tour.HasPendingReceipt);
        Assert.False(tour.Next());
        tour.Start();
        Assert.Equal(0, tour.StepIndex);
    }
}
