using Xunit;

namespace Wisp.Core.Tests;

// Synthetic checks of the locked-driveline, positive-force, one-shift model.
// These do not establish Forza physics, transient output, or driver-command timing.
public sealed class AccelerationShiftDifferentialTests
{
    [Fact]
    public void SeededCurvesAndGearboxesAgreeWithIndependentNumericalTravelTimes()
    {
        var statuses = new HashSet<AccelerationShiftStatus>();
        var compared = 0;
        var multipleCrossings = 0;
        foreach (var example in Examples())
        {
            for (var gear = 1; gear < example.Profile.ForwardRatios.Count; gear++)
            {
                var result = AccelerationShiftSolver.Solve(example.Profile, gear, example.Lower, example.Cap);
                var label = $"case {example.Id}, gear {gear}, status {result.Status}";
                var expectedLower = Math.Max(example.Lower,
                    example.Profile.Samples[0].Rpm * example.Profile.ForwardRatios[gear - 1] / example.Profile.ForwardRatios[gear]);
                var expectedUpper = Math.Min(example.Profile.Samples[^1].Rpm,
                    Math.Min(example.Cap ?? double.PositiveInfinity,
                        example.Profile.VerifiedOperatingCeilingRpm ?? double.PositiveInfinity));
                Assert.InRange(Math.Abs(result.MinimumAnalysisRpm - expectedLower), 0, 1e-8);
                Assert.Equal(expectedUpper, result.MaximumAnalysisRpm);
                CheckNumericalMinimum(example.Profile, gear, result, label);
                statuses.Add(result.Status);
                if (result.IntersectionRpms.Count > 1) multipleCrossings++;
                compared++;
            }
        }
        Assert.True(compared >= 300);
        Assert.True(multipleCrossings >= 8, $"Only {multipleCrossings} multiple-crossing comparisons exercised.");
        Assert.Contains(AccelerationShiftStatus.EstimatedCrossover, statuses);
        Assert.Contains(AccelerationShiftStatus.VerifiedLimitBound, statuses);
        Assert.Contains(AccelerationShiftStatus.NoCrossoverInDomain, statuses);
        Assert.Contains(AccelerationShiftStatus.NonMonotonicAdvantage, statuses);
    }

    [Fact]
    public void TorqueAndCommonRatioScalesIndependentlyPreserveDecisionsAndBands()
    {
        foreach (var example in Examples().Where(example => example.Id < 24))
        {
            for (var gear = 1; gear < example.Profile.ForwardRatios.Count; gear++)
            {
                var original = AccelerationShiftSolver.Solve(example.Profile, gear, example.Lower, example.Cap);
                foreach (var scale in new[] { .001, .1, 10, 1000 })
                {
                    var torqueScaled = new AccelerationShiftProfile(
                        example.Profile.Samples.Select(point => point with { Torque = point.Torque * scale }),
                        example.Profile.ForwardRatios, example.Profile.GearAccelerationFactors,
                        example.Profile.VerifiedOperatingCeilingRpm);
                    var ratioScaled = new AccelerationShiftProfile(example.Profile.Samples,
                        example.Profile.ForwardRatios.Select(ratio => ratio * scale),
                        example.Profile.GearAccelerationFactors, example.Profile.VerifiedOperatingCeilingRpm);
                    AssertEquivalent(original, AccelerationShiftSolver.Solve(torqueScaled, gear, example.Lower, example.Cap));
                    AssertEquivalent(original, AccelerationShiftSolver.Solve(ratioScaled, gear, example.Lower, example.Cap));
                }
            }
        }
    }

    [Fact]
    public void WideToCloseGearSpacingMovesTheNumericallyOptimalShiftEarlier()
    {
        double previous = double.PositiveInfinity;
        foreach (var ratio in new[] { .25, .4, .55, .7, .85, .95, .99, .999 })
        {
            var profile = new AccelerationShiftProfile([new(0, 1000), new(19000, 50)], [1, ratio]);
            var result = AccelerationShiftSolver.Solve(profile, 1, 1000, 18500);
            Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, result.Status);
            Assert.True(result.EstimatedTargetRpm!.Value < previous);
            CheckNumericalMinimum(profile, 1, result, $"spacing {ratio}");
            previous = result.EstimatedTargetRpm.Value;
        }
    }

    [Theory]
    [InlineData(.4)]
    [InlineData(.5)]
    [InlineData(.7)]
    public void NumericallyFlatTravelTimeNeverClaimsOneUniqueShiftRpm(double ratio)
    {
        var ceiling = Math.Min(10000, 4000 / ratio) - 50;
        var profile = new AccelerationShiftProfile(
            [new(0, 700), new(4000, 700), new(5000, 700 * ratio), new(11000, 700 * ratio)],
            [1, ratio], verifiedOperatingCeilingRpm: ceiling);
        var result = AccelerationShiftSolver.Solve(profile, 1, 5000);
        Assert.Equal(AccelerationShiftStatus.EqualOutputPlateau, result.Status);
        Assert.False(result.HasEstimatedTarget);
        var oracle = new TravelTimeOracle(profile, 1, 5000, ceiling, 8192);
        Assert.InRange(oracle.MaximumTime - oracle.MinimumTime, 0, oracle.MinimumTime * 1e-10);
        CheckNumericalMinimum(profile, 1, result, $"plateau spacing {ratio}");
    }

    [Fact]
    public void SourceCoverageCallerCapsAndNonpositiveTailsCannotImpersonateTheVerifiedLimit()
    {
        const double ceiling = 8000;
        foreach (var positiveEnd in new[] { 6000d, 8000d, 10000d })
            foreach (var callerCap in new double?[] { null, 5500, 7999.9999999, 8000, 9500 })
                foreach (var tail in new double?[] { null, 0, -200 })
                {
                    var samples = new List<AccelerationShiftSample> { new(500, 200), new(positiveEnd, 200) };
                    if (tail.HasValue) samples.Add(new(positiveEnd + 1000, tail.Value));
                    var profile = new AccelerationShiftProfile(samples, [2, 1], verifiedOperatingCeilingRpm: ceiling);
                    var result = AccelerationShiftSolver.Solve(profile, 1, 600, callerCap);
                    var upper = Math.Min(positiveEnd, Math.Min(ceiling, callerCap ?? double.PositiveInfinity));
                    Assert.Equal(1000, result.MinimumAnalysisRpm); // Next gear must have source coverage too.
                    Assert.Equal(upper, result.MaximumAnalysisRpm);
                    Assert.Equal(upper, result.ModelCandidateRpm);
                    Assert.Equal(upper == ceiling, result.HasEstimatedTarget);
                    Assert.Equal(upper == ceiling ? AccelerationShiftStatus.VerifiedLimitBound :
                        AccelerationShiftStatus.NoCrossoverInDomain, result.Status);
                }
    }

    private static void CheckNumericalMinimum(AccelerationShiftProfile profile, int gear,
        AccelerationShiftResult result, string label)
    {
        Assert.True(result.ModelCandidateRpm.HasValue, label);
        var coarse = new TravelTimeOracle(profile, gear, result.MinimumAnalysisRpm, result.MaximumAnalysisRpm, 4096);
        var fine = new TravelTimeOracle(profile, gear, result.MinimumAnalysisRpm, result.MaximumAnalysisRpm, 8192);
        var candidate = result.ModelCandidateRpm!.Value;
        Assert.InRange(candidate, result.MinimumAnalysisRpm, result.MaximumAnalysisRpm);
        // Refine the midpoint integration independently. The tolerance covers
        // quadrature/grid error, not uncertainty in real vehicle performance.
        var tolerance = Math.Max(fine.MinimumTime * 2e-7,
            8 * (Math.Abs(fine.MinimumTime - coarse.MinimumTime) + Math.Abs(fine.TimeAt(candidate) - coarse.TimeAt(candidate))));
        Assert.True(fine.TimeAt(candidate) <= fine.MinimumTime + tolerance,
            $"{label}: candidate {candidate:R} time {fine.TimeAt(candidate):R}, numeric minimum {fine.MinimumTime:R}, tolerance {tolerance:R}");
        foreach (var intersection in result.IntersectionRpms)
        {
            var (current, next) = fine.Forces(intersection);
            Assert.InRange(Math.Abs(current - next) / Math.Max(current, next), 0, 1e-8);
        }
        if (result.Status == AccelerationShiftStatus.EstimatedCrossover)
        {
            Assert.Equal(candidate, result.EstimatedTargetRpm);
            var (current, next) = fine.Forces(candidate);
            Assert.InRange(Math.Abs(current - next) / Math.Max(current, next), 0, 1e-8);
            for (var i = 1; i <= 512; i++)
            {
                var (laterCurrent, laterNext) = fine.Forces(candidate + (result.MaximumAnalysisRpm - candidate) * i / 512);
                Assert.True(laterNext >= laterCurrent * (1 - 1e-9), label);
            }
        }
        else if (result.Status == AccelerationShiftStatus.VerifiedLimitBound)
        {
            Assert.Equal(profile.VerifiedOperatingCeilingRpm, result.EstimatedTargetRpm);
            Assert.Equal(result.MaximumAnalysisRpm, result.EstimatedTargetRpm);
        }
        else Assert.False(result.HasEstimatedTarget, label);
    }

    private static void AssertEquivalent(AccelerationShiftResult expected, AccelerationShiftResult actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.HasEstimatedTarget, actual.HasEstimatedTarget);
        Assert.Equal(expected.OperatingCeilingVerified, actual.OperatingCeilingVerified);
        AssertClose(expected.MinimumAnalysisRpm, actual.MinimumAnalysisRpm);
        AssertClose(expected.MaximumAnalysisRpm, actual.MaximumAnalysisRpm);
        AssertClose(expected.ModelCandidateRpm!.Value, actual.ModelCandidateRpm!.Value);
        Assert.Equal(expected.IntersectionRpms.Count, actual.IntersectionRpms.Count);
        for (var i = 0; i < expected.IntersectionRpms.Count; i++) AssertClose(expected.IntersectionRpms[i], actual.IntersectionRpms[i]);
        Assert.Equal(expected.EquivalentForceBands.Count, actual.EquivalentForceBands.Count);
        for (var i = 0; i < expected.EquivalentForceBands.Count; i++)
        {
            AssertClose(expected.EquivalentForceBands[i].MinimumRpm, actual.EquivalentForceBands[i].MinimumRpm);
            AssertClose(expected.EquivalentForceBands[i].MaximumRpm, actual.EquivalentForceBands[i].MaximumRpm);
        }
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(Math.Abs(expected - actual), 0, Math.Max(1e-7, Math.Abs(expected) * 1e-9));

    private sealed record Example(int Id, AccelerationShiftProfile Profile, double Lower, double? Cap);

    private static IEnumerable<Example> Examples()
    {
        var random = new Random(193857);
        double[] spacing = [.35, .5, .65, .8, .92, .99];
        for (var id = 0; id < 96; id++)
        {
            var maximum = 6000 + random.NextDouble() * 14000;
            var first = id % 2 == 0 ? 0 : maximum * .04;
            var amplitude = 50 + random.NextDouble() * 4500;
            var phase = random.NextDouble() * Math.PI * 2;
            var samples = new List<AccelerationShiftSample>();
            for (var i = 0; i <= 24; i++)
            {
                var x = i == 0 ? 0 : i == 24 ? 1 : (i + (random.NextDouble() - .5) * .6) / 24;
                var value = (id % 6) switch
                {
                    0 => 1,
                    1 => 1 - .94 * x,
                    2 => .25 + 1.1 * x,
                    3 => .08 + Math.Exp(-Math.Pow((x - .5) / .23, 2)),
                    4 => .65 + .5 * Math.Sin(x * 5 * Math.PI + phase),
                    _ => .12 + random.NextDouble() * 1.4
                };
                samples.Add(new(first + x * (maximum - first), amplitude * value));
            }
            var ratios = new double[2 + id % 7];
            ratios[0] = 1.5 + random.NextDouble() * 4;
            for (var gear = 1; gear < ratios.Length; gear++) ratios[gear] = ratios[gear - 1] * spacing[(id + gear) % spacing.Length];
            var factors = ratios.Select((_, gear) => id % 5 == 0 ? .7 + .025 * gear : 1).ToArray();
            double? ceiling = id % 3 == 0 ? maximum * .94 : null;
            double? cap = id % 4 == 0 ? maximum * .82 : null;
            yield return new(id, new(samples, ratios, factors, ceiling), maximum * .22, cap);
        }
    }

    // Direct numerical travel time: integrate 1/current before a candidate
    // shift and 1/next after it. No production roots, knot-union construction,
    // logarithmic integrals, or objective implementation are reused.
    private sealed class TravelTimeOracle
    {
        private readonly AccelerationShiftProfile _profile;
        private readonly int _gear;
        private readonly double _lower, _step;
        private readonly double[] _currentPrefix, _nextSuffix;

        internal TravelTimeOracle(AccelerationShiftProfile profile, int gear, double lower, double upper, int cells)
        {
            _profile = profile;
            _gear = gear;
            _lower = lower;
            _step = (upper - lower) / cells;
            _currentPrefix = new double[cells + 1];
            _nextSuffix = new double[cells + 1];
            var nextTimes = new double[cells];
            for (var i = 0; i < cells; i++)
            {
                var (current, next) = Forces(lower + (i + .5) * _step);
                Assert.True(current > 0 && next > 0);
                _currentPrefix[i + 1] = _currentPrefix[i] + _step / current;
                nextTimes[i] = _step / next;
            }
            for (var i = cells - 1; i >= 0; i--) _nextSuffix[i] = _nextSuffix[i + 1] + nextTimes[i];
            var times = Enumerable.Range(0, cells + 1).Select(i => _currentPrefix[i] + _nextSuffix[i]).ToArray();
            MinimumTime = times.Min();
            MaximumTime = times.Max();
        }

        internal double MinimumTime { get; }
        internal double MaximumTime { get; }

        internal double TimeAt(double rpm)
        {
            var i = Math.Clamp((int)((rpm - _lower) / _step), 0, _currentPrefix.Length - 2);
            var left = _lower + i * _step;
            var right = left + _step;
            return _currentPrefix[i] + Math.Max(0, rpm - left) / Forces((left + rpm) / 2).Current
                + _nextSuffix[i + 1] + Math.Max(0, right - rpm) / Forces((rpm + right) / 2).Next;
        }

        internal (double Current, double Next) Forces(double rpm)
        {
            var currentRatio = _profile.ForwardRatios[_gear - 1];
            var nextRatio = _profile.ForwardRatios[_gear];
            return (Torque(rpm) * currentRatio * _profile.GearAccelerationFactors[_gear - 1],
                Torque(rpm * nextRatio / currentRatio) * nextRatio * _profile.GearAccelerationFactors[_gear]);
        }

        private double Torque(double rpm)
        {
            // Linear scan and endpoint-weighted interpolation intentionally
            // differ from the production binary search and slope calculation.
            var points = _profile.Samples;
            Assert.InRange(rpm, points[0].Rpm - 1e-7, points[^1].Rpm + 1e-7);
            var i = 1;
            while (i < points.Count - 1 && points[i].Rpm < rpm) i++;
            var lower = points[i - 1];
            var upper = points[i];
            return (lower.Torque * (upper.Rpm - rpm) + upper.Torque * (rpm - lower.Rpm)) / (upper.Rpm - lower.Rpm);
        }
    }
}
