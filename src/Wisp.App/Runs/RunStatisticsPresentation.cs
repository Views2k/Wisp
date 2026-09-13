using System.Globalization;
using System.ComponentModel;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed class RunStatistic(string key, string label, string group, string valueA,
    string? valueB, string? difference, string description, bool isKey = false) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Group { get; } = group;
    public string ValueA { get; private set; } = valueA;
    public string? ValueB { get; private set; } = valueB;
    public string? Difference { get; private set; } = difference;
    public string Description { get; } = description;
    public bool IsKey { get; } = isKey;

    internal void UpdateValues(RunStatistic next)
    {
        if (ValueA != next.ValueA) { ValueA = next.ValueA; PropertyChanged?.Invoke(this, new(nameof(ValueA))); }
        if (ValueB != next.ValueB) { ValueB = next.ValueB; PropertyChanged?.Invoke(this, new(nameof(ValueB))); }
        if (Difference != next.Difference) { Difference = next.Difference; PropertyChanged?.Invoke(this, new(nameof(Difference))); }
    }
}

internal static class RunStatisticsPresentation
{
    internal const string Overview = "Overview";
    internal const string AllStatistics = "All statistics";
    internal static readonly string[] Groups =
    [
        Overview, "Speed & distance", "Driver inputs", "Engine", "G-force",
        "Tire temperatures", "Recording quality", AllStatistics
    ];

    internal static RunStatistic[] Create(RunStatistics a, RunStatistics? b, SpeedUnit speedUnit,
        TorqueUnit torqueUnit, TireTemperatureUnit temperatureUnit, BoostPressureUnit boostUnit)
    {
        var speedFactor = RunPresentation.SpeedFactor(speedUnit);
        var speedLabel = RunPresentation.SpeedLabel(speedUnit);
        var distanceFactor = speedUnit == SpeedUnit.MilesPerHour ? 1 / 1609.344 : 0.001;
        var distanceLabel = speedUnit == SpeedUnit.MilesPerHour ? "mi" : "km";
        var torqueFactor = torqueUnit == TorqueUnit.NewtonMeters ? 1 : .7375621493;
        var torqueLabel = torqueUnit == TorqueUnit.NewtonMeters ? "Nm" : "lb-ft";
        var temperatureFactor = temperatureUnit == TireTemperatureUnit.Celsius ? 5d / 9 : 1;
        var temperatureLabel = temperatureUnit == TireTemperatureUnit.Celsius ? "°C" : "°F";
        var temperatureOffset = temperatureUnit == TireTemperatureUnit.Celsius ? -32 * temperatureFactor : 0;
        var boostFactor = boostUnit == BoostPressureUnit.Psi ? 1 : .0689475729;
        var boostLabel = boostUnit == BoostPressureUnit.Psi ? "PSI" : "bar";
        return
        [
            Row("recorded-time", "Recorded time", "Recording quality", item => item.RecordedSeconds, "s",
                "Time covered by continuous driving telemetry. Pauses and gaps are excluded.", isKey: true),
            Row("peak-speed", "Peak ground speed", "Speed & distance", item => item.PeakSpeedMetersPerSecond, speedLabel,
                "Highest recorded vehicle speed over the ground.", speedFactor, isKey: true),
            Row("distance", "Distance covered", "Speed & distance", item => WithCoverage(item, item.DistanceMeters), distanceLabel,
                "Distance estimated from ground speed during continuous telemetry. Gaps are excluded.", distanceFactor, 3, isKey: true),
            Row("full-throttle", "Full throttle", "Driver inputs", item => WithCoverage(item, item.FullThrottleSeconds), "s",
                "Time at approximately 98% throttle or more.", isKey: true),
            Row("peak-power", "Peak power", "Engine", item => item.PeakPowerWatts, "hp",
                "Highest power reported by the game during this section.", 1 / 745.699872, 0, isKey: true),
            Row("wheel-speed-excess", "Average wheel-speed excess", "Speed & distance", item => item.AverageWheelSpeedExcessMetersPerSecond, speedLabel,
                "Average positive driven-wheel speed above ground speed, using calibrated samples only. This is not a slip percentage.", speedFactor, isKey: true),
            Row("average-speed", "Average ground speed", "Speed & distance", item => item.AverageSpeedMetersPerSecond, speedLabel,
                "Distance divided by recorded driving time. Pauses and telemetry gaps are excluded.", speedFactor),
            Row("full-throttle-share", "Full-throttle share", "Driver inputs", item => Share(item, item.FullThrottleSeconds), "%",
                "Full-throttle time as a share of recorded driving time.", differenceUnit: "percentage points"),
            Row("braking-time", "Braking", "Driver inputs", item => WithCoverage(item, item.BrakingSeconds), "s",
                "Time with brake input at approximately 5% or more."),
            Row("braking-share", "Braking share", "Driver inputs", item => Share(item, item.BrakingSeconds), "%",
                "Braking time as a share of recorded driving time.", differenceUnit: "percentage points"),
            Row("peak-torque", "Peak torque", "Engine", item => item.PeakTorqueNm, torqueLabel,
                "Highest torque reported by the game during this section.", torqueFactor, 0),
            Row("peak-boost", "Peak boost", "Engine", item => item.PeakBoostPsi, boostLabel,
                "Available when positive boost was observed for the car. Electric and unconfirmed non-boosted cars have no boost value.", boostFactor, 2),
            Row("peak-lateral", "Peak lateral load", "G-force", item => item.PeakLateralG, "g",
                "Largest sideways load in either direction. Shown as a magnitude.", digits: 2),
            Row("peak-longitudinal", "Peak longitudinal load", "G-force", item => item.PeakLongitudinalG, "g",
                "Largest acceleration or braking load. Shown as a magnitude.", digits: 2),
            Row("front-start", "Front tire temperature · start", "Tire temperatures", item => item.StartingFrontTemperatureFahrenheit, temperatureLabel,
                "Front-axle average at the start of the selected section.", temperatureFactor, offset: temperatureOffset),
            Row("front-end", "Front tire temperature · end", "Tire temperatures", item => item.EndingFrontTemperatureFahrenheit, temperatureLabel,
                "Front-axle average at the end of the selected section.", temperatureFactor, offset: temperatureOffset),
            Row("front-change", "Front tire temperature change", "Tire temperatures",
                item => TemperatureChange(item.StartingFrontTemperatureFahrenheit, item.EndingFrontTemperatureFahrenheit), temperatureLabel,
                "End minus start. A positive value means the front tires warmed during this section.", temperatureFactor, signed: true),
            Row("rear-start", "Rear tire temperature · start", "Tire temperatures", item => item.StartingRearTemperatureFahrenheit, temperatureLabel,
                "Rear-axle average at the start of the selected section.", temperatureFactor, offset: temperatureOffset),
            Row("rear-end", "Rear tire temperature · end", "Tire temperatures", item => item.EndingRearTemperatureFahrenheit, temperatureLabel,
                "Rear-axle average at the end of the selected section.", temperatureFactor, offset: temperatureOffset),
            Row("rear-change", "Rear tire temperature change", "Tire temperatures",
                item => TemperatureChange(item.StartingRearTemperatureFahrenheit, item.EndingRearTemperatureFahrenheit), temperatureLabel,
                "End minus start. A negative value means the rear tires cooled during this section.", temperatureFactor, signed: true),
            Row("elapsed-time", "Selected elapsed time", "Recording quality", item => item.DurationSeconds, "s",
                "Elapsed time from the beginning to the end of the selected section, including gaps."),
            Row("uncovered-time", "Time without continuous data", "Recording quality",
                item => Finite(item.DurationSeconds) is { } elapsed && Finite(item.RecordedSeconds) is { } recorded ? Math.Max(0, elapsed - recorded) : null, "s",
                "Selected elapsed time not covered by continuous driving telemetry. This can include menus, pauses, or missing samples."),
            Row("gaps", "Telemetry gaps", "Recording quality", item => item.GapCount >= 0 ? item.GapCount : null, "",
                "Breaks in continuous telemetry within the selected section.", digits: 0),
            Row("samples", "Recorded samples", "Recording quality", item => item.SampleCount >= 0 ? item.SampleCount : null, "",
                "Valid recorded points inside the section. An interval between samples can still contain interpolated driving time.", digits: 0)
        ];

        RunStatistic Row(string key, string label, string group, Func<RunStatistics, double?> read, string unit,
            string description, double factor = 1, int digits = 1, double offset = 0,
            bool signed = false, bool isKey = false, string? differenceUnit = null)
        {
            var first = Finite(read(a));
            var second = b is null ? null : Finite(read(b));
            var difference = first is { } left && second is { } right ? Finite((right - left) * factor) : null;
            return new(key, label, group,
                Format(first * factor + offset, unit, digits, signed),
                b is null ? null : Format(second * factor + offset, unit, digits, signed),
                b is null ? null : Format(difference, differenceUnit ?? unit, digits, true), description, isKey);
        }
    }

    private static double? WithCoverage(RunStatistics statistics, double value) =>
        Finite(statistics.RecordedSeconds) is > 0 ? Finite(value) : null;

    private static double? Share(RunStatistics statistics, double seconds) =>
        WithCoverage(statistics, seconds) is { } value ? Math.Clamp(value / statistics.RecordedSeconds * 100, 0, 100) : null;

    private static double? TemperatureChange(double? start, double? end) =>
        Finite(start) is { } first && Finite(end) is { } last ? Finite(last - first) : null;

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;

    private static string Format(double? value, string unit, int digits, bool signed)
    {
        if (Finite(value) is not { } number) return "Unavailable";
        if (Math.Round(number, digits) == 0) number = 0;
        var format = digits == 0 ? "0" : "0." + new string('0', digits);
        if (signed) format = "+" + format + ";-" + format + ";" + format;
        return number.ToString(format, CultureInfo.CurrentCulture) + (unit.Length == 0 ? "" : " " + unit);
    }
}
