using System.Globalization;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunStatisticsPresentationTests : IDisposable
{
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;

    public RunStatisticsPresentationTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    public void Dispose() => CultureInfo.CurrentCulture = _previousCulture;

    [Fact]
    public void OverviewIsBoundedAndEveryDetailedStatisticHasOneStableKeyAndGroup()
    {
        var rows = Present(new RunStatistics());
        Assert.Equal(6, rows.Count(row => row.IsKey));
        Assert.Equal(rows.Length, rows.Select(row => row.Key).Distinct().Count());
        Assert.All(rows, row =>
        {
            Assert.Contains(row.Group, RunStatisticsPresentation.Groups);
            Assert.False(string.IsNullOrWhiteSpace(row.Description));
            Assert.Null(row.ValueB);
            Assert.Null(row.Difference);
        });
    }

    [Fact]
    public void DifferencesAreBMinusAAndMissingDataIsNotReplacedWithZero()
    {
        var first = new RunStatistics { PeakSpeedMetersPerSecond = 20, PeakTorqueNm = 150, PeakBoostPsi = null };
        var second = new RunStatistics { PeakSpeedMetersPerSecond = 25, PeakTorqueNm = 125, PeakBoostPsi = 4 };
        var rows = Present(first, second);
        Assert.Equal("72.0 km/h", Find(rows, "peak-speed").ValueA);
        Assert.Equal("90.0 km/h", Find(rows, "peak-speed").ValueB);
        Assert.Equal("+18.0 km/h", Find(rows, "peak-speed").Difference);
        Assert.Equal("-25 Nm", Find(rows, "peak-torque").Difference);
        Assert.Equal("Unavailable", Find(rows, "peak-boost").ValueA);
        Assert.Equal("Unavailable", Find(rows, "peak-boost").Difference);
        Assert.Equal("4.00 PSI", Find(rows, "peak-boost").ValueB);
    }

    [Fact]
    public void TemperatureChangesConvertDeltasWithoutApplyingTheAbsoluteOffset()
    {
        var first = new RunStatistics
        {
            StartingFrontTemperatureFahrenheit = 68,
            EndingFrontTemperatureFahrenheit = 86,
            StartingRearTemperatureFahrenheit = 104,
            EndingRearTemperatureFahrenheit = 86
        };
        var second = first with { EndingFrontTemperatureFahrenheit = 104, EndingRearTemperatureFahrenheit = 104 };
        var rows = Present(first, second, temperature: TireTemperatureUnit.Celsius);
        Assert.Equal("20.0 °C", Find(rows, "front-start").ValueA);
        Assert.Equal("30.0 °C", Find(rows, "front-end").ValueA);
        Assert.Equal("+10.0 °C", Find(rows, "front-change").ValueA);
        Assert.Equal("+20.0 °C", Find(rows, "front-change").ValueB);
        Assert.Equal("+10.0 °C", Find(rows, "front-change").Difference);
        Assert.Equal("-10.0 °C", Find(rows, "rear-change").ValueA);
        Assert.Equal("0.0 °C", Find(rows, "rear-change").ValueB);
    }

    [Fact]
    public void MissingTemperatureEndpointPreventsAnInventedTemperatureChange()
    {
        var rows = Present(new RunStatistics { EndingFrontTemperatureFahrenheit = 190 },
            new RunStatistics { StartingFrontTemperatureFahrenheit = 180, EndingFrontTemperatureFahrenheit = 190 });
        Assert.Equal("Unavailable", Find(rows, "front-change").ValueA);
        Assert.Equal("+10.0 °F", Find(rows, "front-change").ValueB);
        Assert.Equal("Unavailable", Find(rows, "front-change").Difference);
    }

    [Fact]
    public void DistanceAndEngineReadingsUseTheSelectedUnits()
    {
        var data = new RunStatistics
        {
            RecordedSeconds = 100,
            DistanceMeters = 1609.344,
            PeakPowerWatts = 74569.9872,
            PeakTorqueNm = 100,
            PeakBoostPsi = 14.5037738
        };
        var rows = RunStatisticsPresentation.Create(data, null, SpeedUnit.MilesPerHour,
            TorqueUnit.PoundFeet, TireTemperatureUnit.Fahrenheit, BoostPressureUnit.Bar);
        Assert.Equal("1.000 mi", Find(rows, "distance").ValueA);
        Assert.Equal("100 hp", Find(rows, "peak-power").ValueA);
        Assert.Equal("74 lb-ft", Find(rows, "peak-torque").ValueA);
        Assert.Equal("1.00 bar", Find(rows, "peak-boost").ValueA);
    }

    [Fact]
    public void InputSharesUseRecordedTimeAndCompareInPercentagePoints()
    {
        var first = new RunStatistics { DurationSeconds = 30, RecordedSeconds = 20, FullThrottleSeconds = 5, BrakingSeconds = 2 };
        var second = new RunStatistics { DurationSeconds = 30, RecordedSeconds = 20, FullThrottleSeconds = 10, BrakingSeconds = 4 };
        var rows = Present(first, second);
        Assert.Equal("25.0 %", Find(rows, "full-throttle-share").ValueA);
        Assert.Equal("50.0 %", Find(rows, "full-throttle-share").ValueB);
        Assert.Equal("+25.0 percentage points", Find(rows, "full-throttle-share").Difference);
        Assert.Equal("10.0 s", Find(rows, "uncovered-time").ValueA);
    }

    [Fact]
    public void AnIsolatedSampleCanHaveAPeakButCannotInventIntegratedDistanceOrInputTime()
    {
        var rows = Present(new RunStatistics { SampleCount = 1, RecordedSeconds = 0, PeakSpeedMetersPerSecond = 10 });
        Assert.Equal("36.0 km/h", Find(rows, "peak-speed").ValueA);
        Assert.Equal("Unavailable", Find(rows, "distance").ValueA);
        Assert.Equal("Unavailable", Find(rows, "full-throttle").ValueA);
        Assert.Equal("Unavailable", Find(rows, "braking-share").ValueA);
    }

    [Fact]
    public void NonFiniteTelemetryIsUnavailableAndSmallDifferencesDoNotShowNegativeZero()
    {
        var first = new RunStatistics { PeakPowerWatts = double.NaN, PeakLongitudinalG = double.PositiveInfinity, PeakLateralG = 1 };
        var second = new RunStatistics { PeakPowerWatts = 100, PeakLateralG = .99999 };
        var rows = Present(first, second);
        Assert.Equal("Unavailable", Find(rows, "peak-power").ValueA);
        Assert.Equal("Unavailable", Find(rows, "peak-power").Difference);
        Assert.Equal("Unavailable", Find(rows, "peak-longitudinal").ValueA);
        Assert.Equal("0.00 g", Find(rows, "peak-lateral").Difference);
        Assert.DoesNotContain(rows, row => row.ValueA.Contains("NaN", StringComparison.Ordinal) || row.ValueA.Contains("Infinity", StringComparison.Ordinal));
    }

    private static RunStatistic[] Present(RunStatistics a, RunStatistics? b = null,
        TireTemperatureUnit temperature = TireTemperatureUnit.Fahrenheit) =>
        RunStatisticsPresentation.Create(a, b, SpeedUnit.KilometersPerHour, TorqueUnit.NewtonMeters, temperature, BoostPressureUnit.Psi);

    private static RunStatistic Find(IEnumerable<RunStatistic> rows, string key) => Assert.Single(rows, row => row.Key == key);
}
