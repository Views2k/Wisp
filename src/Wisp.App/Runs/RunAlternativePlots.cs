using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public enum RunPlotMode { TimeSeries, PowerByRpm, GForce, TireChange }
public enum RunAlternativePlotKind { Scatter, Bars }
public readonly record struct RunAlternativePoint(double X, double Y, double SourceSeconds, int SampleIndex, int Segment, TransmissionGear Gear);
public sealed record RunAlternativeSeries(string Name, bool Comparison, RunAlternativePoint[] Points, int SourcePointCount);
public sealed record RunAlternativeBar(int Category, bool Comparison, double Value, double SourceSeconds);
public readonly record struct RunAlternativeSelection(bool Comparison, double SourceSeconds, int SampleIndex);
public sealed record RunAlternativePlotPanel(string Title, string Description, RunAlternativePlotKind Kind,
    string XLabel, string XUnit, string YLabel, string YUnit,
    double XMinimum, double XMaximum, double YMinimum, double YMaximum,
    RunAlternativeSeries[] Series, RunAlternativeBar[] Bars, string[] Categories, bool EqualAxes = false);

public static class RunAlternativePlots
{
    public const int MaximumSeriesPoints = 1200;
    private const double Gravity = 9.80665;

    public static RunAlternativePlotPanel[] Build(RecordedRun a, RecordedRun? b, RunPlotMode mode,
        TireTemperatureUnit temperatureUnit, TorqueUnit torqueUnit, RunInterval? intervalA = null, RunInterval? intervalB = null,
        bool fullThrottleOnly = true, TransmissionGear? gear = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        Validate(intervalA); Validate(intervalB);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == RunPlotMode.TimeSeries) return [];
        if (mode == RunPlotMode.PowerByRpm && gear is { } selected && (selected < TransmissionGear.First || selected > TransmissionGear.Tenth))
            throw new ArgumentOutOfRangeException(nameof(gear), "Select a forward gear.");
        if (mode == RunPlotMode.TireChange) return [TirePanel(a, b, temperatureUnit, intervalA, intervalB)];
        if (mode == RunPlotMode.GForce)
            return [Scatter("Cornering and acceleration", "+Y acceleration · −Y braking. Each dot is one recorded driving sample.",
                "Lateral", "g", "Longitudinal", "g", sample => sample.State.LateralAccelerationMetersPerSecondSquared / Gravity,
                sample => sample.State.LongitudinalAccelerationMetersPerSecondSquared / Gravity, false, true)];
        var filters = (fullThrottleOnly ? "Full throttle" : "All throttle inputs") + (gear is { } g ? $" · gear {(int)g}" : " · all forward gears");
        return
        [
            Scatter("Power by RPM", filters + ". Each dot is a recorded reading.", "Engine speed", "RPM", "Power", "hp",
                sample => sample.State.EngineRpm, sample => sample.State.PowerWatts / 745.699872, true, false),
            Scatter("Torque by RPM", filters + ". Each dot is a recorded reading.", "Engine speed", "RPM", "Torque",
                torqueUnit == TorqueUnit.NewtonMeters ? "Nm" : "lb-ft", sample => sample.State.EngineRpm,
                sample => sample.State.TorqueNm * (torqueUnit == TorqueUnit.NewtonMeters ? 1 : .7375621493), true, false)
        ];

        RunAlternativePlotPanel Scatter(string title, string description, string xLabel, string xUnit, string yLabel, string yUnit,
            Func<RunSample, double> x, Func<RunSample, double> y, bool power, bool equalAxes)
        {
            var series = new List<RunAlternativeSeries> { Series(a, false, intervalA) };
            if (b is not null) series.Add(Series(b, true, intervalB));
            var all = series.SelectMany(value => value.Points).ToArray();
            double xMin, xMax, yMin, yMax;
            if (equalAxes)
            {
                var limit = Math.Max(1, Math.Ceiling(all.Select(point => Math.Max(Math.Abs(point.X), Math.Abs(point.Y))).DefaultIfEmpty(0).Max() * 1.05 * 4) / 4);
                xMin = yMin = -limit; xMax = yMax = limit;
            }
            else
            {
                (xMin, xMax) = Bounds(all.Select(point => point.X), true);
                (yMin, yMax) = Bounds(all.Select(point => point.Y), true);
            }
            return new(title, description, RunAlternativePlotKind.Scatter, xLabel, xUnit, yLabel, yUnit,
                xMin, xMax, yMin, yMax, series.ToArray(), [], [], equalAxes);

            RunAlternativeSeries Series(RecordedRun run, bool comparison, RunInterval? interval)
            {
                var points = new List<RunAlternativePoint>();
                for (var index = 0; index < run.Samples.Length; index++)
                {
                    var sample = run.Samples[index];
                    if (!Driving(sample) || interval is { } range && (sample.ElapsedSeconds < range.StartSeconds || sample.ElapsedSeconds > range.EndSeconds)) continue;
                    if (power && (sample.State.Gear < TransmissionGear.First || sample.State.Gear > TransmissionGear.Tenth ||
                        sample.State.EngineRpm <= 0 || fullThrottleOnly && sample.State.Accelerator < RunAnalysis.FullThrottleMinimum ||
                        gear is { } selectedGear && sample.State.Gear != selectedGear)) continue;
                    double xx = x(sample), yy = y(sample);
                    if (!double.IsFinite(xx) || !double.IsFinite(yy)) continue;
                    points.Add(new(xx, yy, sample.ElapsedSeconds, index, sample.Segment, sample.State.Gear));
                }
                return new(comparison ? "Run B" : "Run A", comparison, Reduce(points), points.Count);
            }
        }
    }

    // Every retained dot is an original reading. No connection or interpolation crosses a gap.
    internal static RunAlternativePoint[] Reduce(IReadOnlyList<RunAlternativePoint> points)
    {
        if (points.Count <= MaximumSeriesPoints) return points.ToArray();
        var output = new List<RunAlternativePoint>(MaximumSeriesPoints);
        var bucketSize = (int)Math.Ceiling(points.Count / (MaximumSeriesPoints / 6d));
        for (var start = 0; start < points.Count; start += bucketSize)
        {
            int end = Math.Min(points.Count, start + bucketSize), minX = start, maxX = start, minY = start, maxY = start;
            for (var index = start + 1; index < end; index++)
            {
                if (points[index].X < points[minX].X) minX = index;
                if (points[index].X > points[maxX].X) maxX = index;
                if (points[index].Y < points[minY].Y) minY = index;
                if (points[index].Y > points[maxY].Y) maxY = index;
            }
            foreach (var index in new[] { start, minX, maxX, minY, maxY, end - 1 }.Distinct().Order()) output.Add(points[index]);
        }
        return output.ToArray();
    }

    private static RunAlternativePlotPanel TirePanel(RecordedRun a, RecordedRun? b, TireTemperatureUnit unit, RunInterval? intervalA, RunInterval? intervalB)
    {
        var bars = new List<RunAlternativeBar>();
        Add(a, false, intervalA); if (b is not null) Add(b, true, intervalB);
        var (minimum, maximum) = Bounds(bars.Select(bar => bar.Value), true);
        return new("Tire temperature change", "Start/end axle averages. Boundary interpolation stays within continuous data; gaps stay empty.",
            RunAlternativePlotKind.Bars, "", "", "Temperature", unit == TireTemperatureUnit.Celsius ? "°C" : "°F",
            -.5, 3.5, minimum, maximum, b is null ? [new("Run A", false, [], 0)] : [new("Run A", false, [], 0), new("Run B", true, [], 0)],
            bars.ToArray(), ["Front · start", "Front · end", "Rear · start", "Rear · end"]);

        void Add(RecordedRun run, bool comparison, RunInterval? interval)
        {
            var report = RunAnalysis.BuildReport(run, interval: interval);
            var values = new[] { report.Statistics.StartingFrontTemperatureFahrenheit, report.Statistics.EndingFrontTemperatureFahrenheit,
                report.Statistics.StartingRearTemperatureFahrenheit, report.Statistics.EndingRearTemperatureFahrenheit };
            var startAvailable = SupportedBoundary(run, report.Interval.StartSeconds);
            var endAvailable = SupportedBoundary(run, report.Interval.EndSeconds);
            for (var index = 0; index < values.Length; index++)
                if ((index % 2 == 0 ? startAvailable : endAvailable) && values[index] is { } fahrenheit && double.IsFinite(fahrenheit))
                    bars.Add(new(index, comparison, unit == TireTemperatureUnit.Celsius ? (fahrenheit - 32) * 5 / 9 : fahrenheit,
                        index % 2 == 0 ? report.Interval.StartSeconds : report.Interval.EndSeconds));
        }
    }

    private static bool SupportedBoundary(RecordedRun run, double seconds)
    {
        RunSample? previous = null;
        var latest = double.NegativeInfinity;
        foreach (var sample in run.Samples)
        {
            if (!Driving(sample) || sample.ElapsedSeconds < latest - 1e-7) { previous = null; continue; }
            latest = sample.ElapsedSeconds;
            if (Math.Abs(sample.ElapsedSeconds - seconds) <= 1e-7) return true;
            if (previous is not null && seconds > previous.ElapsedSeconds && seconds < sample.ElapsedSeconds && RunAnalysis.AreContinuous(previous, sample))
                return true;
            previous = sample;
        }
        return false;
    }

    private static (double Minimum, double Maximum) Bounds(IEnumerable<double> values, bool includeZero)
    {
        var array = values.ToArray();
        if (array.Length == 0) return (0, 1);
        double minimum = array.Min(), maximum = array.Max();
        if (includeZero) { minimum = Math.Min(0, minimum); maximum = Math.Max(0, maximum); }
        var padding = Math.Max(1, maximum - minimum) * .06;
        return (minimum < 0 ? minimum - padding : minimum, maximum + padding);
    }
    private static bool Driving(RunSample sample) => sample.IsDriving && sample.State.IsRaceOn && double.IsFinite(sample.ElapsedSeconds) && sample.ElapsedSeconds >= 0 &&
        float.IsFinite(sample.State.GroundSpeedMetersPerSecond) && sample.State.GroundSpeedMetersPerSecond >= 0;
    private static void Validate(RunInterval? interval)
    {
        if (interval is { } value && (!double.IsFinite(value.StartSeconds) || !double.IsFinite(value.EndSeconds) || value.StartSeconds < 0 || value.EndSeconds < value.StartSeconds))
            throw new ArgumentOutOfRangeException(nameof(interval));
    }
}
