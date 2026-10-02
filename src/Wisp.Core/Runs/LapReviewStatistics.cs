namespace Wisp.Core.Runs;

public static partial class LapReviewAnalysis
{
    private const double Gravity = 9.80665;

    /// <summary>Inclusive recorded point indices; no unrecorded approach or exit is inferred.</summary>
    public static LapSectionStatistics AnalyzeSection(LapReviewLap lap, int firstPointIndex, int lastPointIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lap);
        if (lap.Points.Length > MaximumPoints || firstPointIndex < 0 || lastPointIndex < firstPointIndex || lastPointIndex >= lap.Points.Length)
            throw new ArgumentOutOfRangeException(nameof(firstPointIndex));
        cancellationToken.ThrowIfCancellationRequested();
        var summaries = Enumerable.Range(0, 24).Select(_ => new ValueAccumulator()).ToArray();
        var events = new List<LapReviewEvent>();
        var first = lap.Points[firstPointIndex];
        var last = lap.Points[lastPointIndex];
        double covered = 0, distance = 0, throttle = 0, full = 0, brake = 0, coast = 0;
        var gaps = 0;
        Span<double> values = stackalloc double[24];
        Span<double> previousValues = stackalloc double[24];
        var quality = lap.Quality & (LapReviewQuality.InferredTiming | LapReviewQuality.MissingTiming | LapReviewQuality.Rewind | LapReviewQuality.Discontinuity);
        if (firstPointIndex == 0) quality |= lap.Quality & LapReviewQuality.PartialStart;
        if (lastPointIndex == lap.Points.Length - 1) quality |= lap.Quality & LapReviewQuality.PartialEnd;
        for (var index = firstPointIndex; index <= lastPointIndex; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var point = lap.Points[index];
            ReadValues(point.Sample.State, values);
            for (var channel = 0; channel < values.Length; channel++)
                summaries[channel].Observe(values[channel], index == firstPointIndex, index == lastPointIndex);
            if (index > firstPointIndex)
            {
                var previous = lap.Points[index - 1];
                var dt = point.LapSeconds - previous.LapSeconds;
                var continuous = ContinuousPoints(previous, point) && dt is >= 0 and <= .25;
                if (continuous)
                    distance += Math.Max(0, point.DistanceMeters - previous.DistanceMeters);
                if (continuous)
                {
                    var a = previous.Sample.State;
                    var b = point.Sample.State;
                    if (dt > 0)
                    {
                        covered += dt;
                        for (var channel = 0; channel < values.Length; channel++)
                            summaries[channel].Integrate(previousValues[channel], values[channel], dt);
                        throttle += dt * ActiveFraction(a.Accelerator, b.Accelerator, InputActiveMinimum);
                        full += dt * ActiveFraction(a.Accelerator, b.Accelerator, RunAnalysis.FullThrottleMinimum);
                        brake += dt * ActiveFraction(a.Brake, b.Brake, InputActiveMinimum);
                        if (a.Gear >= TransmissionGear.First && b.Gear >= TransmissionGear.First)
                            coast += dt * BothInactiveFraction(a.Accelerator, b.Accelerator, a.Brake, b.Brake);
                    }
                    AddInputEvent(a.Brake, b.Brake, LapReviewEventKind.BrakeStart, LapReviewEventKind.BrakeEnd);
                    AddInputEvent(a.Accelerator, b.Accelerator, LapReviewEventKind.ThrottlePickup, null);
                    if (a.Gear >= TransmissionGear.First && b.Gear >= TransmissionGear.First && a.Gear != b.Gear)
                        events.Add(new(b.Gear > a.Gear ? LapReviewEventKind.Upshift : LapReviewEventKind.Downshift,
                            index, point.RunSeconds, point.DistanceMeters, b.EngineRpm, a.Gear, b.Gear));

                    void AddInputEvent(byte from, byte to, LapReviewEventKind rising, LapReviewEventKind? falling)
                    {
                        var began = from < InputActiveMinimum && to >= InputActiveMinimum;
                        var ended = from >= InputActiveMinimum && to < InputActiveMinimum;
                        if (!began && (!ended || falling is null)) return;
                        var fraction = Math.Clamp((InputActiveMinimum - from) / (double)(to - from), 0, 1);
                        events.Add(new(began ? rising : falling!.Value, index,
                            previous.RunSeconds + (point.RunSeconds - previous.RunSeconds) * fraction,
                            previous.DistanceMeters + (point.DistanceMeters - previous.DistanceMeters) * fraction,
                            InputActiveMinimum / 255d * 100));
                    }
                }
                else if (point.BreakBefore || dt < 0 || dt > .25 || !ContinuousSamples(previous.Sample, point.Sample))
                {
                    gaps++;
                    quality |= LapReviewQuality.TelemetryGap;
                }
            }
            values.CopyTo(previousValues);
        }
        var span = Math.Max(0, last.LapSeconds - first.LapSeconds);
        var speed = summaries[0].Summary;
        return new()
        {
            FirstPointIndex = firstPointIndex,
            LastPointIndex = lastPointIndex,
            SampleCount = lastPointIndex - firstPointIndex + 1,
            Quality = quality,
            DurationSeconds = span,
            RecordedSeconds = covered,
            DistanceMeters = distance,
            GapCount = gaps,
            EntrySpeedMetersPerSecond = speed.Start,
            MinimumSpeedMetersPerSecond = speed.Minimum,
            ExitSpeedMetersPerSecond = speed.End,
            AverageSpeedMetersPerSecond = speed.Mean,
            ThrottleSeconds = throttle,
            FullThrottleSeconds = full,
            BrakingSeconds = brake,
            CoastingSeconds = coast,
            ThrottlePercent = summaries[1].Summary,
            BrakePercent = summaries[2].Summary,
            SteeringRaw = summaries[3].Summary,
            LateralG = summaries[4].Summary,
            LongitudinalG = summaries[5].Summary,
            CombinedG = summaries[6].Summary,
            EngineRpm = summaries[7].Summary,
            TireTemperatureFahrenheit = Wheels(8),
            TireSlipRatio = Wheels(12),
            TireSlipAngle = Wheels(16),
            NormalizedSuspensionTravel = Wheels(20),
            Events = events.ToArray(),
            QualityNote = (gaps > 0 ? "Missing spans are excluded from time fractions, distance and averages. " : "") +
                "Distance follows recorded 3D positions. Inputs use a 13/255 threshold; full throttle uses 250/255. " +
                "Input event positions are interpolated between adjacent samples; shifts identify the first recorded new gear. " +
                "Steering is raw input, slip channels are game telemetry, and this is not an official clean-lap assessment."
        };

        LapWheelSummaries Wheels(int offset) => new(summaries[offset].Summary, summaries[offset + 1].Summary,
            summaries[offset + 2].Summary, summaries[offset + 3].Summary);
    }

    private static bool ContinuousPoints(LapReviewPoint a, LapReviewPoint b) => !b.BreakBefore &&
        ContinuousSamples(a.Sample, b.Sample) && b.LapSeconds >= a.LapSeconds;

    private static void ReadValues(VehicleState state, Span<double> target)
    {
        target[0] = state.GroundSpeedMetersPerSecond >= 0 ? state.GroundSpeedMetersPerSecond : double.NaN;
        target[1] = state.Accelerator / 255d * 100;
        target[2] = state.Brake / 255d * 100;
        target[3] = state.Steering;
        target[4] = state.LateralAccelerationMetersPerSecondSquared / Gravity;
        target[5] = state.LongitudinalAccelerationMetersPerSecondSquared / Gravity;
        target[6] = Math.Sqrt(target[4] * target[4] + target[5] * target[5]);
        target[7] = state.EngineRpm >= 0 ? state.EngineRpm : double.NaN;
        SetWheels(state.TireTemperatureFahrenheit, target[8..12]);
        SetWheels(state.TireSlipRatio, target[12..16]);
        SetWheels(state.TireSlipAngle, target[16..20]);
        SetWheels(state.NormalizedSuspensionTravel, target[20..24]);
    }

    private static void SetWheels(WheelValues wheels, Span<double> target)
    {
        target[0] = wheels.FrontLeft;
        target[1] = wheels.FrontRight;
        target[2] = wheels.RearLeft;
        target[3] = wheels.RearRight;
    }

    private static double ActiveFraction(double a, double b, double threshold)
    {
        if (a >= threshold && b >= threshold) return 1;
        if (a < threshold && b < threshold) return 0;
        var crossing = Math.Clamp((threshold - a) / (b - a), 0, 1);
        return a < threshold ? 1 - crossing : crossing;
    }

    private static double BothInactiveFraction(double throttleA, double throttleB, double brakeA, double brakeB)
    {
        var start = 0d;
        var end = 1d;
        Intersect(throttleA, throttleB);
        Intersect(brakeA, brakeB);
        return Math.Max(0, end - start);

        void Intersect(double a, double b)
        {
            if (a >= InputActiveMinimum && b >= InputActiveMinimum) { end = 0; return; }
            if (a < InputActiveMinimum && b < InputActiveMinimum) return;
            var crossing = Math.Clamp((InputActiveMinimum - a) / (b - a), 0, 1);
            if (a < InputActiveMinimum) end = Math.Min(end, crossing);
            else start = Math.Max(start, crossing);
        }
    }

    private sealed class ValueAccumulator
    {
        private double? _start, _end, _minimum, _maximum;
        private double _area, _seconds;
        internal void Observe(double value, bool first, bool last)
        {
            if (!double.IsFinite(value)) return;
            if (first) _start = value;
            if (last) _end = value;
            _minimum = Math.Min(_minimum ?? value, value);
            _maximum = Math.Max(_maximum ?? value, value);
        }
        internal void Integrate(double a, double b, double dt)
        {
            if (!double.IsFinite(a) || !double.IsFinite(b)) return;
            _area += (a + b) / 2 * dt;
            _seconds += dt;
        }
        internal LapValueSummary Summary => new(_start, _end, _minimum, _maximum, _seconds > 0 ? _area / _seconds : null);
    }
}
