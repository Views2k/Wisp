using System.Diagnostics;
using System.IO;
using System.Numerics;
using Wisp.App.Laps;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LapRunCaptureTests
{
    [Fact]
    public async Task OptInIsRequiredEvenWhenTheTrackerCompletesALap()
    {
        var saved = new List<RecordedRun>();
        var capture = new LapRunCapture(run => { saved.Add(run); return Task.CompletedTask; });
        Drive(capture, new(), 0, 600);
        await capture.CompleteAsync();
        Assert.Empty(saved);
    }

    [Fact]
    public async Task CompleteGameLapKeepsEveryOriginalStateThroughFinish()
    {
        var saved = new List<RecordedRun>();
        var capture = Capture(saved);
        var states = Drive(capture, new(), 0, 600);
        await capture.CompleteAsync();
        var run = Assert.Single(saved);
        Assert.Equal(LapTimingMode.GameLaps, run.LapTimingMode);
        Assert.False(run.IsIncomplete);
        Assert.Equal(states.Length, run.Samples.Length);
        for (var i = 0; i < states.Length; i++) Assert.Equal(states[i], run.Samples[i].State);
        Assert.Equal(0, run.Samples[0].ElapsedSeconds);
        Assert.Equal(60, run.Samples[^1].ElapsedSeconds);
        RunStore.Validate(run);
    }

    [Theory]
    [InlineData("current", true)]
    [InlineData("missing", false)]
    [InlineData("future", false)]
    [InlineData("stale", false)]
    [InlineData("wrong-car", false)]
    [InlineData("wrong-drivetrain", false)]
    [InlineData("not-driving", false)]
    [InlineData("expired-driving", false)]
    [InlineData("untrusted-radius", false)]
    public async Task LapWheelSpeedRequiresMatchingContemporaneousTrustedContext(string condition, bool expected)
    {
        var saved = new List<RecordedRun>();
        var capture = Capture(saved);
        var tracker = new LapDeltaTracker();
        for (var tick = 0; tick <= 600; tick++)
        {
            var timestamp = Stopwatch.Frequency * 10 + tick * Stopwatch.Frequency / 10;
            var state = State(tick) with { ReceivedTimestamp = timestamp };
            RunRecordingContext? context = new(timestamp, state.CarOrdinal, state.Drivetrain, true, .3, .31, timestamp + Stopwatch.Frequency / 4);
            context = condition switch
            {
                "missing" => null,
                "future" => context with { EffectiveTimestamp = timestamp + 1 },
                "stale" => context with { EffectiveTimestamp = timestamp - Stopwatch.Frequency },
                "wrong-car" => context with { CarOrdinal = state.CarOrdinal + 1 },
                "wrong-drivetrain" => context with { Drivetrain = state.Drivetrain == DrivetrainType.FrontWheelDrive ? DrivetrainType.RearWheelDrive : DrivetrainType.FrontWheelDrive },
                "not-driving" => context with { IsDriving = false },
                "expired-driving" => context with { DrivingValidUntilTimestamp = timestamp - 1 },
                "untrusted-radius" => context with { RearRadiusMeters = null },
                _ => context
            };
            tracker.Update(state, LapDeltaReference.SessionBest, capture: true);
            capture.Observe(state, LapTimingMode.GameLaps, tracker.CaptureFrame, tracker.CompletedCapture, context);
        }
        await capture.CompleteAsync();
        var run = Assert.Single(saved);
        Assert.Equal(601, run.Samples.Length);
        Assert.All(run.Samples, sample =>
        {
            if (expected)
            {
                Assert.Equal(.3, sample.FrontRadiusMeters);
                Assert.Equal(.31, sample.RearRadiusMeters);
                Assert.Equal(DrivenWheelSpeed.MetersPerSecond(sample.State, new(.3, .31)), sample.WheelSpeedMetersPerSecond);
            }
            else
            {
                Assert.Null(sample.FrontRadiusMeters);
                Assert.Null(sample.RearRadiusMeters);
                Assert.Null(sample.WheelSpeedMetersPerSecond);
            }
        });
        RunStore.Validate(run);
    }

    [Fact]
    public async Task PacketBeforeStartLineKeepsItsOwnCalibration()
    {
        var saved = new List<RecordedRun>();
        var capture = Capture(saved);
        var tracker = new LapDeltaTracker();
        for (var tick = 100; tick <= 1200; tick++)
        {
            var timestamp = Stopwatch.Frequency * 10 + tick * Stopwatch.Frequency / 10;
            var state = State(tick) with { ReceivedTimestamp = timestamp };
            var radius = tick < 600 ? .3 : .31;
            var context = new RunRecordingContext(timestamp, state.CarOrdinal, state.Drivetrain, true, radius, radius);
            tracker.Update(state, LapDeltaReference.SessionBest, capture: true);
            capture.Observe(state, LapTimingMode.GameLaps, tracker.CaptureFrame, tracker.CompletedCapture, context);
        }
        await capture.CompleteAsync();
        var run = Assert.Single(saved);
        Assert.Equal(State(599).GameTimestampMilliseconds, run.Samples[0].State.GameTimestampMilliseconds);
        Assert.Equal(.3, run.Samples[0].RearRadiusMeters);
        Assert.Equal(.31, run.Samples[1].RearRadiusMeters);
        Assert.Null(run.Samples[0].State.ReceivedTimestamp);
        RunStore.Validate(run);
    }

    [Fact]
    public async Task ChangingWheelCalibrationDoesNotBreakTheOriginalLapTimingOrGeometry()
    {
        var saved = new List<RecordedRun>();
        var capture = Capture(saved);
        var tracker = new LapDeltaTracker();
        for (var tick = 0; tick <= 600; tick++)
        {
            var timestamp = Stopwatch.Frequency * 10 + tick * Stopwatch.Frequency / 10;
            var state = State(tick) with { ReceivedTimestamp = timestamp };
            var radius = tick < 300 ? .3 : .31;
            var context = new RunRecordingContext(timestamp, state.CarOrdinal, state.Drivetrain, true, radius, radius);
            tracker.Update(state, LapDeltaReference.SessionBest, capture: true);
            capture.Observe(state, LapTimingMode.GameLaps, tracker.CaptureFrame, tracker.CompletedCapture, context);
        }
        await capture.CompleteAsync();
        var run = Assert.Single(saved);
        RunStore.Validate(run);
        Assert.All(run.Samples, sample => Assert.Equal(0, sample.Segment));
        Assert.Equal(.3, run.Samples[299].RearRadiusMeters);
        Assert.Equal(.31, run.Samples[300].RearRadiusMeters);
        Assert.All(run.Samples.Select((sample, tick) => (sample, tick)), pair =>
        {
            Assert.Equal(State(pair.tick).Lap!.Position, pair.sample.State.Lap!.Position);
            Assert.Equal(pair.tick / 10d, pair.sample.ElapsedSeconds, 6);
        });
        var replay = LapReviewAnalysis.Build(run, cancellationToken: TestContext.Current.CancellationToken);
        var lap = Assert.Single(replay.Laps, candidate => candidate.IsComplete);
        Assert.InRange(lap.DurationSeconds!.Value, 59.95, 60.05);
        Assert.False(lap.Quality.HasFlag(LapReviewQuality.TelemetryGap));
        var panel = Assert.Single(RunPresentation.Charts(run, null, RunChartGroup.Speed,
            SpeedUnit.KilometersPerHour, TireTemperatureUnit.Celsius));
        Assert.Empty(panel.Series.Single(series => series.Name == "A · Ground").Gaps);
        var gap = Assert.Single(panel.Series.Single(series => series.Name == "A · Wheels").Gaps);
        Assert.Equal(29.9, gap.StartSeconds, 6);
        Assert.Equal(30, gap.EndSeconds, 6);
    }

    [Theory]
    [InlineData(LapTimingMode.GameLaps)]
    [InlineData(LapTimingMode.TimeAttack)]
    public async Task DuplicateGameTicksAndJitteredArrivalsReplayACompleteLap(LapTimingMode timing)
    {
        var saved = new List<RecordedRun>();
        var capture = Capture(saved);
        var tracker = new LapDeltaTracker();
        var originals = new Dictionary<(uint Tick, LapPosition Position), VehicleState>();
        for (var packet = timing == LapTimingMode.TimeAttack ? -1 : 0; packet <= 7502; packet++)
        {
            var state = HighRateState(packet, timing);
            originals[(state.GameTimestampMilliseconds, state.Lap!.Position)] = state;
            Feed(capture, tracker, state, timing);
        }
        await capture.CompleteAsync();
        var run = Assert.Single(saved);
        RunStore.Validate(run);
        var replay = LapReviewAnalysis.Build(run, cancellationToken: TestContext.Current.CancellationToken);
        var lap = Assert.Single(replay.Laps, candidate => candidate.IsComplete);
        Assert.InRange(lap.DurationSeconds!.Value, 59.95, 60.05);
        Assert.False(lap.Quality.HasFlag(LapReviewQuality.TelemetryGap));
        Assert.Contains(run.Samples.Zip(run.Samples.Skip(1)), pair => pair.First.ElapsedSeconds == pair.Second.ElapsedSeconds);
        var first = originals[(run.Samples[0].State.GameTimestampMilliseconds, run.Samples[0].State.Lap!.Position)];
        foreach (var sample in run.Samples)
        {
            var original = originals[(sample.State.GameTimestampMilliseconds, sample.State.Lap!.Position)];
            var arrival = (original.ReceivedTimestamp!.Value - first.ReceivedTimestamp!.Value) / (double)Stopwatch.Frequency;
            Assert.Equal(original with
            {
                ReceivedAtUtc = first.ReceivedAtUtc.AddSeconds(arrival),
                ReceivedTimestamp = null
            }, sample.State);
            Assert.Equal(unchecked(original.GameTimestampMilliseconds - first.GameTimestampMilliseconds) / 1000d, sample.ElapsedSeconds);
        }
        Assert.InRange((run.Samples[^1].State.ReceivedAtUtc - run.Samples[0].State.ReceivedAtUtc).TotalSeconds, 59.9, 60.1);
    }

    [Fact]
    public async Task JoiningMidLapWaitsForTheNextCompleteTraversal()
    {
        var saved = new List<RecordedRun>();
        var capture = Capture(saved);
        var tracker = new LapDeltaTracker();
        Drive(capture, tracker, 100, 600);
        Drive(capture, tracker, 601, 1200);
        await capture.CompleteAsync();
        var run = Assert.Single(saved);
        Assert.Equal(State(599).GameTimestampMilliseconds, run.Samples[0].State.GameTimestampMilliseconds);
        Assert.Equal(State(1200).GameTimestampMilliseconds, run.Samples[^1].State.GameTimestampMilliseconds);
    }

    [Fact]
    public async Task TelemetryGapSkipsTheLapWithoutFillingMissingSamples()
    {
        var saved = new List<RecordedRun>();
        var capture = Capture(saved);
        var tracker = new LapDeltaTracker();
        Drive(capture, tracker, 0, 200);
        Drive(capture, tracker, 206, 600);
        await capture.CompleteAsync();
        Assert.Empty(saved);
    }

    [Fact]
    public async Task DisablingDuringLapDoesNotSaveAPartialRun()
    {
        var saved = new List<RecordedRun>();
        var capture = Capture(saved);
        var tracker = new LapDeltaTracker();
        Drive(capture, tracker, 0, 200);
        capture.Configure(false);
        Drive(capture, tracker, 201, 600);
        await capture.CompleteAsync();
        Assert.Empty(saved);
    }

    [Fact]
    public async Task FreeRoamWithoutLapTimingNeverCreatesALapRun()
    {
        var saved = new List<RecordedRun>();
        var capture = Capture(saved);
        var tracker = new LapDeltaTracker();
        for (var tick = 0; tick <= 600; tick++)
        {
            var state = State(tick);
            state = state with { Lap = state.Lap! with { CurrentLapSeconds = 0, LastLapSeconds = 0, LapNumber = 0, RacePosition = 0 } };
            Feed(capture, tracker, state, LapTimingMode.GameLaps);
        }
        await capture.CompleteAsync();
        Assert.Empty(saved);
    }

    [Fact]
    public async Task TimeAttackSavesRawPacketsAndTheirTimingMode()
    {
        var saved = new List<RecordedRun>();
        var capture = Capture(saved);
        var tracker = new LapDeltaTracker();
        Drive(capture, tracker, -1, 602, LapTimingMode.TimeAttack);
        await capture.CompleteAsync();
        var run = Assert.Single(saved);
        Assert.Equal(LapTimingMode.TimeAttack, run.LapTimingMode);
        Assert.All(run.Samples, sample =>
        {
            Assert.Equal(0, sample.State.Lap!.CurrentLapSeconds);
            Assert.Equal(0, sample.State.Lap.LapNumber);
        });
        Assert.True(run.Samples.Length >= 600);
        RunStore.Validate(run);
    }

    [Fact]
    public async Task BoundedSaveQueueReportsBackpressureAndCompletionWaitsForWrites()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var capture = new LapRunCapture(async run =>
        {
            Interlocked.Increment(ref writes);
            started.TrySetResult();
            await release.Task;
        });
        capture.Configure(true);
        var tracker = new LapDeltaTracker();
        Drive(capture, tracker, 0, 600);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Drive(capture, tracker, 601, 2400);
        Assert.Contains("could not keep up", capture.Status);
        var completion = capture.CompleteAsync();
        Assert.False(completion.IsCompleted);
        release.SetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(3, writes);
        Assert.Contains("could not keep up", capture.Status);
    }

    [Fact]
    public async Task StorageFailureIsVisibleAndDoesNotInventASavedRun()
    {
        var capture = new LapRunCapture(_ => throw new RunLibraryFullException());
        var notified = false;
        capture.RunSaved += _ => notified = true;
        capture.Configure(true);
        Drive(capture, new(), 0, 600);
        await capture.CompleteAsync();
        Assert.False(notified);
        Assert.Contains("library is full", capture.Status);
    }

    [Fact]
    public async Task LapServiceSavesThroughSharedRunStoreWithoutMapOverlay()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WispLapRunTests", Guid.NewGuid().ToString("N"));
        var store = new RunStore(directory);
        var service = new LapDeltaService();
        service.AttachRunStore(store);
        service.Configure(true, LapDeltaReference.SessionBest, recordLaps: true);
        try
        {
            for (var tick = 0; tick <= 600; tick++)
            {
                var state = State(tick) with { ReceivedTimestamp = Stopwatch.GetTimestamp() };
                service.UpdateRunContext(new(state.ReceivedTimestamp.Value, state.CarOrdinal, state.Drivetrain, true, .3, .31));
                service.Observe(state);
                service.UpdateRunContext(new(state.ReceivedTimestamp.Value + Stopwatch.Frequency, state.CarOrdinal, state.Drivetrain, true, .5, .6));
                if (tick % 40 == 0 || tick == 600)
                    await WaitFor(() => service.Latest.ReceivedTimestamp == state.ReceivedTimestamp);
            }
            service.Dispose();
            await service.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var summary = Assert.Single(await store.ListAsync());
            var run = await store.LoadAsync(summary.Id);
            Assert.Equal(LapTimingMode.GameLaps, run.LapTimingMode);
            Assert.Equal(601, run.Samples.Length);
            Assert.All(run.Samples, sample =>
            {
                Assert.Equal(.3, sample.FrontRadiusMeters);
                Assert.Equal(.31, sample.RearRadiusMeters);
                Assert.NotNull(sample.WheelSpeedMetersPerSecond);
            });
            Assert.Null(service.LatestMap);
        }
        finally
        {
            service.Dispose();
            await service.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static LapRunCapture Capture(List<RecordedRun> saved)
    {
        var capture = new LapRunCapture(run => { saved.Add(run); return Task.CompletedTask; });
        capture.Configure(true);
        return capture;
    }

    private static VehicleState[] Drive(LapRunCapture capture, LapDeltaTracker tracker, int first, int last,
        LapTimingMode timing = LapTimingMode.GameLaps)
    {
        var states = new List<VehicleState>();
        for (var tick = first; tick <= last; tick++)
        {
            var state = State(tick, timing);
            states.Add(state);
            Feed(capture, tracker, state, timing);
        }
        return states.ToArray();
    }

    private static void Feed(LapRunCapture capture, LapDeltaTracker tracker, VehicleState state, LapTimingMode timing)
    {
        tracker.Update(state, LapDeltaReference.SessionBest, timing, capture: true);
        capture.Observe(state, timing, tracker.CaptureFrame, tracker.CompletedCapture);
    }

    private static VehicleState State(int tick, LapTimingMode timing = LapTimingMode.GameLaps)
    {
        var angle = tick / 600d * Math.Tau;
        var position = new Vector3((float)(150 * Math.Cos(angle)), 0, (float)(150 * Math.Sin(angle)));
        if (timing == LapTimingMode.TimeAttack)
        {
            var forward = Vector2.Normalize(new(-.16602f, .98612f));
            var right = new Vector2(forward.Y, -forward.X);
            var point = new Vector2(4267.63f, -5273.08f) + forward * (float)(150 * Math.Sin(angle)) +
                right * (float)(150 * (1 - Math.Cos(angle)));
            position = new(point.X, 0, point.Y);
        }
        return RunTestData.State((uint)((tick + 1000) * 100)) with
        {
            ReceivedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(tick * 100),
            ReceivedTimestamp = null,
            GroundSpeedMetersPerSecond = 16,
            Lap = timing == LapTimingMode.TimeAttack ? new(position, 0, 0, 0, 0, 0) :
                new(position, tick % 600 / 10f, 60, tick / 10f, (ushort)(tick / 600), 1)
        };
    }

    private static VehicleState HighRateState(int packet, LapTimingMode timing)
    {
        var seconds = packet * .008;
        var angle = seconds / 60 * Math.Tau;
        var position = new Vector3((float)(150 * Math.Cos(angle)), 0, (float)(150 * Math.Sin(angle)));
        if (timing == LapTimingMode.TimeAttack)
        {
            var forward = Vector2.Normalize(new(-.16602f, .98612f));
            var right = new Vector2(forward.Y, -forward.X);
            var point = new Vector2(4267.63f, -5273.08f) + forward * (float)(150 * Math.Sin(angle)) +
                right * (float)(150 * (1 - Math.Cos(angle)));
            position = new(point.X, 0, point.Y);
        }
        var number = (ushort)Math.Max(0, Math.Floor(seconds / 60));
        var receipt = seconds + (packet % 2 == 0 ? 0 : .0007);
        return State(0) with
        {
            GameTimestampMilliseconds = (uint)(100000 + Math.Floor(packet / 2d) * 16),
            ReceivedTimestamp = Stopwatch.Frequency + (long)Math.Round(receipt * Stopwatch.Frequency),
            // A wall-clock correction must not change the saved arrival intervals.
            ReceivedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds + (packet > 3750 ? 3600 : 0)),
            Lap = timing == LapTimingMode.TimeAttack ? new(position, 0, 0, 0, 0, 0) :
                new(position, (float)(seconds - number * 60), 60, (float)seconds, number, 1)
        };
    }

    private static async Task WaitFor(Func<bool> ready)
    {
        var timeout = Stopwatch.StartNew();
        while (!ready() && timeout.ElapsedMilliseconds < 3000) await Task.Delay(1, TestContext.Current.CancellationToken);
        Assert.True(ready());
    }
}
