using System.Globalization;

namespace Wisp.Core.Runs;

public static class RunAnalysis
{
    public const double MaximumContinuousGapSeconds = 0.25;
    public const byte FullThrottleMinimum = 250;
    public const byte BrakingMinimum = 13;
    private const double Epsilon = 1e-7;
    private const double Gravity = 9.80665;

    public static bool AreContinuous(RunSample previous, RunSample current)
    {
        if (!Valid(previous) || !Valid(current)) return false;
        var elapsed = current.ElapsedSeconds - previous.ElapsedSeconds;
        var gameElapsed = unchecked(current.State.GameTimestampMilliseconds - previous.State.GameTimestampMilliseconds);
        return elapsed > Epsilon && elapsed <= MaximumContinuousGapSeconds + Epsilon && gameElapsed > 0 && gameElapsed <= 250 &&
            Math.Abs(elapsed - gameElapsed / 1000d) <= 0.001 + Epsilon &&
            current.Segment == previous.Segment && current.State.CarOrdinal == previous.State.CarOrdinal &&
            current.State.Drivetrain == previous.State.Drivetrain;
    }

    public static RunReport BuildReport(RecordedRun run, RunPurpose purpose = RunPurpose.General, RunInterval? interval = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        var selection = Select(run, interval);
        var statistics = Calculate(selection);
        var findings = new List<RunFinding>();
        if (statistics.RecordedSeconds <= Epsilon)
        {
            findings.Add(new("There is not enough continuous driving data", "Record a longer section with live telemetry to measure changes over time."));
        }
        else
        {
            if (purpose == RunPurpose.Acceleration)
            {
                var excess = LongestStretch(selection, WheelSpeedAhead, 0.5);
                if (excess is { } evidence)
                    findings.Add(new("Wheel speed ran ahead of car speed", $"For {Seconds(Length(evidence))}, driven-wheel speed stayed above car speed with high throttle and little steering.", evidence));
            }
            if (purpose == RunPurpose.Drifting && FindSpeedDrop(selection) is { } drop)
                findings.Add(new("Car speed fell as throttle decreased", "Car speed and throttle both fell in this section. Inspect the input chart alongside speed.", drop, RunEvidenceView.Inputs));

            var throttle = LongestStretch(selection,
                static s => s.A.State.Accelerator >= FullThrottleMinimum && s.B.State.Accelerator >= FullThrottleMinimum, 1);
            if (throttle is { } full)
                findings.Add(new("Longest full-throttle stretch", $"Full throttle totaled {Seconds(statistics.FullThrottleSeconds)} in this selection. The longest uninterrupted stretch lasted {Seconds(Length(full))}.", full, RunEvidenceView.Inputs));

            if (statistics.StartingRearTemperatureFahrenheit is { } start && statistics.EndingRearTemperatureFahrenheit is { } end && Math.Abs(end - start) >= 5)
                findings.Add(new(end > start ? "Rear tires ended warmer" : "Rear tires ended cooler",
                    "Rear tire temperatures changed between the first and last readings in this selection.", selection.Interval, RunEvidenceView.Tires));

            var braking = LongestStretch(selection,
                static s => s.A.State.Brake >= BrakingMinimum && s.B.State.Brake >= BrakingMinimum, 0.5);
            if (braking is { } brake)
                findings.Add(new("Longest braking stretch", $"Brake input stayed above a light press for {Seconds(Length(brake))}.", brake, RunEvidenceView.Inputs));

            if (findings.Count == 0)
                findings.Add(new("Your run is ready to review", "The charts show recorded car speed and driver inputs. Select a section or compare another run to inspect a specific maneuver.", selection.Interval));
        }
        return new(selection.Interval, statistics, findings.Take(3).ToArray(), Quality(run, selection, statistics));
    }

    public static RunComparison Compare(RecordedRun a, RecordedRun b, RunPurpose purpose = RunPurpose.General,
        RunInterval? intervalA = null, RunInterval? intervalB = null,
        double speedFromMetersPerSecond = 8.9408, double speedToMetersPerSecond = 26.8224)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ValidateSpeeds(speedFromMetersPerSecond, speedToMetersPerSecond);
        var reportA = BuildReport(a, purpose, intervalA);
        var reportB = BuildReport(b, purpose, intervalB);
        var selectionA = Select(a, intervalA);
        var selectionB = Select(b, intervalB);
        var accelerationA = Measure(selectionA, speedFromMetersPerSecond, speedToMetersPerSecond);
        var accelerationB = Measure(selectionB, speedFromMetersPerSecond, speedToMetersPerSecond);
        var findings = new List<RunFinding>();
        var conditions = Conditions(a, b, selectionA, selectionB, reportA.Statistics, reportB.Statistics);
        if (conditions.Count > 0)
            findings.Add(new("Check the starting conditions", string.Join(" ", conditions)));

        if (purpose == RunPurpose.Acceleration)
        {
            if (accelerationA is not null && accelerationB is not null)
            {
                var difference = accelerationB.DurationSeconds - accelerationA.DurationSeconds;
                findings.Add(new(Math.Abs(difference) < 0.01 ? "Speed-range times were close" : difference < 0 ? "Run B crossed the speed range sooner" : "Run A crossed the speed range sooner",
                    $"The quickest complete pass through the same speed range took {Seconds(accelerationA.DurationSeconds)} in Run A and {Seconds(accelerationB.DurationSeconds)} in Run B."));
            }
            else
            {
                var missing = accelerationA is null && accelerationB is null ? "Both Run A and Run B need" : accelerationA is null ? "Run A needs" : "Run B needs";
                findings.Add(new("A matching acceleration interval is missing", $"{missing} a complete crossing of the selected speed range in forward gear, without a telemetry gap. Adjust the sections or the speed range."));
            }
        }

        RunFinding? wheelFinding = null;
        if (SameCalibration(selectionA, selectionB) &&
            reportA.Statistics.AverageWheelSpeedExcessMetersPerSecond is { } excessA &&
            reportB.Statistics.AverageWheelSpeedExcessMetersPerSecond is { } excessB && Math.Abs(excessA - excessB) >= 0.5)
            wheelFinding = new(excessB < excessA ? "Run B had less wheel-speed excess" : "Run A had less wheel-speed excess",
                "Driven-wheel speed ran closer to car speed over the calibrated portions of this selection.");
        if (purpose == RunPurpose.Acceleration && wheelFinding is not null)
            findings.Add(wheelFinding);

        if (reportA.Statistics.AverageSpeedMetersPerSecond is { } averageA && reportB.Statistics.AverageSpeedMetersPerSecond is { } averageB)
        {
            var difference = averageB - averageA;
            findings.Add(new(Math.Abs(difference) < 0.2 ? "Average car speed was similar" : difference > 0 ? "Run B carried more car speed on average" : "Run A carried more car speed on average",
                "Averages use each selected section and exclude missing data."));
        }
        else
            findings.Add(new("There is not enough data to compare car speed", "At least one selection has no continuous driving interval. Missing readings are excluded rather than treated as stopped driving."));

        if (purpose != RunPurpose.Acceleration && wheelFinding is not null)
            findings.Add(wheelFinding);

        return new(reportA, reportB, findings.Take(3).ToArray(), accelerationA, accelerationB);
    }

    /// <summary>Returns the quickest complete crossing wholly contained in a continuous forward-driving interval.</summary>
    public static RunSpeedRange? MeasureAcceleration(RecordedRun run, double fromMps, double toMps, RunInterval? interval = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ValidateSpeeds(fromMps, toMps);
        return Measure(Select(run, interval), fromMps, toMps);
    }

    private static RunSpeedRange? Measure(Selection selection, double from, double to)
    {
        RunSpeedRange? best = null;
        double? started = null;
        Span? previous = null;
        foreach (var span in selection.Spans)
        {
            if (previous is null || !Continues(previous, span) || !Forward(span.A) || !Forward(span.B))
                started = null;
            previous = span;
            if (!Forward(span.A) || !Forward(span.B))
                continue;
            var speedA = span.Speed(span.Start);
            var speedB = span.Speed(span.End);
            if (speedA > to)
                started = null;
            if (speedA < from - Epsilon)
                started = null;
            if (started is null && speedA <= from + Epsilon && speedB > from && speedB > speedA)
                started = Cross(span, from, speedA, speedB);
            if (started is { } start && speedA <= to && speedB >= to && speedB > speedA)
            {
                var end = Cross(span, to, speedA, speedB);
                var candidate = new RunSpeedRange(from, to, end - start, new(start, end));
                if (candidate.DurationSeconds > Epsilon && (best is null || candidate.DurationSeconds < best.DurationSeconds))
                    best = candidate;
                started = null;
            }
            if (speedB < from - Epsilon)
                started = null;
        }
        return best;
    }

    private static double Cross(Span span, double speed, double a, double b) => span.Start + (span.End - span.Start) * Math.Clamp((speed - a) / (b - a), 0, 1);

    private static void ValidateSpeeds(double from, double to)
    {
        if (!double.IsFinite(from) || !double.IsFinite(to) || from < 0 || to <= from)
            throw new ArgumentOutOfRangeException(nameof(from), "Use two finite, increasing nonnegative speeds.");
    }

    private static Selection Select(RecordedRun run, RunInterval? requested)
    {
        if (requested is { } range && (!double.IsFinite(range.StartSeconds) || !double.IsFinite(range.EndSeconds) || range.EndSeconds < range.StartSeconds))
            throw new ArgumentOutOfRangeException(nameof(requested), "Use a finite interval with its end after its start.");
        var source = run.Samples ?? [];
        var times = source.Where(static s => s is not null && double.IsFinite(s.ElapsedSeconds) && s.ElapsedSeconds >= 0).Select(static s => s.ElapsedSeconds);
        var first = times.DefaultIfEmpty(0).Min();
        var last = times.DefaultIfEmpty(0).Max();
        var interval = requested is { } chosen
            ? new RunInterval(Math.Clamp(chosen.StartSeconds, first, last), Math.Clamp(chosen.EndSeconds, first, last))
            : new RunInterval(first, last);
        var result = new Selection(interval);
        RunSample? previous = null;
        double latestTime = double.NegativeInfinity;
        foreach (var sample in source)
        {
            if (!Valid(sample))
            {
                previous = null;
                continue;
            }
            if (sample.ElapsedSeconds < latestTime - Epsilon)
            {
                previous = null;
                continue;
            }
            latestTime = sample.ElapsedSeconds;
            if (!sample.State.IsElectric && float.IsFinite(sample.State.BoostPressurePsi) && sample.State.BoostPressurePsi > 0)
                result.BoostedCars.Add(sample.State.CarOrdinal);
            if (sample.ElapsedSeconds >= interval.StartSeconds && sample.ElapsedSeconds <= interval.EndSeconds)
                result.Points.Add(sample);
            if (previous is not null)
            {
                if (AreContinuous(previous, sample))
                {
                    var start = Math.Max(previous.ElapsedSeconds, interval.StartSeconds);
                    var end = Math.Min(sample.ElapsedSeconds, interval.EndSeconds);
                    if (end > start + Epsilon)
                        result.Spans.Add(new(previous, sample, start, end));
                }
            }
            previous = sample;
        }
        var cursor = interval.StartSeconds;
        foreach (var span in result.Spans)
        {
            if (span.Start > cursor + Epsilon)
                result.Gaps++;
            cursor = Math.Max(cursor, span.End);
        }
        if (interval.EndSeconds > cursor + Epsilon)
            result.Gaps++;
        return result;
    }

    private static bool Valid(RunSample? sample) => sample is not null && sample.State is not null && sample.IsDriving && sample.State.IsRaceOn &&
        double.IsFinite(sample.ElapsedSeconds) && sample.ElapsedSeconds >= 0 &&
        float.IsFinite(sample.State.GroundSpeedMetersPerSecond) && sample.State.GroundSpeedMetersPerSecond >= 0;

    private static RunStatistics Calculate(Selection selection)
    {
        double seconds = 0, distance = 0, throttle = 0, braking = 0, wheelSeconds = 0, wheelIntegral = 0;
        double? peakSpeed = null, peakPower = null, peakTorque = null, peakBoost = null, peakLateral = null, peakLongitudinal = null;
        double? startFront = null, startRear = null, endFront = null, endRear = null;
        var firstTime = double.PositiveInfinity;
        var lastTime = double.NegativeInfinity;

        void Observe(double time, double speed, double? power, double? torque, double? boost, double? lateral, double? longitudinal, double? front, double? rear)
        {
            peakSpeed = Max(peakSpeed, speed);
            peakPower = Max(peakPower, power);
            peakTorque = Max(peakTorque, torque);
            peakBoost = Max(peakBoost, boost);
            peakLateral = Max(peakLateral, lateral is { } lat ? Math.Abs(lat) : null);
            peakLongitudinal = Max(peakLongitudinal, longitudinal is { } longitudinalValue ? Math.Abs(longitudinalValue) : null);
            if (time <= firstTime)
            {
                firstTime = time;
                startFront = front;
                startRear = rear;
            }
            if (time >= lastTime)
            {
                lastTime = time;
                endFront = front;
                endRear = rear;
            }
        }

        double? BoostValue(RunSample sample) => Boost(sample, selection.BoostedCars);
        void ObserveSpan(Span span, double time) => Observe(time, span.Speed(time), span.Value(Power, time), span.Value(Torque, time),
            span.Value(BoostValue, time), span.Value(Lateral, time), span.Value(Longitudinal, time), span.Value(FrontTemperature, time), span.Value(RearTemperature, time));
        foreach (var span in selection.Spans)
        {
            var dt = span.End - span.Start;
            seconds += dt;
            distance += (span.Speed(span.Start) + span.Speed(span.End)) * 0.5 * dt;
            throttle += AboveDuration(span.Lerp(span.A.State.Accelerator, span.B.State.Accelerator, span.Start), span.Lerp(span.A.State.Accelerator, span.B.State.Accelerator, span.End), FullThrottleMinimum, dt);
            braking += AboveDuration(span.Lerp(span.A.State.Brake, span.B.State.Brake, span.Start), span.Lerp(span.A.State.Brake, span.B.State.Brake, span.End), BrakingMinimum, dt);
            if (HasSameCalibration(span.A, span.B) && Wheel(span.A) is { } wheelA && Wheel(span.B) is { } wheelB)
            {
                var excessA = span.Lerp(wheelA - span.A.State.GroundSpeedMetersPerSecond, wheelB - span.B.State.GroundSpeedMetersPerSecond, span.Start);
                var excessB = span.Lerp(wheelA - span.A.State.GroundSpeedMetersPerSecond, wheelB - span.B.State.GroundSpeedMetersPerSecond, span.End);
                wheelIntegral += PositiveIntegral(excessA, excessB, dt);
                wheelSeconds += dt;
            }
            ObserveSpan(span, span.Start);
            ObserveSpan(span, span.End);
        }
        foreach (var point in selection.Points)
            Observe(point.ElapsedSeconds, point.State.GroundSpeedMetersPerSecond, Power(point), Torque(point), BoostValue(point), Lateral(point), Longitudinal(point), FrontTemperature(point), RearTemperature(point));
        selection.WheelSeconds = wheelSeconds;
        return new()
        {
            SampleCount = selection.Points.Count,
            DurationSeconds = Length(selection.Interval),
            RecordedSeconds = seconds,
            GapCount = selection.Gaps,
            AverageSpeedMetersPerSecond = seconds > Epsilon ? distance / seconds : null,
            PeakSpeedMetersPerSecond = peakSpeed,
            DistanceMeters = distance,
            FullThrottleSeconds = throttle,
            BrakingSeconds = braking,
            PeakPowerWatts = peakPower,
            PeakTorqueNm = peakTorque,
            PeakBoostPsi = peakBoost,
            PeakLateralG = peakLateral,
            PeakLongitudinalG = peakLongitudinal,
            StartingFrontTemperatureFahrenheit = startFront,
            StartingRearTemperatureFahrenheit = startRear,
            EndingFrontTemperatureFahrenheit = endFront,
            EndingRearTemperatureFahrenheit = endRear,
            AverageWheelSpeedExcessMetersPerSecond = wheelSeconds > Epsilon ? wheelIntegral / wheelSeconds : null
        };
    }

    private static string Quality(RecordedRun run, Selection selection, RunStatistics statistics)
    {
        var notes = new List<string>();
        if (run.IsIncomplete)
            notes.Add("This recording ended before it was complete.");
        if (run.DroppedDatagrams > 0)
            notes.Add("Some samples were lost while recording.");
        if (run.RejectedDatagrams > 0)
            notes.Add("Some telemetry packets could not be read.");
        if (statistics.GapCount > 0)
            notes.Add("Sections without continuous driving data are excluded from time-based statistics.");
        if (statistics.RecordedSeconds <= Epsilon)
            notes.Add("There is not enough continuous data for averages or acceleration times.");
        if (selection.WheelSeconds <= Epsilon && statistics.RecordedSeconds > Epsilon)
            notes.Add("No continuous section has trusted, unchanged tire calibration and available wheel readings.");
        else if (selection.WheelSeconds + Epsilon < statistics.RecordedSeconds)
            notes.Add("Wheel-speed results cover only sections with trusted, unchanged tire calibration and available wheel readings.");
        if (statistics.StartingFrontTemperatureFahrenheit is null || statistics.StartingRearTemperatureFahrenheit is null ||
            statistics.EndingFrontTemperatureFahrenheit is null || statistics.EndingRearTemperatureFahrenheit is null)
            notes.Add("Some starting or ending tire temperatures are unavailable.");
        return notes.Count == 0 ? "Calculated from recorded telemetry; peak readings are instantaneous observations." : string.Join(" ", notes);
    }

    private static List<string> Conditions(RecordedRun a, RecordedRun b, Selection sa, Selection sb, RunStatistics sta, RunStatistics stb)
    {
        var notes = new List<string>();
        var carsA = ContextPoints(sa).Select(static s => s.State.CarOrdinal).Distinct().ToArray();
        var carsB = ContextPoints(sb).Select(static s => s.State.CarOrdinal).Distinct().ToArray();
        if (carsA.Length != 1 || carsB.Length != 1 || carsA[0] != carsB[0])
            notes.Add("The selections do not identify the same single car.");
        if (!SameCalibration(sa, sb))
            notes.Add("Wheel-speed calibration differs or is incomplete.");
        if (sta.StartingRearTemperatureFahrenheit is { } rearA && stb.StartingRearTemperatureFahrenheit is { } rearB && Math.Abs(rearA - rearB) >= 5 ||
            sta.StartingFrontTemperatureFahrenheit is { } frontA && stb.StartingFrontTemperatureFahrenheit is { } frontB && Math.Abs(frontA - frontB) >= 5)
            notes.Add("The tires started at different temperatures.");
        else if (sta.StartingRearTemperatureFahrenheit is null || stb.StartingRearTemperatureFahrenheit is null ||
                 sta.StartingFrontTemperatureFahrenheit is null || stb.StartingFrontTemperatureFahrenheit is null)
            notes.Add("Starting tire temperatures are not available for both runs.");
        if (Math.Abs(sta.DurationSeconds - stb.DurationSeconds) > Math.Max(1, Math.Min(sta.DurationSeconds, stb.DurationSeconds) * 0.1))
            notes.Add("The selected sections have different durations.");
        if (a.IsIncomplete || b.IsIncomplete || a.DroppedDatagrams > 0 || b.DroppedDatagrams > 0 || a.RejectedDatagrams > 0 || b.RejectedDatagrams > 0 || sta.GapCount > 0 || stb.GapCount > 0)
            notes.Add("At least one selection comes from an incomplete recording or has gaps.");
        return notes;
    }

    private static bool SameCalibration(Selection a, Selection b)
    {
        var pointsA = ContextPoints(a);
        var pointsB = ContextPoints(b);
        var referenceA = pointsA.FirstOrDefault();
        var referenceB = pointsB.FirstOrDefault();
        return referenceA is not null && referenceB is not null && HasSameCalibration(referenceA, referenceB) &&
               pointsA.All(p => Wheel(p) is not null && HasSameCalibration(referenceA, p)) &&
               pointsB.All(p => Wheel(p) is not null && HasSameCalibration(referenceB, p));
    }

    private static IEnumerable<RunSample> ContextPoints(Selection selection) => selection.Points.Concat(selection.Spans.SelectMany(static s => new[] { s.A, s.B }));

    private static bool WheelSpeedAhead(Span span)
    {
        return Forward(span.A) && Forward(span.B) && HasSameCalibration(span.A, span.B) &&
            Ahead(span.A) && Ahead(span.B);

        static bool Ahead(RunSample s) => Wheel(s) is { } wheel && s.State.Accelerator >= FullThrottleMinimum &&
            Math.Abs((int)s.State.Steering) <= 8 && Math.Abs(s.State.LateralAccelerationMetersPerSecondSquared) <= 2 &&
            s.State.GroundSpeedMetersPerSecond >= 1 && wheel - s.State.GroundSpeedMetersPerSecond >= Math.Max(3, s.State.GroundSpeedMetersPerSecond * 0.15);
    }

    private static RunInterval? LongestStretch(Selection selection, Func<Span, bool> predicate, double minimum)
    {
        RunInterval? best = null;
        Span? previous = null;
        double? start = null;
        foreach (var span in selection.Spans)
        {
            if (previous is null || !Continues(previous, span) || !predicate(span))
                start = null;
            if (predicate(span))
            {
                start ??= span.Start;
                var current = new RunInterval(start.Value, span.End);
                if (Length(current) >= minimum && (best is null || Length(current) > Length(best.Value)))
                    best = current;
            }
            previous = span;
        }
        return best;
    }

    private static RunInterval? FindSpeedDrop(Selection selection)
    {
        RunInterval? best = null;
        double largestDrop = 2;
        int window = 0, blockStart = 0;
        for (var i = 0; i < selection.Spans.Count; i++)
        {
            var end = selection.Spans[i];
            if (i == 0 || !Continues(selection.Spans[i - 1], end))
                blockStart = window = i;
            var startTime = end.End - 1;
            if (startTime < selection.Spans[blockStart].Start)
                continue;
            while (window < i && selection.Spans[window].End < startTime)
                window++;
            var start = selection.Spans[window];
            var drop = start.Speed(startTime) - end.Speed(end.End);
            var throttleStart = start.Lerp(start.A.State.Accelerator, start.B.State.Accelerator, startTime);
            var throttleEnd = end.Lerp(end.A.State.Accelerator, end.B.State.Accelerator, end.End);
            if (drop > largestDrop && throttleStart >= 128 && throttleEnd <= 64)
            {
                largestDrop = drop;
                best = new(startTime, end.End);
            }
        }
        return best;
    }

    private static bool Continues(Span previous, Span next) => Math.Abs(previous.End - next.Start) <= Epsilon && previous.B.Segment == next.A.Segment && previous.B.State.CarOrdinal == next.A.State.CarOrdinal;
    private static bool Forward(RunSample sample) => sample.State.Gear >= TransmissionGear.First && sample.State.Gear <= TransmissionGear.Tenth;
    private static double Length(RunInterval interval) => Math.Max(0, interval.EndSeconds - interval.StartSeconds);
    private static string Seconds(double value) => value.ToString("0.00", CultureInfo.InvariantCulture) + " seconds";
    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
    private static double? Max(double? a, double? b) => b is null ? a : a is null ? b : Math.Max(a.Value, b.Value);
    private static double? Power(RunSample p) => Finite(p.State.PowerWatts);
    private static double? Torque(RunSample p) => Finite(p.State.TorqueNm);
    private static double? Lateral(RunSample p) => Finite(p.State.LateralAccelerationMetersPerSecondSquared / Gravity);
    private static double? Longitudinal(RunSample p) => Finite(p.State.LongitudinalAccelerationMetersPerSecondSquared / Gravity);
    private static double? Boost(RunSample p, HashSet<int> boosted) => !p.State.IsElectric && boosted.Contains(p.State.CarOrdinal) ? Finite(p.State.BoostPressurePsi) : null;
    private static double? FrontTemperature(RunSample p) => AxleTemperature(p.State.TireTemperatureFahrenheit.FrontLeft, p.State.TireTemperatureFahrenheit.FrontRight);
    private static double? RearTemperature(RunSample p) => AxleTemperature(p.State.TireTemperatureFahrenheit.RearLeft, p.State.TireTemperatureFahrenheit.RearRight);
    private static double? AxleTemperature(float a, float b) => float.IsFinite(a) && float.IsFinite(b) && a > 0 && b > 0 ? ((double)a + b) * 0.5 : null;

    private static double? Wheel(RunSample p) => TrustedCalibration(p) && p.WheelSpeedMetersPerSecond is { } speed && double.IsFinite(speed) && speed >= 0 ? speed : null;
    private static bool Radius(double? radius) => radius is { } value && double.IsFinite(value) && value > 0;
    private static bool TrustedCalibration(RunSample p) => p.State.Drivetrain switch
    {
        DrivetrainType.FrontWheelDrive => Radius(p.FrontRadiusMeters),
        DrivetrainType.RearWheelDrive => Radius(p.RearRadiusMeters),
        DrivetrainType.AllWheelDrive => Radius(p.FrontRadiusMeters) && Radius(p.RearRadiusMeters),
        _ => false
    };
    private static bool HasSameCalibration(RunSample a, RunSample b) => TrustedCalibration(a) && TrustedCalibration(b) && a.State.Drivetrain == b.State.Drivetrain &&
        (a.State.Drivetrain == DrivetrainType.RearWheelDrive || Math.Abs(a.FrontRadiusMeters!.Value - b.FrontRadiusMeters!.Value) <= 1e-6) &&
        (a.State.Drivetrain == DrivetrainType.FrontWheelDrive || Math.Abs(a.RearRadiusMeters!.Value - b.RearRadiusMeters!.Value) <= 1e-6);

    private static double AboveDuration(double a, double b, double threshold, double dt)
    {
        if (a >= threshold && b >= threshold) return dt;
        if (a < threshold && b < threshold) return 0;
        var crossing = Math.Clamp((threshold - a) / (b - a), 0, 1);
        return dt * (a >= threshold ? crossing : 1 - crossing);
    }

    private static double PositiveIntegral(double a, double b, double dt)
    {
        if (a >= 0 && b >= 0) return (a + b) * 0.5 * dt;
        if (a <= 0 && b <= 0) return 0;
        var positive = Math.Max(a, b);
        return 0.5 * positive * positive / Math.Abs(b - a) * dt;
    }

    private sealed class Selection(RunInterval interval)
    {
        public RunInterval Interval { get; } = interval;
        public List<RunSample> Points { get; } = [];
        public List<Span> Spans { get; } = [];
        public HashSet<int> BoostedCars { get; } = [];
        public int Gaps { get; set; }
        public double WheelSeconds { get; set; }
    }

    private sealed record Span(RunSample A, RunSample B, double Start, double End)
    {
        public double Lerp(double a, double b, double time) => a + (b - a) * Math.Clamp((time - A.ElapsedSeconds) / (B.ElapsedSeconds - A.ElapsedSeconds), 0, 1);
        public double Speed(double time) => Lerp(A.State.GroundSpeedMetersPerSecond, B.State.GroundSpeedMetersPerSecond, time);
        public double? Value(Func<RunSample, double?> selector, double time) => selector(A) is { } a && selector(B) is { } b ? Lerp(a, b, time) : null;
    }
}
