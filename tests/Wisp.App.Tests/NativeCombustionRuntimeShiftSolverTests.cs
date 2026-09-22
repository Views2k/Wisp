using System.Buffers.Binary;
using System.Text.Json;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeCombustionRuntimeShiftSolverTests
{
    private const double RadiansToRpm = 30 / Math.PI;

    [Fact]
    public void CapturedC7MatchesIndependentContinuousRootsInEveryForwardGear()
    {
        var fixture = Fixture();
        var modifiers = ModifierBytes(fixture.ModifierBits);
        Assert.True(NativeCombustionRuntimeCurve.TryCreate(fixture.Raw, fixture.Step, fixture.InverseStep,
            fixture.Redline, fixture.OperatingCeiling, modifiers, out var curve));
        var samples = fixture.Raw.Select((_, i) =>
        {
            Assert.True(curve!.TryEvaluate(i * fixture.Step, out var torque));
            return new AccelerationShiftSample(i * (double)fixture.Step * RadiansToRpm, torque * 100d);
        }).ToArray();
        var profile = new AccelerationShiftProfile(samples, fixture.ForwardRatios,
            fixture.GearAccelerationFactors, fixture.OperatingCeiling * RadiansToRpm);
        foreach (var expected in fixture.Expected)
        {
            var result = NativeCombustionRuntimeShiftSolver.Solve(curve, profile, expected.Gear,
                Math.Max(500, fixture.Redline * RadiansToRpm * .3));
            Assert.Equal(Enum.Parse<AccelerationShiftStatus>(expected.Status), result.Status);
            Assert.True(result.HasEstimatedTarget);
            Assert.Equal(expected.TargetRpm, result.EstimatedTargetRpm!.Value, 6);
            Assert.Equal(expected.IntersectionsRpm.Length, result.IntersectionRpms.Count);
            for (var i = 0; i < expected.IntersectionsRpm.Length; i++)
                Assert.Equal(expected.IntersectionsRpm[i], result.IntersectionRpms[i], 6);
            Assert.True(result.EstimatedTargetRpm <= profile.VerifiedOperatingCeilingRpm);
        }
        Assert.Equal(AccelerationShiftStatus.NoNextGear,
            NativeCombustionRuntimeShiftSolver.Solve(curve, profile, 7).Status);
    }

    [Fact]
    public void CapturedC7RuntimeLawChangesFifthAndSixthTargetsWithoutChangingItsLimiter()
    {
        var fixture = Fixture();
        Assert.True(NativeCombustionCurve.TryCreate(fixture.Raw, fixture.Step, fixture.InverseStep,
            fixture.Redline, ModifierBytes(fixture.ModifierBits), out var exported));
        var ceiling = fixture.OperatingCeiling * RadiansToRpm;
        var oldProfile = new AccelerationShiftProfile(exported!.Samples.Select((torque, i) =>
            new AccelerationShiftSample(i * (double)fixture.Step * RadiansToRpm, torque * 100d)),
            fixture.ForwardRatios, fixture.GearAccelerationFactors, ceiling);
        foreach (var gear in new[] { 5, 6 })
        {
            var old = AccelerationShiftSolver.Solve(oldProfile, gear, fixture.Redline * RadiansToRpm * .3);
            Assert.Equal(AccelerationShiftStatus.VerifiedLimitBound, old.Status);
            Assert.Equal(ceiling, old.EstimatedTargetRpm);
            Assert.True(fixture.Expected[gear - 1].TargetRpm < ceiling);
        }
    }

    [Fact]
    public void ContinuousLinearTorqueCrossesInsideAnUnsampledInterval()
    {
        // T(w)=10-w, next ratio=1/2: next-current=-5+3w/4.
        var result = Synthetic(new(0, 9, 10, -1, 0, 0), 2);
        Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, result.Status);
        Assert.Equal((20d / 3) * RadiansToRpm, result.EstimatedTargetRpm!.Value, 8);
    }

    [Fact]
    public void DerivativePartitionsFindThreeHiddenCubicAdvantageChanges()
    {
        // next-current=(w-2)(w-3)(w-4). Endpoints alone suggest one
        // crossover, while the interior contains two further sign changes.
        var result = Synthetic(new(0, 4.5, 48, -104d / 3, 72d / 7, -16d / 15), 1.5);
        Assert.Equal(AccelerationShiftStatus.NonMonotonicAdvantage, result.Status);
        Assert.Null(result.EstimatedTargetRpm);
        Assert.Null(result.ModelCandidateRpm);
        Assert.Equal(3, result.IntersectionRpms.Count);
        for (var i = 0; i < 3; i++) Assert.Equal((i + 2) * RadiansToRpm, result.IntersectionRpms[i], 8);
    }

    [Fact]
    public void TangentIntersectionDoesNotInventAnAdvantageChange()
    {
        // next-current=-(w-3)^2: no beneficial upshift before the limit.
        var result = Synthetic(new(0, 5, 18, -8, 8d / 7, 0), 1);
        Assert.Equal(AccelerationShiftStatus.VerifiedLimitBound, result.Status);
        Assert.Equal(5 * RadiansToRpm, result.EstimatedTargetRpm);
        Assert.Equal(3 * RadiansToRpm, Assert.Single(result.IntersectionRpms), 8);
    }

    [Fact]
    public void EarlyAdvantageDoesNotInventALaterShiftTarget()
    {
        var result = Synthetic(new(0, 9, 10, -1, 0, 0), 7);
        Assert.Equal(AccelerationShiftStatus.AlreadyNextGearBeneficial, result.Status);
        Assert.Null(result.EstimatedTargetRpm);
        Assert.Equal(7 * RadiansToRpm, result.ModelCandidateRpm);
    }

    [Fact]
    public void InteriorNonPositiveTorqueIsRejectedEvenWithPositiveEndpoints()
    {
        var result = Synthetic(new(0, 4, 8.9, -6, 1, 0), 2);
        Assert.Equal(AccelerationShiftStatus.NonPositiveOutput, result.Status);
        Assert.Null(result.EstimatedTargetRpm);
    }

    [Fact]
    public void EqualOutputPlateauDoesNotSelectAnArbitraryPoint()
    {
        var segment = new NativeCombustionRuntimeSegment(0, 5, 2, 0, 0, 0);
        var result = NativeCombustionRuntimeShiftSolver.SolveSegments([segment], 5, 5,
            Profile(5, factors: [1, 2]), 1, RadiansToRpm);
        Assert.Equal(AccelerationShiftStatus.EqualOutputPlateau, result.Status);
        Assert.Null(result.EstimatedTargetRpm);
    }

    [Fact]
    public void SlightlyLowerCallerCapIsNotPromotedToVerifiedEngineLimit()
    {
        var segment = new NativeCombustionRuntimeSegment(0, 5, 2, 0, 0, 0);
        var result = NativeCombustionRuntimeShiftSolver.SolveSegments([segment], 5, 5,
            Profile(5), 1, RadiansToRpm, 5 * RadiansToRpm - 1e-7);
        Assert.Equal(AccelerationShiftStatus.NoCrossoverInDomain, result.Status);
        Assert.Null(result.EstimatedTargetRpm);
    }

    [Fact]
    public void UnsupportedUpperDomainDoesNotBecomeALimiterWithoutVerifiedMetadata()
    {
        var segment = new NativeCombustionRuntimeSegment(0, 5, 2, 0, 0, 0);
        var result = NativeCombustionRuntimeShiftSolver.SolveSegments([segment], 5, 5,
            Profile(5, verified: false), 1, RadiansToRpm);
        Assert.Equal(AccelerationShiftStatus.NoCrossoverInDomain, result.Status);
        Assert.False(result.OperatingCeilingVerified);
    }

    [Fact]
    public void StrictNativeLimitUsesPositiveLeftBranchAndNeverQueriesTheCutBranch()
    {
        NativeCombustionRuntimeSegment[] segments = [new(0, 5, 2, 0, 0, 0), new(5, 10, 0, 0, 0, 0)];
        var result = NativeCombustionRuntimeShiftSolver.SolveSegments(segments, 10, 5,
            Profile(5), 1, RadiansToRpm);
        Assert.Equal(AccelerationShiftStatus.VerifiedLimitBound, result.Status);
        Assert.Equal(5 * RadiansToRpm, result.EstimatedTargetRpm);
    }

    [Fact]
    public void GapInPolynomialDomainIsRejected()
    {
        NativeCombustionRuntimeSegment[] segments = [new(0, 2, 2, 0, 0, 0), new(3, 5, 2, 0, 0, 0)];
        var result = NativeCombustionRuntimeShiftSolver.SolveSegments(segments, 5, 5,
            Profile(5), 1, RadiansToRpm);
        Assert.Equal(AccelerationShiftStatus.InvalidProfile, result.Status);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    public void InvalidRequestedLowerDomainHasNoTarget(double minimum)
    {
        var segment = new NativeCombustionRuntimeSegment(0, 5, 2, 0, 0, 0);
        var result = NativeCombustionRuntimeShiftSolver.SolveSegments([segment], 5, 5,
            Profile(5), 1, minimum);
        Assert.Equal(AccelerationShiftStatus.InvalidProfile, result.Status);
        Assert.False(result.HasEstimatedTarget);
    }

    private static AccelerationShiftResult Synthetic(NativeCombustionRuntimeSegment segment, double lowerOmega) =>
        NativeCombustionRuntimeShiftSolver.SolveSegments([segment], segment.MaximumOmega, segment.MaximumOmega,
            Profile(segment.MaximumOmega), 1, lowerOmega * RadiansToRpm);

    private static AccelerationShiftProfile Profile(double maximumOmega, bool verified = true, double[]? factors = null) =>
        new([new(0, 1), new(maximumOmega * RadiansToRpm, 1)], [1, .5], factors,
            verified ? maximumOmega * RadiansToRpm : null);

    private static byte[] ModifierBytes(uint[] bits)
    {
        var bytes = new byte[bits.Length * sizeof(uint)];
        for (var i = 0; i < bits.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint), sizeof(uint)), bits[i]);
        return bytes;
    }

    private static RecordedCurve Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Wisp.App", "Wisp.App.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return JsonSerializer.Deserialize<RecordedCurve>(File.ReadAllText(Path.Combine(directory.FullName,
            "tests", "Wisp.App.Tests", "Fixtures", "ShiftCue", "runtime-ae0-c7-2177.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed record RecordedCurve(float[] Raw, float Step, float InverseStep, float Redline,
        float OperatingCeiling, uint[] ModifierBits, double[] ForwardRatios, double[] GearAccelerationFactors,
        ExpectedTarget[] Expected);
    private sealed record ExpectedTarget(int Gear, string Status, double TargetRpm, double[] IntersectionsRpm);
}
