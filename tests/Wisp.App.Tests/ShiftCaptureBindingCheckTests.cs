using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCaptureBindingCheckTests
{
    [Fact]
    public void BAndXRequireTheirCorrespondingSubsequentTelemetryTransitions()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1, 10), 1_000);
        check.ObserveButton(Button("B"), 1_100);
        Assert.False(check.UpVerified);
        check.ObserveState(State(1, 11), 1_200);
        Assert.False(check.UpVerified);
        check.ObserveState(State(2, 12), 1_300);
        Assert.True(check.UpVerified);
        Assert.False(check.DownVerified);
        check.ObserveButton(Button("X"), 1_400);
        check.ObserveState(State(1, 13), 1_500);
        Assert.True(check.DownVerified);
    }

    [Theory]
    [InlineData(150, true)]
    [InlineData(151, false)]
    [InlineData(-1, false)]
    public void BaselineMustBeRecentAndNotFromTheFuture(int milliseconds, bool expected)
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 1_000);
        check.ObserveButton(Button("B"), 1_000 + milliseconds);
        check.ObserveState(State(2, 2), 1_500);
        Assert.Equal(expected, check.UpVerified);
    }

    [Theory]
    [InlineData(2_000, true)]
    [InlineData(2_001, false)]
    public void ResultMustArriveWithinTwoSeconds(int milliseconds, bool expected)
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 1_000);
        check.ObserveButton(Button("B"), 1_100);
        check.ObserveState(State(2, 2), 1_100 + milliseconds);
        Assert.Equal(expected, check.UpVerified);
    }

    [Fact]
    public void ThresholdsUseInjectedQpcFrequency()
    {
        var check = new ShiftCaptureBindingCheck(10_000_000);
        check.ObserveState(State(1), 10_000_000);
        check.ObserveButton(Button("B"), 11_500_000);
        check.ObserveState(State(2, 2), 31_500_000);
        Assert.True(check.UpVerified);
    }

    [Theory]
    [InlineData(10u, false)]
    [InlineData(9u, false)]
    [InlineData(11u, true)]
    public void FrozenOrBackwardsGameClockCannotVerifyTransition(uint timestamp, bool expected)
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1, 10), 1_000);
        check.ObserveButton(Button("B"), 1_100);
        check.ObserveState(State(2, timestamp), 1_200);
        Assert.Equal(expected, check.UpVerified);
    }

    [Fact]
    public void GameClockWrapStillRepresentsDistinctForwardTelemetry()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1, uint.MaxValue), 1_000);
        check.ObserveButton(Button("B"), 1_100);
        check.ObserveState(State(2, 0), 1_200);
        Assert.True(check.UpVerified);
    }

    [Theory]
    [InlineData("moving")]
    [InlineData("moving-backwards")]
    [InlineData("non-finite-speed")]
    [InlineData("throttle")]
    [InlineData("race-off")]
    [InlineData("electric")]
    [InlineData("unknown-engine")]
    [InlineData("reverse")]
    [InlineData("unknown-gear")]
    public void UnsafeBaselineCannotArmAndUnsafeResultCancelsPending(string condition)
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(Unsafe(State(1), condition), 1_000);
        check.ObserveButton(Button("B"), 1_100);
        check.ObserveState(State(2, 2), 1_200);
        Assert.False(check.UpVerified);

        check.Reset();
        check.ObserveState(State(1), 2_000);
        check.ObserveButton(Button("B"), 2_100);
        check.ObserveState(Unsafe(State(2, 2), condition), 2_200);
        check.ObserveState(State(2, 3), 2_300);
        Assert.False(check.UpVerified);
    }

    [Theory]
    [InlineData("B", 1, 2)]
    [InlineData("X", 2, 1)]
    public void ParkedShiftCanBrieflyPassThroughNeutral(string button, int from, int to)
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(from), 990);
        check.ObserveButton(MeasuredButton(button, 1_000, 1_004), 1_004);
        check.ObserveState(State(0, 2), 1_010);
        Assert.False(check.UpVerified);
        Assert.False(check.DownVerified);
        check.ObserveState(State(to, 3), 1_025);
        Assert.Equal(button == "B", check.UpVerified);
        Assert.Equal(button == "X", check.DownVerified);
    }

    [Fact]
    public void NeutralCannotProvideTheButtonBaseline()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(0), 990);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
        check.ObserveState(State(1, 2), 1_010);
        check.ObserveState(State(2, 3), 1_025);
        Assert.False(check.UpVerified);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void NeutralBridgeMustFinishWithinHalfASecond(int milliseconds, bool expected)
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 990);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
        check.ObserveState(State(0, 2), 1_010);
        check.ObserveState(State(0, 3), 1_250); // Repeated neutral must not restart its limit.
        check.ObserveState(State(2, 4), 1_010 + milliseconds);
        Assert.Equal(expected, check.UpVerified);
    }

    [Theory]
    [InlineData(3_000, true)]
    [InlineData(3_001, false)]
    public void NeutralDoesNotExtendOverallTwoSecondLimit(int resultQpc, bool expected)
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 990);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
        check.ObserveState(State(1, 2), 2_900);
        check.ObserveState(State(0, 3), 2_990);
        check.ObserveState(State(2, 4), resultQpc);
        Assert.Equal(expected, check.UpVerified);
    }

    [Theory]
    [InlineData("moving")]
    [InlineData("moving-backwards")]
    [InlineData("non-finite-speed")]
    [InlineData("throttle")]
    [InlineData("race-off")]
    [InlineData("electric")]
    [InlineData("unknown-engine")]
    [InlineData("reverse")]
    [InlineData("unknown-gear")]
    public void NeutralBridgeRetainsParkedAndGearSafetyChecks(string condition)
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 990);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
        check.ObserveState(Unsafe(State(0, 2), condition), 1_010);
        check.ObserveState(State(2, 3), 1_025);
        Assert.False(check.UpVerified);
    }

    [Fact]
    public void CarChangeDuringNeutralCancelsBindingCheck()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 990);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
        check.ObserveState(State(0, 2) with { CarOrdinal = 999 }, 1_010);
        check.ObserveState(State(2, 3), 1_025);
        Assert.False(check.UpVerified);
    }

    [Fact]
    public void NeutralAndResultInsideMeasuredBracketAreReplayed()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 990);
        check.ObserveState(State(0, 2), 1_001);
        check.ObserveState(State(2, 3), 1_002);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
        Assert.True(check.UpVerified);
    }

    [Fact]
    public void QueuedPreEdgeStateInOriginalGearDoesNotCancelPendingCheck()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 990);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
        check.ObserveState(State(1, 2), 999);
        check.ObserveState(State(2, 3), 1_005);
        Assert.True(check.UpVerified);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void QueuedPreEdgeGearChangeCannotBeAttributedToButton(int gear)
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 990);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
        check.ObserveState(State(gear, 2), 999);
        check.ObserveState(State(2, 3), 1_005);
        Assert.False(check.UpVerified);
    }

    [Fact]
    public void CarChangeOrWrongGearCancelsRatherThanAcceptingLaterCoincidence()
    {
        foreach (var mismatch in new[] { State(2, 2) with { CarOrdinal = 999 }, State(3, 2) })
        {
            var check = new ShiftCaptureBindingCheck(1_000);
            check.ObserveState(State(1), 1_000);
            check.ObserveButton(Button("B"), 1_100);
            check.ObserveState(mismatch, 1_200);
            check.ObserveState(State(2, 3), 1_300);
            Assert.False(check.UpVerified);
        }
    }

    [Fact]
    public void NextPressOverwritesPendingButReleaseDoesNot()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(2), 1_000);
        check.ObserveButton(Button("B"), 1_050);
        check.ObserveButton(Button("X"), 1_100);
        check.ObserveButton(Button("X") with { Edge = "released" }, 1_110);
        check.ObserveState(State(1, 2), 1_200);
        Assert.False(check.UpVerified);
        Assert.True(check.DownVerified);
    }

    [Fact]
    public void OnlyExplicitDefaultBindingsAndForwardExpectedGearsArm()
    {
        foreach (var pair in new[]
        {
            (Gear: 1, Input: Button("X"), Result: 0),
            (Gear: 10, Input: Button("B"), Result: 11),
            (Gear: 1, Input: Button("B") with { Action = "downshift" }, Result: 2),
            (Gear: 1, Input: Button("A"), Result: 2)
        })
        {
            var check = new ShiftCaptureBindingCheck(1_000);
            check.ObserveState(State(pair.Gear), 1_000);
            check.ObserveButton(pair.Input, 1_100);
            check.ObserveState(State(pair.Result, 2), 1_200);
            Assert.False(check.UpVerified);
            Assert.False(check.DownVerified);
        }
    }

    [Fact]
    public void FocusResetClearsPendingAndBaselineButRetainsCompletedVerification()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 1_000);
        check.ObserveButton(Button("B"), 1_100);
        check.ObserveState(State(2, 2), 1_200);
        check.ObserveButton(Button("X"), 1_250);
        check.ResetPending();
        check.ObserveButton(Button("X"), 1_260); // No post-focus baseline yet.
        check.ObserveState(State(1, 3), 1_300);
        Assert.True(check.UpVerified);
        Assert.False(check.DownVerified);
        check.Reset();
        Assert.False(check.UpVerified);
        Assert.False(check.DownVerified);
    }

    [Fact]
    public void OutOfOrderQpcStateInvalidatesPendingMatch()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 1_000);
        check.ObserveButton(Button("B"), 1_100);
        check.ObserveState(State(2, 2), 999);
        check.ObserveState(State(2, 3), 1_200);
        Assert.False(check.UpVerified);
    }

    [Fact]
    public void TelemetryTransitionBeforeButtonPollCompletesIsReplayedFromPreEdgeBaseline()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 990);
        check.ObserveState(State(2, 2), 1_002);
        Assert.False(check.UpVerified);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
        Assert.True(check.UpVerified);

        check.ObserveState(State(1, 3), 1_012);
        check.ObserveButton(MeasuredButton("X", 1_010, 1_014), 1_014);
        Assert.True(check.DownVerified);
    }

    [Theory]
    [InlineData(849)] // Baseline is older than 150 ms before the possible edge.
    [InlineData(1_001)] // Baseline is already inside the edge interval.
    public void MeasuredEdgeRequiresFreshStateAtOrBeforeItsEarliestBound(int baselineQpc)
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), baselineQpc);
        check.ObserveState(State(2, 2), 1_002);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
        check.ObserveState(State(2, 3), 1_005);
        Assert.False(check.UpVerified);
    }

    [Fact]
    public void ReplayDoesNotUseStateFromAfterTheMeasuredPollCompletion()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 990);
        check.ObserveState(State(2, 2), 1_005);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
        Assert.False(check.UpVerified);
    }

    [Fact]
    public void TransitionAlreadyBeforeTheEdgeCannotBeAttributedToIt()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 990);
        check.ObserveState(State(2, 2), 995);
        check.ObserveState(State(2, 3), 1_002);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
        Assert.False(check.UpVerified);
    }

    [Theory]
    [InlineData(1_005)] // Bracket extends into the future relative to event timestamp.
    [InlineData(999)] // Reversed interval.
    [InlineData(1_151)] // A stalled poll has no bounded recent edge evidence.
    public void InvalidMeasuredBracketNeverFallsBackToLatestState(int latest)
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        check.ObserveState(State(1), 990);
        check.ObserveButton(MeasuredButton("B", 1_000, latest), latest == 1_151 ? latest : 1_004);
        check.ObserveState(State(2, 2), 1_200);
        Assert.False(check.UpVerified);
    }

    [Fact]
    public void WrongOrUnsafeIntermediateStateInvalidatesReplayedInterval()
    {
        foreach (var unsafeState in new[] { State(3, 2), State(1, 2) with { GroundSpeedMetersPerSecond = 1 } })
        {
            var check = new ShiftCaptureBindingCheck(1_000);
            check.ObserveState(State(1), 990);
            check.ObserveState(unsafeState, 1_001);
            check.ObserveState(State(2, 3), 1_002);
            check.ObserveButton(MeasuredButton("B", 1_000, 1_004), 1_004);
            Assert.False(check.UpVerified);
        }
    }

    [Fact]
    public void HistoryCapacityCannotResurrectAnEvictedBaseline()
    {
        var check = new ShiftCaptureBindingCheck(1_000);
        for (var index = 0; index < 40; index++) check.ObserveState(State(1, (uint)index), 1_000 + index);
        check.ObserveState(State(2, 41), 1_040);
        check.ObserveButton(MeasuredButton("B", 1_000, 1_044), 1_044);
        Assert.False(check.UpVerified);
    }

    [Fact]
    public void InvalidFrequencyIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShiftCaptureBindingCheck(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShiftCaptureBindingCheck(-1));
    }

    private static VehicleState Unsafe(VehicleState state, string condition) => condition switch
    {
        "moving" => state with { GroundSpeedMetersPerSecond = .501f },
        "moving-backwards" => state with { GroundSpeedMetersPerSecond = -.501f },
        "non-finite-speed" => state with { GroundSpeedMetersPerSecond = float.NaN },
        "throttle" => state with { Accelerator = 6 },
        "race-off" => state with { IsRaceOn = false },
        "electric" => state with { NumCylinders = 0 },
        "unknown-engine" => state with { NumCylinders = -1 },
        "neutral" => state with { Gear = TransmissionGear.Neutral },
        "reverse" => state with { Gear = TransmissionGear.Reverse },
        "unknown-gear" => state with { Gear = TransmissionGear.Unknown },
        _ => throw new ArgumentOutOfRangeException(nameof(condition))
    };

    private static ShiftCaptureButtonEvent Button(string button) => new(button,
        button == "B" ? "upshift" : "downshift", "pressed", 0, 0, 0, 0, 0, 0, "Synthetic binding fixture");

    private static ShiftCaptureButtonEvent MeasuredButton(string button, long earliest, long latest) =>
        Button(button) with
        {
            PreviousPollStartedQpc = earliest,
            PreviousPollCompletedQpc = earliest + 1,
            PollStartedQpc = latest - 1,
            PollCompletedQpc = latest,
            EarliestObservedEdgeQpc = earliest,
            LatestObservedEdgeQpc = latest
        };

    private static VehicleState State(int gear, uint gameTime = 1) => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = gameTime,
        ReceivedAtUtc = DateTimeOffset.UnixEpoch,
        CarOrdinal = 2177,
        Drivetrain = DrivetrainType.RearWheelDrive,
        NumCylinders = 8,
        GroundSpeedMetersPerSecond = 0,
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        EngineRpm = 1_000,
        EngineMaximumRpm = 7_000,
        Gear = (TransmissionGear)gear,
        Steering = 0,
        Accelerator = 0,
        Brake = 255
    };
}
