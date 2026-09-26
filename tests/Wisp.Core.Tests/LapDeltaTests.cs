using System.Numerics;
using System.Text.Json;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class LapDeltaTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    [Fact]
    public void MapLearnsThenCachesCircuitAndKeepsLivePosition()
    {
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 60, 0, 10);
        var learning = tracker.ReadMap(1)!;
        Assert.False(learning.Outline.Complete);
        Assert.True(learning.Outline.Points.Count > 20);
        tracker.Update(State(10.1, 10.1, 0, Circle(10.1 / 60)), LapDeltaReference.SessionBest);
        var next = tracker.ReadMap(2)!;
        Assert.Same(learning.Outline, next.Outline);
        Assert.NotEqual(learning.Position, next.Position);
        for (var i = 102; i < 600; i++) tracker.Update(State(i / 10d, i / 10d, 0, Circle(i / 600d)), LapDeltaReference.SessionBest);
        tracker.Update(State(60, 0, 1, Circle(0)), LapDeltaReference.SessionBest);
        var complete = tracker.ReadMap(3)!;
        Assert.True(complete.Outline.Complete);
        Drive(tracker, 60, 60, 1, 25);
        Assert.Same(complete.Outline, tracker.ReadMap(4)!.Outline);
        Assert.InRange(complete.Outline.Points.Count, 20, 4096);
        tracker.Reset();
        Assert.Null(tracker.ReadMap(5));
    }

    [Fact]
    public void MapProjectionPreservesAspectAndIncludesOffTrackCar()
    {
        var outline = new LapTrackOutline(Array.AsReadOnly(new[] { new LapPosition(-100, 5, -50), new LapPosition(100, 20, 50) }), true);
        var bounds = LapMapBounds.From(outline);
        Assert.Equal(200, bounds.Size);
        Assert.Equal((0d, .75d), bounds.Project(outline.Points[0]));
        Assert.Equal((1d, .25d), bounds.Project(outline.Points[1]));
        var car = new LapPosition(400, 0, -200);
        var expanded = bounds.Include(car);
        foreach (var position in outline.Points.Append(car))
        {
            var point = expanded.Project(position);
            Assert.InRange(point.X, 0, 1);
            Assert.InRange(point.Y, 0, 1);
        }
        Assert.Equal(50, LapMapBounds.From(new(Array.Empty<LapPosition>(), false)).Size);
    }
    private static Vector3 Circle(double progress) => new((float)(150 * Math.Cos(progress * Math.Tau)), 0,
        (float)(150 * Math.Sin(progress * Math.Tau)));

    internal static VehicleState State(double race, double lap, int number, Vector3 position, double last = 60) => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = (uint)Math.Round(race * 1000),
        ReceivedAtUtc = Epoch.AddSeconds(race),
        CarOrdinal = 42,
        Drivetrain = DrivetrainType.RearWheelDrive,
        GroundSpeedMetersPerSecond = 16,
        Lap = new(position, (float)lap, (float)last, (float)race, (ushort)number, 1),
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        EngineRpm = 3000,
        EngineMaximumRpm = 7000,
        Gear = TransmissionGear.Third,
        Steering = 0,
        Accelerator = 100,
        Brake = 0
    };

    private static LapDeltaReading Drive(LapDeltaTracker tracker, double start, double duration, int number,
        double until, LapDeltaReference mode = LapDeltaReference.SessionBest, Func<double, Vector3>? path = null, double last = 60)
    {
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var i = 0; i <= (int)Math.Round(until * 10); i++)
        {
            var t = i / 10d;
            reading = tracker.Update(State(start + t, t, number, (path ?? Circle)(t / duration), last), mode);
        }
        return reading;
    }

    [Theory]
    [InlineData(64, 2)]
    [InlineData(56, -2)]
    public void ComparesAtPositionInsteadOfElapsedTime(double duration, double expected)
    {
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 60, 0, 59.9);
        var result = Drive(tracker, 60, duration, 1, duration / 2);
        Assert.Equal(LapDeltaStatus.Comparing, result.Status);
        Assert.Equal(expected, result.Seconds!.Value, 2);
        Assert.Equal(60, result.ReferenceSeconds);
    }

    [Fact]
    public void PartialFirstLapBecomesReferenceOnlyAfterAFullLap()
    {
        var tracker = new LapDeltaTracker();
        for (var i = 200; i < 600; i++) tracker.Update(State(i / 10d, i / 10d, 0, Circle(i / 600d)), LapDeltaReference.SessionBest);
        var first = Drive(tracker, 60, 60, 1, 59.9);
        Assert.Equal(LapDeltaStatus.RecordingLap, first.Status);
        Assert.Null(first.Seconds);
        Assert.Equal(LapDeltaStatus.Comparing, Drive(tracker, 120, 60, 2, 5).Status);
    }

    [Fact]
    public void BestAndPreviousReferencesCanBeSwitchedMidLap()
    {
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 60, 0, 59.9);
        Drive(tracker, 60, 64, 1, 63.9);
        var best = Drive(tracker, 124, 60, 2, 30, last: 64);
        var previous = tracker.Update(State(154.1, 30.1, 2, Circle(30.1 / 60), 64), LapDeltaReference.PreviousLap);
        Assert.Equal(0, best.Seconds!.Value, 2);
        Assert.Equal(-2.007, previous.Seconds!.Value, 2);
        Assert.Equal(64, previous.ReferenceSeconds);
    }

    [Fact]
    public void FasterCompleteLapReplacesBest()
    {
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 60, 0, 59.9);
        Drive(tracker, 60, 56, 1, 55.9);
        var result = Drive(tracker, 116, 60, 2, 30, last: 56);
        Assert.Equal(56, result.ReferenceSeconds);
        Assert.Equal(2, result.Seconds!.Value, 2);
    }

    [Theory]
    [InlineData("car")]
    [InlineData("teleport")]
    [InlineData("race-off")]
    public void SessionDiscontinuitiesFenceReference(string cause)
    {
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 60, 0, 59.9);
        Drive(tracker, 60, 60, 1, 10);
        var state = State(70.1, 10.1, 1, Circle(10.1 / 60));
        state = cause switch
        {
            "rewind" => State(69, 9, 1, Circle(9d / 60)),
            "car" => state with { CarOrdinal = 43 },
            "teleport" => state with { Lap = state.Lap! with { Position = new Vector3(10000, 0, 0) } },
            "gap" => State(72, 12, 1, Circle(.2)),
            "race-off" => state with { IsRaceOn = false },
            "freeroam" => state with { Lap = state.Lap! with { RacePosition = 0 } },
            "restart" => State(70.1, 0, 0, Circle(0)) with { Lap = new(Circle(0), 0, 0, 0, 0, 1) },
            "paused-clock" => state with { ReceivedAtUtc = Epoch.AddSeconds(75) },
            _ => state
        };
        Assert.Null(tracker.Update(state, LapDeltaReference.SessionBest).Seconds);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("rewind")]
    [InlineData("gap")]
    [InlineData("invalid")]
    public void InterruptionsKeepReferenceAndMapAndResumeDelta(string cause)
    {
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 60, 0, 59.9);
        Drive(tracker, 60, 60, 1, 30);
        var outline = tracker.ReadMap(1)!.Outline;
        if (cause == "pause") tracker.Update(State(90, 30, 1, Circle(.5)) with { IsRaceOn = false }, LapDeltaReference.SessionBest);
        if (cause == "invalid") tracker.Interrupt();
        var resume = cause == "rewind" ? 20 : cause == "gap" ? 32 : 30.1;
        LapDeltaReading result = LapDeltaReading.Waiting;
        for (var i = 0; i < 10; i++) result = tracker.Update(State(60 + resume + i / 10d, resume + i / 10d, 1,
            Circle((resume + i / 10d) / 60)), LapDeltaReference.SessionBest);
        Assert.Same(outline, tracker.ReadMap(2)!.Outline);
        Assert.Equal(LapDeltaStatus.Comparing, result.Status);
        Assert.InRange(result.Seconds!.Value, -.02, .02);
        // Interrupted recordings cannot replace a complete reference.
        Drive(tracker, 60, 60, 1, 59.9);
        var next = Drive(tracker, 120, 60, 2, 2);
        Assert.Equal(60, next.ReferenceSeconds);
    }

    [Fact]
    public void PausingFirstLapRetainsTheAlreadyLearnedOutline()
    {
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 60, 0, 20);
        var outline = tracker.ReadMap(1)!.Outline;
        tracker.Interrupt();
        for (var i = 0; i < 50; i++) tracker.Update(State(21 + i / 10d, 21 + i / 10d, 0,
            Circle((21 + i / 10d) / 60)), LapDeltaReference.SessionBest);
        Assert.True(tracker.ReadMap(2)!.Outline.Points.Count > outline.Points.Count);
    }

    private static VehicleState At(VehicleState state, double wall) => state with
    {
        GameTimestampMilliseconds = (uint)Math.Round(wall * 1000),
        ReceivedAtUtc = Epoch.AddSeconds(wall)
    };

    // Drives lap `number` from lap time `from` to `until`; the race clock starts at `raceStart`
    // and the wall clock runs `wallOffset` seconds ahead of it (time spent paused).
    private static LapDeltaReading DriveLap(LapDeltaTracker tracker, double raceStart, int number, double from,
        double until, double wallOffset = 0, double duration = 60, double last = 60, byte position = 1)
    {
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var i = (int)Math.Round(from * 10); i <= (int)Math.Round(until * 10); i++)
        {
            var t = i / 10d;
            var state = At(State(raceStart + t, t, number, Circle(t / duration), last), raceStart + t + wallOffset);
            reading = tracker.Update(state with { Lap = state.Lap! with { RacePosition = position } }, LapDeltaReference.SessionBest);
        }
        return reading;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PausingMidLapKeepsRecordingTheSameLap(bool raceOffPackets)
    {
        var tracker = new LapDeltaTracker();
        DriveLap(tracker, 0, 0, 0, 30);
        var before = tracker.ReadMap(1)!.Outline;
        // Thirty seconds paused: the lap clocks stop while the wall clock continues,
        // either with race-off packets or with no packets at all.
        if (raceOffPackets)
            for (var i = 1; i <= 300; i++)
                tracker.Update(At(State(30, 30, 0, Circle(.5)) with { IsRaceOn = false }, 30 + i / 10d), LapDeltaReference.SessionBest);
        var resumed = DriveLap(tracker, 0, 0, 30.1, 45, wallOffset: 30);
        Assert.Equal(LapDeltaStatus.RecordingLap, resumed.Status);
        var map = tracker.ReadMap(2)!;
        Assert.True(map.IsRecording);
        Assert.True(map.Outline.Points.Count > before.Points.Count);
        DriveLap(tracker, 0, 0, 45.1, 59.9, wallOffset: 30);
        var next = DriveLap(tracker, 60, 1, 0, 30, wallOffset: 30);
        Assert.Equal(LapDeltaStatus.Comparing, next.Status);
        Assert.Equal(60, next.ReferenceSeconds);
        Assert.InRange(next.Seconds!.Value, -.02, .02);
    }

    [Fact]
    public void PausedZeroPositionRivalsLapResumesOnceItsTimerAdvances()
    {
        var tracker = new LapDeltaTracker();
        DriveLap(tracker, 0, 0, 0, 30, position: 0);
        tracker.Update(At(State(30, 30, 0, Circle(.5)) with { IsRaceOn = false }, 40), LapDeltaReference.SessionBest);
        Assert.Equal(LapDeltaStatus.RecordingLap, DriveLap(tracker, 0, 0, 30.1, 59.9, wallOffset: 10, position: 0).Status);
        var next = DriveLap(tracker, 60, 1, 0, 30, wallOffset: 10, position: 0);
        Assert.Equal(LapDeltaStatus.Comparing, next.Status);
        Assert.Equal(60, next.ReferenceSeconds);
    }

    [Fact]
    public void LapThatKeptRunningWithoutSamplesIsComparedButNotAReference()
    {
        var tracker = new LapDeltaTracker();
        DriveLap(tracker, 0, 0, 0, 59.9);
        DriveLap(tracker, 60, 1, 0, 30, duration: 56);
        // Five seconds without samples while the game kept running and the car kept driving.
        var resumed = DriveLap(tracker, 60, 1, 35, 45, duration: 56);
        Assert.Equal(LapDeltaStatus.Comparing, resumed.Status);
        DriveLap(tracker, 60, 1, 45.1, 55.9, duration: 56);
        var next = DriveLap(tracker, 116, 2, 0, 10, last: 56);
        Assert.Equal(LapDeltaStatus.Comparing, next.Status);
        Assert.Equal(60, next.ReferenceSeconds);
    }

    [Fact]
    public void LearningMapRedrawsOnTheLapAfterAnInterruption()
    {
        var tracker = new LapDeltaTracker();
        DriveLap(tracker, 0, 0, 0, 20);
        tracker.ReadMap(1);
        tracker.Interrupt();
        DriveLap(tracker, 0, 0, 20.1, 59.9);
        var frozen = tracker.ReadMap(2)!.Outline;
        DriveLap(tracker, 60, 1, 0, 5);
        var learning = tracker.ReadMap(3)!;
        Assert.NotSame(frozen, learning.Outline);
        Assert.True(learning.IsRecording);
    }

    [Fact]
    public void RestartingTheRaceBeginsANewLapComparedFromTheStart()
    {
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 60, 0, 59.9);
        Drive(tracker, 60, 60, 1, 10);
        // Restart: every game clock returns to zero at the start line.
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var i = 0; i <= 100; i++)
        {
            var t = i / 10d;
            var state = At(State(t, t, 0, Circle(t / 60)), 80 + t);
            reading = tracker.Update(state with { Lap = state.Lap! with { LastLapSeconds = 0 } }, LapDeltaReference.SessionBest);
        }
        Assert.Equal(LapDeltaStatus.Comparing, reading.Status);
        Assert.InRange(reading.Seconds!.Value, -.02, .02);
    }

    [Fact]
    public void RewindingMidLapKeepsTheLapAndItsLearningMap()
    {
        var tracker = new LapDeltaTracker();
        DriveLap(tracker, 0, 0, 0, 30);
        var before = tracker.ReadMap(1)!.Outline;
        // Rewind five seconds: the game clocks and the car return to an earlier moment of the lap.
        for (var i = 1; i <= 50; i++)
        {
            var t = 30 - i / 10d;
            tracker.Update(State(t, t, 0, Circle(t / 60)) with { ReceivedAtUtc = Epoch.AddSeconds(30 + i / 10d) }, LapDeltaReference.SessionBest);
        }
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var i = 1; i <= 349; i++)
        {
            var t = 25 + i / 10d;
            reading = tracker.Update(State(t, t, 0, Circle(t / 60)) with { ReceivedAtUtc = Epoch.AddSeconds(35 + i / 10d) }, LapDeltaReference.SessionBest);
        }
        Assert.Equal(LapDeltaStatus.RecordingLap, reading.Status);
        Assert.True(tracker.ReadMap(2)!.Outline.Points.Count > before.Points.Count);
        for (var i = 0; i <= 300; i++)
        {
            var t = i / 10d;
            reading = tracker.Update(State(60 + t, t, 1, Circle(t / 60)) with { ReceivedAtUtc = Epoch.AddSeconds(70 + t) }, LapDeltaReference.SessionBest);
        }
        Assert.Equal(LapDeltaStatus.Comparing, reading.Status);
        Assert.Equal(60, reading.ReferenceSeconds);
        Assert.InRange(reading.Seconds!.Value, -.02, .02);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BeingMovedFarFromTheCircuitForgetsItsLapsAndMap(bool far)
    {
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 60, 0, 59.9);
        Drive(tracker, 60, 60, 1, 20);
        // A livery change in the menus, then the car is placed at the festival (or back on the circuit).
        for (var i = 1; i <= 50; i++)
            tracker.Update(State(80, 20, 1, Circle(20d / 60)) with { IsRaceOn = false, ReceivedAtUtc = Epoch.AddSeconds(80 + i / 10d) },
                LapDeltaReference.SessionBest);
        var place = far ? new Vector3(3000, 0, -2000) : Circle(.9);
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var i = 1; i <= 20; i++)
        {
            var state = State(80 + i / 10d, 20 + i / 10d, 1, place + new Vector3(i * 1.5f, 0, 0)) with { ReceivedAtUtc = Epoch.AddSeconds(85 + i / 10d) };
            reading = tracker.Update(state, LapDeltaReference.SessionBest);
        }
        if (far)
        {
            Assert.Null(reading.ReferenceSeconds);
            Assert.DoesNotContain(tracker.ReadMap(1)!.Outline.Points, point => Vector3.Distance(point.ToVector(), Circle(.5)) < 200);
        }
        else Assert.Equal(60, reading.ReferenceSeconds);
    }

    [Fact]
    public void EveryPacketOfATimestampTickIsUsed()
    {
        // FH6 sends about two packets per timestamp tick, each with new data.
        var tracker = new LapDeltaTracker();
        LapDeltaReading reading = LapDeltaReading.Waiting;
        for (var lap = 0; lap < 2; lap++)
            for (var i = 0; i < 1200; i++)
            {
                var t = i / 20d;
                var race = lap * 60 + t;
                reading = tracker.Update(State(race, t, lap, Circle(t / 60)) with
                {
                    GameTimestampMilliseconds = (uint)(Math.Floor(race * 10) * 100),
                    ReceivedAtUtc = Epoch.AddSeconds(race)
                }, LapDeltaReference.SessionBest);
            }
        Assert.Equal(LapDeltaStatus.Comparing, reading.Status);
        Assert.Equal(60, reading.ReferenceSeconds!.Value, 1);
        Assert.InRange(reading.Seconds!.Value, -.02, .02);
    }

    [Fact]
    public void MapProjectsNorthThenEastAsARightTurn()
    {
        var outline = new LapTrackOutline(new LapPosition[] { new(0, 0, 0), new(0, 0, 100), new(50, 0, 100) }, true);
        foreach (var car in new[] { new LapPosition(0, 0, -200), new LapPosition(0, 0, 300) })
        {
            var bounds = LapMapBounds.From(outline).Include(car);
            var a = bounds.Project(outline.Points[0]); var b = bounds.Project(outline.Points[1]); var c = bounds.Project(outline.Points[2]);
            Assert.True(b.Y < a.Y && c.X > b.X && c.Y == b.Y);
        }
    }

    [Fact]
    public void PausedLongHairpinSearchesPastNearbyOutboundLeg()
    {
        static Vector3 Path(double progress)
        {
            var distance = progress * 2020;
            return distance switch
            {
                < 1000 => new((float)distance, 0, 0),
                < 1010 => new(1000, 0, (float)(distance - 1000)),
                < 2010 => new((float)(2010 - distance), 0, 10),
                _ => new(0, 0, (float)(2020 - distance))
            };
        }
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 202, 0, 201.9, path: Path, last: 202);
        Drive(tracker, 202, 202, 1, 150, path: Path, last: 202);
        tracker.Interrupt();
        LapDeltaReading result = LapDeltaReading.Waiting;
        for (var i = 1; i <= 20; i++) result = tracker.Update(State(352 + i / 10d, 150 + i / 10d, 1,
            Path((150 + i / 10d) / 202), 202), LapDeltaReference.SessionBest);
        Assert.Equal(LapDeltaStatus.Comparing, result.Status);
        Assert.InRange(result.Seconds!.Value, -.05, .05);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public void FreeRoamWorldClockOrStaleLapTimerDoesNotLearnRoads(float staleLap)
    {
        var tracker = new LapDeltaTracker();
        for (var i = 0; i < 100; i++)
        {
            var state = State(100 + i / 10d, staleLap, 0, new Vector3(i * 2, 0, 0));
            state = state with { Lap = state.Lap! with { RacePosition = 0 } };
            Assert.Equal(LapDeltaStatus.WaitingForLap, tracker.Update(state, LapDeltaReference.SessionBest).Status);
            Assert.Null(tracker.ReadMap(i + 1));
        }
    }

    [Fact]
    public void ZeroPositionRivalsTimerStillBuildsReferenceAcrossLapBoundary()
    {
        var tracker = new LapDeltaTracker();
        LapDeltaReading result = LapDeltaReading.Waiting;
        for (var i = 0; i <= 620; i++)
        {
            var time = i / 10d;
            var lap = time % 60;
            var state = State(time, lap, i / 600, Circle(lap / 60));
            result = tracker.Update(state with { Lap = state.Lap! with { RacePosition = 0 } }, LapDeltaReference.SessionBest);
        }
        Assert.Equal(LapDeltaStatus.Comparing, result.Status);
        Assert.InRange(result.Seconds!.Value, -.02, .02);
        Assert.True(tracker.ReadMap(1)!.IsRecording);
    }

    [Fact]
    public void ZeroPositionRivalsSupportsTimerValuesRepeatedAcrossPackets()
    {
        var tracker = new LapDeltaTracker();
        LapDeltaReading result = LapDeltaReading.Waiting;
        for (var i = 0; i <= 3900; i++)
        {
            var milliseconds = i * 16;
            var time = milliseconds / 1000d;
            var lap = milliseconds % 60000 / 1000d;
            var clock = Math.Floor(lap * 10) / 10;
            var state = State(time, clock, milliseconds / 60000, Circle(lap / 60));
            result = tracker.Update(state with { Lap = state.Lap! with { RacePosition = 0 } }, LapDeltaReference.SessionBest);
        }
        Assert.Equal(LapDeltaStatus.Comparing, result.Status);
        Assert.InRange(result.Seconds!.Value, -.15, .15);
        Assert.True(tracker.ReadMap(1)!.Outline.Complete);
    }

    [Fact]
    public void LeavingAnEventRetainsOutlineButStopsLearningAndComparison()
    {
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 60, 0, 10);
        var outline = tracker.ReadMap(1)!.Outline;
        for (var i = 1; i <= 20; i++)
        {
            var state = State(10 + i / 10d, 10, 0, Circle((10 + i / 10d) / 60));
            tracker.Update(state with { Lap = state.Lap! with { RacePosition = 0 } }, LapDeltaReference.SessionBest);
        }
        var idle = tracker.ReadMap(2)!;
        Assert.Same(outline, idle.Outline);
        Assert.False(idle.IsRecording);
        var resume = tracker.Update(State(12.1, 12.1, 0, Circle(12.1 / 60)), LapDeltaReference.SessionBest);
        Assert.True(tracker.ReadMap(3)!.IsRecording);
        Assert.True(tracker.ReadMap(4)!.Outline.Points.Count >= outline.Points.Count);
    }

    [Fact]
    public void TimestampWrapDoesNotResetLap()
    {
        var tracker = new LapDeltaTracker();
        LapDeltaReading result = LapDeltaReading.Waiting;
        for (var i = 0; i <= 610; i++)
        {
            var t = i / 10d;
            var lap = t < 60 ? t : t - 60;
            var state = State(t, lap, t < 60 ? 0 : 1, Circle(lap / 60)) with
            { GameTimestampMilliseconds = unchecked(uint.MaxValue - 30_000 + (uint)(i * 100)) };
            result = tracker.Update(state, LapDeltaReference.SessionBest);
        }
        Assert.Equal(LapDeltaStatus.Comparing, result.Status);
        Assert.Equal(0, result.Seconds!.Value, 2);
    }

    [Fact]
    public void RejoinsAfterLongDetourWithoutHoldingAnOldDelta()
    {
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 60, 0, 59.9);
        var unavailable = false;
        LapDeltaReading result = LapDeltaReading.Waiting;
        for (var i = 0; i <= 450; i++)
        {
            var t = i / 10d;
            var offset = t is > 10 and < 40 ? Math.Min(60, Math.Min((t - 10) * 12, (40 - t) * 12)) : 0;
            result = tracker.Update(State(60 + t, t, 1, Circle(t / 60) * (float)(1 + offset / 150)), LapDeltaReference.SessionBest);
            if (t is > 16 and < 34) unavailable |= result.Seconds is null;
        }
        Assert.True(unavailable);
        Assert.Equal(LapDeltaStatus.Comparing, result.Status);
        Assert.InRange(result.Seconds!.Value, -.05, .05);
    }

    [Fact]
    public void ParallelOppositeLegDoesNotStealTheMatch()
    {
        static Vector3 Hairpin(double progress)
        {
            var distance = progress * 620;
            return distance switch
            {
                < 300 => new((float)distance, 0, 0),
                < 310 => new(300, 0, (float)(distance - 300)),
                < 610 => new((float)(610 - distance), 0, 10),
                _ => new(0, 0, (float)(620 - distance))
            };
        }
        var tracker = new LapDeltaTracker();
        Drive(tracker, 0, 62, 0, 61.9, path: Hairpin);
        LapDeltaReading result = LapDeltaReading.Waiting;
        for (var i = 0; i <= 310; i++)
        {
            var t = i / 10d;
            var p = Hairpin(Math.Min(t, 29.5) / 62);
            p.Z += (float)Math.Min(6, t);
            result = tracker.Update(State(62 + t, t, 1, p, 62), LapDeltaReference.SessionBest);
        }
        Assert.Equal(LapDeltaStatus.Comparing, result.Status);
        Assert.InRange(result.Seconds!.Value, 1.4, 1.6);
    }

    [Fact]
    public void StationaryReferenceTimeIsNotSpreadOverTheNextStraight()
    {
        var tracker = new LapDeltaTracker();
        // A ten-second stop at quarter lap extends this reference to 70 seconds.
        for (var i = 0; i < 700; i++)
        {
            var t = i / 10d;
            var moving = t < 15 ? t : t <= 25 ? 15 : t - 10;
            tracker.Update(State(t, t, 0, Circle(moving / 60)), LapDeltaReference.SessionBest);
        }
        var result = Drive(tracker, 70, 60, 1, 15.5, last: 70);
        Assert.Equal(LapDeltaStatus.Comparing, result.Status);
        Assert.InRange(result.Seconds!.Value, -10.1, -9.9);
    }

    [Fact]
    public void LapCoordinatesSurviveSavedRunSerialization()
    {
        var state = State(10, 10, 1, new Vector3(12, 34, 56));
        var restored = JsonSerializer.Deserialize<VehicleState>(JsonSerializer.Serialize(state));
        Assert.Equal(state.Lap, restored!.Lap);
        var old = JsonSerializer.Deserialize<VehicleState>(JsonSerializer.Serialize(state with { Lap = null }));
        Assert.Null(old!.Lap);
    }
}
