using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCueCadencePredictorTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(33)]
    public void SustainedRampPredictsOnlyTheNextMeasuredInterval(int interval)
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        var increment = interval * 2;
        var initial = 7000 - increment * 2.5f;
        Assert.False(predictor.ObserveAndPredict(State(1000, initial), 1000, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1000 + interval, initial + increment), 1000 + interval, 7000));
        Assert.True(predictor.ObserveAndPredict(State(1000 + interval * 2, initial + increment * 2), 1000 + interval * 2, 7000));
    }

    [Fact]
    public void DistantTargetDoesNotAcquireAnInventedRpmMargin()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        ObserveRamp(predictor, 6900, 6910);
        Assert.False(predictor.ObserveAndPredict(State(1040, 6920), 1040, 7000));
    }

    [Fact]
    public void RisingThrottleRetainsTwoMeasuredPositiveIntervals()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        Assert.False(predictor.ObserveAndPredict(State(1000, 6900) with { Accelerator = 205 }, 1000, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1020, 6940) with { Accelerator = 246 }, 1020, 7000));
        Assert.True(predictor.ObserveAndPredict(State(1040, 6980) with { Accelerator = 255 }, 1040, 7000));
    }

    [Fact]
    public void PartialLiftAfterThrottleIncreaseCannotReuseEarlierAcceleration()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        Assert.False(predictor.ObserveAndPredict(State(1000, 6900) with { Accelerator = 205 }, 1000, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1020, 6940) with { Accelerator = 255 }, 1020, 7000));
        // Still above the seed throttle, but lower than the last actual observation.
        Assert.False(predictor.ObserveAndPredict(State(1040, 6980) with { Accelerator = 251 }, 1040, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1060, 6990) with { Accelerator = 255 }, 1060, 7000));
    }

    [Fact]
    public void RisingThrottleDoesNotBypassRateSpikeRejection()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        Assert.False(predictor.ObserveAndPredict(State(1000, 6870) with { Accelerator = 205 }, 1000, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1020, 6880) with { Accelerator = 246 }, 1020, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1040, 6970) with { Accelerator = 255 }, 1040, 7000));
    }

    [Fact]
    public void RisingThrottleDoesNotRefreshAFrozenGameClock()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        for (var i = 0; i < 8; i++)
            Assert.False(predictor.ObserveAndPredict(State(1000 + i * 20, 6640 + i * 40) with
            { GameTimestampMilliseconds = 100, Accelerator = (byte)(205 + i) }, 1000 + i * 20, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1160, 6960) with
        { GameTimestampMilliseconds = 100, Accelerator = 255 }, 1160, 7000));
    }

    [Fact]
    public void FreshPacketAgeContributesOnlyWhenAtMostOneMeasuredIntervalOld()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        ObserveRamp(predictor, 6900, 6930);
        Assert.True(predictor.ObserveAndPredict(State(1040, 6960), 1050, 7000));

        predictor.Reset();
        ObserveRamp(predictor, 6900, 6930);
        Assert.False(predictor.ObserveAndPredict(State(1040, 6960), 1061, 7000));
    }

    [Fact]
    public void DuplicateUiObservationDoesNotCountAsAnotherPositiveInterval()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        predictor.ObserveAndPredict(State(1000, 6900), 1000, 7000);
        Assert.False(predictor.ObserveAndPredict(State(1020, 6940), 1020, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1020, 6940), 1021, 7000));
        Assert.True(predictor.ObserveAndPredict(State(1040, 6980), 1040, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1040, 6980), 1041, 7000));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(33)]
    public void ChangedPacketsWithTheSameGameTimeRetainMeasuredAcceleration(int interval)
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        var increment = interval * 2;
        var initial = 7000 - increment * 2.5f;
        var first = State(1000, initial) with { GameTimestampMilliseconds = 100 };
        var second = State(1000 + interval, initial + increment) with { GameTimestampMilliseconds = 116 };
        var third = State(1000 + interval * 2, initial + increment * 2) with { GameTimestampMilliseconds = 116 };
        Assert.False(predictor.ObserveAndPredict(first, 1000, 7000));
        Assert.False(predictor.ObserveAndPredict(second, 1000 + interval, 7000));
        Assert.True(predictor.ObserveAndPredict(third, 1000 + interval * 2, 7000));
    }

    [Fact]
    public void FreshReceiptsCannotKeepAFrozenGameClockPredicting()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        for (var i = 0; i < 8; i++)
        {
            var sample = State(1000 + i * 20, 6640 + i * 40) with { GameTimestampMilliseconds = 100 };
            Assert.False(predictor.ObserveAndPredict(sample, 1000 + i * 20, 7000));
        }

        // Two consistent positive slopes would reach the target, but the clock
        // has now been frozen for longer than the 150 ms freshness limit.
        Assert.False(predictor.ObserveAndPredict(State(1160, 6960) with { GameTimestampMilliseconds = 100 }, 1160, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1180, 6980) with { GameTimestampMilliseconds = 100 }, 1180, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1200, 6990) with { GameTimestampMilliseconds = 100 }, 1200, 7000));

        // Advancing the clock permits new evidence, not reuse of pre-freeze slopes.
        Assert.False(predictor.ObserveAndPredict(State(1220, 6995) with { GameTimestampMilliseconds = 116 }, 1220, 7000));
        Assert.True(predictor.ObserveAndPredict(State(1240, 6998) with { GameTimestampMilliseconds = 132 }, 1240, 7000));
    }

    [Fact]
    public void EqualGameTimeWithoutRpmProgressIsNotAccelerationEvidence()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        ObserveRamp(predictor, 6900, 6940);
        Assert.False(predictor.ObserveAndPredict(State(1040, 6940) with { GameTimestampMilliseconds = 20 }, 1040, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1060, 6980), 1060, 7000));
    }

    [Fact]
    public void PacketGapCannotRestartTheFrozenClockDeadline()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        Assert.False(predictor.ObserveAndPredict(State(1000, 6900) with { GameTimestampMilliseconds = 100 }, 1000, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1200, 6940) with { GameTimestampMilliseconds = 100 }, 1200, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1220, 6960) with { GameTimestampMilliseconds = 100 }, 1220, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1240, 6980) with { GameTimestampMilliseconds = 100 }, 1240, 7000));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("missing_timestamp")]
    [InlineData("race_off")]
    [InlineData("electric")]
    [InlineData("lift")]
    [InlineData("braking")]
    [InlineData("stationary")]
    [InlineData("invalid_speed")]
    [InlineData("invalid_rpm")]
    [InlineData("absurd_rpm")]
    public void InvalidObservationClearsAccelerationHistory(string reason)
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        ObserveRamp(predictor, 6900, 6940);
        var sample = State(1040, 6980);
        sample = reason switch
        {
            "missing_timestamp" => sample with { ReceivedTimestamp = null },
            "race_off" => sample with { IsRaceOn = false },
            "electric" => sample with { NumCylinders = 0 },
            "lift" => sample with { Accelerator = 0 },
            "braking" => sample with { Brake = 2 },
            "stationary" => sample with { GroundSpeedMetersPerSecond = 0 },
            "invalid_speed" => sample with { GroundSpeedMetersPerSecond = float.NaN },
            "invalid_rpm" => sample with { EngineRpm = float.NaN },
            "absurd_rpm" => sample with { EngineRpm = 100_000 },
            _ => sample
        };
        var now = reason == "stale" ? 1191 : reason == "future" ? 1039 : 1040;
        Assert.False(predictor.ObserveAndPredict(sample, now, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1060, 6990), 1060, 7000));
    }

    [Theory]
    [InlineData("car")]
    [InlineData("gear")]
    [InlineData("throttle")]
    [InlineData("target")]
    [InlineData("game_time_rollback")]
    [InlineData("receipt_rollback")]
    [InlineData("missing_packet")]
    [InlineData("stall")]
    public void TransitionOrClockDiscontinuityCannotReuseAcceleration(string reason)
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        ObserveRamp(predictor, 6900, 6940);
        var sample = State(1040, 6980);
        sample = reason switch
        {
            "car" => sample with { CarOrdinal = 22 },
            "gear" => sample with { Gear = TransmissionGear.Third },
            "throttle" => sample with { Accelerator = 250 },
            "game_time_rollback" => sample with { GameTimestampMilliseconds = 19 },
            "receipt_rollback" => sample with { ReceivedTimestamp = 1019 },
            "missing_packet" => sample with { ReceivedTimestamp = 1090 },
            "stall" => sample with { ReceivedTimestamp = 1190 },
            _ => sample
        };
        Assert.False(predictor.ObserveAndPredict(sample, sample.ReceivedTimestamp!.Value,
            reason == "target" ? 6990 : 7000));
    }

    [Theory]
    [InlineData(6940)]
    [InlineData(6930)]
    [InlineData(9000)]
    public void FlatFallingOrDiscontinuousRpmDoesNotPredict(float rpm)
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        ObserveRamp(predictor, 6900, 6940);
        Assert.False(predictor.ObserveAndPredict(State(1040, rpm), 1040, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1060, 6980), 1060, 7000));
    }

    [Fact]
    public void RateSpikeCannotBorrowThePreviousSlowInterval()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        ObserveRamp(predictor, 6870, 6880);
        Assert.False(predictor.ObserveAndPredict(State(1040, 6970), 1040, 7000));
    }

    [Fact]
    public void ExplicitResetRequiresTwoNewPositiveIntervals()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        ObserveRamp(predictor, 6900, 6940);
        predictor.Reset();
        Assert.False(predictor.ObserveAndPredict(State(1040, 6980), 1040, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1060, 6990), 1060, 7000));
    }

    [Fact]
    public void AlreadyCrossedTargetUsesRawCrossingRatherThanPrediction()
    {
        var predictor = new ShiftCueCadencePredictor(1000);
        ObserveRamp(predictor, 6900, 6950);
        Assert.False(predictor.ObserveAndPredict(State(1040, 7000), 1040, 7000));
    }

    private static void ObserveRamp(ShiftCueCadencePredictor predictor, float first, float second)
    {
        Assert.False(predictor.ObserveAndPredict(State(1000, first), 1000, 7000));
        Assert.False(predictor.ObserveAndPredict(State(1020, second), 1020, 7000));
    }

    private static VehicleState State(long received, float rpm) => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = (uint)Math.Max(0, received - 1000),
        ReceivedAtUtc = DateTimeOffset.UnixEpoch,
        ReceivedTimestamp = received,
        CarOrdinal = 2177,
        Drivetrain = DrivetrainType.RearWheelDrive,
        NumCylinders = 8,
        GroundSpeedMetersPerSecond = 30,
        EngineRpm = rpm,
        EngineMaximumRpm = 8000,
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        Gear = TransmissionGear.Second,
        Steering = 0,
        Accelerator = 255,
        Brake = 0
    };
}
