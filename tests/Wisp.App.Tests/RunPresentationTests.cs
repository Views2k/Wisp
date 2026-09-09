using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunPresentationTests
{
    [Fact]
    public void LongRunsKeepExtremaWithinTheGeometryBudget()
    {
        var samples = Enumerable.Range(0, 180_000).Select(i => Sample(i / 100d, i == 72001 ? 9000 : 1000)).ToArray();
        var points = RunPresentation.PreparePoints(samples, s => s.State.EngineRpm, 0);
        Assert.InRange(points.Length, 2, RunPresentation.MaximumSeriesPoints);
        Assert.Equal(0, points[0].Seconds);
        Assert.Equal(samples[^1].ElapsedSeconds, points[^1].Seconds);
        Assert.Contains(points, point => point.Value == 9000);
    }

    [Fact]
    public void NumerousInterruptionsCannotGrowTheGeometryBudgetOrJoinSegments()
    {
        var samples = Enumerable.Range(0, 12_000).Select(i => Sample(i / 100d, 1000) with { Segment = i }).ToArray();
        var points = RunPresentation.PreparePoints(samples, s => s.State.EngineRpm, 0);
        Assert.InRange(points.Length, 1, RunPresentation.MaximumSeriesPoints);
        Assert.All(points, point => Assert.True(point.BreakBefore));
    }

    [Fact]
    public void AShortMissingSectionInsideOneBucketStillBreaksTheLine()
    {
        var samples = Enumerable.Range(0, 12_000).Select(i => Sample(i / 100d, 1000)).ToArray();
        samples[19] = samples[19] with { IsDriving = false };
        var points = RunPresentation.PreparePoints(samples, s => s.State.EngineRpm, 0);
        Assert.Contains(points, point => point.Seconds > .19 && point.Seconds < .4 && point.BreakBefore);
    }

    [Fact]
    public void DuplicatedGameTimeKeepsTheLatestReadingWithoutPaintingAGap()
    {
        RunSample[] samples = [Sample(0, 1000), Sample(.01, 2000), Sample(.01, 2400), Sample(.02, 3000)];
        var points = RunPresentation.PreparePoints(samples, s => s.State.EngineRpm, 0);
        Assert.Single(points, point => point.BreakBefore);
        Assert.Equal(2400, RunPresentation.RecordedValueAt(samples, s => s.State.EngineRpm, .01));
    }

    [Theory]
    [InlineData(.003, 1000)]
    [InlineData(.008, 2000)]
    public void CursorReturnsARecordedValueRatherThanAnInterpolatedValue(double seconds, double expected)
    {
        RunSample[] samples = [Sample(0, 1000), Sample(.01, 2000), Sample(.02, 3000)];
        Assert.Equal(expected, RunPresentation.RecordedValueAt(samples, s => s.State.EngineRpm, seconds));
    }

    [Fact]
    public void CursorDoesNotInventDataInsideAnInterruption()
    {
        RunSample[] samples = [Sample(0, 1000), Sample(1, 2000)];
        Assert.Null(RunPresentation.RecordedValueAt(samples, s => s.State.EngineRpm, .5));
        Assert.Null(RunPresentation.RecordedValueAt(samples, s => s.State.EngineRpm, -1));
        Assert.Null(RunPresentation.RecordedValueAt(samples, s => s.State.EngineRpm, 2));
    }

    [Fact]
    public void NativeMenuContextIsExcludedFromBothGeometryAndReadouts()
    {
        RunSample[] samples = [Sample(0, 1000), Sample(.01, 8000) with { IsDriving = false }, Sample(.02, 1200)];
        var points = RunPresentation.PreparePoints(samples, s => s.State.EngineRpm, 0);
        Assert.DoesNotContain(points, point => point.Value == 8000);
        Assert.True(points[^1].BreakBefore);
        Assert.Null(RunPresentation.RecordedValueAt(samples, s => s.State.EngineRpm, .01));
    }

    [Fact]
    public void CursorNeverReadsAValueOutsideTheMatchedInterval()
    {
        RunSample[] samples = [Sample(.04, 1000), Sample(.05, 2000), Sample(.06, 3000)];
        Assert.Equal(2000, RunPresentation.RecordedValueAt(samples, s => s.State.EngineRpm, .044, new(.044, .055)));
        Assert.Equal(2000, RunPresentation.RecordedValueAt(samples, s => s.State.EngineRpm, .055, new(.044, .055)));
    }

    [Fact]
    public void GapShadingUsesOriginalEdgesRatherThanReducedVertexTimes()
    {
        var samples = Enumerable.Range(0, 12_000).Select(i => Sample(i / 100d, 1000)).ToArray();
        samples[19] = samples[19] with { IsDriving = false };
        var gap = Assert.Single(RunPresentation.PrepareGaps(samples, s => s.State.EngineRpm, 0));
        Assert.Equal(.18, gap.StartSeconds, 6);
        Assert.Equal(.20, gap.EndSeconds, 6);
    }

    [Fact]
    public void UnknownTemperatureAndNaturallyAspiratedVacuumAreUnavailable()
    {
        var sample = Sample(0, 1000);
        var run = new RecordedRun { Samples = [sample with { State = sample.State with { TireTemperatureFahrenheit = default, BoostPressurePsi = -12, NumCylinders = 8 } }] };
        var tires = RunPresentation.Charts(run, null, RunChartGroup.Tires, SpeedUnit.MilesPerHour, TireTemperatureUnit.Celsius);
        Assert.All(tires[0].Series, series => Assert.Empty(series.Points));
        var boost = Assert.Single(RunPresentation.Charts(run, null, RunChartGroup.Engine, SpeedUnit.MilesPerHour, TireTemperatureUnit.Fahrenheit), panel => panel.Unit == "PSI");
        Assert.All(boost.Series, series => Assert.Empty(series.Points));
    }

    [Fact]
    public void SteeringEndpointsFitThePercentageScale()
    {
        var sample = Sample(0, 1000);
        var run = new RecordedRun
        {
            Samples = [sample with { State = sample.State with { Steering = -128 } },
            Sample(.01, 1000) with { State = sample.State with { Steering = 127, GameTimestampMilliseconds = 10 } }]
        };
        var panel = Assert.Single(RunPresentation.Charts(run, null, RunChartGroup.Inputs, SpeedUnit.MilesPerHour, TireTemperatureUnit.Fahrenheit), chart => chart.Unit == "%");
        var steering = Assert.Single(panel.Series, series => series.ColorIndex == 2);
        Assert.Equal(-100, steering.Points[0].Value);
        Assert.Equal(100, steering.Points[1].Value);
    }

    [Fact]
    public void MatchedChartsEndAtEachRunsOwnTargetCrossing()
    {
        var a = new RecordedRun { Samples = Enumerable.Range(0, 1001).Select(i => Sample(i / 100d, 1000)).ToArray() };
        var b = a with { Id = Guid.NewGuid() };
        var chart = Assert.Single(RunPresentation.Charts(a, b, RunChartGroup.Engine, SpeedUnit.MilesPerHour,
            TireTemperatureUnit.Fahrenheit, 2, 4, new(2, 5), new(4, 6)), p => p.Unit == "RPM");
        var lineA = Assert.Single(chart.Series, series => !series.Comparison);
        var lineB = Assert.Single(chart.Series, series => series.Comparison);
        Assert.All(lineA.Points, point => Assert.InRange(point.Seconds, 0, 3));
        Assert.All(lineB.Points, point => Assert.InRange(point.Seconds, 0, 2));
        Assert.Null(lineB.ReadRecordedValue!(2.1));
        Assert.Equal(1000, lineA.ReadRecordedValue!(2.1));
    }

    [Fact]
    public void SpeedAndTemperatureChartsLabelAndConvertTheSelectedUnits()
    {
        var run = new RecordedRun { Samples = [Sample(0, 1000)] };
        var speed = Assert.Single(RunPresentation.Charts(run, null, RunChartGroup.Speed,
            SpeedUnit.KilometersPerHour, TireTemperatureUnit.Celsius));
        Assert.Equal("km/h", speed.Unit);
        Assert.Equal(36, speed.Series[0].Points[0].Value, 3);
        var temperature = Assert.Single(RunPresentation.Charts(run, null, RunChartGroup.Tires,
            SpeedUnit.KilometersPerHour, TireTemperatureUnit.Celsius), panel => panel.Unit == "°C");
        Assert.Equal("°C", temperature.Unit);
        Assert.Equal(100, temperature.Series[0].Points[0].Value, 3);
    }

    internal static RunSample Sample(double seconds, float rpm) => new()
    {
        ElapsedSeconds = seconds,
        Segment = 0,
        IsDriving = true,
        State = new VehicleState
        {
            IsRaceOn = true,
            GameTimestampMilliseconds = (uint)Math.Round(seconds * 1000),
            ReceivedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(seconds),
            CarOrdinal = 1,
            NumCylinders = 8,
            Drivetrain = DrivetrainType.RearWheelDrive,
            GroundSpeedMetersPerSecond = 10,
            WheelRotationRadiansPerSecond = default,
            TireSlipRatio = default,
            TireSlipAngle = default,
            NormalizedSuspensionTravel = default,
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = 0,
            EngineRpm = rpm,
            EngineMaximumRpm = 9000,
            Gear = TransmissionGear.Third,
            Steering = 0,
            Accelerator = 255,
            Brake = 0,
            TireTemperatureFahrenheit = new(212, 212, 212, 212)
        }
    };
}
