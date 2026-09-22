using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCueCrossingTrackerTests
{
    [Fact]
    public void CrossingThenLimiterDropSurvivesUntilUiConsumption()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7005));
        tracker.Observe(State(1026, 6800) with { TorqueNm = -500 });

        Assert.True(tracker.TryConsume(epoch, 1030, out var observed));
        Assert.Equal(1010, observed);
        Assert.False(tracker.TryConsume(epoch, 1031, out _));
        tracker.Observe(State(1042, 7100));
        Assert.False(tracker.TryConsume(epoch, 1043, out _));
    }

    [Fact]
    public void SameWatchRefreshPreservesPendingCrossingOlderThanNewestUiSample()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7005));
        tracker.Observe(State(1026, 6800));
        var refreshed = tracker.Arm("tune-A", 2177, 2, 7000, 1026, 2200, 1030);

        Assert.Equal(epoch, refreshed);
        Assert.True(tracker.TryConsume(refreshed, 1030, out var observed));
        Assert.Equal(1010, observed);
    }

    [Theory]
    [InlineData("brake")]
    [InlineData("lift")]
    [InlineData("neutral")]
    [InlineData("gear")]
    [InlineData("car")]
    [InlineData("race_off")]
    [InlineData("electric")]
    [InlineData("stationary")]
    [InlineData("invalid_speed")]
    [InlineData("invalid_rpm")]
    [InlineData("negative_rpm")]
    [InlineData("missing_timestamp")]
    [InlineData("zero_timestamp")]
    [InlineData("rejected")]
    public void InterveningInvalidStateDisarmsUntilExplicitNewWatch(string reason)
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7005));
        var state = State(1026, 6900);
        VehicleState? invalid = reason switch
        {
            "brake" => state with { Brake = 2 },
            "lift" => state with { Accelerator = 0 },
            "neutral" => state with { Gear = TransmissionGear.Neutral },
            "gear" => state with { Gear = TransmissionGear.Third },
            "car" => state with { CarOrdinal = 1335 },
            "race_off" => state with { IsRaceOn = false },
            "electric" => state with { NumCylinders = 0 },
            "stationary" => state with { GroundSpeedMetersPerSecond = .49f },
            "invalid_speed" => state with { GroundSpeedMetersPerSecond = float.NaN },
            "invalid_rpm" => state with { EngineRpm = float.PositiveInfinity },
            "negative_rpm" => state with { EngineRpm = -1 },
            "missing_timestamp" => state with { ReceivedTimestamp = null },
            "zero_timestamp" => state with { ReceivedTimestamp = 0 },
            _ => null
        };
        tracker.Observe(invalid);
        tracker.Observe(State(1042, 7200));
        Assert.False(tracker.TryConsume(epoch, 1043, out _));

        var nextEpoch = tracker.Arm("tune-A", 2177, 2, 7000, 1042, 2000, 1043);
        Assert.NotEqual(epoch, nextEpoch);
        tracker.Observe(State(1058, 7100));
        Assert.True(tracker.TryConsume(nextEpoch, 1059, out _));
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("car")]
    [InlineData("gear")]
    [InlineData("target")]
    public void SemanticWatchChangeCannotReuseOldCrossing(string change)
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7005));
        var nextEpoch = tracker.Arm(change == "fingerprint" ? "tune-B" : "tune-A",
            change == "car" ? 1335 : 2177, change == "gear" ? 3 : 2,
            change == "target" ? 7100 : 7000, 1010, 2000, 1011);

        Assert.NotEqual(epoch, nextEpoch);
        Assert.False(tracker.TryConsume(epoch, 1011, out _));
        Assert.False(tracker.TryConsume(nextEpoch, 1011, out _));
    }

    [Fact]
    public void DuplicateGameTimeWithAdvancingReceiptCanCaptureCrossing()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 6995) with { GameTimestampMilliseconds = 50 });
        tracker.Observe(State(1018, 7005) with { GameTimestampMilliseconds = 50 });

        Assert.True(tracker.TryConsume(epoch, 1020, out var observed));
        Assert.Equal(1018, observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegressingOrRepeatedReceiptDisarms(bool repeated)
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1020, 7005));
        tracker.Observe(State(repeated ? 1020 : 1019, 7100));
        Assert.False(tracker.TryConsume(epoch, 1021, out _));
    }

    [Fact]
    public void RegressingGameTimeDisarms()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7005) with { GameTimestampMilliseconds = 50 });
        tracker.Observe(State(1026, 7100) with { GameTimestampMilliseconds = 49 });
        Assert.False(tracker.TryConsume(epoch, 1027, out _));
    }

    [Fact]
    public void ReceiptGapBeyondFreshnessBoundDisarms()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7005));
        tracker.Observe(State(1161, 7100));
        Assert.False(tracker.TryConsume(epoch, 1162, out _));
    }

    [Fact]
    public void PreArmSampleIsIgnoredWithoutInvalidatingNewWatch()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(999, 7200) with { CarOrdinal = 1335 });
        Assert.False(tracker.TryConsume(epoch, 1001, out _));
        tracker.Observe(State(1010, 7005));
        Assert.True(tracker.TryConsume(epoch, 1011, out _));
    }

    [Fact]
    public void BelowReleaseBandClearsOldEvidenceAndStartsNewPullEpoch()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7005));
        tracker.Observe(State(1026, 5949));
        Assert.False(tracker.TryConsume(epoch, 1027, out _));
        var nextEpoch = tracker.Arm("tune-A", 2177, 2, 7000, 1026, 2000, 1027);
        Assert.NotEqual(epoch, nextEpoch);
        tracker.Observe(State(1042, 7005));
        Assert.True(tracker.TryConsume(nextEpoch, 1043, out _));
    }

    [Fact]
    public void ExactReleaseBoundaryKeepsPendingEvidence()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7000));
        tracker.Observe(State(1026, 5950));
        Assert.True(tracker.TryConsume(epoch, 1027, out _));
    }

    [Fact]
    public void BelowBandChangesEpochEvenWhenInitialUiCrossingPrecededWatch()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 5900));
        tracker.Observe(State(1026, 6800));

        var nextEpoch = tracker.Arm("tune-A", 2177, 2, 7000, 1026, 2000, 1027);
        Assert.NotEqual(epoch, nextEpoch);
        Assert.False(tracker.TryConsume(epoch, 1027, out _));
        Assert.False(tracker.TryConsume(nextEpoch, 1027, out _));
    }

    [Fact]
    public void RemainingBelowBandDoesNotContinuouslyAdvanceEpoch()
    {
        var tracker = NewTracker(out _);
        tracker.Observe(State(1010, 5900));
        var belowEpoch = tracker.Arm("tune-A", 2177, 2, 7000, 1010, 2000, 1011);
        tracker.Observe(State(1026, 5800));
        Assert.Equal(belowEpoch, tracker.Arm("tune-A", 2177, 2, 7000, 1026, 2000, 1027));

        tracker.Observe(State(1042, 6800));
        tracker.Observe(State(1058, 5900));
        Assert.NotEqual(belowEpoch, tracker.Arm("tune-A", 2177, 2, 7000, 1058, 2000, 1059));
    }

    [Fact]
    public void SameWatchAfterConsumptionDoesNotEmitAgainUntilPullResets()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7000));
        Assert.True(tracker.TryConsume(epoch, 1011, out _));
        Assert.Equal(epoch, tracker.Arm("tune-A", 2177, 2, 7000, 1010, 2200, 1012));
        tracker.Observe(State(1026, 7500));
        Assert.False(tracker.TryConsume(epoch, 1027, out _));
    }

    [Theory]
    [InlineData(150, true)]
    [InlineData(151, false)]
    public void CrossingConsumptionUsesExisting150MillisecondFreshnessBound(int age, bool expected)
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7005));
        Assert.Equal(expected, tracker.TryConsume(epoch, 1010 + age, out _));
    }

    [Fact]
    public void ObservationAfterWatchExpiryDisarmsEvenWhenArmLaterRefreshes()
    {
        var tracker = new ShiftCueCrossingTracker(1000);
        var epoch = tracker.Arm("tune-A", 2177, 2, 7000, 1000, 1020, 1000);
        tracker.Observe(State(1021, 7005));
        Assert.False(tracker.TryConsume(epoch, 1022, out _));
        var nextEpoch = tracker.Arm("tune-A", 2177, 2, 7000, 1021, 2000, 1022);
        Assert.NotEqual(epoch, nextEpoch);
        Assert.False(tracker.TryConsume(nextEpoch, 1022, out _));
    }

    [Fact]
    public void ExpiredWatchCannotConsumeFreshCrossing()
    {
        var tracker = new ShiftCueCrossingTracker(1000);
        var epoch = tracker.Arm("tune-A", 2177, 2, 7000, 1000, 1020, 1000);
        tracker.Observe(State(1010, 7005));
        Assert.False(tracker.TryConsume(epoch, 1021, out _));
    }

    [Fact]
    public void NewerConcurrentObservationWaitsUntilUiClockCatchesUp()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7005));
        Assert.False(tracker.TryConsume(epoch, 1009, out _));
        Assert.True(tracker.TryConsume(epoch, 1011, out var observed));
        Assert.Equal(1010, observed);
    }

    [Fact]
    public void ResetRequiresRearmAndCannotBeUndoneByLaterPacket()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7005));
        tracker.Reset();
        tracker.Observe(State(1026, 7100));
        Assert.False(tracker.TryConsume(epoch, 1027, out _));
    }

    [Theory]
    [InlineData("empty_identity")]
    [InlineData("invalid_car")]
    [InlineData("invalid_gear")]
    [InlineData("invalid_target")]
    [InlineData("future_floor")]
    [InlineData("stale_floor")]
    [InlineData("expired")]
    public void InvalidArmClearsPendingEvidenceAndReturnsNoEpoch(string reason)
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7005));
        var result = tracker.Arm(reason == "empty_identity" ? "" : "tune-A",
            reason == "invalid_car" ? 0 : 2177, reason == "invalid_gear" ? 0 : 2,
            reason == "invalid_target" ? double.NaN : 7000,
            reason == "future_floor" ? 1030 : reason == "stale_floor" ? 800 : 1000,
            reason == "expired" ? 1020 : 2000, 1020);
        Assert.Equal(0, result);
        Assert.False(tracker.TryConsume(epoch, 1021, out _));
    }

    [Fact]
    public void ConcurrentConsumersCanConsumeOnlyOnce()
    {
        var tracker = NewTracker(out var epoch);
        tracker.Observe(State(1010, 7005));
        var consumed = 0;
        Parallel.For(0, 100, index =>
        {
            if (tracker.TryConsume(epoch, 1011, out _)) Interlocked.Increment(ref consumed);
        });
        Assert.Equal(1, consumed);
    }

    private static ShiftCueCrossingTracker NewTracker(out long epoch)
    {
        var tracker = new ShiftCueCrossingTracker(1000);
        epoch = tracker.Arm("tune-A", 2177, 2, 7000, 1000, 2000, 1000);
        return tracker;
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
