using System.Numerics;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class TimeAttackTests
{
    private static readonly (Vector2 Center, Vector2 Heading)[] Circuits =
    [
        (new(4267.63f, -5273.08f), new(-.16602f, .98612f)),
        (new(2826.10f, 2697.94f), new(-.63556f, .77205f)),
        (new(2785.70f, 4990.71f), new(.94037f, .34014f)),
        (new(2495.16f, -5063.40f), new(-.01200f, -.99993f))
    ];
    private static Vector3 Route(double seconds, int circuit = 0)
    {
        var (center, forward) = Circuits[circuit];
        forward = Vector2.Normalize(forward);
        var right = new Vector2(forward.Y, -forward.X);
        var angle = seconds / 60 * Math.Tau;
        var p = center + forward * (float)(150 * Math.Sin(angle)) + right * (float)(150 * (1 - Math.Cos(angle)));
        return new(p.X, 0, p.Y);
    }
    private static VehicleState State(double time, double route, int circuit = 0) =>
        LapDeltaTests.State(time + 100, 0, 0, Route(route, circuit)) with
        { Lap = new(Route(route, circuit), 0, 0, 0, 0, 0) };
    private static LapDeltaReading Update(LapDeltaTracker tracker, double time, double route, int circuit = 0) =>
        tracker.Update(State(time, route, circuit), LapDeltaReference.SessionBest, LapTimingMode.TimeAttack);
    private static void Reference(LapDeltaTracker tracker, int circuit = 0)
    {
        for (var i = -1; i <= 650; i++) Update(tracker, i / 10d, i / 10d, circuit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ZeroDashClocksLearnKnownCircuitAndCompareAtPosition(int circuit)
    {
        var tracker = new LapDeltaTracker();
        Reference(tracker, circuit);
        var result = Update(tracker, 65.1, 65.1, circuit);
        Assert.Equal(LapDeltaStatus.Comparing, result.Status);
        Assert.InRange(result.ReferenceSeconds!.Value, 59.98, 60.02);
        Assert.InRange(result.Seconds!.Value, -.02, .02);
        Assert.True(tracker.ReadMap(1)!.Outline.Complete);
    }

    [Fact]
    public void CrossingIsInterpolatedBetweenPackets()
    {
        var clock = new TimeAttackClock();
        Assert.Null(clock.Update(State(-.04, -.04)));
        var start = clock.Update(State(.06, .06));
        Assert.InRange(start!.CurrentLapSeconds, .0599, .0601);
    }

    [Theory]
    [InlineData("reverse")]
    [InlineData("outside")]
    [InlineData("teleport")]
    [InlineData("gap")]
    public void InvalidCrossingsDoNotStartClock(string cause)
    {
        var clock = new TimeAttackClock();
        var before = State(0, -.1);
        var after = State(.1, .1);
        if (cause == "reverse") { before = State(0, .1); after = State(.1, -.1); }
        if (cause == "outside")
        {
            var side = new Vector3(100, 0, 100);
            before = before with { Lap = before.Lap! with { Position = before.Lap.Position.ToVector() + side } };
            after = after with { Lap = after.Lap! with { Position = after.Lap.Position.ToVector() + side } };
        }
        if (cause == "teleport") before = State(0, -5);
        if (cause == "gap") after = State(3, .1);
        clock.Update(before);
        Assert.Null(clock.Update(after));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PauseOrRewindKeepsTimeAttackReference(bool rewind)
    {
        var tracker = new LapDeltaTracker();
        Reference(tracker);
        var outline = tracker.ReadMap(1)!.Outline;
        if (rewind)
        {
            for (var i = 1; i <= 20; i++) Update(tracker, 65 - i / 10d, 65 - i / 10d);
        }
        // A paused game freezes its timestamp while wall time passes.
        else tracker.Update(State(65, 65) with { IsRaceOn = false, ReceivedAtUtc = State(66, 0).ReceivedAtUtc },
            LapDeltaReference.SessionBest, LapTimingMode.TimeAttack);
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var i = 1; i <= 10; i++)
        {
            var time = (rewind ? 63 : 65) + i / 10d;
            reading = tracker.Update(State(time, time) with { ReceivedAtUtc = State(time + 2, 0).ReceivedAtUtc },
                LapDeltaReference.SessionBest, LapTimingMode.TimeAttack);
        }
        Assert.Same(outline, tracker.ReadMap(2)!.Outline);
        Assert.Equal(LapDeltaStatus.Comparing, reading.Status);
        Assert.InRange(reading.Seconds!.Value, -.15, .15);
    }

    private static LapDeltaReading UpdateAt(LapDeltaTracker tracker, double time, Vector3 position) =>
        tracker.Update(LapDeltaTests.State(time + 100, 0, 0, position) with { Lap = new(position, 0, 0, 0, 0, 0) },
            LapDeltaReference.SessionBest, LapTimingMode.TimeAttack);

    [Fact]
    public void NewAttemptAfterAResetComparesFromTheStartInsteadOfRejoining()
    {
        var tracker = new LapDeltaTracker();
        Reference(tracker);
        for (var i = 651; i <= 800; i++) Update(tracker, i / 10d, i / 10d);
        // A crash: the car is reset behind the start gate and a new attempt begins.
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var i = 0; i <= 50; i++) reading = Update(tracker, 85 + i / 10d, -3 + i / 10d);
        Assert.Equal(LapDeltaStatus.Comparing, reading.Status);
        Assert.InRange(reading.Seconds!.Value, -.15, .15);
        for (var i = 51; i <= 700; i++) reading = Update(tracker, 85 + i / 10d, -3 + i / 10d);
        Assert.Equal(LapDeltaStatus.Comparing, reading.Status);
        Assert.Equal(60, reading.ReferenceSeconds!.Value, 1);
    }

    [Fact]
    public void AbandonedAttemptThatReturnsThroughTheGateIsNotALap()
    {
        var tracker = new LapDeltaTracker();
        Reference(tracker);
        for (var i = 651; i <= 800; i++) Update(tracker, i / 10d, i / 10d);
        // Abandon the attempt: drive straight back to behind the gate, then through it.
        var from = Route(80);
        var to = Route(-2);
        for (var i = 1; i <= 150; i++) UpdateAt(tracker, 80 + i / 10d, Vector3.Lerp(from, to, i / 150f));
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var i = 1; i <= 70; i++) reading = Update(tracker, 95 + i / 10d, -2 + i / 10d);
        Assert.Equal(LapDeltaStatus.Comparing, reading.Status);
        Assert.Equal(60, reading.ReferenceSeconds!.Value, 1);
    }

    [Fact]
    public void TwoFullLapsReplaceAReferenceThatWasAnAbandonedAttempt()
    {
        var tracker = new LapDeltaTracker();
        // The first attempt is abandoned halfway and driven straight back through the start.
        for (var i = -1; i <= 300; i++) Update(tracker, i / 10d, i / 10d);
        var from = Route(30);
        var to = Route(-2);
        for (var i = 1; i <= 170; i++) UpdateAt(tracker, 30 + i / 10d, Vector3.Lerp(from, to, i / 170f));
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var i = 1; i <= 30; i++) reading = Update(tracker, 47 + i / 10d, -2 + i / 10d);
        Assert.InRange(reading.ReferenceSeconds!.Value, 45, 50);
        // Two full laps of the circuit follow each other, not the abandoned attempt.
        for (var i = 31; i <= 1230; i++) reading = Update(tracker, 47 + i / 10d, -2 + i / 10d);
        Assert.Equal(LapDeltaStatus.Comparing, reading.Status);
        Assert.Equal(60, reading.ReferenceSeconds!.Value, 1);
    }

    [Fact]
    public void TwoSimilarAbandonedAttemptsDoNotReplaceTheReference()
    {
        var tracker = new LapDeltaTracker();
        Reference(tracker);
        var time = 65d;
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var start = time;
            for (; time < start + 25; time += .1) Update(tracker, time, time - start + 5);
            var from = Route(time - start + 5);
            var to = Route(-2);
            for (var i = 1; i <= 150; i++) UpdateAt(tracker, time + i / 10d, Vector3.Lerp(from, to, i / 150f));
            time += 15;
            for (var i = 1; i <= 70; i++) reading = Update(tracker, time + i / 10d, -2 + i / 10d);
            time += 7.1;
        }
        Assert.Equal(LapDeltaStatus.Comparing, reading.Status);
        Assert.Equal(60, reading.ReferenceSeconds!.Value, 1);
    }

    [Fact]
    public void RewindDuringTheFirstTimeAttackLapStillLearnsIt()
    {
        var tracker = new LapDeltaTracker();
        for (var i = -1; i <= 300; i++) Update(tracker, i / 10d, i / 10d);
        for (var i = 1; i <= 30; i++) Update(tracker, 30 - i / 10d, 30 - i / 10d);
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var i = 271; i <= 650; i++) reading = Update(tracker, i / 10d, i / 10d);
        Assert.Equal(LapDeltaStatus.Comparing, reading.Status);
        Assert.Equal(60, reading.ReferenceSeconds!.Value, 1);
        Assert.InRange(reading.Seconds!.Value, -.15, .15);
    }

    [Fact]
    public void RollingBackInForwardGearKeepsElapsedTimeAdvancing()
    {
        var clock = new TimeAttackClock();
        for (var i = -1; i <= 100; i++) clock.Update(State(i / 10d, i / 10d));
        LapTelemetry? sample = null;
        for (var i = 1; i <= 10; i++) sample = clock.Update(State(10 + i / 10d, 10 - i / 10d));
        Assert.InRange(sample!.CurrentLapSeconds, 10.99, 11.01);
    }

    [Fact]
    public void TimeAttackDeltaMeasuresTimeAtTheSamePosition()
    {
        var tracker = new LapDeltaTracker();
        Reference(tracker);
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var i = 651; i <= 900; i++) reading = Update(tracker, i / 10d, 60 + (i / 10d - 60) * .9);
        Assert.Equal(LapDeltaStatus.Comparing, reading.Status);
        Assert.InRange(reading.Seconds!.Value, 2.97, 3.03);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClockFollowsTheGameClockAcrossAPause(bool gameKeptRunning)
    {
        var clock = new TimeAttackClock();
        for (var i = -1; i <= 100; i++) clock.Update(State(i / 10d, i / 10d));
        // Five seconds of wall time pass. A paused game freezes its timestamp; a game that
        // kept running (online) advances it, and so does its own timer.
        var resumed = State(gameKeptRunning ? 15 : 10, 10) with { ReceivedAtUtc = State(15, 0).ReceivedAtUtc };
        var sample = clock.Update(resumed);
        Assert.InRange(sample!.CurrentLapSeconds, (gameKeptRunning ? 15 : 10) - .01, (gameKeptRunning ? 15 : 10) + .01);
    }

    [Fact]
    public void RewindToAfterGateRestoresNewLapMetadata()
    {
        var clock = new TimeAttackClock();
        for (var i = -1; i <= 612; i++) clock.Update(State(i / 10d - .05, i / 10d - .05));
        var sample = clock.Update(State(60.02, 60.02));
        Assert.Equal(1, sample!.LapNumber);
        Assert.InRange(sample.CurrentLapSeconds, .019, .021);
        Assert.InRange(sample.LastLapSeconds, 59.99, 60.01);
    }

    [Fact]
    public void RewindingWithinAStopUsesTimestampRatherThanFirstVisitToPosition()
    {
        var clock = new TimeAttackClock();
        for (var i = -1; i <= 150; i++) clock.Update(State(i / 10d, Math.Min(i / 10d, 10)));
        var sample = clock.Update(State(14, 10));
        Assert.InRange(sample!.CurrentLapSeconds, 13.99, 14.01);
    }

    [Fact]
    public void ContinuousRewindRetainsEveryRestoredCheckpoint()
    {
        var clock = new TimeAttackClock();
        for (var i = -1; i <= 1000; i++) clock.Update(State(i * .016, i * .016));
        for (var i = 1; i <= 80; i++)
        {
            var time = 16 - i * .016;
            var sample = clock.Update(State(time, time));
            Assert.NotNull(sample);
            Assert.InRange(sample.CurrentLapSeconds, time - .002, time + .002);
        }
    }

    [Fact]
    public void DifferentCircuitAndTimingModeClearOldReferences()
    {
        var tracker = new LapDeltaTracker();
        Reference(tracker);
        Assert.Null(Update(tracker, 66, -.1, 1).Seconds);
        Assert.Null(tracker.ReadMap(1));
        Update(tracker, 66.1, .1, 1);
        Assert.False(tracker.ReadMap(2)!.Outline.Complete);
        Assert.Null(tracker.Update(State(67, 1, 1), LapDeltaReference.SessionBest).Seconds);
        Assert.Null(tracker.ReadMap(3));
    }
}
