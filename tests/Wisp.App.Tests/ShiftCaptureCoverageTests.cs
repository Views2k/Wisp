using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCaptureCoverageTests
{
    [Fact]
    public void ParkedBindingChecksNeverCompleteDrivingEvidence()
    {
        var coverage = new ShiftCaptureCoverage(1_000);
        coverage.ObserveTelemetry(State(1, 100, 900, throttle: 0, speed: 0), 1_000, "a");
        coverage.ObserveButton(Button(1_010, 1_020), 1_020);
        coverage.ObserveTelemetry(State(2, 150, 900, throttle: 0, speed: 0), 1_050, "a");
        Assert.Empty(coverage.Snapshot().Upshifts);
        Assert.False(coverage.Snapshot().DrivingEvidenceChecklistSatisfied);
    }

    [Fact]
    public void SweepPowerCutAndTwoBracketedShiftsProduceAnEvidenceChecklistOnly()
    {
        var capture = CompleteFixture();
        var result = capture.Snapshot();
        Assert.True(result.DrivingEvidenceChecklistSatisfied);
        Assert.Equal("a", result.DrivingEvidenceFingerprint);
        Assert.Equal(2, result.Upshifts.Length);
        Assert.Equal(2, result.RecordedMovingUpshifts);
        Assert.Equal(2, result.BracketedEngineOutputTimings);
        Assert.Equal(2, result.TractionQualifiedEngineOutputTimings);
        Assert.All(result.Upshifts, shift =>
        {
            Assert.NotNull(shift.ButtonBracket);
            Assert.True(shift.PositiveTorqueSustained);
        });
        Assert.Contains("not proof", result.Scope);
        Assert.Contains("not verified", result.LimiterMeaning);
        Assert.Contains("does not prove", result.TorqueTimingMeaning);
        Assert.Contains("separate from capture integrity", result.ChecklistMeaning);
        Assert.Contains("does not invalidate", result.ChecklistMeaning);
    }

    [Fact]
    public void DelayedButtonProducerCanPairWithAlreadyObservedGearTransition()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 1_000, 7_000), 1_000, "a");
        capture.ObserveTelemetry(State(3, 1_050, 5_000), 1_050, "a");
        capture.ObserveButton(Button(1_005, 1_060), 1_060);
        Assert.NotNull(Assert.Single(capture.Snapshot().Upshifts).ButtonBracket);
    }

    [Fact]
    public void MultipleButtonCandidatesNeverBecomeAnExactCommandTime()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 1_000, 7_000), 1_000, "a");
        capture.ObserveButton(Button(1_005, 1_015), 1_015);
        capture.ObserveButton(Button(1_020, 1_030), 1_030);
        capture.ObserveTelemetry(State(3, 1_050, 5_000), 1_050, "a");
        var shift = Assert.Single(capture.Snapshot().Upshifts);
        Assert.Null(shift.ButtonBracket);
        Assert.Equal(2, shift.CandidateButtonCount);
    }

    [Fact]
    public void BriefNeutralTransitionRetainsGearBracketAndTorqueCutEvidence()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 1_000, 7_000), 1_000, "a");
        capture.ObserveButton(Button(1_010, 1_020), 1_020);
        capture.ObserveTelemetry(State(0, 1_050, 6_500, torque: 0), 1_050, "a");
        capture.ObserveTelemetry(State(0, 1_100, 6_000, torque: 0), 1_100, "a");
        capture.ObserveTelemetry(State(3, 1_150, 5_000), 1_150, "a");
        capture.ObserveTelemetry(State(3, 1_200, 5_100), 1_200, "a");
        capture.ObserveTelemetry(State(3, 1_250, 5_200), 1_250, "a");
        var shift = Assert.Single(capture.Snapshot().Upshifts);
        Assert.Equal(new ShiftCaptureTimeBracket(1_000, 1_150), shift.GearChange);
        Assert.True(shift.ObservedTorqueInterruption);
        Assert.True(shift.PositiveTorqueSustained);
        Assert.Equal(new ShiftCaptureTimeBracket(1_000, 1_150), shift.FirstPositiveTorqueBracket);
        Assert.NotNull(shift.ButtonBracket);
    }

    [Fact]
    public void CarOrTuneChangeDuringNeutralCannotInheritPriorShift()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 1_000, 7_000), 1_000, "a");
        capture.ObserveTelemetry(State(0, 1_050, 6_500, torque: 0), 1_050, "a");
        capture.ObserveTelemetry(State(3, 1_100, 5_000), 1_100, "b");
        Assert.Empty(capture.Snapshot().Upshifts);
        Assert.Equal(1, capture.Snapshot().ProfileTransitions);
    }

    [Fact]
    public void WheelspinNeverSatisfiesCleanLoadOrLimiterChecklist()
    {
        var capture = CompleteFixture(slip: .8f);
        var result = capture.Snapshot();
        Assert.False(result.DrivingEvidenceChecklistSatisfied);
        Assert.All(result.Profiles.SelectMany(p => p.Gears), gear =>
        {
            Assert.False(gear.CleanSweepObserved);
            Assert.Equal(0, gear.HighRpmPowerCutCandidates);
        });
        Assert.All(result.Upshifts, shift => Assert.False(shift.CleanFullLoad));
        Assert.All(result.Upshifts, shift => Assert.True(shift.EngineOutputTimingObserved));
        Assert.Equal(2, result.BracketedEngineOutputTimings);
        Assert.Equal(0, result.TractionQualifiedEngineOutputTimings);
    }

    [Fact]
    public void RecordedTest6SlipExcursionRetainsOutputTimingWithoutQualifyingAcceleration()
    {
        var capture = new ShiftCaptureCoverage(10_000_000);
        capture.ObserveButton(Button(892_866, 949_104), 949_104);
        // Test6 ZIP 017cab3c...: relative ingress QPC and recorded fields around
        // the fourth-to-fifth shift. Tire slip previously erased recovery.
        (long Qpc, uint Clock, int Gear, float Rpm, float Torque, float Power, float Slip, float Lateral)[] packets =
        [
            (750_457, 482298484, 4, 9653.093f, 689.37085f, 696211.9f, .03890496f, .0624902f),
            (828_461, 482298500, 4, 9680.283f, 684.7194f, 693797.8f, .07854614f, -.12080424f),
            (905_865, 482298500, 4, 9688.762f, 682.8653f, 692779.56f, .08570359f, -.2006051f),
            (1_000_000, 482298515, 4, 9563.214f, -474.92627f, -476865.47f, .0068570706f, -.1333128f),
            (1_072_109, 482298515, 0, 9464.464f, -363.0118f, -360739.72f, .000985174f, -.06584012f),
            (1_182_559, 482298531, 0, 9367.372f, -346.523f, -340802.2f, .0021766971f, .08866689f),
            (1_256_529, 482298546, 5, 9256.037f, -143.35606f, -139528f, .0025442787f, .060458392f),
            (1_362_675, 482298546, 5, 8960.196f, 696.008f, 659993f, .12360063f, -.031631663f),
            (1_451_172, 482298562, 5, 8577.358f, 774.87994f, 703732.44f, .22656849f, .0045247953f),
            (1_539_573, 482298562, 5, 7973.936f, 802.0091f, 671889.1f, .24909364f, .046656705f),
            (1_626_143, 482298578, 5, 7924.893f, 828.36456f, 688071.2f, .13865979f, .06042172f)
        ];
        foreach (var packet in packets)
            capture.ObserveTelemetry(State(packet.Gear, packet.Clock, packet.Rpm, torque: packet.Torque, slip: packet.Slip) with
            {
                PowerWatts = packet.Power,
                LateralAccelerationMetersPerSecondSquared = packet.Lateral
            }, packet.Qpc, "a");
        var result = capture.Snapshot();
        var shift = Assert.Single(result.Upshifts);
        Assert.True(shift.CleanFullLoad);
        Assert.True(shift.FullLoadTimingEligible);
        Assert.True(shift.ObservedTorqueInterruption);
        Assert.True(shift.PositiveTorqueSustained);
        Assert.True(shift.EngineOutputTimingObserved);
        Assert.False(shift.RecoveryTractionQualified);
        Assert.Equal(new ShiftCaptureTimeBracket(1_000_000, 1_256_529), shift.GearChange);
        Assert.Equal(new ShiftCaptureTimeBracket(1_256_529, 1_362_675), shift.FirstPositiveTorqueBracket);
        Assert.Equal(1, result.BracketedEngineOutputTimings);
        Assert.Equal(0, result.TractionQualifiedEngineOutputTimings);
        Assert.False(result.DrivingEvidenceChecklistSatisfied);
    }

    [Theory]
    [InlineData(.3f, 0)]
    [InlineData(0, 2f)]
    public void NeutralTractionExcursionRetainsOutputTimingButCannotQualifyAcceleration(float slip, float lateral)
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 1_000, 7_000), 1_000, "a");
        capture.ObserveButton(Button(1_010, 1_020), 1_020);
        capture.ObserveTelemetry(State(0, 1_050, 6_000, torque: 0, slip: slip) with
        { LateralAccelerationMetersPerSecondSquared = lateral }, 1_050, "a");
        capture.ObserveTelemetry(State(3, 1_100, 5_000), 1_100, "a");
        capture.ObserveTelemetry(State(3, 1_150, 5_100), 1_150, "a");
        capture.ObserveTelemetry(State(3, 1_200, 5_200), 1_200, "a");
        var shift = Assert.Single(capture.Snapshot().Upshifts);
        Assert.True(shift.EngineOutputTimingObserved);
        Assert.False(shift.CleanFullLoad);
        Assert.False(shift.RecoveryTractionQualified);
    }

    [Theory]
    [InlineData("lift")]
    [InlineData("brake")]
    [InlineData("context")]
    [InlineData("invalid-slip")]
    public void TractionIndependentRecoveryStillStopsForInvalidOrInterruptedLoad(string interruption)
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 1_000, 7_000), 1_000, "a");
        capture.ObserveButton(Button(1_005, 1_015), 1_015);
        capture.ObserveTelemetry(State(3, 1_020, 5_000, slip: .3f), 1_020, "a");
        var state = State(3, 1_030, 5_100, slip: .3f);
        if (interruption == "lift") state = state with { Accelerator = 0 };
        if (interruption == "brake") state = state with { Brake = 2 };
        if (interruption == "invalid-slip") state = state with { TireSlipRatio = new(float.NaN, 0, 0, 0) };
        if (interruption == "context") capture.InvalidateContext();
        capture.ObserveTelemetry(state, 1_030, "a");
        capture.ObserveTelemetry(State(3, 1_050, 5_200), 1_050, "a");
        capture.ObserveTelemetry(State(3, 1_100, 5_300), 1_100, "a");
        var shift = Assert.Single(capture.Snapshot().Upshifts);
        Assert.False(shift.PositiveTorqueSustained);
        Assert.False(shift.EngineOutputTimingObserved);
    }

    [Fact]
    public void SharedGameTimestampDoesNotDiscardChangedRpmGearOrTorque()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 100, 6_900), 1_000, "a");
        capture.ObserveTelemetry(State(2, 100, 7_000, torque: 0), 1_010, "a");
        capture.ObserveButton(Button(1_011, 1_019), 1_019);
        capture.ObserveTelemetry(State(3, 100, 5_000), 1_020, "a");
        capture.ObserveTelemetry(State(3, 100, 5_010), 1_035, "a");
        capture.ObserveTelemetry(State(3, 100, 5_020), 1_050, "a");
        var result = capture.Snapshot();
        Assert.Equal(4, result.RepeatedGameTimestamps);
        Assert.Equal(result.RepeatedGameTimestamps, result.RepeatedGameStates);
        Assert.Contains("changed physical observations", result.RepeatedTimestampMeaning);
        var second = Assert.Single(Assert.Single(result.Profiles).Gears, gear => gear.Gear == 2);
        Assert.Equal(2, second.Samples);
        Assert.Equal(7_000, second.MaximumRpm);
        Assert.Equal(1, second.HighRpmPowerCutCandidates);
        var shift = Assert.Single(result.Upshifts);
        Assert.Equal(new ShiftCaptureTimeBracket(1_010, 1_020), shift.GearChange);
        Assert.NotNull(shift.ButtonBracket);
        Assert.True(shift.PositiveTorqueSustained);
    }

    [Fact]
    public void ClockStallCannotAccumulateSweepTimeOrRearmWhileStillStalled()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        for (var i = 0; i <= 20; i++)
            capture.ObserveTelemetry(State(i < 15 ? 2 : 3, 100, 4_000 + i * 150), 1_000 + i * 50, "a");
        var result = capture.Snapshot();
        Assert.Equal(20, result.RepeatedGameTimestamps);
        Assert.Equal(1, result.ContinuityGaps);
        Assert.Empty(result.Upshifts);
        var gear = Assert.Single(Assert.Single(result.Profiles).Gears);
        Assert.Equal(6, gear.Samples);
        Assert.False(gear.CleanSweepObserved);

        capture.ObserveTelemetry(State(3, 101, 7_000), 2_050, "a");
        capture.ObserveTelemetry(State(4, 102, 5_000), 2_100, "a");
        var resumed = Assert.Single(capture.Snapshot().Upshifts);
        Assert.Equal(3, resumed.FromGear);
        Assert.Equal(new ShiftCaptureTimeBracket(2_050, 2_100), resumed.GearChange);
    }

    [Fact]
    public void StaleClockDuringNeutralCannotBridgeOrRearmThePreviousGear()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 100, 7_000), 1_000, "a");
        capture.ObserveButton(Button(1_010, 1_020), 1_020);
        for (var qpc = 1_050; qpc <= 1_550; qpc += 50)
            capture.ObserveTelemetry(State(0, 100, 6_000, torque: 0), qpc, "a");
        capture.ObserveTelemetry(State(3, 100, 5_000), 1_600, "a");
        Assert.Empty(capture.Snapshot().Upshifts);
        Assert.Equal(1, capture.Snapshot().ContinuityGaps);
        capture.ObserveTelemetry(State(3, 116, 5_100), 1_650, "a");
        Assert.Empty(capture.Snapshot().Upshifts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackwardGameTimeCannotBridgeAnEngagedOrNeutralShift(bool neutral)
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 200, 7_000), 1_000, "a");
        if (neutral) capture.ObserveTelemetry(State(0, 216, 6_000, torque: 0), 1_020, "a");
        capture.ObserveTelemetry(State(3, 199, 5_000), 1_050, "a");
        Assert.Empty(capture.Snapshot().Upshifts);
        Assert.Equal(1, capture.Snapshot().InvalidOrUnassociatedSamples);
    }

    [Fact]
    public void ContextLossBreaksASharedTimestampShiftAndCommandAssociation()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 100, 7_000), 1_000, "a");
        capture.ObserveButton(Button(1_010, 1_020), 1_020);
        capture.InvalidateContext();
        capture.ObserveTelemetry(State(3, 100, 5_000), 1_050, "a");
        Assert.Empty(capture.Snapshot().Upshifts);
        capture.ObserveTelemetry(State(4, 100, 4_000), 1_100, "a");
        var shift = Assert.Single(capture.Snapshot().Upshifts);
        Assert.Null(shift.ButtonBracket);
        Assert.Equal(0, shift.CandidateButtonCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedFourthToFifthPacketsPreserveTimingWithoutBridgingMissingConfiguration(bool contextInterrupted)
    {
        var capture = new ShiftCaptureCoverage(10_000_000);
        capture.ObserveButton(Button(128_089, 177_467), 177_467);
        // Test4 ZIP AAEEC45F...: relative clocks, recorded RPM/output/lateral values
        // and maximum absolute tire slip; unrelated car identity is omitted.
        // The live reader lost profile association during the two 328 ms rows.
        (long Qpc, uint Clock, int Gear, float Rpm, float Torque, float Power, float Slip, float Lateral)[] packets =
        [
            (55_209, 281, 4, 9617.989f, -458.25134f, -461944.53f, .042830415f, -.008700722f),
            (147_203, 281, 4, 9608.261f, -441.64844f, -444482.22f, .05409434f, .0523249f),
            (229_835, 296, 0, 9491.6455f, -351.02936f, -350134.1f, .0048715076f, .023505852f),
            (311_470, 296, 0, 9398.211f, -324.62894f, -320551.56f, .0013686478f, .047905304f),
            (398_217, 312, 5, 8093.942f, 783.7512f, 663247.7f, .055718664f, .027534371f),
            (489_216, 328, 5, 8113.9727f, 781.9934f, 664088.4f, .098501846f, .06998151f),
            (560_871, 328, 5, 8121.7534f, 781.3286f, 664363.9f, .10894208f, -.18740813f),
            (654_490, 343, 5, 8126.493f, 781.03644f, 664577.44f, .11014376f, .14564529f)
        ];
        foreach (var packet in packets)
            capture.ObserveTelemetry(State(packet.Gear, packet.Clock, packet.Rpm, torque: packet.Torque, slip: packet.Slip) with
            {
                PowerWatts = packet.Power,
                LateralAccelerationMetersPerSecondSquared = packet.Lateral
            }, packet.Qpc, contextInterrupted && packet.Clock == 328 ? null : "a");
        var result = capture.Snapshot();
        var shift = Assert.Single(result.Upshifts);
        Assert.Equal(new ShiftCaptureTimeBracket(147_203, 398_217), shift.GearChange);
        Assert.Equal(new ShiftCaptureTimeBracket(128_089, 177_467), shift.ButtonBracket);
        Assert.True(shift.CleanFullLoad);
        Assert.True(shift.ObservedTorqueInterruption);
        Assert.Equal(!contextInterrupted, shift.PositiveTorqueSustained);
        Assert.Equal(contextInterrupted ? 2 : 0, result.InvalidOrUnassociatedSamples);
        Assert.False(result.DrivingEvidenceChecklistSatisfied);
        Assert.All(result.Profiles.SelectMany(profile => profile.Gears), gear => Assert.False(gear.CleanSweepObserved));
    }

    [Fact]
    public void PacketGapCannotInventAGearChangeOrAContinuousSweep()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        for (var i = 0; i < 15; i++)
            capture.ObserveTelemetry(State(2, (uint)(100 + i * 50), 4_000), 1_000 + i * 50, "a");
        for (var i = 0; i < 15; i++)
            capture.ObserveTelemetry(State(3, (uint)(3_000 + i * 50), 7_000), 3_000 + i * 50, "a");
        var result = capture.Snapshot();
        Assert.Empty(result.Upshifts);
        Assert.True(result.ContinuityGaps > 0);
        Assert.All(result.Profiles.SelectMany(p => p.Gears), gear => Assert.False(gear.CleanSweepObserved));
    }

    [Fact]
    public void TuneRefreshSplitsCoverageAndDoesNotInventAShift()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        for (var i = 0; i < 10; i++)
            capture.ObserveTelemetry(State(2, (uint)(100 + i * 50), 4_000 + i * 100), 1_000 + i * 50, "a");
        for (var i = 0; i < 10; i++)
            capture.ObserveTelemetry(State(3, (uint)(600 + i * 50), 6_000 + i * 100), 1_500 + i * 50, "b");
        var result = capture.Snapshot();
        Assert.Equal(2, result.Profiles.Length);
        Assert.Equal(1, result.ProfileTransitions);
        Assert.Empty(result.Upshifts);
        Assert.False(result.DrivingEvidenceChecklistSatisfied);
        Assert.All(result.Profiles.SelectMany(p => p.Gears), gear => Assert.False(gear.CleanSweepObserved));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ButtonBeforeGapOrTuneResetCannotPairWithLaterGearChange(bool tuneChange)
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 1_000, 7_000), 1_000, "a");
        capture.ObserveButton(Button(1_010, 1_020), 1_020);
        var next = tuneChange ? 1_050 : 1_250;
        var profile = tuneChange ? "b" : "a";
        capture.ObserveTelemetry(State(2, (uint)next, 7_000), next, profile);
        capture.ObserveTelemetry(State(3, (uint)(next + 50), 5_000), next + 50, profile);
        var shift = Assert.Single(capture.Snapshot().Upshifts);
        Assert.Null(shift.ButtonBracket);
        Assert.Equal(0, shift.CandidateButtonCount);
    }

    [Fact]
    public void MissingConfigurationOrInvalidValuesCannotCountAsCoverage()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 100, 4_000), 1_000, null);
        capture.ObserveTelemetry(State(2, 150, float.NaN), 1_050, "a");
        capture.ObserveTelemetry(State(2, 200, 5_000) with { NumCylinders = 0 }, 1_100, "a");
        var result = capture.Snapshot();
        Assert.Empty(result.Profiles);
        Assert.Equal(3, result.InvalidOrUnassociatedSamples);
    }

    [Fact]
    public void StationaryPartialTuningsCannotCombineIntoACompleteSweep()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        for (var i = 0; i < 10; i++)
            capture.ObserveTelemetry(State(2, (uint)(1_000 + i * 50), 4_000 + i * 200), 1_000 + i * 50, "a");
        for (var i = 0; i < 10; i++)
            capture.ObserveTelemetry(State(2, (uint)(1_500 + i * 50), 6_000 + i * 100), 1_500 + i * 50, "b");
        capture.ObserveTelemetry(State(2, 2_000, 6_900, torque: 0), 2_000, "b");
        var result = capture.Snapshot();
        Assert.False(result.DrivingEvidenceChecklistSatisfied);
        Assert.All(result.Profiles.SelectMany(p => p.Gears), gear => Assert.False(gear.CleanSweepObserved));
    }

    [Fact]
    public void ProfileStorageIsBoundedAndOverflowRemainsExplicit()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        for (var i = 0; i < 12; i++)
            capture.ObserveTelemetry(State(2, (uint)(100 + i * 50), 4_000), 1_000 + i * 50, "tune" + i);
        Assert.Equal(8, capture.Snapshot().Profiles.Length);
        Assert.True(capture.Snapshot().Truncated);
        Assert.False(capture.Snapshot().DrivingEvidenceChecklistSatisfied);
    }

    [Fact]
    public void UnsignedGameClockWrapIsAcceptedAsForwardProgress()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, uint.MaxValue - 20, 7_000), 1_000, "a");
        capture.ObserveTelemetry(State(3, 29, 5_000), 1_050, "a");
        Assert.Single(capture.Snapshot().Upshifts);
        Assert.Equal(0, capture.Snapshot().InvalidOrUnassociatedSamples);
    }

    private static ShiftCaptureCoverage CompleteFixture(float slip = 0)
    {
        var capture = new ShiftCaptureCoverage(1_000);
        for (var i = 0; i <= 20; i++) Add(2, 1_000 + i * 50, 4_000 + i * 150);
        Add(2, 2_050, 6_900, torque: 0);
        capture.ObserveButton(Button(2_060, 2_070), 2_070);
        Add(3, 2_100, 5_000);
        for (var i = 1; i <= 30; i++) Add(3, 2_100 + i * 50, 5_000 + i * 60);
        capture.ObserveButton(Button(3_610, 3_620), 3_620);
        Add(4, 3_650, 5_200);
        Add(4, 3_700, 5_300);
        Add(4, 3_750, 5_400);
        return capture;

        void Add(int gear, int qpc, float rpm, float torque = 500) =>
            capture.ObserveTelemetry(State(gear, (uint)qpc, rpm, torque: torque, slip: slip), qpc, "a");
    }

    private static ShiftCaptureButtonEvent Button(long earliest, long latest) =>
        new("B", "upshift", "pressed", earliest, earliest + 1, latest - 1, latest, earliest, latest, "polling interval");

    private static VehicleState State(int gear, uint time, float rpm, byte throttle = 255,
        float speed = 30, float torque = 500, float slip = 0) => new()
        {
            IsRaceOn = true,
            CarOrdinal = 1,
            NumCylinders = 8,
            Gear = (TransmissionGear)gear,
            GameTimestampMilliseconds = time,
            ReceivedAtUtc = DateTimeOffset.UnixEpoch,
            Drivetrain = DrivetrainType.RearWheelDrive,
            EngineRpm = rpm,
            EngineMaximumRpm = 7_000,
            GroundSpeedMetersPerSecond = speed,
            Accelerator = throttle,
            Brake = 0,
            Steering = 0,
            TorqueNm = torque,
            PowerWatts = torque * rpm / 9.5493f,
            WheelRotationRadiansPerSecond = default,
            TireSlipRatio = new(slip, slip, slip, slip),
            TireSlipAngle = default,
            NormalizedSuspensionTravel = default,
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = 2
        };
}
