using System.Text.Json;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryActivityTests
{
    [Fact]
    public void FreshVisibleContinuousMovingSamplesIntegrateOnlyObservedIntervals()
    {
        var clock = new ActivityClock();
        var recorder = Started(clock);
        Observe(100, 10); Observe(100, 20); Observe(100, 20);
        var summary = recorder.Snapshot();
        Assert.Equal(new SupplementaryActivitySummary(200, 0, 0, 100, 300, 3500, 20000, 1, "moving"), summary);
        Assert.True(summary.IsValid);
        Assert.Equal(summary, recorder.Snapshot());
        clock.Advance(501);
        Assert.Equal(summary with { CurrentState = "unknown" }, recorder.Snapshot());
        void Observe(int elapsed, double speed)
        {
            clock.Advance(elapsed);
            recorder.Observe(Sample(clock, speed));
        }
    }

    [Fact]
    public void LowSpeedTimeHasAnExplicitThresholdAndTransitionsAreNotIntegrated()
    {
        var clock = new ActivityClock();
        var recorder = Started(clock);
        Observe(.5); Observe(.5); Observe(1); Observe(1);
        var summary = recorder.Snapshot();
        Assert.Equal(100, summary.ObservedStationaryMs);
        Assert.Equal(100, summary.ObservedMovingMs);
        Assert.Equal(100, summary.ActivityUnknownMs);
        Assert.Equal(150, summary.DistanceMillimeters);
        Assert.Equal(1000, summary.PeakSpeedMillimetersPerSecond);
        Assert.True(summary.IsValid);
        void Observe(double speed) { clock.Advance(100); recorder.Observe(Sample(clock, speed)); }
    }

    [Fact]
    public void GapsSleepAndReportingPauseStayUnobservedWithoutResettingTotals()
    {
        var clock = new ActivityClock();
        var recorder = Started(clock);
        Observe(100); Observe(100); Observe(251); Observe(100);
        recorder.Enabled = false;
        clock.Advance(30000);
        recorder.Observe(Sample(clock));
        recorder.Enabled = true;
        Observe(100); Observe(100);
        var summary = recorder.Snapshot();
        Assert.Equal(300, summary.ObservedMovingMs);
        Assert.Equal(30451, summary.UnobservedMs);
        Assert.Equal(3000, summary.DistanceMillimeters);
        Assert.Equal(3, summary.DiscardedIntervals);
        Assert.True(summary.IsValid);
        void Observe(int elapsed) { clock.Advance(elapsed); recorder.Observe(Sample(clock)); }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(500.001)]
    public void InvalidSpeedsCannotBecomeMileageOrPeak(double speed)
    {
        var clock = new ActivityClock();
        var recorder = Started(clock);
        clock.Advance(100); recorder.Observe(Sample(clock));
        clock.Advance(100); recorder.Observe(Sample(clock, speed));
        AssertUnknown(recorder.Snapshot());
    }

    [Theory]
    [InlineData("menu")]
    [InlineData("race-off")]
    [InlineData("old-packet")]
    [InlineData("future-packet")]
    [InlineData("same-packet")]
    [InlineData("frozen-game")]
    [InlineData("game-reset")]
    [InlineData("clock-disagreement")]
    [InlineData("car-change")]
    [InlineData("drivetrain-change")]
    [InlineData("speed-jump")]
    public void MissingGameplayEvidenceAndDiscontinuitiesCannotBecomeDriving(string reason)
    {
        var clock = new ActivityClock();
        var recorder = Started(clock);
        clock.Advance(100); recorder.Observe(Sample(clock));
        clock.Advance(100);
        var sample = Sample(clock);
        sample = reason switch
        {
            "menu" => sample with { GameplayVisible = false },
            "race-off" => sample with { IsRaceOn = false },
            "old-packet" => sample with { ReceivedTimestamp = clock.GetTimestamp() - 301 },
            "future-packet" => sample with { ReceivedTimestamp = clock.GetTimestamp() + 1 },
            "same-packet" => sample with { ReceivedTimestamp = 100 },
            "frozen-game" => sample with { GameTimestampMilliseconds = 100 },
            "game-reset" => sample with { GameTimestampMilliseconds = 1 },
            "clock-disagreement" => sample with { GameTimestampMilliseconds = 251 },
            "car-change" => sample with { CarOrdinal = 2 },
            "drivetrain-change" => sample with { Drivetrain = 2 },
            _ => sample with { SpeedMetersPerSecond = 20.001 }
        };
        recorder.Observe(sample);
        AssertUnknown(recorder.Snapshot());
    }

    [Fact]
    public void CadenceIsBoundedAndUnsignedGameTimestampWrapRemainsContinuous()
    {
        var clock = new ActivityClock();
        var recorder = Started(clock);
        for (var i = 0; i < 99; i++) { clock.Advance(1); recorder.Observe(Sample(clock)); }
        Assert.Equal(0, recorder.Snapshot().SessionAgeMs);
        clock.Advance(1); recorder.Observe(Sample(clock) with { GameTimestampMilliseconds = uint.MaxValue - 49 });
        clock.Advance(100); recorder.Observe(Sample(clock) with { GameTimestampMilliseconds = 50 });
        Assert.Equal(1000, recorder.Snapshot().DistanceMillimeters);
        Assert.Equal(100, recorder.Snapshot().ObservedMovingMs);
        var before = recorder.Snapshot();
        clock.Advance(-100); recorder.Observe(Sample(clock));
        Assert.Equal(before with { CurrentState = "unknown" }, recorder.Snapshot());
    }

    [Fact]
    public void DisabledAndMissingSamplesNeverProduceZeroAsMeasuredSpeed()
    {
        var clock = new ActivityClock();
        var recorder = new SupplementaryActivityRecorder(clock);
        clock.Advance(100); recorder.Observe(Sample(clock));
        Assert.Equal(0, recorder.Snapshot().SessionAgeMs);
        recorder.Start();
        clock.Advance(100); recorder.Observe(null);
        clock.Advance(100); recorder.Observe(null);
        AssertUnknown(recorder.Snapshot());
    }

    [Fact]
    public void ActivityWireIsHeartbeatOnlyAndContainsNoLocalContinuityIdentifiers()
    {
        var summary = new SupplementaryActivitySummary(200, 0, 0, 100, 300, 3500, 20000, 1, "moving");
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var heartbeat = new SupplementaryEvent(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now,
            "2.6.6", "test", "private", "heartbeat", "app", "none", "unknown", ActivitySummary: summary);
        Assert.True(SupplementarySchema.Valid(heartbeat, now));
        Assert.False(SupplementarySchema.Valid(heartbeat with { Kind = "resource" }, now));
        Assert.False((summary with { CurrentState = "driving" }).IsValid);
        Assert.False((summary with { SessionAgeMs = 301 }).IsValid);
        Assert.False((summary with { DistanceMillimeters = 100001 }).IsValid);
        Assert.False((summary with { PeakSpeedMillimetersPerSecond = 500001 }).IsValid);
        Assert.False((summary with { DiscardedIntervals = -1 }).IsValid);
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(heartbeat, SupplementarySchema.Json));
        var activity = json.RootElement.GetProperty("activitySummary");
        Assert.Equal(9, activity.EnumerateObject().Count());
        Assert.Equal(3500, activity.GetProperty("distanceMillimeters").GetInt64());
        Assert.False(activity.TryGetProperty("carOrdinal", out _));
        Assert.False(activity.TryGetProperty("isValid", out _));
    }

    private static void AssertUnknown(SupplementaryActivitySummary summary)
    {
        Assert.Equal(0, summary.ObservedMovingMs);
        Assert.Equal(0, summary.ObservedStationaryMs);
        Assert.Equal(100, summary.ActivityUnknownMs);
        Assert.Equal(0, summary.DistanceMillimeters);
        Assert.Equal(0, summary.PeakSpeedMillimetersPerSecond);
        Assert.Equal("unknown", summary.CurrentState);
        Assert.True(summary.IsValid);
    }
    private static SupplementaryActivityRecorder Started(ActivityClock clock)
    { var recorder = new SupplementaryActivityRecorder(clock); recorder.Start(); return recorder; }
    private static SupplementaryActivitySample Sample(ActivityClock clock, double speed = 10) =>
        new(clock.GetTimestamp(), (uint)clock.GetTimestamp(), 1, 1, speed, true, true);
    private sealed class ActivityClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(long milliseconds) => _timestamp += milliseconds;
    }
}
