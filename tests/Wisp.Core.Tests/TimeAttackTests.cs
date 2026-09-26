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
        if (cause == "gap") after = State(3, 2.9);
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
        // Nothing arrives for two seconds while the PC's uptime, the game's timestamp, runs on. A
        // pause resumes where the car was; a rewind resumes two seconds back, at that moment's speed.
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var i = 1; i <= 10; i++)
        {
            var time = (rewind ? 63 : 65) + i / 10d;
            var now = State(67 + i / 10d, 0);
            reading = tracker.Update(State(time, time) with
            {
                GameTimestampMilliseconds = now.GameTimestampMilliseconds,
                ReceivedAtUtc = now.ReceivedAtUtc
            }, LapDeltaReference.SessionBest, LapTimingMode.TimeAttack);
        }
        Assert.Same(outline, tracker.ReadMap(2)!.Outline);
        Assert.Equal(LapDeltaStatus.Comparing, reading.Status);
        Assert.InRange(reading.Seconds!.Value, -.01, .01);
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
    [InlineData(.5, false)]
    [InlineData(20, false)]
    [InlineData(5, true)]
    public void ClockFollowsTheGameAcrossAGapInItsData(double seconds, bool gameKeptRunning)
    {
        var clock = new TimeAttackClock();
        for (var i = -1; i <= 100; i++) clock.Update(State(i / 10d, i / 10d));
        // The PC's uptime runs on with no data. A paused game resumes one frame on from where the
        // car was; a game that kept running (online, or with its packets lost) resumes further on.
        var lap = gameKeptRunning ? 10 + seconds : 10.01;
        var sample = clock.Update(State(10 + seconds, lap));
        Assert.InRange(sample!.CurrentLapSeconds, lap - .01, lap + .01);
    }

    [Fact]
    public void LapTimeIsMeasuredWithinTheTimestampTicks()
    {
        // FH6 stamps packets with the PC's uptime in 15.625 ms ticks and sends about two per tick.
        var clock = new TimeAttackClock();
        var random = new Random(7);
        LapTelemetry? sample = null;
        for (var i = -12; i <= 120 * 120 + 60; i++)
        {
            var time = i / 120d;
            sample = clock.Update(State(time, time) with
            {
                GameTimestampMilliseconds = (uint)Math.Floor(Math.Floor((time + 5000.3) * 64) * 15.625),
                ReceivedAtUtc = DateTimeOffset.UnixEpoch.AddTicks((long)((time + .0003 + random.NextDouble() * .001) * TimeSpan.TicksPerSecond))
            });
        }
        Assert.Equal(2, sample!.LapNumber);
        Assert.InRange(sample.LastLapSeconds, 59.998, 60.002);
        Assert.InRange(sample.CurrentLapSeconds, .498, .502);
    }

    [Fact]
    public void RewindToJustAfterTheGateKeepsTheCompletedLap()
    {
        var clock = new TimeAttackClock();
        for (var i = -1; i <= 650; i++) clock.Update(State(i / 10d - .05, i / 10d - .05));
        // Two seconds without data, then the car is back just after the gate.
        var sample = clock.Update(State(66.95, 60.25));
        Assert.Equal(1, sample!.LapNumber);
        Assert.InRange(sample.CurrentLapSeconds, .24, .26);
        Assert.InRange(sample.LastLapSeconds, 59.99, 60.01);
    }

    [Fact]
    public void RewindToBeforeTheGateEndsTheAttemptAndKeepsTheCompletedLap()
    {
        var clock = new TimeAttackClock();
        for (var i = -1; i <= 650; i++) clock.Update(State(i / 10d - .05, i / 10d - .05));
        Assert.Null(clock.Update(State(66.95, 59.5)));
        LapTelemetry? sample = null;
        for (var i = 1; i <= 10; i++) sample = clock.Update(State(66.95 + i / 10d, 59.5 + i / 10d));
        Assert.Equal(1, sample!.LapNumber);
        Assert.InRange(sample.LastLapSeconds, 59.99, 60.01);
        Assert.InRange(sample.CurrentLapSeconds, .49, .51);
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
