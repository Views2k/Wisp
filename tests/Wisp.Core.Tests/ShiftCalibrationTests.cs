using Xunit;

namespace Wisp.Core.Tests;

public sealed class ShiftCalibrationTests
{
    private const long Frequency = 1_000;
    private const string Fingerprint = "same-car-tune";
    private static ShiftCalibrationContext Context(double? ceiling = 9_000, double[]? ratios = null) =>
        new(100, Fingerprint, ratios ?? [2, 1], configuredOperatingCeilingRpm: ceiling);

    [Fact]
    public void MeasuredDecliningCurveProducesInteriorCrossoverAfterIndependentGearConfirmation()
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 8_500, Falling);
        var before = session.Evaluate();
        Assert.Equal(ShiftCalibrationStatus.NeedConfirmingUpshift, before.Status);
        Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, before.Gears[0].Status);
        Assert.Equal(20_000d / 3, before.Gears[0].EstimatedTargetRpm!.Value, 3);
        Assert.Null(before.EmpiricalUpperRpm);
        Sweep(session, ref time, 2, 4_300, 6_400, Falling);
        var result = session.Evaluate();
        Assert.True(result.Ready, result.Reason);
        Assert.Equal(1, result.ConfirmingUpshifts);
        Assert.Equal(AccelerationShiftStatus.NoNextGear, result.Gears[1].Status);
        Assert.Null(result.Profile!.VerifiedOperatingCeilingRpm);
        Assert.False(result.Gears[0].OperatingCeilingVerified);
    }

    [Fact]
    public void DifferentRatiosProduceDifferentMeasuredTargets()
    {
        var samples = Enumerable.Range(0, 77).Select(i => new AccelerationShiftSample(1_000 + i * 100,
            Falling(1_000 + i * 100))).ToArray();
        Assert.True(ShiftCalibrationSession.TryRestore(Context(ratios: [3, 2, 1]), samples,
            null, 1, 400, out var restored));
        Assert.NotEqual(restored!.Gears[0].EstimatedTargetRpm, restored.Gears[1].EstimatedTargetRpm);
        Assert.Equal(6_000, restored.Gears[0].EstimatedTargetRpm!.Value, 5);
        Assert.Equal(20_000d / 3, restored.Gears[1].EstimatedTargetRpm!.Value, 5);
    }

    [Fact]
    public void SampledMaximumAndNativeCeilingAloneDoNotCreateLimitTarget()
    {
        var session = new ShiftCalibrationSession(Context(8_000), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 7_980, Constant);
        Sweep(session, ref time, 2, 4_000, 6_000, Constant);
        var result = session.Evaluate();
        Assert.False(result.Ready);
        Assert.Equal(AccelerationShiftStatus.NoCrossoverInDomain, result.Gears[0].Status);
        Assert.Null(result.Gears[0].EstimatedTargetRpm);
        Assert.Null(result.EmpiricalUpperRpm);
        Assert.Equal(8_000, result.ConfiguredOperatingCeilingRpm);
    }

    [Fact]
    public void ObservedNativeCutCreatesConservativeMeasuredBoundaryNotVerifiedCeiling()
    {
        var session = new ShiftCalibrationSession(Context(8_000), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 7_980, Constant);
        Cut(session, ref time, 1, 8_000, holdSameGear: true);
        Sweep(session, ref time, 2, 4_000, 6_000, Constant);
        var result = session.Evaluate();
        Assert.True(result.Ready, result.Reason);
        Assert.Equal(7_980, result.EmpiricalUpperRpm);
        Assert.Equal(AccelerationShiftStatus.MeasuredUpperBoundary, result.Gears[0].Status);
        Assert.Equal(7_980, result.Gears[0].EstimatedTargetRpm);
        Assert.True(result.Gears[0].HasEstimatedTarget);
        Assert.False(result.Gears[0].OperatingCeilingVerified);
        Assert.Null(result.Profile!.VerifiedOperatingCeilingRpm);
    }

    [Fact]
    public void NativeCutImmediatelyFollowedByUpshiftDoesNotCreateBoundary()
    {
        var session = new ShiftCalibrationSession(Context(8_000), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 7_980, Constant);
        Cut(session, ref time, 1, 8_000, holdSameGear: false);
        Sweep(session, ref time, 2, 4_000, 6_000, Constant);
        Assert.Null(session.Evaluate().EmpiricalUpperRpm);
        Assert.False(session.Evaluate().Ready);
    }

    [Fact]
    public void TorqueCutBeforeNativeFlagRetainsLastQualifiedPositiveBoundary()
    {
        var session = new ShiftCalibrationSession(Context(8_000), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 7_980, Constant);
        for (var i = 0; i < 3; i++)
        {
            session.Observe(State(time, 1, 7_950 - i * 10, 0), Fingerprint, time, outputSettled: false);
            time += 10;
        }
        Cut(session, ref time, 1, 7_900, holdSameGear: true);
        Sweep(session, ref time, 2, 4_000, 6_000, Constant);
        var result = session.Evaluate();
        Assert.True(result.Ready, result.Reason);
        Assert.Equal(7_980, result.EmpiricalUpperRpm);
        Assert.Equal(AccelerationShiftStatus.MeasuredUpperBoundary, result.Gears[0].Status);
        Assert.False(result.Gears[0].OperatingCeilingVerified);
    }

    [Fact]
    public void UnrelatedNativeLimiterFlagAfterContinuityGapCannotSupplyBoundary()
    {
        var session = new ShiftCalibrationSession(Context(8_000), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 7_980, Constant);
        time += 150;
        Cut(session, ref time, 1, 7_900, holdSameGear: true);
        Sweep(session, ref time, 2, 4_000, 6_000, Constant);
        Assert.Null(session.Evaluate().EmpiricalUpperRpm);
        Assert.False(session.Evaluate().Ready);
    }

    [Fact]
    public void UnclassifiedTorqueInterruptionDoesNotBecomeLimiter()
    {
        var session = new ShiftCalibrationSession(Context(8_000), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 7_980, Constant);
        for (var i = 0; i < 20; i++) Add(session, ref time, 1, 7_950, 0);
        Sweep(session, ref time, 2, 4_000, 6_000, Constant);
        var result = session.Evaluate();
        Assert.Null(result.EmpiricalUpperRpm);
        Assert.False(result.Ready);
    }

    [Fact]
    public void InsufficientPostShiftDomainDoesNotExtrapolate()
    {
        var samples = Enumerable.Range(0, 30).Select(i => new AccelerationShiftSample(5_000 + i * 100,
            Falling(5_000 + i * 100))).ToArray();
        Assert.False(ShiftCalibrationSession.TryRestore(Context(), samples, null, 1, 300, out _));
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 4_000, 8_500, Falling);
        Assert.False(session.Evaluate().Ready);
        Assert.False(session.Evaluate().Gears[0].HasEstimatedTarget);
    }

    [Fact]
    public void PersistedCurveGapCannotBeBridged()
    {
        var samples = Enumerable.Range(0, 77).Where(i => i is < 25 or > 30)
            .Select(i => new AccelerationShiftSample(1_000 + i * 100, Falling(1_000 + i * 100))).ToArray();
        Assert.False(ShiftCalibrationSession.TryRestore(Context(), samples, null, 1, 400, out _));
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("throttle")]
    [InlineData("slip")]
    [InlineData("brake")]
    public void InterruptedFragmentsDoNotCombineIntoFullSweep(string interruption)
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        for (var fragment = 0; fragment < 12; fragment++)
        {
            Sweep(session, ref time, 1, 1_200 + fragment * 400, 1_580 + fragment * 400, Falling);
            if (interruption == "gap") time += 200;
            else
            {
                var state = State(time, 1, 1_600 + fragment * 400, 500);
                state = interruption switch
                {
                    "throttle" => state with { Accelerator = 100 },
                    "slip" => state with { TireSlipRatio = new(.3f, .3f, .3f, .3f) },
                    _ => state with { Brake = 100 }
                };
                session.Observe(state, Fingerprint, time);
                time += 10;
            }
        }
        var result = session.Evaluate();
        Assert.False(result.Ready);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void RapidlyChangingBoostCannotSupplyAStableCurve()
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        for (var rpm = 1_200; rpm <= 8_500; rpm += 20)
            Add(session, ref time, 1, rpm, Falling(rpm), (rpm / 100 % 2) * 20);
        var result = session.Evaluate();
        Assert.Equal(ShiftCalibrationStatus.UnstableOutput, result.Status);
        Assert.False(result.Ready);
    }

    [Fact]
    public void SettledBoostSupportsMeasuredCrossover()
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 8_500, Falling, boost: 25);
        Sweep(session, ref time, 2, 4_300, 6_400, Falling, boost: 25);
        Assert.True(session.Evaluate().Ready, session.Evaluate().Reason);
    }

    [Fact]
    public void DifferentRecoveredOutputCannotConfirmTheMeasuredCurve()
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 8_500, Falling, boost: 25);
        Sweep(session, ref time, 2, 4_300, 6_400, rpm => Falling(rpm) * .75, boost: 10);
        var result = session.Evaluate();
        Assert.Equal(ShiftCalibrationStatus.NeedConfirmingUpshift, result.Status);
        Assert.Equal(0, result.ConfirmingUpshifts);
    }

    [Fact]
    public void UpshiftWithoutRecoveredSamplesDoesNotComplete()
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 8_500, Falling);
        Sweep(session, ref time, 2, 4_300, 4_800, Falling);
        Assert.Equal(ShiftCalibrationStatus.NeedConfirmingUpshift, session.Evaluate().Status);
    }

    [Fact]
    public void ABriefNeutralTransitionCanConfirmAnUpshift()
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 8_500, Falling);
        for (var i = 0; i < 5; i++) Add(session, ref time, 0, 5_000, 0);
        Sweep(session, ref time, 2, 4_300, 6_400, Falling);
        Assert.True(session.Evaluate().Ready, session.Evaluate().Reason);
    }

    [Fact]
    public void UnsettledPositiveOutputIsExcludedWithoutLosingGearTransition()
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 7_400, Falling);
        for (var rpm = 7_420; rpm <= 8_500; rpm += 20)
        {
            session.Observe(State(time, 1, rpm, 100), Fingerprint, time, outputSettled: false);
            time += 10;
        }
        for (var i = 0; i < 3; i++)
        {
            session.Observe(State(time, 2, 4_200, 100), Fingerprint, time, outputSettled: false);
            time += 10;
        }
        Sweep(session, ref time, 2, 4_300, 6_400, Falling);
        var result = session.Evaluate();
        Assert.True(result.Ready, result.Reason);
        Assert.True(result.CoveredMaximumRpm < 7_500);
        Assert.Equal(20_000d / 3, result.Gears[0].EstimatedTargetRpm!.Value, 3);
        Assert.Equal(1, result.ConfirmingUpshifts);
    }

    [Fact]
    public void ExplicitMissingObservationBreakDoesNotJoinShortFragments()
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        for (var i = 0; i < 12; i++)
        {
            Sweep(session, ref time, 1, 1_200 + i * 400, 1_580 + i * 400, Falling);
            session.BreakObservation();
        }
        Assert.Null(session.Evaluate().Profile);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedCarOrFingerprintPermanentlyEndsSession(bool changeCar)
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 8_500, Falling);
        var state = State(time, 2, 4_500, Falling(4_500));
        session.Observe(changeCar ? state with { CarOrdinal = 200 } : state,
            changeCar ? Fingerprint : "new-tune", time);
        time += 10;
        Sweep(session, ref time, 2, 4_500, 6_500, Falling);
        Assert.Equal(ShiftCalibrationStatus.ContextChanged, session.Evaluate().Status);
        Assert.Null(session.Evaluate().Profile);
    }

    [Fact]
    public void MissingMetadataAndRaceOffBreakContinuityWithoutChangingIdentity()
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        session.Observe(State(1, 1, 2_000, 500), null, 1);
        session.Observe(State(11, 1, 2_000, 500) with { IsRaceOn = false, CarOrdinal = 0 }, null, 11);
        Assert.Equal(ShiftCalibrationStatus.WaitingForPull, session.Evaluate().Status);
        long time = 21;
        Sweep(session, ref time, 1, 1_200, 8_500, Falling);
        Sweep(session, ref time, 2, 4_300, 6_400, Falling);
        Assert.True(session.Evaluate().Ready, session.Evaluate().Reason);
    }

    [Fact]
    public void ConfigurationCopiesMutableInputArrays()
    {
        var ratios = new[] { 2d, 1 };
        var factors = new[] { .9, .95 };
        var context = new ShiftCalibrationContext(100, Fingerprint, ratios, factors, 9_000);
        ratios[0] = 100;
        factors[0] = 0;
        Assert.Equal(2, context.ForwardRatios[0]);
        Assert.Equal(.9, context.GearAccelerationFactors[0]);
        Assert.True(context.IsValid);
        Assert.Throws<ArgumentException>(() => new ShiftCalibrationSession(
            new(100, Fingerprint, [1, 2]), Frequency));
    }

    [Fact]
    public void RestoreRecalculatesTargetsAndPreservesEmpiricalBoundaryMeaning()
    {
        var session = new ShiftCalibrationSession(Context(8_000), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 7_980, Constant);
        Cut(session, ref time, 1, 8_000, holdSameGear: true);
        Sweep(session, ref time, 2, 4_000, 6_000, Constant);
        var saved = session.Evaluate();
        Assert.True(saved.Ready, saved.Reason);
        Assert.True(ShiftCalibrationSession.TryRestore(saved.Context, saved.Profile!.Samples,
            saved.EmpiricalUpperRpm, saved.ConfirmingUpshifts, saved.AcceptedSamples, out var restored));
        Assert.Equal(saved.Gears[0].EstimatedTargetRpm, restored!.Gears[0].EstimatedTargetRpm);
        Assert.Equal(AccelerationShiftStatus.MeasuredUpperBoundary, restored.Gears[0].Status);
        Assert.False(restored.Gears[0].OperatingCeilingVerified);
        Assert.False(ShiftCalibrationSession.TryRestore(saved.Context, saved.Profile.Samples,
            8_000, 1, saved.AcceptedSamples, out _));
        Assert.False(ShiftCalibrationSession.TryRestore(saved.Context, saved.Profile.Samples,
            saved.EmpiricalUpperRpm, 0, saved.AcceptedSamples, out _));
    }

    [Fact]
    public void FrozenGameClockDoesNotProduceAFullCurve()
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        for (var i = 0; i < 400; i++)
            session.Observe(State(1 + i * 10, 1, 1_200 + i * 18, 500) with { GameTimestampMilliseconds = 1 },
                Fingerprint, 1 + i * 10);
        Assert.False(session.Evaluate().Ready);
        Assert.Null(session.Evaluate().Profile);
    }

    [Fact]
    public void ExplicitInvalidationWithdrawsPreviouslyCompleteResult()
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        Sweep(session, ref time, 1, 1_200, 8_500, Falling);
        Sweep(session, ref time, 2, 4_300, 6_400, Falling);
        var ready = session.Evaluate();
        Assert.True(ready.Ready, ready.Reason);
        session.InvalidateContext();
        var after = session.Evaluate();
        Assert.Equal(ShiftCalibrationStatus.ContextChanged, after.Status);
        Assert.True(after.Revision > ready.Revision);
        Assert.Null(after.Profile);
    }

    [Fact]
    public void CollectionStopsAtBoundedCapacity()
    {
        var session = new ShiftCalibrationSession(Context(), Frequency);
        long time = 1;
        for (var i = 0; i < 12_100; i++) Add(session, ref time, 1, 3_000, 500);
        var result = session.Evaluate();
        Assert.Equal(ShiftCalibrationStatus.BufferFull, result.Status);
        var revision = result.Revision;
        Add(session, ref time, 1, 3_100, 500);
        Assert.Equal(revision, session.Evaluate().Revision);
    }

    private static double Falling(double rpm) => 1_000 - rpm / 10;
    private static double Constant(double _) => 500;

    private static void Sweep(ShiftCalibrationSession session, ref long time, int gear, int first, int last,
        Func<double, double> torque, float boost = 0)
    {
        for (var rpm = first; rpm <= last; rpm += 20) Add(session, ref time, gear, rpm, torque(rpm), boost);
    }

    private static void Cut(ShiftCalibrationSession session, ref long time, int gear, int rpm, bool holdSameGear)
    {
        Add(session, ref time, gear, rpm, 0, limiterActive: true);
        if (holdSameGear)
            for (var i = 0; i < 17; i++) Add(session, ref time, gear, rpm - 80, 0, limiterActive: true);
    }

    private static void Add(ShiftCalibrationSession session, ref long time, int gear, double rpm, double torque,
        float boost = 0, bool limiterActive = false)
    {
        session.Observe(State(time, gear, rpm, torque) with { BoostPressurePsi = boost }, Fingerprint, time, limiterActive);
        time += 10;
    }

    private static VehicleState State(long time, int gear, double rpm, double torque) => TestVehicleState.Create() with
    {
        ReceivedAtUtc = DateTimeOffset.UnixEpoch,
        ReceivedTimestamp = time,
        GameTimestampMilliseconds = unchecked((uint)time),
        NumCylinders = 8,
        Gear = (TransmissionGear)gear,
        EngineRpm = (float)rpm,
        TorqueNm = (float)torque,
        PowerWatts = (float)(torque * rpm * Math.PI / 30),
        Accelerator = 255
    };
}
