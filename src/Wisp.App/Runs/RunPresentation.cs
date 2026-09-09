using System.Globalization;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public enum RunChartGroup { Speed, Inputs, Engine, Tires, Handling }
public readonly record struct RunPlotPoint(double Seconds, double Value, bool BreakBefore);
public sealed record RunPlotSeries(string Name, bool Comparison, int ColorIndex, RunPlotPoint[] Points)
{
    internal Func<double, double?>? ReadRecordedValue { get; init; }
    internal RunInterval[] Gaps { get; init; } = [];
}
public sealed record RunPlotPanel(string Title, string Unit, RunPlotSeries[] Series, double Minimum, double Maximum);
public sealed record RunMetric(string Label, string Value, string? Comparison = null);

internal static class RunPresentation
{
    internal const int MaximumSeriesPoints = 1200;
    internal static string Time(double seconds) => seconds >= 60
        ? $"{(int)(seconds / 60)}:{seconds % 60:00.0}" : $"{seconds:0.0}s";
    internal static double SpeedFactor(SpeedUnit unit) => unit == SpeedUnit.MilesPerHour ? 2.2369362921 : 3.6;
    internal static string SpeedLabel(SpeedUnit unit) => unit == SpeedUnit.MilesPerHour ? "mph" : "km/h";
    internal static string Number(double? value, string suffix = "", string format = "0.0") =>
        value is double finite && double.IsFinite(finite) ? finite.ToString(format, CultureInfo.CurrentCulture) + suffix : "Unavailable";

    internal static RunMetric[] Metrics(RunReport a, RunReport? b, SpeedUnit speed, TorqueUnit torque)
    {
        var factor = SpeedFactor(speed);
        string Speed(double? value) => Number(value * factor, " " + SpeedLabel(speed));
        string Power(double? value) => Number(value / 745.699872, " hp", "0");
        string Torque(double? value) => Number(value * (torque == TorqueUnit.NewtonMeters ? 1 : .7375621493),
            torque == TorqueUnit.NewtonMeters ? " Nm" : " lb-ft", "0");
        string? Difference(double? first, double? second, Func<double?, string> format, double scale, string unit, string digits = "0.0")
        {
            if (b is null) return null;
            var formatted = format(second);
            if (first is not double left || second is not double right || !double.IsFinite(left) || !double.IsFinite(right)) return formatted;
            return formatted + " (" + ((right - left) * scale).ToString("+" + digits + ";-" + digits + ";" + digits, CultureInfo.CurrentCulture) + unit + ")";
        }
        string Duration(double? value) => value is double seconds ? Time(seconds) : "Unavailable";
        return
        [
            new("Recorded time", Time(a.Statistics.RecordedSeconds), Difference(a.Statistics.RecordedSeconds, b?.Statistics.RecordedSeconds, Duration, 1, "s")),
            new("Peak ground speed", Speed(a.Statistics.PeakSpeedMetersPerSecond), Difference(a.Statistics.PeakSpeedMetersPerSecond, b?.Statistics.PeakSpeedMetersPerSecond, Speed, factor, " " + SpeedLabel(speed))),
            new("Average car speed", Speed(a.Statistics.AverageSpeedMetersPerSecond), Difference(a.Statistics.AverageSpeedMetersPerSecond, b?.Statistics.AverageSpeedMetersPerSecond, Speed, factor, " " + SpeedLabel(speed))),
            new("Full throttle", Time(a.Statistics.FullThrottleSeconds), Difference(a.Statistics.FullThrottleSeconds, b?.Statistics.FullThrottleSeconds, Duration, 1, "s")),
            new("Braking", Time(a.Statistics.BrakingSeconds), Difference(a.Statistics.BrakingSeconds, b?.Statistics.BrakingSeconds, Duration, 1, "s")),
            new("Peak power", Power(a.Statistics.PeakPowerWatts), Difference(a.Statistics.PeakPowerWatts, b?.Statistics.PeakPowerWatts, Power, 1 / 745.699872, " hp", "0")),
            new("Peak torque", Torque(a.Statistics.PeakTorqueNm), Difference(a.Statistics.PeakTorqueNm, b?.Statistics.PeakTorqueNm, Torque, torque == TorqueUnit.NewtonMeters ? 1 : .7375621493, torque == TorqueUnit.NewtonMeters ? " Nm" : " lb-ft", "0")),
            new("Peak lateral load", Number(a.Statistics.PeakLateralG, " g"), Difference(a.Statistics.PeakLateralG, b?.Statistics.PeakLateralG, value => Number(value, " g"), 1, " g")),
            new("Telemetry gaps", a.Statistics.GapCount.ToString(CultureInfo.CurrentCulture), b?.Statistics.GapCount.ToString(CultureInfo.CurrentCulture))
        ];
    }

    internal static RunPlotPanel[] Charts(RecordedRun a, RecordedRun? b, RunChartGroup group,
        SpeedUnit speedUnit, TireTemperatureUnit temperatureUnit, double offsetA = 0, double offsetB = 0,
        RunInterval? boundsA = null, RunInterval? boundsB = null, TorqueUnit torqueUnit = TorqueUnit.NewtonMeters,
        BoostPressureUnit boostUnit = BoostPressureUnit.Psi)
    {
        double speed = SpeedFactor(speedUnit);
        double? Temperature(RunSample sample, bool front)
        {
            var value = sample.State.TireTemperatureFahrenheit;
            if (front ? !float.IsFinite(value.FrontLeft) || !float.IsFinite(value.FrontRight) || value.FrontLeft <= 0 || value.FrontRight <= 0
                : !float.IsFinite(value.RearLeft) || !float.IsFinite(value.RearRight) || value.RearLeft <= 0 || value.RearRight <= 0) return null;
            var fahrenheit = front ? (value.FrontLeft + value.FrontRight) / 2d : (value.RearLeft + value.RearRight) / 2d;
            return temperatureUnit == TireTemperatureUnit.Celsius ? (fahrenheit - 32) * 5 / 9 : fahrenheit;
        }
        return group switch
        {
            RunChartGroup.Inputs =>
            [
                Panel("Driver inputs", "%", -100, 100,
                    ("Throttle", 0, sample => sample.State.Accelerator / 255d * 100),
                    ("Brake", 1, sample => sample.State.Brake / 255d * 100),
                    ("Steering", 2, sample => sample.State.Steering / (sample.State.Steering < 0 ? 128d : 127d) * 100)),
                Panel("Ground and driven-wheel speed", SpeedLabel(speedUnit), 0, null,
                    ("Ground", 0, sample => sample.State.GroundSpeedMetersPerSecond * speed),
                    ("Wheels", 1, sample => sample.WheelSpeedMetersPerSecond * speed))
            ],
            RunChartGroup.Engine =>
            [
                Panel("Engine speed", "RPM", 0, null, ("Engine", 0, sample => sample.State.EngineRpm)),
                Panel("Engine power", "hp", null, null, ("Power", 0, sample => sample.State.PowerWatts / 745.699872)),
                Panel("Engine torque", torqueUnit == TorqueUnit.NewtonMeters ? "Nm" : "lb-ft", null, null,
                    ("Torque", 0, sample => sample.State.TorqueNm * (torqueUnit == TorqueUnit.NewtonMeters ? 1 : .7375621493))),
                Panel("Boost pressure", boostUnit == BoostPressureUnit.Psi ? "PSI" : "bar", null, null,
                    ("Boost", 1, sample => sample.State.IsElectric ? null : sample.State.BoostPressurePsi * (boostUnit == BoostPressureUnit.Psi ? 1 : .0689475729)))
            ],
            RunChartGroup.Tires =>
            [
                Panel("Tire temperature", temperatureUnit == TireTemperatureUnit.Celsius ? "°C" : "°F", null, null,
                    ("Front", 0, sample => Temperature(sample, true)), ("Rear", 1, sample => Temperature(sample, false)))
            ],
            RunChartGroup.Handling =>
            [
                Panel("Cornering and acceleration load", "g", null, null,
                    ("Lateral", 0, sample => sample.State.LateralAccelerationMetersPerSecondSquared / 9.80665),
                    ("Longitudinal", 1, sample => sample.State.LongitudinalAccelerationMetersPerSecondSquared / 9.80665))
            ],
            _ => [Panel("Ground and driven-wheel speed", SpeedLabel(speedUnit), 0, null,
                ("Ground", 0, sample => sample.State.GroundSpeedMetersPerSecond * speed),
                ("Wheels", 1, sample => sample.WheelSpeedMetersPerSecond * speed))]
        };

        RunPlotPanel Panel(string title, string unit, double? minimum, double? maximum,
            params (string Label, int Color, Func<RunSample, double?> Read)[] channels)
        {
            var series = new List<RunPlotSeries>();
            foreach (var (label, color, read) in channels)
            {
                series.Add(Series(a, "A · " + label, false, offsetA, boundsA));
                if (b is not null) series.Add(Series(b, "B · " + label, true, offsetB, boundsB));
                RunPlotSeries Series(RecordedRun run, string name, bool comparison, double offset, RunInterval? bounds)
                {
                    var boostedCars = label == "Boost" ? run.Samples.Where(sample => Driving(sample) && !sample.State.IsElectric &&
                        float.IsFinite(sample.State.BoostPressurePsi) && sample.State.BoostPressurePsi > 0).Select(sample => sample.State.CarOrdinal).ToHashSet() : null;
                    double? Read(RunSample sample) => boostedCars is not null && !boostedCars.Contains(sample.State.CarOrdinal) ? null : read(sample);
                    return new(name, comparison, color, PreparePoints(run.Samples, Read, offset, bounds))
                    {
                        ReadRecordedValue = seconds => RecordedValueAt(run.Samples, Read, seconds + offset, bounds),
                        Gaps = PrepareGaps(run.Samples, Read, offset, bounds)
                    };
                }
            }
            var values = series.SelectMany(item => item.Points).Select(point => point.Value).ToArray();
            double low = minimum ?? (values.Length == 0 ? 0 : values.Min());
            double high = maximum ?? (values.Length == 0 ? 1 : values.Max());
            if (high - low < .01) high = low + Math.Max(1, Math.Abs(low) * .1);
            if (maximum is null) high += (high - low) * .06;
            return new(title, unit, series.ToArray(), low, high);
        }
    }

    // Keep extrema and endpoints in chronological order, never bridge missing data.
    internal static RunPlotPoint[] PreparePoints(RunSample[] samples, Func<RunSample, double?> read, double offset, RunInterval? bounds = null)
    {
        if (samples.Length == 0) return [];
        var points = new List<RunPlotPoint>(Math.Min(samples.Length, MaximumSeriesPoints));
        var bucketSize = Math.Max(1, (int)Math.Ceiling(samples.Length / (MaximumSeriesPoints / 4d)));
        int continuity = 0, lastContinuity = -1;
        for (int start = 0; start < samples.Length; start += bucketSize)
        {
            var candidates = new List<(int Index, double Value, int Continuity)>(bucketSize);
            for (int index = start; index < Math.Min(samples.Length, start + bucketSize); index++)
            {
                if (index == 0 || !ChartContinuous(samples[index - 1], samples[index])) continuity++;
                var sample = samples[index];
                var value = read(sample);
                if (!Driving(sample) || value is not double finite || !double.IsFinite(finite) ||
                    (bounds is { } range && (sample.ElapsedSeconds < range.StartSeconds || sample.ElapsedSeconds > range.EndSeconds)))
                { continuity++; continue; }
                candidates.Add((index, finite, continuity));
            }
            if (candidates.Count == 0) continue;
            var selected = new[] { candidates[0], candidates.MinBy(point => point.Value), candidates.MaxBy(point => point.Value), candidates[^1] }
                .DistinctBy(point => point.Index).OrderBy(point => point.Index);
            foreach (var point in selected)
            {
                points.Add(new(samples[point.Index].ElapsedSeconds - offset, point.Value, point.Continuity != lastContinuity));
                lastContinuity = point.Continuity;
            }
        }
        return points.ToArray();
    }

    internal static bool ChartContinuous(RunSample previous, RunSample current) => RunAnalysis.AreContinuous(previous, current) ||
        (Driving(previous) && Driving(current) && current.ElapsedSeconds == previous.ElapsedSeconds &&
         current.State.GameTimestampMilliseconds == previous.State.GameTimestampMilliseconds && current.Segment == previous.Segment &&
         current.State.CarOrdinal == previous.State.CarOrdinal && current.State.Drivetrain == previous.State.Drivetrain);

    private static bool Driving(RunSample sample) => sample.IsDriving && sample.State.IsRaceOn &&
        double.IsFinite(sample.ElapsedSeconds) && sample.ElapsedSeconds >= 0 &&
        float.IsFinite(sample.State.GroundSpeedMetersPerSecond) && sample.State.GroundSpeedMetersPerSecond >= 0;

    // Cursor values come from the nearest original reading, never from reduced drawing vertices.
    internal static double? RecordedValueAt(RunSample[] samples, Func<RunSample, double?> read, double seconds, RunInterval? bounds = null)
    {
        if (samples.Length == 0 || seconds < samples[0].ElapsedSeconds || seconds > samples[^1].ElapsedSeconds ||
            (bounds is { } range && (seconds < range.StartSeconds || seconds > range.EndSeconds))) return null;
        int low = 0, high = samples.Length - 1;
        while (low < high)
        { int middle = (low + high + 1) / 2; if (samples[middle].ElapsedSeconds <= seconds) low = middle; else high = middle - 1; }
        var nearest = samples[low];
        if (low < samples.Length - 1 && seconds > nearest.ElapsedSeconds)
        {
            var next = samples[low + 1];
            if (!ChartContinuous(nearest, next) || read(nearest) is not double left || !double.IsFinite(left) ||
                read(next) is not double right || !double.IsFinite(right)) return null;
            if (next.ElapsedSeconds - seconds < seconds - nearest.ElapsedSeconds) nearest = next;
        }
        var value = read(nearest);
        if (bounds is { } selected && (nearest.ElapsedSeconds < selected.StartSeconds || nearest.ElapsedSeconds > selected.EndSeconds))
        {
            var alternative = ReferenceEquals(nearest, samples[low]) && low + 1 < samples.Length ? samples[low + 1] : samples[low];
            if (alternative.ElapsedSeconds < selected.StartSeconds || alternative.ElapsedSeconds > selected.EndSeconds) return null;
            nearest = alternative; value = read(nearest);
        }
        return Driving(nearest) && value is double finite && double.IsFinite(finite) ? finite : null;
    }

    internal static RunInterval[] PrepareGaps(RunSample[] samples, Func<RunSample, double?> read, double offset, RunInterval? bounds = null)
    {
        var gaps = new List<RunInterval>();
        RunSample? previous = null; bool interrupted = false;
        foreach (var sample in samples)
        {
            if (!Driving(sample) || read(sample) is not double value || !double.IsFinite(value)) { interrupted = true; continue; }
            if (previous is not null && (interrupted || !ChartContinuous(previous, sample)) && sample.ElapsedSeconds > previous.ElapsedSeconds)
            {
                var from = Math.Max(previous.ElapsedSeconds, bounds?.StartSeconds ?? 0);
                var to = Math.Min(sample.ElapsedSeconds, bounds?.EndSeconds ?? double.MaxValue);
                if (to > from) gaps.Add(new(from - offset, to - offset));
            }
            previous = sample; interrupted = false;
        }
        // Lines retain every continuity break. Bound the extra shading geometry for pathological files.
        return gaps.Count <= MaximumSeriesPoints ? gaps.ToArray() : gaps.OrderByDescending(gap => gap.EndSeconds - gap.StartSeconds).Take(MaximumSeriesPoints).ToArray();
    }
}
