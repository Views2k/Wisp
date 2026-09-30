using System.Numerics;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class LapReviewTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static RunSample Sample(double seconds, LapPosition position, float? lapSeconds = null,
        ushort number = 0, float lastLap = 0, int segment = 0, float speed = 20) => new()
        {
            ElapsedSeconds = seconds,
            Segment = segment,
            IsDriving = true,
            State = TestVehicleState.Create(groundSpeed: speed) with
            {
                GameTimestampMilliseconds = (uint)Math.Round((seconds + 100) * 1000),
                ReceivedAtUtc = Epoch.AddSeconds(seconds),
                ReceivedTimestamp = long.MaxValue,
                Lap = new(position, lapSeconds ?? (float)seconds, lastLap, (float)seconds, number, 1),
                NumCylinders = 8,
                Accelerator = 0,
                Brake = 0,
                TireTemperatureFahrenheit = new(100, 110, 120, 130)
            }
        };

    private static LapPosition Circle(double phase, float radius = 100) =>
        new((float)(radius * Math.Cos(phase)), 5, (float)(radius * Math.Sin(phase)));

    private static RecordedRun Circuit(double duration = 20, int count = 2, int startSample = 0) => new()
    {
        Name = "Recorded test lap",
        Tune = "Baseline",
        Samples = Enumerable.Range(startSample, (int)Math.Round(duration * count * 10) + 2 - startSample)
            .Select(index =>
            {
                var time = index / 10d;
                var number = (ushort)Math.Floor((time + .00001) / duration);
                var clock = time - number * duration;
                return Sample(time, Circle(time / duration * Math.Tau), (float)clock, number,
                    number == 0 ? 0 : (float)duration, speed: (float)(Math.Tau * 100 / duration));
            }).ToArray()
    };

    private static LapReviewLap FirstLap(double duration = 20) =>
        LapReviewAnalysis.Build(Circuit(duration), cancellationToken: TestContext.Current.CancellationToken).Laps.First(lap => lap.IsComplete);

    [Fact]
    public void CompletedLapsKeepMetadataAndEveryRecordedPositionWithoutMirroring()
    {
        var run = Circuit();
        var result = LapReviewAnalysis.Build(run, cancellationToken: TestContext.Current.CancellationToken);
        var complete = result.Laps.Where(lap => lap.IsComplete).ToArray();
        Assert.Equal(2, complete.Length);
        var lap = complete[0];
        Assert.Equal(run.Id, lap.RunId);
        Assert.Equal(run.Name, lap.RunName);
        Assert.Equal(run.Tune, lap.Tune);
        Assert.Equal(100, lap.CarOrdinal);
        Assert.Equal(20, lap.DurationSeconds);
        Assert.Equal(0, lap.StartSeconds);
        Assert.Equal(20, lap.EndSeconds, 5);
        Assert.Equal(200, lap.Points.Length);
        for (var index = 0; index < lap.Points.Length; index++)
        {
            Assert.Equal(index, lap.Points[index].SampleIndex);
            Assert.Equal(run.Samples[index].State.Lap!.Position, lap.Points[index].Position);
        }
        var bounds = LapMapBounds.From(new(lap.Points.Select(point => point.Position).ToArray(), true));
        Assert.True(bounds.Project(lap.Points[50].Position).Y < bounds.Project(lap.Points[150].Position).Y);
        Assert.True(bounds.Project(lap.Points[0].Position).X > bounds.Project(lap.Points[100].Position).X);
        Assert.InRange(lap.RecordedDistanceMeters, 620, 630);
    }

    [Fact]
    public void RecordingStartedMidLapDoesNotPretendItsFirstPathWasComplete()
    {
        var result = LapReviewAnalysis.Build(Circuit(startSample: 50), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Laps[0].IsComplete);
        Assert.True(result.Laps[0].Quality.HasFlag(LapReviewQuality.PartialStart));
        Assert.Null(result.Laps[0].DurationSeconds);
        Assert.Single(result.Laps, lap => lap.IsComplete);
        Assert.False(result.Laps[^1].IsComplete);
        Assert.True(result.Laps[^1].Quality.HasFlag(LapReviewQuality.PartialEnd));
    }

    [Fact]
    public void MissingPositionHasAnHonestEmptyState()
    {
        var sample = Sample(0, new(0, 0, 0));
        var result = LapReviewAnalysis.Build(new() { Samples = [sample with { State = sample.State with { Lap = null } }] }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(result.Laps);
        Assert.Contains("no usable position", result.Message);
    }

    [Fact]
    public void MissingSampleAcrossTheLineCannotMergeTwoLapsIntoOne()
    {
        var run = Circuit();
        var samples = run.Samples.Select((sample, index) => index == 200
            ? sample with { State = sample.State with { Lap = null } } : sample).ToArray();
        var result = LapReviewAnalysis.Build(run with { Samples = samples }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(result.Laps, lap => lap.IsComplete);
        Assert.True(result.Laps[0].Quality.HasFlag(LapReviewQuality.MissingPosition));
        Assert.True(result.Laps[0].Quality.HasFlag(LapReviewQuality.Discontinuity));
        Assert.True(result.Laps[1].Quality.HasFlag(LapReviewQuality.PartialStart));
    }

    [Fact]
    public void GapRemainsVisibleAndDoesNotBecomeDistanceOrCoveredTime()
    {
        var original = Circuit();
        var samples = original.Samples.Where((_, index) => index is < 80 or > 85)
            .Select(sample => sample.ElapsedSeconds > 8 ? sample with { Segment = 1 } : sample).ToArray();
        var lap = LapReviewAnalysis.Build(original with { Samples = samples }, cancellationToken: TestContext.Current.CancellationToken).Laps[0];
        Assert.True(lap.IsComplete);
        Assert.True(lap.Quality.HasFlag(LapReviewQuality.TelemetryGap));
        var point = lap.Points.First(point => point.RunSeconds == 8.6);
        Assert.True(point.BreakBefore);
        var pointIndex = Array.IndexOf(lap.Points, point);
        Assert.Equal(lap.Points[pointIndex - 1].DistanceMeters, point.DistanceMeters);
        var stats = LapReviewAnalysis.AnalyzeSection(lap, 0, lap.Points.Length - 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, stats.GapCount);
        Assert.InRange(stats.DurationSeconds - stats.RecordedSeconds, .69, .71);
        Assert.False(LapReviewAnalysis.Compare(lap, FirstLap(), cancellationToken: TestContext.Current.CancellationToken).CanCompare);
    }

    [Fact]
    public void RewindSplitsThePathAndCannotBecomeACompletedLap()
    {
        var run = Circuit(count: 1);
        var samples = run.Samples.Select(sample => sample.ElapsedSeconds >= 10
            ? sample with { State = sample.State with { Lap = sample.State.Lap! with { CurrentLapSeconds = (float)(sample.ElapsedSeconds - 5), LapNumber = 0 } } }
            : sample).ToArray();
        var laps = LapReviewAnalysis.Build(run with { Samples = samples }, cancellationToken: TestContext.Current.CancellationToken).Laps;
        Assert.Equal(2, laps.Length);
        Assert.All(laps, lap => Assert.False(lap.IsComplete));
        Assert.All(laps, lap => Assert.True(lap.Quality.HasFlag(LapReviewQuality.Rewind)));
    }

    [Fact]
    public void TeleportIsNotDrawnAsAConnectingTrackSegment()
    {
        var samples = new[] { Sample(0, new(0, 0, 0)), Sample(.1, new(1, 0, 0)),
            Sample(.2, new(1000, 0, 0)), Sample(.3, new(1001, 0, 0)) };
        var laps = LapReviewAnalysis.Build(new() { Samples = samples }, cancellationToken: TestContext.Current.CancellationToken).Laps;
        Assert.Equal(2, laps.Length);
        Assert.All(laps, lap => Assert.Equal(1, lap.RecordedDistanceMeters));
        Assert.All(laps, lap => Assert.True(lap.Quality.HasFlag(LapReviewQuality.Discontinuity)));
    }

    [Fact]
    public void TimeAttackReplaysRecordedArrivalTimeAndIgnoresPersistedStopwatchTicks()
    {
        var center = new Vector2(4267.63f, -5273.08f);
        var forward = Vector2.Normalize(new(-.16602f, .98612f));
        var right = new Vector2(forward.Y, -forward.X);
        var samples = Enumerable.Range(0, 604).Select(index =>
        {
            var routeSeconds = index / 10d - .1;
            var angle = routeSeconds / 60 * Math.Tau;
            var position = center + forward * (float)(150 * Math.Sin(angle)) + right * (float)(150 * (1 - Math.Cos(angle)));
            var sample = Sample(index / 10d, new(position.X, 0, position.Y), 0, speed: (float)(Math.Tau * 150 / 60));
            return sample with { State = sample.State with { Lap = sample.State.Lap! with { RacePosition = 0, RaceSeconds = 0 } } };
        }).ToArray();
        var result = LapReviewAnalysis.Build(new() { Samples = samples }, LapTimingMode.TimeAttack, cancellationToken: TestContext.Current.CancellationToken);
        var lap = Assert.Single(result.Laps, candidate => candidate.IsComplete);
        Assert.InRange(lap.DurationSeconds!.Value, 59.97, 60.03);
        Assert.True(lap.Quality.HasFlag(LapReviewQuality.InferredTiming));
        Assert.All(lap.Points, point => Assert.Equal(0, point.Sample.State.Lap!.CurrentLapSeconds));
    }

    [Fact]
    public void TimeAttackLateFinishCorrectionKeepsTheAcceptedLapAndBoundsItsRecordedBoundary()
    {
        var center = new Vector2(4267.63f, -5273.08f);
        var forward = Vector2.Normalize(new(-.16602f, .98612f));
        var right = new Vector2(forward.Y, -forward.X);
        var samples = Enumerable.Range(0, 604).Select(index =>
        {
            var seconds = index / 10d;
            var angle = (seconds - .001) / 60 * Math.Tau;
            var position = center + forward * (float)(150 * Math.Sin(angle)) + right * (float)(150 * (1 - Math.Cos(angle)));
            var sample = Sample(seconds, new(position.X, 0, position.Y), 0, speed: (float)(Math.Tau * 150 / 60));
            return sample with
            {
                State = sample.State with
                {
                    ReceivedAtUtc = Epoch.AddSeconds(seconds + (index == 600 ? .015 : 0)),
                    Lap = sample.State.Lap! with { RacePosition = 0, RaceSeconds = 0 }
                }
            };
        }).ToArray();
        var clock = new TimeAttackClock();
        var timing = samples.Select(sample => clock.Update(sample.State with { ReceivedTimestamp = null }))
            .OfType<LapTelemetry>().ToArray();
        var last = timing.Last(lap => lap.LapNumber == 0);
        var next = timing.First(lap => lap.LapNumber == 1);
        Assert.InRange(last.CurrentLapSeconds - next.LastLapSeconds, .005f, .05f);
        var result = LapReviewAnalysis.Build(new() { Samples = samples }, LapTimingMode.TimeAttack,
            TestContext.Current.CancellationToken);
        var complete = Assert.Single(result.Laps, lap => lap.IsComplete);
        Assert.Equal(next.LastLapSeconds, complete.DurationSeconds);
        Assert.Equal(complete.Points[^1].RunSeconds, complete.EndSeconds);
        Assert.Equal(complete.EndSeconds, result.Laps[^1].StartSeconds);
    }

    [Fact]
    public void GameLapClocksDoNotUseTheTimeAttackLateFinishAllowance()
    {
        var run = Circuit(count: 1);
        var samples = run.Samples.ToArray();
        samples[199] = samples[199] with
        {
            State = samples[199].State with
            { Lap = samples[199].State.Lap! with { CurrentLapSeconds = 20.02f } }
        };
        samples[200] = samples[200] with
        {
            State = samples[200].State with
            { Lap = samples[200].State.Lap! with { CurrentLapSeconds = .04f } }
        };
        var result = LapReviewAnalysis.Build(run with { Samples = samples }, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(result.Laps, lap => lap.IsComplete);
        Assert.True(result.Laps[0].Quality.HasFlag(LapReviewQuality.Discontinuity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedLapClockKeepsRecordedTravelWithoutInventingTime(bool duplicatePacketTick)
    {
        var samples = new[] { Sample(0, new(0, 0, 0)), Sample(.1, new(1, 0, 0)),
            Sample(duplicatePacketTick ? .1 : .108, new(3, 0, 0), .1f), Sample(.2, new(4, 0, 0)) };
        var lap = Assert.Single(LapReviewAnalysis.Build(new() { Samples = samples }, TestContext.Current.CancellationToken).Laps);
        var whole = LapReviewAnalysis.AnalyzeSection(lap, 0, 3, TestContext.Current.CancellationToken);
        Assert.Equal(4, whole.DistanceMeters);
        Assert.Equal(lap.RecordedDistanceMeters, whole.DistanceMeters);
        Assert.Equal(.2, whole.RecordedSeconds, 6);
        Assert.Equal(0, whole.GapCount);
        var repeated = LapReviewAnalysis.AnalyzeSection(lap, 1, 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, repeated.DistanceMeters);
        Assert.Equal(0, repeated.RecordedSeconds);
        Assert.Equal(0, repeated.DurationSeconds);
        Assert.Null(repeated.AverageSpeedMetersPerSecond);
        Assert.Null(repeated.BrakingFraction);
        Assert.Empty(repeated.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LapClockDiscontinuityMarksContinuousPacketsAsInterrupted(bool backwardCorrection)
    {
        var run = Circuit(count: 1);
        var samples = run.Samples.Select((sample, index) =>
        {
            var lap = sample.State.Lap!;
            if (backwardCorrection && index == 100)
                lap = lap with { CurrentLapSeconds = 9.875f };
            else if (!backwardCorrection && index >= 100)
                lap = lap.LapNumber == 0 ? lap with { CurrentLapSeconds = lap.CurrentLapSeconds + .4f }
                    : lap with { LastLapSeconds = 20.4f };
            return sample with { State = sample.State with { Lap = lap } };
        }).ToArray();
        var result = LapReviewAnalysis.Build(run with { Samples = samples }, TestContext.Current.CancellationToken);
        var lap = Assert.Single(result.Laps, candidate => candidate.IsComplete);
        Assert.True(lap.Quality.HasFlag(LapReviewQuality.TelemetryGap));
        Assert.True(lap.Points[100].BreakBefore);
        Assert.Equal(lap.Points[99].DistanceMeters, lap.Points[100].DistanceMeters);
        var section = LapReviewAnalysis.AnalyzeSection(lap, 0, lap.Points.Length - 1, TestContext.Current.CancellationToken);
        Assert.Equal(1, section.GapCount);
        Assert.False(LapReviewAnalysis.Compare(lap, FirstLap(), TestContext.Current.CancellationToken).CanCompare);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedLapClockRetainsObservedInputAndGearTransitions(bool duplicatePacketTick)
    {
        var samples = new[] { Sample(0, new(0, 0, 0)), Sample(.1, new(1, 0, 0)),
            Sample(duplicatePacketTick ? .1 : .108, new(3, 0, 0), .1f), Sample(.2, new(4, 0, 0)) };
        for (var index = 2; index < samples.Length; index++)
            samples[index] = samples[index] with
            {
                State = samples[index].State with
                { Brake = 26, Accelerator = 26, Gear = TransmissionGear.Fifth, EngineRpm = 5000 }
            };
        var lap = Assert.Single(LapReviewAnalysis.Build(new() { Samples = samples }, TestContext.Current.CancellationToken).Laps);
        var whole = LapReviewAnalysis.AnalyzeSection(lap, 0, 3, TestContext.Current.CancellationToken);
        Assert.Equal(.2, whole.RecordedSeconds, 6);
        Assert.Equal(.1, whole.BrakingSeconds, 6);
        Assert.Equal(.1, whole.ThrottleSeconds, 6);
        var repeated = LapReviewAnalysis.AnalyzeSection(lap, 1, 2, TestContext.Current.CancellationToken);
        Assert.Equal(0, repeated.RecordedSeconds);
        Assert.Equal(0, repeated.BrakingSeconds);
        Assert.Null(repeated.AverageSpeedMetersPerSecond);
        Assert.Equal(3, repeated.Events.Length);
        var brake = Assert.Single(repeated.Events, item => item.Kind == LapReviewEventKind.BrakeStart);
        var pickup = Assert.Single(repeated.Events, item => item.Kind == LapReviewEventKind.ThrottlePickup);
        Assert.Equal(2, brake.PointIndex);
        Assert.Equal(2, brake.DistanceMeters);
        Assert.Equal(brake.DistanceMeters, pickup.DistanceMeters);
        var shift = Assert.Single(repeated.Events, item => item.Kind == LapReviewEventKind.Upshift);
        Assert.Equal(2, shift.PointIndex);
        Assert.Equal(3, shift.DistanceMeters);
        Assert.Equal(samples[2].ElapsedSeconds, shift.RunSeconds);
        Assert.Equal(5000, shift.Value);
        Assert.Equal(TransmissionGear.Fourth, shift.FromGear);
        Assert.Equal(TransmissionGear.Fifth, shift.ToGear);
    }

    [Fact]
    public void SectionMetricsAreTimeWeightedAndKeepAllFourWheelsSeparate()
    {
        var samples = new[] { Sample(0, new(0, 0, 0), speed: 0), Sample(.1, new(1, 0, 0), speed: 10), Sample(.3, new(3, 0, 0), speed: 10) };
        samples[0] = samples[0] with { State = samples[0].State with { TireTemperatureFahrenheit = new(100, 110, 120, 130) } };
        samples[1] = samples[1] with { State = samples[1].State with { TireTemperatureFahrenheit = new(110, 120, 130, 140) } };
        samples[2] = samples[2] with { State = samples[2].State with { TireTemperatureFahrenheit = new(120, 130, 140, 150) } };
        var lap = Assert.Single(LapReviewAnalysis.Build(new() { Samples = samples }, cancellationToken: TestContext.Current.CancellationToken).Laps);
        var stats = LapReviewAnalysis.AnalyzeSection(lap, 0, 2, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(.3, stats.RecordedSeconds, 6);
        Assert.Equal(2.5 / .3, stats.AverageSpeedMetersPerSecond!.Value, 5);
        Assert.Equal(0, stats.EntrySpeedMetersPerSecond);
        Assert.Equal(0, stats.MinimumSpeedMetersPerSecond);
        Assert.Equal(10, stats.ExitSpeedMetersPerSecond);
        Assert.Equal(3, stats.DistanceMeters);
        Assert.Equal(120, stats.TireTemperatureFahrenheit.FrontLeft.End);
        Assert.Equal(150, stats.TireTemperatureFahrenheit.RearRight.End);
        Assert.Equal(111.666666, stats.TireTemperatureFahrenheit.FrontLeft.Mean!.Value, 5);
        Assert.Equal(141.666666, stats.TireTemperatureFahrenheit.RearRight.Mean!.Value, 5);
    }

    [Fact]
    public void InputsUseDefinedThresholdsAndTransitionsKeepDistanceAndSampleIdentity()
    {
        var samples = Enumerable.Range(0, 4).Select(index => Sample(index * .1, new(index * 2, 0, 0))).ToArray();
        samples[1] = samples[1] with { State = samples[1].State with { Brake = 26 } };
        samples[2] = samples[2] with { State = samples[2].State with { Accelerator = 26, Gear = TransmissionGear.Fifth } };
        samples[3] = samples[3] with { State = samples[3].State with { Accelerator = 255, Gear = TransmissionGear.Fifth } };
        var lap = Assert.Single(LapReviewAnalysis.Build(new() { Samples = samples }, cancellationToken: TestContext.Current.CancellationToken).Laps);
        var stats = LapReviewAnalysis.AnalyzeSection(lap, 0, 3, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(.1, stats.BrakingSeconds, 6);
        Assert.Equal(.15, stats.ThrottleSeconds, 6);
        Assert.Equal(.05, stats.CoastingSeconds, 6);
        var start = Assert.Single(stats.Events, item => item.Kind == LapReviewEventKind.BrakeStart);
        Assert.Equal(1, start.PointIndex);
        Assert.Equal(1, start.DistanceMeters, 6);
        Assert.Equal(.05, start.RunSeconds, 6);
        var end = Assert.Single(stats.Events, item => item.Kind == LapReviewEventKind.BrakeEnd);
        Assert.Equal(3, end.DistanceMeters, 6);
        var pickup = Assert.Single(stats.Events, item => item.Kind == LapReviewEventKind.ThrottlePickup);
        Assert.Equal(3, pickup.DistanceMeters, 6);
        var shift = Assert.Single(stats.Events, item => item.Kind == LapReviewEventKind.Upshift);
        Assert.Equal(TransmissionGear.Fourth, shift.FromGear);
        Assert.Equal(TransmissionGear.Fifth, shift.ToGear);
        Assert.Equal(2, shift.PointIndex);
    }

    [Fact]
    public void EventsDoNotInventAnInputTransitionAcrossAGap()
    {
        var a = Sample(0, new(0, 0, 0));
        var b = Sample(1, new(2, 0, 0), segment: 1);
        b = b with { State = b.State with { Brake = 255, Accelerator = 255, Gear = TransmissionGear.Fifth } };
        var lap = Assert.Single(LapReviewAnalysis.Build(new() { Samples = [a, b] }, cancellationToken: TestContext.Current.CancellationToken).Laps);
        var stats = LapReviewAnalysis.AnalyzeSection(lap, 0, 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, stats.RecordedSeconds);
        Assert.Null(stats.BrakingFraction);
        Assert.Null(stats.AverageSpeedMetersPerSecond);
        Assert.Empty(stats.Events);
    }

    [Fact]
    public void ComparisonAlignsWorldPositionInsteadOfTimeOrSampleNumber()
    {
        var current = FirstLap(22);
        var reference = FirstLap(20);
        var comparison = LapReviewAnalysis.Compare(current, reference, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(comparison.CanCompare, comparison.Message);
        Assert.InRange(comparison.Coverage, .98, 1);
        var halfway = comparison.Points[110];
        Assert.InRange(halfway.ReferenceLapSeconds!.Value, 9.99, 10.01);
        Assert.InRange(halfway.DeltaSeconds!.Value, .99, 1.01);
        Assert.NotEqual(110, halfway.ReferencePointIndex);
    }

    [Fact]
    public void ReverseDirectionAndAnotherRouteCannotManufactureADelta()
    {
        var original = FirstLap();
        var mirrored = original with { Points = original.Points.Select(point => point with { Position = point.Position with { Z = -point.Position.Z } }).ToArray() };
        Assert.False(LapReviewAnalysis.Compare(original, mirrored, cancellationToken: TestContext.Current.CancellationToken).CanCompare);
        var displaced = original with { Points = original.Points.Select(point => point with { Position = point.Position with { X = point.Position.X + 1000 } }).ToArray() };
        Assert.False(LapReviewAnalysis.Compare(original, displaced, cancellationToken: TestContext.Current.CancellationToken).CanCompare);
        Assert.False(LapReviewAnalysis.Compare(original, original with { CarOrdinal = 999 }, cancellationToken: TestContext.Current.CancellationToken).CanCompare);
    }

    [Fact]
    public void FigureEightCrossingsStayOnTheOrderedPassThroughTheTrack()
    {
        static RecordedRun FigureEight(double duration) => new()
        {
            Samples = Enumerable.Range(0, (int)(duration * 10) + 2).Select(index =>
            {
                var time = index / 10d;
                var phase = time / duration * Math.Tau;
                var number = (ushort)(time >= duration ? 1 : 0);
                return Sample(time, new((float)(100 * Math.Sin(phase)), 0, (float)(100 * Math.Sin(phase) * Math.Cos(phase))),
                    (float)(time - number * duration), number, (float)duration, speed: 30);
            }).ToArray()
        };
        var a = LapReviewAnalysis.Build(FigureEight(22), cancellationToken: TestContext.Current.CancellationToken).Laps.First(lap => lap.IsComplete);
        var b = LapReviewAnalysis.Build(FigureEight(20), cancellationToken: TestContext.Current.CancellationToken).Laps.First(lap => lap.IsComplete);
        var comparison = LapReviewAnalysis.Compare(a, b, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(comparison.CanCompare, comparison.Message);
        Assert.InRange(comparison.Points[110].ReferenceLapSeconds!.Value, 9.99, 10.01);
        Assert.InRange(comparison.Points[110].DeltaSeconds!.Value, .99, 1.01);
    }

    [Fact]
    public void SelectedSectionDoesNotCountAnEarlierGapOutsideItsBounds()
    {
        var samples = new[] { Sample(0, new(0, 0, 0)), Sample(1, new(1, 0, 0), segment: 1),
            Sample(1.1, new(2, 0, 0), segment: 1), Sample(1.2, new(3, 0, 0), segment: 1) };
        var lap = Assert.Single(LapReviewAnalysis.Build(new() { Samples = samples }, cancellationToken: TestContext.Current.CancellationToken).Laps);
        var stats = LapReviewAnalysis.AnalyzeSection(lap, 1, 3, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, stats.GapCount);
        Assert.False(stats.Quality.HasFlag(LapReviewQuality.TelemetryGap));
        Assert.Equal(.2, stats.RecordedSeconds, 5);
    }

    [Fact]
    public void OversizeInputIsRejectedRatherThanQuietlyDownsampled()
    {
        var sample = Sample(0, new(0, 0, 0));
        var result = LapReviewAnalysis.Build(new() { Samples = Enumerable.Repeat(sample, LapReviewAnalysis.MaximumPoints + 1).ToArray() }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(result.Laps);
        Assert.Contains("not truncated", result.Message);
    }

    [Fact]
    public void AnalysisHonorsCancellationAndRejectsInvalidSectionBounds()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => LapReviewAnalysis.Build(Circuit(), cancellationToken: cancellation.Token));
        var lap = FirstLap();
        Assert.Throws<OperationCanceledException>(() => LapReviewAnalysis.AnalyzeSection(lap, 0, 1, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => LapReviewAnalysis.Compare(lap, lap, cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => LapReviewAnalysis.AnalyzeSection(lap, 2, 1, cancellationToken: TestContext.Current.CancellationToken));
    }
}
