namespace Wisp.Core;

/// <summary>
/// Piecewise-linear, fixed-speed, single-upshift model. Common constant shift
/// interruption cancels from the comparison. This model does not account for
/// command latency, transient boost, road load, traction or clutch dynamics.
/// </summary>
public static class AccelerationShiftSolver
{
    private const double RpmTolerance = 1e-6;
    private const double ForceRelativeTolerance = 1e-11;

    public static AccelerationShiftResult Solve(
        AccelerationShiftProfile? profile,
        int currentGear,
        double minimumAnalysisRpm = 2000,
        double? maximumAnalysisRpm = null,
        double forceEquivalenceTolerance = .01)
    {
        if (profile is null) return Empty(AccelerationShiftStatus.Unavailable);
        if (!profile.IsValid || !double.IsFinite(minimumAnalysisRpm) || minimumAnalysisRpm <= 0 ||
            maximumAnalysisRpm is { } requested && (!double.IsFinite(requested) || requested <= minimumAnalysisRpm) ||
            !double.IsFinite(forceEquivalenceTolerance) || forceEquivalenceTolerance is < 0 or >= 1)
            return Empty(AccelerationShiftStatus.InvalidProfile);
        if (currentGear < 1 || currentGear >= profile.ForwardRatios.Count)
            return Empty(AccelerationShiftStatus.NoNextGear);

        var g = profile.ForwardRatios[currentGear - 1];
        var h = profile.ForwardRatios[currentGear];
        var k = h / g;
        if (!PositiveFinite(k)) return Empty(AccelerationShiftStatus.InvalidProfile);
        var samples = profile.Samples;
        var lower = Math.Max(minimumAnalysisRpm, samples[0].Rpm / k);
        var upper = Math.Min(maximumAnalysisRpm ?? samples[^1].Rpm, samples[^1].Rpm);
        if (profile.VerifiedOperatingCeilingRpm is { } ceiling) upper = Math.Min(upper, ceiling);
        // A negative final limiter/braking sample does not invalidate otherwise
        // useful metadata. Stop at the previous positive source knot, including
        // when the requested ceiling lies inside the interval to the bad knot.
        // Never interpolate that terminal decline into an operating bound.
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].Rpm > lower && samples[i].Torque <= 0 &&
                (i == 0 || samples[i - 1].Rpm < upper))
            {
                upper = i == 0 ? lower : samples[i - 1].Rpm;
                break;
            }
        }
        if (!double.IsFinite(lower) || upper <= lower + RpmTolerance)
            return Empty(AccelerationShiftStatus.InsufficientCurveDomain);

        var fg = g * profile.GearAccelerationFactors[currentGear - 1];
        var fh = h * profile.GearAccelerationFactors[currentGear];
        double Current(double rpm) => Interpolate(samples, rpm) * fg;
        double Next(double rpm) => Interpolate(samples, rpm * k) * fh;
        var knots = new SortedSet<double> { lower, upper };
        foreach (var sample in samples)
        {
            if (sample.Rpm > lower && sample.Rpm < upper) knots.Add(sample.Rpm);
            var postShiftKnot = sample.Rpm / k;
            if (postShiftKnot > lower && postShiftKnot < upper) knots.Add(postShiftKnot);
        }
        var xs = knots.ToArray();
        var segments = new List<Segment>(xs.Length - 1);
        var roots = new SortedSet<double>();
        var plateaus = new List<AccelerationShiftBand>();
        var equivalentBands = new List<AccelerationShiftBand>();
        var objective = 0d;
        for (var i = 0; i + 1 < xs.Length; i++)
        {
            var left = xs[i];
            var right = xs[i + 1];
            var al = Current(left);
            var ar = Current(right);
            var bl = Next(left);
            var br = Next(right);
            if (!PositiveFinite(al) || !PositiveFinite(ar) || !PositiveFinite(bl) || !PositiveFinite(br))
                return Empty(AccelerationShiftStatus.NonPositiveOutput);
            var dl = bl - al;
            var dr = br - ar;
            var tolerance = ForceRelativeTolerance * Math.Max(Math.Max(al, ar), Math.Max(bl, br));
            if (Math.Abs(dl) <= tolerance && Math.Abs(dr) <= tolerance)
                plateaus.Add(new(left, right));
            else
            {
                if (dl * dr < 0) roots.Add(left + (right - left) * (-dl) / (dr - dl));
                else
                {
                    if (dl == 0) roots.Add(left);
                    if (dr == 0) roots.Add(right);
                }
            }
            AddEquivalenceBands(equivalentBands, left, right, al, ar, bl, br, forceEquivalenceTolerance);
            var segment = new Segment(left, right, al, ar, bl, br, objective);
            segments.Add(segment);
            objective = segment.Objective(right);
            if (!double.IsFinite(objective)) return Empty(AccelerationShiftStatus.InvalidProfile);
        }

        double Objective(double rpm)
        {
            var index = Array.BinarySearch(xs, rpm);
            if (index < 0) index = ~index - 1;
            return segments[Math.Clamp(index, 0, segments.Count - 1)].Objective(rpm);
        }
        var candidates = new SortedSet<double>(knots);
        candidates.UnionWith(roots);
        var best = lower;
        var minimum = Objective(lower);
        foreach (var candidate in candidates)
        {
            var value = Objective(candidate);
            if (value < minimum)
            {
                minimum = value;
                best = candidate;
            }
        }
        var objectiveTolerance = Math.Max(1e-12, segments.Max(s => Math.Abs(s.Objective(s.Right))) * 1e-11);
        var optimalPlateau = plateaus.Any(p => Objective((p.MinimumRpm + p.MaximumRpm) * .5) <= minimum + objectiveTolerance);
        // Math.Min retains the actual supplied boundary value. Require exact
        // equality: a nearby caller cap or incomplete curve is not a limiter.
        var verifiedLimitIsBest = best == upper && profile.VerifiedOperatingCeilingRpm == upper;
        var status = optimalPlateau ? AccelerationShiftStatus.EqualOutputPlateau :
            best <= lower + RpmTolerance ? AccelerationShiftStatus.AlreadyNextGearBeneficial :
            verifiedLimitIsBest ? AccelerationShiftStatus.VerifiedLimitBound :
            best >= upper - RpmTolerance ? AccelerationShiftStatus.NoCrossoverInDomain :
            AccelerationShiftStatus.EstimatedCrossover;
        if (status == AccelerationShiftStatus.EstimatedCrossover &&
            xs.Where(x => x > best).Any(x => Next(x) < Current(x) * (1 - ForceRelativeTolerance)))
            status = AccelerationShiftStatus.NonMonotonicAdvantage;
        return new(status,
            status is AccelerationShiftStatus.EstimatedCrossover or AccelerationShiftStatus.VerifiedLimitBound ? best : null,
            best, lower, upper, profile.VerifiedOperatingCeilingRpm.HasValue,
            Array.AsReadOnly(roots.Where(r => r > lower && r < upper).ToArray()),
            Array.AsReadOnly(MergeBands(equivalentBands).ToArray()));
    }

    private static AccelerationShiftResult Empty(AccelerationShiftStatus status) =>
        new(status, null, null, 0, 0, false, Array.Empty<double>(), Array.Empty<AccelerationShiftBand>());

    private static bool PositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static double Interpolate(IReadOnlyList<AccelerationShiftSample> points, double rpm)
    {
        var low = 0;
        var high = points.Count - 1;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (points[middle].Rpm <= rpm) low = middle;
            else high = middle;
        }
        var a = points[low];
        var b = points[high];
        return a.Torque + (b.Torque - a.Torque) * ((rpm - a.Rpm) / (b.Rpm - a.Rpm));
    }

    private static double ReciprocalIntegral(double width, double left, double right)
    {
        if (width == 0) return 0;
        var z = (right - left) / left;
        // log(1+z)/z avoids loss of significance around constant output.
        if (Math.Abs(z) > .5) return width * (Math.Log(right) - Math.Log(left)) / (right - left);
        var factor = Math.Abs(z) < 1e-5 ? 1 - z / 2 + z * z / 3 - z * z * z / 4 : Math.Log(1 + z) / z;
        return width / left * factor;
    }

    private static void AddEquivalenceBands(List<AccelerationShiftBand> bands, double left, double right,
        double al, double ar, double bl, double br, double tolerance)
    {
        var cuts = new List<double> { left, right };
        foreach (var threshold in new[] { 1 - tolerance, 1 + tolerance })
        {
            var dl = bl - threshold * al;
            var dr = br - threshold * ar;
            if (dl * dr < 0) cuts.Add(left + (right - left) * (-dl) / (dr - dl));
        }
        cuts.Sort();
        for (var i = 0; i + 1 < cuts.Count; i++)
        {
            var t = ((cuts[i] + cuts[i + 1]) * .5 - left) / (right - left);
            var ratio = (bl + (br - bl) * t) / (al + (ar - al) * t);
            if (ratio >= 1 - tolerance - ForceRelativeTolerance && ratio <= 1 + tolerance + ForceRelativeTolerance)
                bands.Add(new(cuts[i], cuts[i + 1]));
        }
    }

    private static IEnumerable<AccelerationShiftBand> MergeBands(List<AccelerationShiftBand> bands)
    {
        if (bands.Count == 0) yield break;
        var current = bands[0];
        foreach (var next in bands.Skip(1))
        {
            if (next.MinimumRpm <= current.MaximumRpm + RpmTolerance)
                current = new(current.MinimumRpm, Math.Max(current.MaximumRpm, next.MaximumRpm));
            else
            {
                yield return current;
                current = next;
            }
        }
        yield return current;
    }

    private readonly record struct Segment(double Left, double Right, double Al, double Ar, double Bl, double Br, double Prefix)
    {
        public double Objective(double rpm)
        {
            var t = (rpm - Left) / (Right - Left);
            return Prefix + ReciprocalIntegral(rpm - Left, Al, Al + (Ar - Al) * t)
                - ReciprocalIntegral(rpm - Left, Bl, Bl + (Br - Bl) * t);
        }
    }
}
