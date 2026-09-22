using Xunit;

namespace Wisp.Core.Tests;

public sealed class AccelerationShiftSolverTests
{
    private static AccelerationShiftProfile Falling(double? ceiling = null, double[]? factors = null) =>
        new([new(0, 1000), new(9000, 100)], [2, 1], factors, ceiling);

    [Fact]
    public void AnalyticalTorqueCrossoverIsDistinctFromPeakHorsepower()
    {
        var result = AccelerationShiftSolver.Solve(Falling(), 1, 1000, 8500);
        Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, result.Status);
        Assert.Equal(20000d / 3, result.EstimatedTargetRpm!.Value, 6);
        Assert.True(result.EstimatedTargetRpm > 5000); // Exact peak of RPM*(1000-RPM/10).
        Assert.False(result.OperatingCeilingVerified);
        Assert.Single(result.IntersectionRpms);
    }

    [Fact]
    public void FiniteInputDomainDoesNotBecomeAFakeLimiterTarget()
    {
        var profile = new AccelerationShiftProfile([new(0, 200), new(10000, 200)], [2, 1]);
        var result = AccelerationShiftSolver.Solve(profile, 1, 1000, 9500);
        Assert.Equal(AccelerationShiftStatus.NoCrossoverInDomain, result.Status);
        Assert.Null(result.EstimatedTargetRpm);
        Assert.Equal(9500, result.ModelCandidateRpm);
        Assert.False(result.HasEstimatedTarget);
    }

    [Fact]
    public void VerifiedOperatingCeilingProducesADistinctConstrainedTargetBeforeCrossover()
    {
        var result = AccelerationShiftSolver.Solve(Falling(6000), 1, 1000, 8500);
        Assert.Equal(AccelerationShiftStatus.VerifiedLimitBound, result.Status);
        Assert.True(result.OperatingCeilingVerified);
        Assert.Equal(6000, result.MaximumAnalysisRpm);
        Assert.Equal(6000, result.EstimatedTargetRpm);
        Assert.True(result.HasEstimatedTarget);
        Assert.Empty(result.IntersectionRpms);
    }

    [Fact]
    public void InteriorCrossoverStillWinsWhenVerifiedCeilingIsHigher()
    {
        var result = AccelerationShiftSolver.Solve(Falling(8500), 1, 1000);
        Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, result.Status);
        Assert.Equal(20000d / 3, result.EstimatedTargetRpm!.Value, 6);
        Assert.True(result.OperatingCeilingVerified);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(8000d)]
    [InlineData(9000d)]
    public void ConstantTorqueUsesOnlyTheExactVerifiedActiveCeiling(double? callerCap)
    {
        var profile = new AccelerationShiftProfile([new(0, 200), new(10000, 200)], [2, 1],
            verifiedOperatingCeilingRpm: 8000);
        var result = AccelerationShiftSolver.Solve(profile, 1, 1000, callerCap);
        Assert.Equal(AccelerationShiftStatus.VerifiedLimitBound, result.Status);
        Assert.Equal(8000, result.EstimatedTargetRpm);
        Assert.True(result.HasEstimatedTarget);
    }

    [Theory]
    [InlineData(7000d)]
    [InlineData(7999.9999999)]
    public void CallerCapBelowVerifiedCeilingNeverBecomesALimitTarget(double callerCap)
    {
        var profile = new AccelerationShiftProfile([new(0, 200), new(10000, 200)], [2, 1],
            verifiedOperatingCeilingRpm: 8000);
        var result = AccelerationShiftSolver.Solve(profile, 1, 1000, callerCap);
        Assert.Equal(AccelerationShiftStatus.NoCrossoverInDomain, result.Status);
        Assert.Equal(callerCap, result.ModelCandidateRpm);
        Assert.Null(result.EstimatedTargetRpm);
        Assert.False(result.HasEstimatedTarget);
    }

    [Fact]
    public void IncompleteCurveBelowVerifiedCeilingCannotEstablishALimitTarget()
    {
        var profile = new AccelerationShiftProfile([new(0, 200), new(8000, 200)], [2, 1],
            verifiedOperatingCeilingRpm: 9000);
        var result = AccelerationShiftSolver.Solve(profile, 1, 1000);
        Assert.Equal(AccelerationShiftStatus.NoCrossoverInDomain, result.Status);
        Assert.Equal(8000, result.MaximumAnalysisRpm);
        Assert.Null(result.EstimatedTargetRpm);
    }

    [Theory]
    [InlineData(0d, 10000d)]
    [InlineData(-100d, 10000d)]
    [InlineData(-100d, 9500d)]
    public void NonpositiveTerminalKnotCannotEstablishACeilingOrItsInterpolation(double terminalTorque, double ceiling)
    {
        var profile = new AccelerationShiftProfile(
            [new(0, 200), new(9000, 200), new(10000, terminalTorque)], [2, 1],
            verifiedOperatingCeilingRpm: ceiling);
        var result = AccelerationShiftSolver.Solve(profile, 1, 1000);
        Assert.Equal(AccelerationShiftStatus.NoCrossoverInDomain, result.Status);
        Assert.Equal(9000, result.MaximumAnalysisRpm);
        Assert.Null(result.EstimatedTargetRpm);
    }

    [Fact]
    public void EarlierCurveCrossingsDoNotOverrideAGloballyBetterVerifiedLimit()
    {
        var profile = new AccelerationShiftProfile(
            [new(0, 400), new(4000, 400), new(4200, 100), new(4300, 100),
             new(4400, 350), new(8000, 350)], [2, 1], verifiedOperatingCeilingRpm: 8000);
        var result = AccelerationShiftSolver.Solve(profile, 1, 4000);
        Assert.Equal(2, result.IntersectionRpms.Count);
        Assert.Equal(AccelerationShiftStatus.VerifiedLimitBound, result.Status);
        Assert.Equal(8000, result.EstimatedTargetRpm);
    }

    [Fact]
    public void VerifiedCeilingDoesNotTurnAnEqualOutputPlateauIntoOneBestRpm()
    {
        var profile = new AccelerationShiftProfile(
            [new(1000, 2), new(2000, 2), new(4000, 1), new(8000, 1)], [4, 1], [.5, 1], 8000);
        var result = AccelerationShiftSolver.Solve(profile, 1, 4000);
        Assert.Equal(AccelerationShiftStatus.EqualOutputPlateau, result.Status);
        Assert.Null(result.EstimatedTargetRpm);
        Assert.False(result.HasEstimatedTarget);
    }

    [Fact]
    public void RecoveredFactorHypothesisChangesTheModelRatherThanItsRawTorqueSamples()
    {
        var raw = AccelerationShiftSolver.Solve(Falling(), 1, 1000, 8500);
        var corrected = AccelerationShiftSolver.Solve(Falling(factors: [.8, .95]), 1, 1000, 8500);
        Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, corrected.Status);
        Assert.True(corrected.EstimatedTargetRpm < raw.EstimatedTargetRpm);
        Assert.Equal((1600d - 950) / (.16 - .0475), corrected.EstimatedTargetRpm!.Value, 6);
    }

    [Theory]
    [InlineData(.001)]
    [InlineData(1)]
    [InlineData(1000)]
    public void CommonTorqueAndRatioScalingDoesNotChangeTheCrossing(double scale)
    {
        var profile = new AccelerationShiftProfile([new(0, 1000 * scale), new(9000, 100 * scale)], [10, 5]);
        Assert.Equal(20000d / 3, AccelerationShiftSolver.Solve(profile, 1, 1000, 8500).EstimatedTargetRpm!.Value, 6);
    }

    [Fact]
    public void MultipleRootsUseGlobalSingleShiftComparisonAndSuppressLaterReversals()
    {
        var profile = new AccelerationShiftProfile(
            [new(0, 400), new(4000, 400), new(4600, 100), new(5600, 100),
             new(6200, 300), new(7200, 300), new(8000, 100)], [2, 1]);
        var result = AccelerationShiftSolver.Solve(profile, 1, 4000, 8000);
        Assert.Equal(3, result.IntersectionRpms.Count);
        Assert.Equal(4400, result.ModelCandidateRpm!.Value, 6);
        Assert.Equal(AccelerationShiftStatus.NonMonotonicAdvantage, result.Status);
        Assert.Null(result.EstimatedTargetRpm);
    }

    [Fact]
    public void FirstCrossoverMayLoseToALaterGlobalStableCrossover()
    {
        var profile = new AccelerationShiftProfile(
            [new(0, 400), new(4000, 400), new(4200, 100), new(4300, 100),
             new(4400, 350), new(7600, 350), new(8000, 100)], [2, 1]);
        var result = AccelerationShiftSolver.Solve(profile, 1, 4000, 8000);
        Assert.Equal(3, result.IntersectionRpms.Count);
        Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, result.Status);
        Assert.Equal(7840, result.EstimatedTargetRpm!.Value, 6);
    }

    [Fact]
    public void AWholeEqualForceIntervalDoesNotInventOnePreciselyBestRpm()
    {
        var profile = new AccelerationShiftProfile(
            [new(1000, 2), new(2000, 2), new(4000, 1), new(8000, 1)], [4, 1], [.5, 1]);
        var result = AccelerationShiftSolver.Solve(profile, 1, 4000, 8000);
        Assert.Equal(AccelerationShiftStatus.EqualOutputPlateau, result.Status);
        Assert.Null(result.EstimatedTargetRpm);
        Assert.Equal(new AccelerationShiftBand(4000, 8000), Assert.Single(result.EquivalentForceBands));
    }

    [Fact]
    public void ForceEquivalenceBandIsReportedSeparatelyFromTheExactModelRoot()
    {
        var result = AccelerationShiftSolver.Solve(Falling(), 1, 1000, 8500);
        var band = Assert.Single(result.EquivalentForceBands);
        Assert.True(band.MinimumRpm < result.EstimatedTargetRpm);
        Assert.True(band.MaximumRpm > result.EstimatedTargetRpm);
        Assert.Equal((1980d - 1000) / (.198 - .05), band.MinimumRpm, 6);
        Assert.Equal((2020d - 1000) / (.202 - .05), band.MaximumRpm, 6);
    }

    [Fact]
    public void NegativeTailIsKeptButComparisonStopsAtLastPositiveSourceKnot()
    {
        var profile = new AccelerationShiftProfile([new(0, 1000), new(9000, 100), new(10000, -100)], [2, 1]);
        var result = AccelerationShiftSolver.Solve(profile, 1, 1000);
        Assert.True(profile.IsValid);
        Assert.Equal(9000, result.MaximumAnalysisRpm);
        Assert.Equal(20000d / 3, result.EstimatedTargetRpm!.Value, 6);
        Assert.False(result.OperatingCeilingVerified);
    }

    [Fact]
    public void MissingLowerPostshiftCurveCoverageRaisesTheAnalysisFloor()
    {
        var profile = new AccelerationShiftProfile([new(1000, 900), new(9000, 100)], [2, 1]);
        var result = AccelerationShiftSolver.Solve(profile, 1, 1000, 8500);
        Assert.Equal(2000, result.MinimumAnalysisRpm);
        Assert.Equal(20000d / 3, result.EstimatedTargetRpm!.Value, 6);
    }

    [Fact]
    public void RootBelowAnalysisWindowDoesNotInventANewTargetAtItsLowerBoundary()
    {
        var result = AccelerationShiftSolver.Solve(Falling(), 1, 7000, 8500);
        Assert.Equal(AccelerationShiftStatus.AlreadyNextGearBeneficial, result.Status);
        Assert.Null(result.EstimatedTargetRpm);
    }

    [Fact]
    public void ProfileCopiesSourceCollectionsBeforeTuneDataCanChange()
    {
        AccelerationShiftSample[] samples = [new(0, 1000), new(9000, 100)];
        double[] ratios = [2, 1];
        double[] factors = [1, 1];
        var profile = new AccelerationShiftProfile(samples, ratios, factors);
        samples[1] = new(9000, 1000);
        ratios[0] = 3;
        factors[0] = .5;
        Assert.Equal(20000d / 3, AccelerationShiftSolver.Solve(profile, 1, 1000, 8500).EstimatedTargetRpm!.Value, 6);
    }

    [Fact]
    public void EmptyOrMalformedProfileCannotProduceATarget()
    {
        AccelerationShiftProfile[] invalid =
        [
            new([], [2, 1]),
            new([new(0, 1), new(0, 2)], [2, 1]),
            new([new(0, 1), new(1000, double.NaN)], [2, 1]),
            new([new(0, 1), new(1000, 2)], [1, 2]),
            new([new(0, 1), new(1000, 2)], [2, 2]),
            new([new(0, 1), new(1000, 2)], [2, 1], [1]),
            new([new(0, 1), new(1000, 2)], [2, 1], [1, -1]),
            new([new(0, 1), new(1000, 2)], [2, 1], verifiedOperatingCeilingRpm: double.NaN)
        ];
        foreach (var profile in invalid)
        {
            var result = AccelerationShiftSolver.Solve(profile, 1, 100);
            Assert.Equal(AccelerationShiftStatus.InvalidProfile, result.Status);
            Assert.Null(result.EstimatedTargetRpm);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void NeutralReverseAndFinalGearHaveNoUpshiftTarget(int gear)
    {
        Assert.Equal(AccelerationShiftStatus.NoNextGear, AccelerationShiftSolver.Solve(Falling(), gear).Status);
    }

    [Fact]
    public void UnavailableAndNonpositiveDomainsNeverGenerateATarget()
    {
        Assert.Equal(AccelerationShiftStatus.Unavailable, AccelerationShiftSolver.Solve(null, 1).Status);
        var profile = new AccelerationShiftProfile([new(0, -1), new(4000, 1), new(9000, 2)], [2, 1]);
        var result = AccelerationShiftSolver.Solve(profile, 1, 1000, 8000);
        Assert.Equal(AccelerationShiftStatus.NonPositiveOutput, result.Status);
        Assert.Null(result.EstimatedTargetRpm);
    }

    [Fact]
    public void ExtremeFiniteValuesFailClosedWhenRatioOrObjectiveOverflows()
    {
        var ratioUnderflow = new AccelerationShiftProfile([new(0, 1000), new(9000, 100)], [1e200, 1e-200]);
        Assert.Equal(AccelerationShiftStatus.InvalidProfile, AccelerationShiftSolver.Solve(ratioUnderflow, 1).Status);
        var integralOverflow = new AccelerationShiftProfile([new(0, 1e-308), new(9000, 1e-308)], [2, 1]);
        Assert.Equal(AccelerationShiftStatus.InvalidProfile, AccelerationShiftSolver.Solve(integralOverflow, 1).Status);
    }

    [Fact]
    public void ArchivedNaturallyAspiratedSamplesMatchIndependentPythonReference()
    {
        // Technical source-index curve fragment captured during the private
        // development audit. This tests numerical agreement, not road physics.
        double[] torque =
        [
            8.16246509552002, 8.16345119476318, 8.15983200073242, 8.14900398254395, 8.13106060028076,
            8.10615062713623, 8.07448673248291, 8.03633689880371, 7.99202156066895, 7.94191837310791,
            7.88645029067993, 7.82608795166016, 7.76134347915649, 7.69276475906372, 7.62093591690063,
            7.54646682739258, 7.46999073028564, 7.39215993881226, 7.31363677978516, 7.23555326461792,
            7.15255451202393, 7.06468439102173, 6.9720630645752, 6.87488317489624, 6.77340459823608,
            6.66795301437378, 6.55891752243042, 6.44674205780029, 6.33192205429077, 6.21499967575073
        ];
        var samples = torque.Select((value, i) => new AccelerationShiftSample((70 + i) * 99.9999526946996, value));
        var profile = new AccelerationShiftProfile(samples, [1.809999942779541, 1.4579999446868896]);
        var result = AccelerationShiftSolver.Solve(profile, 1, 8800, 9760.119);
        Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, result.Status);
        Assert.Equal(9717.55754913, result.EstimatedTargetRpm!.Value, 5);
        Assert.Equal(9638.456571842295, Assert.Single(result.EquivalentForceBands).MinimumRpm, 5);

        var constrained = AccelerationShiftSolver.Solve(profile, 1, 8800, 9499.995077971274);
        Assert.Equal(AccelerationShiftStatus.NoCrossoverInDomain, constrained.Status);
        Assert.Null(constrained.EstimatedTargetRpm);
    }
}
