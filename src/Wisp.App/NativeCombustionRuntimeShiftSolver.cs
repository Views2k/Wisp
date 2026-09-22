using Wisp.Core;

namespace Wisp.App;

// Fixed-speed, full-control equilibrium comparison. Polynomial branch boundaries
// and derivative roots isolate every advantage change; a sampled graph cannot do
// that for the product of a torque curve and continuous native modifiers.
// This does not model clutch interruption, transient boost or driver-command time.
internal static class NativeCombustionRuntimeShiftSolver
{
    private const double RadiansToRpm = 30 / Math.PI;
    private const double RelativeTolerance = 1e-11;

    internal static AccelerationShiftResult Solve(NativeCombustionRuntimeCurve? curve,
        AccelerationShiftProfile? profile, int currentGear, double minimumAnalysisRpm = 2000,
        double? maximumAnalysisRpm = null) => curve is null
        ? Empty(AccelerationShiftStatus.Unavailable)
        : SolveSegments(curve.Segments, curve.MaximumOmega, curve.OperatingCeiling,
            profile, currentGear, minimumAnalysisRpm, maximumAnalysisRpm);

    // Kept separate to test analytic curves whose interior roots and extrema are
    // known independently of the native modifier parser.
    internal static AccelerationShiftResult SolveSegments(IReadOnlyList<NativeCombustionRuntimeSegment> curve,
        double maximumOmega, double operatingCeilingOmega, AccelerationShiftProfile? profile,
        int currentGear, double minimumAnalysisRpm, double? maximumAnalysisRpm = null)
    {
        if (profile is null) return Empty(AccelerationShiftStatus.Unavailable);
        if (!profile.IsValid || !Positive(minimumAnalysisRpm) || !Positive(maximumOmega) ||
            !Positive(operatingCeilingOmega) || operatingCeilingOmega > maximumOmega ||
            maximumAnalysisRpm is { } requested && (!Positive(requested) || requested <= minimumAnalysisRpm) ||
            !ValidSegments(curve, maximumOmega)) return Empty(AccelerationShiftStatus.InvalidProfile);
        if (currentGear < 1 || currentGear >= profile.ForwardRatios.Count)
            return Empty(AccelerationShiftStatus.NoNextGear);

        var currentRatio = profile.ForwardRatios[currentGear - 1];
        var nextRatio = profile.ForwardRatios[currentGear];
        var ratio = nextRatio / currentRatio;
        var currentFactor = currentRatio * profile.GearAccelerationFactors[currentGear - 1];
        var nextFactor = nextRatio * profile.GearAccelerationFactors[currentGear];
        if (!Positive(ratio) || !Positive(currentFactor) || !Positive(nextFactor))
            return Empty(AccelerationShiftStatus.InvalidProfile);

        var lower = Math.Max(minimumAnalysisRpm, curve[0].MinimumOmega * RadiansToRpm / ratio);
        var upper = Math.Min(maximumAnalysisRpm ?? double.PositiveInfinity, operatingCeilingOmega * RadiansToRpm);
        if (profile.VerifiedOperatingCeilingRpm is { } ceiling) upper = Math.Min(upper, ceiling);
        if (!double.IsFinite(lower) || upper <= lower + 1e-6)
            return Empty(AccelerationShiftStatus.InsufficientCurveDomain);

        var boundaries = new SortedSet<double> { lower, upper };
        foreach (var segment in curve)
        {
            AddBoundary(segment.MinimumOmega * RadiansToRpm);
            AddBoundary(segment.MaximumOmega * RadiansToRpm);
            AddBoundary(segment.MinimumOmega * RadiansToRpm / ratio);
            AddBoundary(segment.MaximumOmega * RadiansToRpm / ratio);
        }
        void AddBoundary(double rpm)
        {
            if (rpm > lower && rpm < upper) boundaries.Add(rpm);
        }

        var knots = boundaries.ToArray();
        var intersections = new List<double>();
        var signs = new List<int>();
        var plateau = false;
        for (var i = 0; i + 1 < knots.Length; i++)
        {
            var left = knots[i];
            var right = knots[i + 1];
            var origin = left / RadiansToRpm;
            var width = (right - left) / RadiansToRpm;
            var middle = origin + width / 2;
            var current = Normalize(Find(curve, middle), origin, width, currentFactor);
            var next = Normalize(Find(curve, middle * ratio), origin * ratio, width * ratio, nextFactor);
            if (!current.IsFinite || !next.IsFinite) return Empty(AccelerationShiftStatus.InvalidProfile);
            if (!current.IsPositive || !next.IsPositive) return Empty(AccelerationShiftStatus.NonPositiveOutput);
            var difference = next - current;
            var tolerance = RelativeTolerance * Math.Max(current.MaximumAbsoluteValue, next.MaximumAbsoluteValue);
            if (difference.MaximumAbsoluteValue <= tolerance)
            {
                plateau = true;
                continue;
            }

            // Normalize to [0,1] before solving: the original omega coefficients
            // can cancel strongly at high RPM. Derivative cuts make every root
            // bracket monotone, including crossings hidden between grid knots.
            var roots = difference.Roots(tolerance);
            intersections.AddRange(roots.Select(t => left + t * (right - left)));
            var cuts = new List<double> { 0, 1 };
            cuts.AddRange(roots.Where(t => t > 0 && t < 1));
            cuts.Sort();
            for (var j = 0; j + 1 < cuts.Count; j++)
            {
                if (cuts[j] == cuts[j + 1]) continue;
                var value = difference.Evaluate((cuts[j] + cuts[j + 1]) / 2);
                if (Math.Abs(value) <= tolerance) { plateau = true; continue; }
                var sign = Math.Sign(value);
                if (signs.Count == 0 || signs[^1] != sign) signs.Add(sign);
            }
        }

        var rootsRpm = intersections.Where(x => x > lower && x < upper).Order().ToList();
        for (var i = rootsRpm.Count - 1; i > 0; i--)
            if (rootsRpm[i] - rootsRpm[i - 1] <= 1e-6) rootsRpm.RemoveAt(i);
        var status = plateau || signs.Count == 0 ? AccelerationShiftStatus.EqualOutputPlateau :
            signs.Count > 2 || signs.Count == 2 && signs[0] > 0 ? AccelerationShiftStatus.NonMonotonicAdvantage :
            signs.Count == 1 && signs[0] > 0 ? AccelerationShiftStatus.AlreadyNextGearBeneficial :
            signs.Count == 1 ? profile.VerifiedOperatingCeilingRpm == upper
                ? AccelerationShiftStatus.VerifiedLimitBound : AccelerationShiftStatus.NoCrossoverInDomain :
            AccelerationShiftStatus.EstimatedCrossover;
        double? candidate = status switch
        {
            AccelerationShiftStatus.VerifiedLimitBound or AccelerationShiftStatus.NoCrossoverInDomain => upper,
            AccelerationShiftStatus.AlreadyNextGearBeneficial => lower,
            AccelerationShiftStatus.EstimatedCrossover => FindCrossing(rootsRpm, curve, ratio, currentFactor, nextFactor, lower, upper),
            _ => null
        };
        if (status == AccelerationShiftStatus.EstimatedCrossover && candidate is null)
            status = AccelerationShiftStatus.NonMonotonicAdvantage;
        return new(status,
            status is AccelerationShiftStatus.EstimatedCrossover or AccelerationShiftStatus.VerifiedLimitBound ? candidate : null,
            candidate, lower, upper, profile.VerifiedOperatingCeilingRpm.HasValue,
            rootsRpm.AsReadOnly(), Array.Empty<AccelerationShiftBand>());
    }

    private static double? FindCrossing(List<double> roots, IReadOnlyList<NativeCombustionRuntimeSegment> curve,
        double ratio, double currentFactor, double nextFactor, double lower, double upper)
    {
        for (var i = 0; i < roots.Count; i++)
        {
            var before = ((i == 0 ? lower : roots[i - 1]) + roots[i]) / 2 / RadiansToRpm;
            var after = (roots[i] + (i + 1 == roots.Count ? upper : roots[i + 1])) / 2 / RadiansToRpm;
            double Difference(double omega) => Find(curve, omega * ratio).Evaluate(omega * ratio) * nextFactor -
                Find(curve, omega).Evaluate(omega) * currentFactor;
            if (Difference(before) < 0 && Difference(after) > 0) return roots[i];
        }
        return null;
    }

    private static bool ValidSegments(IReadOnlyList<NativeCombustionRuntimeSegment> segments, double maximum)
    {
        if (segments.Count == 0 || segments[0].MinimumOmega != 0 || segments[^1].MaximumOmega != maximum) return false;
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (!double.IsFinite(s.MinimumOmega) || !double.IsFinite(s.MaximumOmega) || s.MaximumOmega <= s.MinimumOmega ||
                !double.IsFinite(s.C0) || !double.IsFinite(s.C1) || !double.IsFinite(s.C2) || !double.IsFinite(s.C3) ||
                i > 0 && segments[i - 1].MaximumOmega != s.MinimumOmega) return false;
        }
        return true;
    }

    private static NativeCombustionRuntimeSegment Find(IReadOnlyList<NativeCombustionRuntimeSegment> curve, double omega)
    {
        var low = 0;
        var high = curve.Count - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (omega > curve[middle].MaximumOmega) low = middle + 1;
            else high = middle;
        }
        return curve[low];
    }

    private static Polynomial Normalize(NativeCombustionRuntimeSegment s, double origin, double width, double factor) => new(
        s.Evaluate(origin) * factor,
        width * (s.C1 + 2 * s.C2 * origin + 3 * s.C3 * origin * origin) * factor,
        width * width * (s.C2 + 3 * s.C3 * origin) * factor,
        width * width * width * s.C3 * factor);

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static AccelerationShiftResult Empty(AccelerationShiftStatus status) =>
        new(status, null, null, 0, 0, false, Array.Empty<double>(), Array.Empty<AccelerationShiftBand>());

    private readonly record struct Polynomial(double C0, double C1, double C2, double C3)
    {
        internal double Evaluate(double x) => ((C3 * x + C2) * x + C1) * x + C0;
        internal bool IsFinite => double.IsFinite(C0) && double.IsFinite(C1) && double.IsFinite(C2) && double.IsFinite(C3);
        internal bool IsPositive
        {
            get
            {
                foreach (var x in Extrema()) if (!Positive(Evaluate(x))) return false;
                return true;
            }
        }
        internal double MaximumAbsoluteValue
        {
            get
            {
                var maximum = 0d;
                foreach (var x in Extrema()) maximum = Math.Max(maximum, Math.Abs(Evaluate(x)));
                return maximum;
            }
        }
        public static Polynomial operator -(Polynomial a, Polynomial b) => new(a.C0 - b.C0, a.C1 - b.C1, a.C2 - b.C2, a.C3 - b.C3);

        private List<double> Extrema()
        {
            var points = new List<double> { 0, 1 };
            var scale = Math.Max(Math.Abs(C1), Math.Max(Math.Abs(C2), Math.Abs(C3)));
            if (scale == 0) return points;
            var a = 3 * (C3 / scale);
            var b = 2 * (C2 / scale);
            var c = C1 / scale;
            void Add(double x) { if (double.IsFinite(x) && x > 0 && x < 1) points.Add(x); }
            if (a == 0)
            {
                if (b != 0) Add(-c / b);
            }
            else
            {
                var discriminant = b * b - 4 * a * c;
                if (discriminant >= 0)
                {
                    var q = -.5 * (b + Math.CopySign(Math.Sqrt(discriminant), b));
                    if (q == 0) Add(-b / (2 * a));
                    else { Add(q / a); Add(c / q); }
                }
            }
            points.Sort();
            return points;
        }

        internal List<double> Roots(double tolerance)
        {
            var cuts = Extrema();
            var roots = new List<double>();
            foreach (var x in cuts) if (Math.Abs(Evaluate(x)) <= tolerance) roots.Add(x);
            for (var i = 0; i + 1 < cuts.Count; i++)
            {
                var left = cuts[i];
                var right = cuts[i + 1];
                var atLeft = Evaluate(left);
                var atRight = Evaluate(right);
                if (Math.Abs(atLeft) <= tolerance || Math.Abs(atRight) <= tolerance || Math.Sign(atLeft) == Math.Sign(atRight)) continue;
                for (var iteration = 0; iteration < 64 && right - left > 1e-13; iteration++)
                {
                    var middle = (left + right) / 2;
                    var value = Evaluate(middle);
                    if (value == 0) { left = right = middle; break; }
                    if (Math.Sign(value) == Math.Sign(atLeft)) left = middle;
                    else right = middle;
                }
                roots.Add((left + right) / 2);
            }
            roots.Sort();
            return roots;
        }
    }
}
