using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCapturePollStatisticsTests
{
    [Fact]
    public void MeasuredQueryDurationAndSpacingIncludeLongForegroundGaps()
    {
        var stats = new ShiftCapturePollStatistics(1_000);
        Assert.Null(stats.Observe(1_000, 1_002, true, true, true));
        Assert.Null(stats.Observe(1_025, 1_029, true, true, true));
        Assert.Null(stats.Observe(1_080, 1_086, true, true, true));
        var total = stats.Snapshot().Total;
        Assert.Equal(3, total.Polls);
        Assert.Equal(3, total.ForegroundAvailablePolls);
        Assert.Equal(2, total.ApiRead.MinimumMilliseconds);
        Assert.Equal(6, total.ApiRead.MaximumMilliseconds);
        Assert.Equal(4, total.ApiRead.MeanMilliseconds);
        Assert.Equal(25, total.PollSpacing.MinimumMilliseconds);
        Assert.Equal(55, total.PollSpacing.MaximumMilliseconds);
        Assert.Equal(40, total.PollSpacing.MeanMilliseconds);
        Assert.Equal(2, total.ForegroundGapsOver20Milliseconds);
        Assert.Equal(1, total.ForegroundGapsOver50Milliseconds);
    }

    [Fact]
    public void BackgroundCadenceIsReportedSeparatelyFromForegroundStalls()
    {
        var stats = new ShiftCapturePollStatistics(1_000);
        stats.Observe(1_000, 1_001, true, true, true);
        stats.Observe(1_004, 1_005, false, true, true);
        stats.Observe(1_105, 1_106, false, false, false);
        stats.Observe(1_207, 1_208, true, true, false);
        stats.Observe(1_212, 1_213, true, true, true);
        var total = stats.Snapshot().Total;
        Assert.Equal(5, total.Polls);
        Assert.Equal(3, total.ForegroundPolls);
        Assert.Equal(4, total.ApiAvailablePolls);
        Assert.Equal(3, total.SingleControllerAvailablePolls);
        Assert.Equal(2, total.ForegroundAvailablePolls);
        Assert.Equal(2, total.GapsOver50Milliseconds);
        Assert.Equal(0, total.ForegroundGapsOver20Milliseconds);
        Assert.Equal(1, total.ForegroundPollSpacing.Samples);
        Assert.Equal(5, total.ForegroundPollSpacing.MeanMilliseconds);
    }

    [Fact]
    public void ReportsAreBoundedToOnePerSecondAndSnapshotPreservesThePartialInterval()
    {
        var stats = new ShiftCapturePollStatistics(1_000);
        Assert.Null(stats.Observe(1_000, 1_001, true, true, true));
        Assert.Null(stats.Observe(1_999, 2_000, true, true, true));
        var first = Assert.IsType<ShiftCapturePollWindow>(stats.Observe(2_000, 2_001, true, true, true));
        Assert.Equal(3, first.Polls);
        Assert.Null(stats.Observe(2_004, 2_005, true, true, true));
        var snapshot = stats.Snapshot();
        Assert.Equal(4, snapshot.Total.Polls);
        Assert.Equal(1, snapshot.PendingInterval.Polls);
        Assert.Equal(2_004, snapshot.PendingInterval.StartedQpc);
        Assert.Equal(1, snapshot.PendingInterval.PollSpacing.Samples);
        var later = Assert.IsType<ShiftCapturePollWindow>(stats.Observe(8_000, 8_001, true, true, true));
        Assert.Equal(2, later.Polls); // A delayed poll creates one report, not a catch-up burst.
        Assert.Null(stats.Observe(8_004, 8_005, true, true, true));
    }

    [Fact]
    public void InvalidAndOverlappingTimestampsDoNotBecomeNegativeDurationsOrSpacing()
    {
        var stats = new ShiftCapturePollStatistics(1_000);
        stats.Observe(1_000, 1_010, true, true, true);
        stats.Observe(1_005, 1_011, true, true, true);
        stats.Observe(1_020, 1_019, true, true, true);
        stats.Observe(0, 0, false, false, false);
        stats.Observe(1_100, 1_101, true, true, true);
        var snapshot = stats.Snapshot();
        Assert.Equal(3, snapshot.Total.NonMonotonicTimestampPolls);
        Assert.Equal(3, snapshot.Total.ApiRead.Samples);
        Assert.Equal(0, snapshot.Total.PollSpacing.Samples);
        Assert.Null(snapshot.Total.PollSpacing.MinimumMilliseconds);
        Assert.Null(snapshot.Total.PollSpacing.MeanMilliseconds);
    }

    [Fact]
    public void EmptySnapshotDoesNotFabricateTimingSamples()
    {
        var stats = new ShiftCapturePollStatistics(10_000_000);
        var total = stats.Snapshot().Total;
        Assert.Equal(0, total.Polls);
        Assert.Equal(0, total.ApiRead.Samples);
        Assert.Null(total.ApiRead.MinimumMilliseconds);
        Assert.Null(total.ApiRead.MaximumMilliseconds);
        Assert.Null(total.ApiRead.MeanMilliseconds);
    }
}
