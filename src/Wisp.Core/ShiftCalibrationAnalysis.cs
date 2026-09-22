namespace Wisp.Core;

internal static class ShiftCalibrationAnalysis
{
    private const double BinWidth = 100;
    private const double MaximumKnotGap = 200;
    private const double WarmupSeconds = .35;
    private const double BoostWindowSeconds = .25;

    internal static ShiftCalibrationResult Empty(ShiftCalibrationContext context, long revision,
        ShiftCalibrationStatus status, string reason) =>
        new(context, revision, status, reason, null, Array.Empty<AccelerationShiftResult>(), 0, 0, null);

    internal static ShiftCalibrationResult Evaluate(ShiftCalibrationContext context, long revision,
        ShiftCalibrationPoint[] points, long frequency, string currentReason)
    {
        if (points.Length < 30)
            return Empty(context, revision, points.Length == 0 ? ShiftCalibrationStatus.WaitingForPull : ShiftCalibrationStatus.Collecting,
                currentReason);
        var eligible = Qualify(points, frequency, context.ConfiguredOperatingCeilingRpm, out var unstableBoost);
        var bestStart = -1;
        var bestEnd = -1;
        var start = -1;
        for (var i = 0; i <= points.Length; i++)
        {
            var continues = i < points.Length && eligible[i] &&
                (start < 0 || Continuous(points[i - 1], points[i], frequency));
            if (!continues && start >= 0)
            {
                var end = i - 1;
                if (end - start >= 29 && Seconds(points[end].Timestamp - points[start].Timestamp, frequency) >= .75 &&
                    points[end].Rpm - points[start].Rpm >= 700 &&
                    (bestStart < 0 || points[end].Rpm - points[start].Rpm > points[bestEnd].Rpm - points[bestStart].Rpm))
                {
                    bestStart = start;
                    bestEnd = end;
                }
                start = -1;
            }
            if (i < points.Length && eligible[i] && start < 0) start = i;
        }
        if (bestStart < 0)
            return Empty(context, revision, unstableBoost ? ShiftCalibrationStatus.UnstableOutput : ShiftCalibrationStatus.Collecting,
                unstableBoost
                    ? "Boost is still changing. Keep full throttle in a gear with grip until output settles."
                    : "A longer continuous pull is needed; keep full throttle in one gear with grip.");

        if (!TryFit(points, bestStart, bestEnd, out var samples, out var fitReason))
            return Empty(context, revision, ShiftCalibrationStatus.InsufficientCurveCoverage, fitReason);
        var profile = new AccelerationShiftProfile(samples, context.ForwardRatios, context.GearAccelerationFactors);
        if (!ValidCurve(profile))
            return Empty(context, revision, ShiftCalibrationStatus.InsufficientCurveCoverage,
                "More continuous RPM coverage is needed. Extend the clean pull in one gear.");
        var observedUpper = ObservedUpper(points, bestEnd, frequency, context.ConfiguredOperatingCeilingRpm);
        double? upper = observedUpper is { } observed && observed >= profile.Samples[^1].Rpm &&
            observed - profile.Samples[^1].Rpm <= MaximumKnotGap ? profile.Samples[^1].Rpm : null;
        var confirmations = ConfirmingUpshifts(points, eligible, bestStart, bestEnd, profile, frequency);
        return Complete(context, revision, profile, upper, bestEnd - bestStart + 1, confirmations);
    }

    internal static ShiftCalibrationResult Complete(ShiftCalibrationContext context, long revision,
        AccelerationShiftProfile profile, double? empiricalUpperRpm, int acceptedSamples, int confirmingUpshifts)
    {
        var gears = new AccelerationShiftResult[context.ForwardRatios.Count];
        var unresolvedGear = 0;
        for (var gear = 1; gear <= gears.Length; gear++)
        {
            var solved = AccelerationShiftSolver.Solve(profile, gear, Math.Max(500, profile.Samples[0].Rpm));
            // A measured cut supplies a conservative, actually covered upper
            // boundary. Never tell the solver it is an exact native ceiling.
            if (solved.Status == AccelerationShiftStatus.NoCrossoverInDomain && empiricalUpperRpm is { } upper &&
                solved.ModelCandidateRpm == upper && solved.MaximumAnalysisRpm == upper)
                solved = solved with
                {
                    Status = AccelerationShiftStatus.MeasuredUpperBoundary,
                    EstimatedTargetRpm = upper,
                    OperatingCeilingVerified = false
                };
            // A sliver at the end of a curve does not establish a useful gear
            // comparison. Ask for its missing lower/post-shift RPM range.
            if (gear < gears.Length && solved.MaximumAnalysisRpm > 0 &&
                solved.MaximumAnalysisRpm - solved.MinimumAnalysisRpm < Math.Max(300, solved.MaximumAnalysisRpm * .1))
                solved = solved with { Status = AccelerationShiftStatus.InsufficientCurveDomain, EstimatedTargetRpm = null };
            gears[gear - 1] = solved;
            if (gear < gears.Length && !solved.HasEstimatedTarget && unresolvedGear == 0) unresolvedGear = gear;
        }
        var status = unresolvedGear > 0 && gears[unresolvedGear - 1].Status is
            AccelerationShiftStatus.EqualOutputPlateau or AccelerationShiftStatus.NonMonotonicAdvantage or
            AccelerationShiftStatus.AlreadyNextGearBeneficial
            ? ShiftCalibrationStatus.NoDistinctTarget :
            unresolvedGear > 0 ? ShiftCalibrationStatus.InsufficientCurveCoverage :
            confirmingUpshifts == 0 ? ShiftCalibrationStatus.NeedConfirmingUpshift : ShiftCalibrationStatus.Ready;
        var reason = status switch
        {
            ShiftCalibrationStatus.Ready => "Calibration complete for this car and tune. Targets use measured full-load output; driver reaction and shift recovery are not included in the target.",
            ShiftCalibrationStatus.NeedConfirmingUpshift => "The curve is collected. Make one full-throttle upshift and keep accelerating until output settles in the next gear.",
            _ => MissingReason(unresolvedGear, gears[unresolvedGear - 1].Status)
        };
        return new(context, revision, status, reason, profile, Array.AsReadOnly(gears),
            acceptedSamples, confirmingUpshifts, empiricalUpperRpm);
    }

    internal static bool ValidCurve(AccelerationShiftProfile profile) => profile.IsValid &&
        profile.Samples.Count is >= 8 and <= 602 && profile.Samples[0].Rpm >= 500 && profile.Samples[^1].Rpm <= 30_000 &&
        profile.Samples[^1].Rpm - profile.Samples[0].Rpm >= 700 &&
        profile.Samples.All(s => s.Torque is > 1 and <= 100_000) &&
        profile.Samples.Zip(profile.Samples.Skip(1)).All(p => p.Second.Rpm - p.First.Rpm <= MaximumKnotGap);

    private static string MissingReason(int gear, AccelerationShiftStatus status) => status switch
    {
        AccelerationShiftStatus.NoCrossoverInDomain => $"Gear {gear} still benefits from holding. Hold this gear through one brief limiter pulse, then upshift and keep full throttle.",
        AccelerationShiftStatus.EqualOutputPlateau =>
            $"Gear {gear} has a range of equal output, rather than one distinct best RPM. This calibration cannot provide a precise shift target for that gear.",
        AccelerationShiftStatus.NonMonotonicAdvantage =>
            $"Gear {gear} changes advantage more than once in the measured curve. A single shift target is not supported for this comparison.",
        AccelerationShiftStatus.AlreadyNextGearBeneficial =>
            $"The next gear already benefits acceleration at the bottom of gear {gear}'s measured comparison. There is no in-range crossing; lower RPM coverage may locate it.",
        _ => $"Gear {gear} needs more of the lower/post-shift RPM range. Start the clean pull earlier in the same gear."
    };

    private static bool[] Qualify(ShiftCalibrationPoint[] points, long frequency, double? ceiling, out bool unstableBoost)
    {
        var eligible = new bool[points.Length];
        var minimum = new int[points.Length];
        var maximum = new int[points.Length];
        var minHead = 0;
        var minTail = 0;
        var maxHead = 0;
        var maxTail = 0;
        var start = 0;
        unstableBoost = false;
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (!p.Positive || ceiling is { } limit && p.Rpm > limit ||
                i == 0 || !Continuous(points[i - 1], p, frequency) || !points[i - 1].Positive)
            {
                start = i;
                minHead = minTail = maxHead = maxTail = 0;
            }
            if (!p.Positive || ceiling is { } cap && p.Rpm > cap) continue;
            while (minTail > minHead && points[minimum[minTail - 1]].Boost >= p.Boost) minTail--;
            while (maxTail > maxHead && points[maximum[maxTail - 1]].Boost <= p.Boost) maxTail--;
            minimum[minTail++] = i;
            maximum[maxTail++] = i;
            while (minTail > minHead && Seconds(p.Timestamp - points[minimum[minHead]].Timestamp, frequency) > BoostWindowSeconds) minHead++;
            while (maxTail > maxHead && Seconds(p.Timestamp - points[maximum[maxHead]].Timestamp, frequency) > BoostWindowSeconds) maxHead++;
            if (Seconds(p.Timestamp - points[start].Timestamp, frequency) < WarmupSeconds) continue;
            var spread = points[maximum[maxHead]].Boost - points[minimum[minHead]].Boost;
            // These are acceptance tolerances, not a claim that constant measured
            // pressure proves equilibrium. A different gear must confirm output.
            var stable = spread <= Math.Max(1.5, Math.Abs(points[maximum[maxHead]].Boost) * .075);
            unstableBoost |= !stable;
            eligible[i] = stable;
        }
        return eligible;
    }

    private static bool TryFit(ShiftCalibrationPoint[] points, int start, int end,
        out AccelerationShiftSample[] samples, out string reason)
    {
        samples = [];
        reason = "The pull crossed RPM bins too quickly or left gaps. Use a gear that gives a longer clean pull.";
        var bins = new Bin[301];
        var first = 300;
        var last = 0;
        for (var i = start; i <= end; i++)
        {
            var p = points[i];
            var index = (int)(p.Rpm / BinWidth);
            bins[index].Add(p.Rpm, p.Torque);
            first = Math.Min(first, index);
            last = Math.Max(last, index);
        }
        // A pull can begin/end part-way through an RPM bin. Trim an isolated
        // endpoint observation; never bridge a deficient interior bin.
        while (first < last && bins[first].Count < 2) first++;
        while (last > first && bins[last].Count < 2) last--;
        if (last - first < 7) return false;
        var fitted = new List<AccelerationShiftSample>(last - first + 3);
        for (var bin = first; bin <= last; bin++)
        {
            var b = bins[bin];
            if (b.Count < 2) return false;
            if (b.RelativeResidual > .03)
            {
                reason = "Engine output varied within the pull. Repeat with full throttle, steady boost and no wheelspin.";
                return false;
            }
            if (bin == first && b.MinimumRpm < b.MeanRpm)
                fitted.Add(new(b.MinimumRpm, b.At(b.MinimumRpm)));
            fitted.Add(new(b.MeanRpm, b.MeanTorque));
            if (bin == last && b.MaximumRpm > b.MeanRpm)
                fitted.Add(new(b.MaximumRpm, b.At(b.MaximumRpm)));
        }
        samples = fitted.ToArray();
        if (samples.Any(s => !double.IsFinite(s.Torque) || s.Torque is <= 1 or > 100_000)) return false;
        return samples.Zip(samples.Skip(1)).All(p => p.Second.Rpm > p.First.Rpm && p.Second.Rpm - p.First.Rpm <= MaximumKnotGap);
    }

    private static double? ObservedUpper(ShiftCalibrationPoint[] points, int end,
        long frequency, double? configuredCeiling)
    {
        var sweepEnd = points[end];
        for (var i = end + 1; i < points.Length; i++)
        {
            var cut = points[i];
            if (cut.Epoch != sweepEnd.Epoch || cut.Gear != sweepEnd.Gear ||
                Seconds(cut.Timestamp - sweepEnd.Timestamp, frequency) > .3) break;
            if (!cut.LimiterActive) continue;
            // Telemetry can observe torque suppression before the asynchronous
            // native limiter flag. Use the last qualified positive endpoint,
            // across only this bounded same-gear/full-load trail; do not require
            // the immediately preceding (possibly already cut) packet to be positive.
            if (!sweepEnd.Positive || cut.Timestamp <= sweepEnd.Timestamp ||
                configuredCeiling is { } cap && sweepEnd.Rpm < cap * .95)
                continue;
            // Require continued full load in this same gear after the observed
            // native cut. A normal upshift/intervention must not create a bound.
            for (var j = i + 1; j < points.Length; j++)
            {
                var after = points[j];
                if (after.Epoch != cut.Epoch || after.Gear != cut.Gear ||
                    Seconds(after.Timestamp - points[j - 1].Timestamp, frequency) > .1) break;
                if (Seconds(after.Timestamp - cut.Timestamp, frequency) >= .15)
                    return sweepEnd.Rpm;
            }
        }
        return null;
    }

    private static int ConfirmingUpshifts(ShiftCalibrationPoint[] points, bool[] eligible,
        int sweepStart, int sweepEnd, AccelerationShiftProfile profile, long frequency)
    {
        var confirmed = 0;
        var previousGearIndex = -1;
        for (var i = 0; i < points.Length; i++)
        {
            var current = points[i];
            if (current.Gear == 0) continue;
            if (previousGearIndex >= 0)
            {
                var before = points[previousGearIndex];
                if (current.Gear == before.Gear + 1 && current.Epoch == before.Epoch &&
                    Seconds(current.Timestamp - before.Timestamp, frequency) <= .5)
                {
                    var recovered = false;
                    var count = 0;
                    var sumSquaredError = 0d;
                    var worst = 0d;
                    var minimumRpm = double.MaxValue;
                    var maximumRpm = 0d;
                    void Add(int index)
                    {
                        if (!eligible[index] || index >= sweepStart && index <= sweepEnd) return;
                        var point = points[index];
                        if (!TryInterpolate(profile.Samples, point.Rpm, out var expected)) return;
                        var error = Math.Abs(point.Torque / expected - 1);
                        sumSquaredError += error * error;
                        worst = Math.Max(worst, error);
                        minimumRpm = Math.Min(minimumRpm, point.Rpm);
                        maximumRpm = Math.Max(maximumRpm, point.Rpm);
                        count++;
                    }
                    // The other gear is held-out evidence even when the longer
                    // main sweep happened after this upshift.
                    for (var j = previousGearIndex; j >= 0; j--)
                    {
                        if (points[j].Epoch != before.Epoch || points[j].Gear != before.Gear ||
                            Seconds(before.Timestamp - points[j].Timestamp, frequency) > 1) break;
                        Add(j);
                    }
                    for (var j = i; j < points.Length; j++)
                    {
                        if (points[j].Epoch != current.Epoch || points[j].Gear != current.Gear ||
                            Seconds(points[j].Timestamp - current.Timestamp, frequency) > 1.5) break;
                        if (eligible[j] && Seconds(points[j].Timestamp - current.Timestamp, frequency) >= .6) recovered = true;
                        Add(j);
                    }
                    if (recovered && count >= 6 && maximumRpm - minimumRpm >= 100 &&
                        Math.Sqrt(sumSquaredError / count) <= .04 && worst <= .1)
                        confirmed++;
                }
            }
            previousGearIndex = i;
        }
        return confirmed;
    }

    private static bool TryInterpolate(IReadOnlyList<AccelerationShiftSample> samples, double rpm, out double torque)
    {
        torque = 0;
        if (rpm < samples[0].Rpm || rpm > samples[^1].Rpm) return false;
        var low = 0;
        var high = samples.Count - 1;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (samples[middle].Rpm <= rpm) low = middle;
            else high = middle;
        }
        var a = samples[low];
        var b = samples[high];
        torque = a.Torque + (b.Torque - a.Torque) * ((rpm - a.Rpm) / (b.Rpm - a.Rpm));
        return true;
    }

    private static bool Continuous(ShiftCalibrationPoint a, ShiftCalibrationPoint b, long frequency) =>
        a.Epoch == b.Epoch && a.Gear == b.Gear && b.Timestamp > a.Timestamp &&
        Seconds(b.Timestamp - a.Timestamp, frequency) <= .1 && b.Rpm >= a.Rpm - 35;

    private static double Seconds(long ticks, long frequency) => (double)ticks / frequency;

    private struct Bin
    {
        internal int Count;
        internal double MinimumRpm, MaximumRpm;
        private double _rpm, _torque, _rpmSquared, _rpmTorque, _torqueSquared;
        internal readonly double MeanRpm => _rpm / Count;
        internal readonly double MeanTorque => _torque / Count;
        private readonly double Denominator => _rpmSquared - _rpm * _rpm / Count;
        private readonly double Covariance => _rpmTorque - _rpm * _torque / Count;
        private readonly double Slope => Denominator > 1e-6 ? Covariance / Denominator : 0;
        internal readonly double RelativeResidual => Math.Sqrt(Math.Max(0,
            (_torqueSquared - _torque * _torque / Count - Slope * Covariance) / Count)) / MeanTorque;
        internal readonly double At(double rpm) => MeanTorque + Slope * (rpm - MeanRpm);
        internal void Add(double rpm, double torque)
        {
            MinimumRpm = Count == 0 ? rpm : Math.Min(MinimumRpm, rpm);
            MaximumRpm = Math.Max(MaximumRpm, rpm);
            Count++;
            _rpm += rpm;
            _torque += torque;
            _rpmSquared += rpm * rpm;
            _rpmTorque += rpm * torque;
            _torqueSquared += torque * torque;
        }
    }
}
