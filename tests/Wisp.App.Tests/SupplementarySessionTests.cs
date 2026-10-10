using System.Text.Json;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementarySessionTests
{
    [Fact]
    public void DurationsCreditOnlyBoundedMatchingObservationsAndIgnoreWallClockChanges()
    {
        var clock = new SessionClock();
        var recorder = new SupplementarySessionRecorder(clock);
        recorder.Start();
        Sample(2_000, false);
        Sample(2_000, false);
        Sample(2_000, true);
        clock.UtcNow = clock.UtcNow.AddDays(-2);
        Sample(2_000, true);
        clock.UtcNow = clock.UtcNow.AddYears(1);
        clock.Advance(2_000);
        recorder.RecordSample(true, true, 20, 2, 100, 50);
        var summary = recorder.Snapshot();
        Assert.Equal(new SupplementarySessionSummary(8_000, 2_000, 2_000, 4_000, 2_000, 10_000), summary);
        Assert.True(summary.IsValid);
        clock.Advance(300_000);
        Assert.Equal(summary, recorder.Snapshot()); // A snapshot never fabricates the unobserved tail or an exit.
        void Sample(long elapsed, bool connected)
        {
            clock.Advance(elapsed);
            recorder.RecordSample(connected, false, connected ? 20 : null, 2, 100, 50);
        }
    }

    [Fact]
    public void SleepCollectionGapsAndReportingPauseRemainUnobserved()
    {
        var clock = new SessionClock();
        var recorder = new SupplementarySessionRecorder(clock);
        recorder.Start();
        Observe(2_000); Observe(2_000); Observe(60_000); Observe(2_000);
        Assert.Equal(4_000, recorder.Snapshot().ConnectedMs);
        Assert.Equal(62_000, recorder.Snapshot().UnobservedMs);
        recorder.Enabled = false;
        clock.Advance(30_000);
        recorder.RecordSample(true, false, 1, 5, 100, 50);
        Assert.Null(recorder.LatestResource());
        recorder.Enabled = true;
        Observe(2_000); Observe(2_000);
        Assert.Equal(6_000, recorder.Snapshot().ConnectedMs);
        Assert.Equal(94_000, recorder.Snapshot().UnobservedMs);
        Assert.True(recorder.Snapshot().IsValid);
        void Observe(long elapsed)
        {
            clock.Advance(elapsed);
            recorder.RecordSample(true, false, 1, 5, 100, 50);
        }
    }

    [Fact]
    public void RecorderDoesNotCollectBeforeStartAndKeepsOnlyTheLatestResourcePoint()
    {
        var clock = new SessionClock();
        var recorder = new SupplementarySessionRecorder(clock);
        clock.Advance(2_000);
        recorder.RecordSample(true, false, 1, 2, 100, 50);
        Assert.Null(recorder.LatestResource());
        Assert.Equal(0, recorder.Snapshot().SessionAgeMs);
        recorder.Start();
        for (var i = 1; i <= 2_000; i++)
        {
            clock.Advance(2_000);
            recorder.RecordSample(true, false, 1, 2, i, i / 2);
        }
        var sample = Assert.IsType<SupplementaryResourceObservation>(recorder.LatestResource());
        Assert.Equal(4_000_000, sample.SessionAgeMs);
        Assert.Equal(2_000, sample.WorkingSetBytes);
        Assert.Equal(1_000, sample.ManagedHeapBytes);
        Assert.Equal(recorder.Snapshot(), recorder.Snapshot()); // Read-only cumulative snapshot, no drain/reset.
        var before = recorder.Snapshot();
        clock.Advance(-2_000);
        recorder.RecordSample(true, false, 1, 2, 1, 1);
        Assert.Equal(before, recorder.Snapshot());
        Assert.Equal(sample, recorder.LatestResource());
    }

    [Fact]
    public void InvalidInputCannotBecomeConnectedTimeOrAnInventedResourceZero()
    {
        var clock = new SessionClock();
        var recorder = new SupplementarySessionRecorder(clock);
        recorder.Start();
        clock.Advance(2_000);
        recorder.RecordSample(true, false, double.NaN, double.NaN, 100, 50);
        clock.Advance(2_000);
        recorder.RecordSample(true, false, -1, double.PositiveInfinity, 100, 50);
        Assert.Equal(2_000, recorder.Snapshot().StateUnknownMs);
        Assert.Equal(0, recorder.Snapshot().ConnectedMs);
        Assert.Null(recorder.LatestResource()!.Value.CpuPercent);
        var latest = recorder.LatestResource();
        clock.Advance(2_000);
        recorder.RecordSample(false, false, null, 1, -1, 50);
        Assert.Equal(latest, recorder.LatestResource());
    }

    [Fact]
    public void WireSummaryRequiresExactInvariantAndTheCorrectEventKinds()
    {
        using var fixture = new SupplementaryFixture();
        var summary = new SupplementarySessionSummary(6_000, 2_000, 2_000, 2_000, 4_000, 10_000);
        var heartbeat = fixture.Event() with { Kind = "heartbeat", SessionSummary = summary };
        Assert.True(SupplementarySchema.Valid(heartbeat, fixture.Now));
        Assert.False(SupplementarySchema.Valid(heartbeat with { Kind = "resource" }, fixture.Now));
        Assert.False(SupplementarySchema.Valid(heartbeat with { SessionSummary = summary with { IdleMs = 3_000 } }, fixture.Now));
        Assert.False(SupplementarySchema.Valid(heartbeat with { SessionSummary = summary with { SessionAgeMs = 9_999 } }, fixture.Now));
        Assert.False(SupplementarySchema.Valid(heartbeat with { SessionSummary = summary with { UnobservedMs = -1 } }, fixture.Now));
        var resource = fixture.Event() with { Kind = "resource", Stage = "working-set", Value = 100, DurationMs = null, SessionAgeMs = 10_000 };
        Assert.True(SupplementarySchema.Valid(resource, fixture.Now));
        Assert.False(SupplementarySchema.Valid(resource with { DurationMs = 20 }, fixture.Now));
        Assert.False(SupplementarySchema.Valid(resource with { Stage = "accepted-packets" }, fixture.Now));
        Assert.False(SupplementarySchema.Valid(resource with { Value = null }, fixture.Now));
        Assert.False(SupplementarySchema.Valid(heartbeat with { SessionAgeMs = 10_000 }, fixture.Now));
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(heartbeat, SupplementarySchema.Json));
        Assert.Equal(6_000, json.RootElement.GetProperty("sessionSummary").GetProperty("observedOpenMs").GetInt64());
        Assert.False(json.RootElement.GetProperty("sessionSummary").TryGetProperty("isValid", out _));
    }

    private sealed class SessionClock : TimeProvider
    {
        private long _timestamp;
        internal DateTimeOffset UtcNow = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        internal void Advance(long milliseconds) => _timestamp += milliseconds;
    }
}
