using System.Collections.Immutable;
using System.Text.Json;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryDiagnosticHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HistoryExpiresAtTwentyMinutesRegardlessOfArrivalOrder()
    {
        var clock = new TestClock(Now);
        var buffer = new SupplementaryDiagnosticBuffer(clock);
        buffer.Observe(Event(Now, 3));
        buffer.Observe(Event(Now.AddMinutes(-20), 1));
        buffer.Observe(Event(Now.AddMinutes(-10), 2));
        buffer.Observe(Event(Now.AddMinutes(-20).AddTicks(-1), 0));

        var history = buffer.Snapshot(Now);
        Assert.Equal(new double?[] { 1, 2, 3 }, history.Observations.Select(p => p.Value));
        Assert.Equal(Now.AddMinutes(-20), history.From);
        Assert.True(history.IsValid(Now));

        clock.Now = Now.AddTicks(1);
        history = buffer.Snapshot(clock.Now);
        Assert.Equal(new double?[] { 2, 3 }, history.Observations.Select(p => p.Value));
        Assert.InRange(clock.Now - history.From, TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(20));
        Assert.True(history.IsValid(clock.Now));

        clock.Now = Now.AddMinutes(20).AddSeconds(1);
        Assert.Empty(buffer.Snapshot(clock.Now).Observations);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void FutureNonUtcAndPreviousRunObservationsDoNotEnterHistory()
    {
        var buffer = new SupplementaryDiagnosticBuffer(new TestClock(Now));
        buffer.Observe(Event(Now.AddSeconds(1)));
        buffer.Observe(Event(Now.ToOffset(TimeSpan.FromHours(1))));
        buffer.Observe(Event(Now) with { RelatedRun = new(Guid.NewGuid(), "2.6.8", "previous", "stable") });

        Assert.Empty(buffer.Snapshot(Now).Observations);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void RecentRecoveredCrashKeepsItsOriginalRunAndVersion()
    {
        var buffer = new SupplementaryDiagnosticBuffer(new TestClock(Now));
        var previous = new SupplementaryRelatedRun(Guid.NewGuid(), "2.6.8", "previous", "stable");
        buffer.Observe(Event(Now.AddMinutes(-5)) with { Kind = "exception", Outcome = "failure", RelatedRun = previous });
        var history = buffer.Snapshot(Now);
        Assert.Equal(previous, Assert.Single(history.Observations).RelatedRun);
        Assert.True(history.IsValid(Now));
    }

    [Fact]
    public void CapacityRetainsNewestEvidenceWhenAnOlderSummaryArrivesLast()
    {
        var buffer = new SupplementaryDiagnosticBuffer(new TestClock(Now));
        for (var i = 0; i < 2500; i++) buffer.Observe(Event(Now.AddSeconds((-2499 + i) / 3.0), i));
        buffer.Observe(Event(Now.AddMinutes(-15), 2500));

        var history = buffer.Snapshot(Now);
        Assert.Equal(2048, buffer.Count);
        Assert.Equal(2048, history.Observations.Length);
        Assert.Equal(453, history.Omitted);
        Assert.Equal(452d, history.Observations[0].Value);
        Assert.Equal(2499d, history.Observations[^1].Value);
        Assert.True(history.IsValid(Now));
    }

    [Fact]
    public void OmissionCountsAreExactBeyondTheOldMetadataCapAndExpireWithTheirWindow()
    {
        var clock = new TestClock(Now);
        var buffer = new SupplementaryDiagnosticBuffer(clock);
        for (var i = 0; i < 6_000; i++) buffer.Observe(Event(Now.AddMinutes(-19), i));

        var history = buffer.Snapshot(Now);
        Assert.Equal(2048, history.Observations.Length);
        Assert.Equal(3_952, history.Omitted);
        Assert.Equal(3_952d, history.Observations[0].Value);

        clock.Now = Now.AddMinutes(1).AddTicks(1);
        history = buffer.Snapshot(clock.Now);
        Assert.Empty(history.Observations);
        Assert.Equal(0, history.Omitted);
        Assert.True(history.IsValid(clock.Now));
    }

    [Fact]
    public void DetailedPayloadRetainsSummariesBeyondTheFormerSmallAttachmentLimit()
    {
        var buffer = new SupplementaryDiagnosticBuffer(new TestClock(Now));
        var measurements = new SupplementaryMeasurements(1_000_000, 60000, 60000, 60000, 60000, 60000, 60000,
            Enumerable.Repeat(62_500, 16).ToImmutableArray());
        for (var i = 0; i < 2048; i++) buffer.Observe(Event(Now.AddMilliseconds(-2047 + i), i) with
        {
            DurationMs = 604_800_000,
            Stage = "native-observation-to-ui-adoption",
            Measurements = measurements
        });

        var history = buffer.Snapshot(Now);
        Assert.Equal(2048, history.Observations.Length);
        Assert.Equal(2048, history.Observations.Length + history.Omitted);
        Assert.Equal((double)history.Omitted, history.Observations[0].Value);
        Assert.Equal(2047d, history.Observations[^1].Value);
        Assert.Equal(Now.AddMinutes(-20), history.From);
        Assert.True(history.IsValid(Now));
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(history, SupplementarySchema.Json).Length,
            1, SupplementaryDiagnosticHistory.MaximumBytes);
        Assert.Equal(2048, buffer.Count);
    }

    [Fact]
    public void CapturedHistoryRemainsUnchangedAfterLaterObservationsAndExpiry()
    {
        var clock = new TestClock(Now);
        var buffer = new SupplementaryDiagnosticBuffer(clock);
        buffer.Observe(Event(Now, 1));
        var captured = buffer.Snapshot(Now);
        var capturedJson = JsonSerializer.Serialize(captured, SupplementarySchema.Json);
        for (var i = 0; i < 2500; i++) buffer.Observe(Event(Now, i + 2));
        clock.Now = Now.AddMinutes(21);
        Assert.Empty(buffer.Snapshot(clock.Now).Observations);

        Assert.Equal(capturedJson, JsonSerializer.Serialize(captured, SupplementarySchema.Json));
        Assert.Equal(1d, Assert.Single(captured.Observations).Value);
        Assert.Equal(0, captured.Omitted);
    }

    [Fact]
    public async Task ConcurrentCollectionAndSnapshotsRemainBoundedAndConsistent()
    {
        var buffer = new SupplementaryDiagnosticBuffer(new TestClock(Now));
        var cancellationToken = TestContext.Current.CancellationToken;
        var producers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 2048; i++) buffer.Observe(Event(Now, worker * 2048 + i));
        }, cancellationToken)).ToArray();
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                var history = buffer.Snapshot(Now);
                Assert.True(history.IsValid(Now));
                Assert.InRange(history.Observations.Length, 0, 2048);
                Assert.Equal(history.Observations.Length,
                    history.Observations.Select(p => p.Value).Distinct().Count());
            }
        }, cancellationToken);
        await Task.WhenAll(producers.Append(reader));

        var final = buffer.Snapshot(Now);
        Assert.Equal(2048, buffer.Count);
        Assert.Equal(8_192, final.Observations.Length + final.Omitted);
        Assert.True(final.IsValid(Now));
    }

    private static SupplementaryEvent Event(DateTimeOffset at, double value = 0) =>
        new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), at,
            "2.6.9", "diagnostic-buffer-tests", "stable", "resource", "app", "none", "unknown", Value: value, Stage: "cpu");

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
