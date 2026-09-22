using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCaptureExperimentCoverageTests
{
    [Fact]
    public void SixPassCandidatesAndParkedIdentityRoundTripProvideCoverageWithoutClaimingAccuracy()
    {
        var capture = new ShiftCaptureExperimentCoverage(1_000);
        long clock = 1_000;
        Pass(capture, ref clock, 3, 20);
        Pass(capture, ref clock, 3, 21);
        Pass(capture, ref clock, 4, 22);
        Pass(capture, ref clock, 4, 23);
        Pass(capture, ref clock, 4, 30, fingerprint: "boost", car: 2, boost: 8);
        Pass(capture, ref clock, 4, 31, fingerprint: "boost", car: 2, boost: 9);
        Park(capture, ref clock, "a");
        capture.InvalidateContext();
        Park(capture, ref clock, "tune-b");
        capture.InvalidateContext();
        Park(capture, ref clock, "a");
        var result = capture.Snapshot();
        Assert.True(result.ExperimentChecklistSatisfied);
        Assert.Equal(6, result.Segments.Length);
        var match = Assert.Single(result.MatchedSpeedCandidates);
        Assert.Equal(3, match.LowerGear);
        Assert.Equal(4, match.UpperGear);
        Assert.Equal(2, match.LowerGearPasses);
        Assert.Equal(2, match.UpperGearPasses);
        Assert.Equal(23, match.MinimumSpeedMetersPerSecond);
        Assert.Equal(26, match.MaximumSpeedMetersPerSecond);
        Assert.Equal(2, Assert.Single(result.Profiles, profile => profile.Fingerprint == "boost").PositiveBoostSegments);
        Assert.Equal(1, Assert.Single(result.ParkedIdentityRoundTrips).CarOrdinal);
        Assert.Contains("not equivalent", result.Scope);
        Assert.Contains("not engine type", result.Scope);
        Assert.Contains("No new limiter cut", result.Heuristics);
    }

    [Fact]
    public void OnePassPerGearOrNonoverlappingRepeatedPassesDoNotProvideMatchedCoverage()
    {
        var capture = new ShiftCaptureExperimentCoverage(1_000);
        long clock = 1_000;
        Pass(capture, ref clock, 3, 20);
        Pass(capture, ref clock, 4, 20);
        Assert.Empty(capture.Snapshot().MatchedSpeedCandidates);
        Pass(capture, ref clock, 3, 40);
        Pass(capture, ref clock, 4, 40);
        Assert.Empty(capture.Snapshot().MatchedSpeedCandidates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentCarsOrTunesCannotSupplyEachOthersMissingPasses(bool differentCar)
    {
        var capture = new ShiftCaptureExperimentCoverage(1_000);
        long clock = 1_000;
        Pass(capture, ref clock, 3, 20);
        Pass(capture, ref clock, 4, 20);
        Pass(capture, ref clock, 3, 20, car: differentCar ? 2 : 1, fingerprint: differentCar ? "a" : "b");
        Pass(capture, ref clock, 4, 20, car: differentCar ? 2 : 1, fingerprint: differentCar ? "a" : "b");
        Assert.Empty(capture.Snapshot().MatchedSpeedCandidates);
    }

    [Fact]
    public void LiftGearAndContextChangesSealCandidatesWithoutJoiningThem()
    {
        var capture = new ShiftCaptureExperimentCoverage(1_000);
        long clock = 1_000;
        Pass(capture, ref clock, 3, 20, lift: false);
        Assert.Equal("active", Assert.Single(capture.Snapshot().Segments).EndReason);
        Pass(capture, ref clock, 4, 25, lift: false);
        capture.InvalidateContext();
        var segments = capture.Snapshot().Segments;
        Assert.Equal(2, segments.Length);
        Assert.Equal("gear-changed", segments[0].EndReason);
        Assert.Equal("context-lost", segments[1].EndReason);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("unassociated")]
    [InlineData("gap")]
    [InlineData("traction")]
    [InlineData("backward-clock")]
    public void BrokenContinuityCannotJoinTwoShortSegments(string interruption)
    {
        var capture = new ShiftCaptureExperimentCoverage(1_000);
        for (var i = 0; i <= 6; i++)
            capture.Observe(State(3, (uint)(1_000 + i * 50), 20 + i), 1_000 + i * 50, "a");
        if (interruption == "context") capture.InvalidateContext();
        if (interruption == "unassociated") capture.Observe(State(3, 1_325, 26), 1_325, null);
        if (interruption == "traction") capture.Observe(State(3, 1_325, 26) with { TireSlipRatio = new(.5f, 0, 0, 0) }, 1_325, "a");
        if (interruption == "backward-clock") capture.Observe(State(3, 1_200, 26), 1_325, "a");
        var start = interruption == "gap" ? 1_500 : 1_350;
        for (var i = 0; i <= 6; i++)
            capture.Observe(State(3, (uint)(start + i * 50), 27 + i), start + i * 50, "a");
        capture.InvalidateContext();
        Assert.Empty(capture.Snapshot().Segments);
    }

    [Fact]
    public void SharedTimestampsRetainSamplesButAStalledClockCannotCreateALongPass()
    {
        var capture = new ShiftCaptureExperimentCoverage(1_000);
        for (var i = 0; i <= 40; i++)
            capture.Observe(State(3, (uint)(1_000 + i / 2 * 50), 20 + i * .2f), 1_000 + i * 25, "a");
        var segment = Assert.Single(capture.Snapshot().Segments);
        Assert.Equal(41, segment.Samples);
        Assert.Equal(1, segment.DurationSeconds);

        var stalled = new ShiftCaptureExperimentCoverage(1_000);
        for (var i = 0; i <= 40; i++)
            stalled.Observe(State(3, 1_000, 20 + i * .2f), 1_000 + i * 25, "a");
        Assert.Empty(stalled.Snapshot().Segments);
        Assert.Equal(1, stalled.Snapshot().ContinuityGaps);
    }

    [Theory]
    [InlineData("different-car")]
    [InlineData("moving-departure")]
    [InlineData("moving-arrival")]
    [InlineData("not-returned")]
    public void IdentityRoundTripRequiresSameCarAndParkedObservations(string failure)
    {
        var capture = new ShiftCaptureExperimentCoverage(1_000);
        long clock = 1_000;
        Park(capture, ref clock, "a");
        if (failure == "moving-departure") capture.Observe(State(1, (uint)clock, 10), clock++, "a");
        if (failure == "moving-arrival") capture.Observe(State(1, (uint)clock, 10), clock++, "b");
        else Park(capture, ref clock, "b", failure == "different-car" ? 2 : 1);
        Park(capture, ref clock, failure == "not-returned" ? "c" : "a");
        Assert.Empty(capture.Snapshot().ParkedIdentityRoundTrips);
    }

    [Fact]
    public void PositiveBoostIsDescriptiveAndRepeatedEvidenceMustBelongToOneProfile()
    {
        var capture = new ShiftCaptureExperimentCoverage(1_000);
        long clock = 1_000;
        Pass(capture, ref clock, 3, 20, boost: .01f);
        Pass(capture, ref clock, 3, 20, fingerprint: "b", boost: 20);
        Assert.All(capture.Snapshot().Profiles, profile => Assert.Equal(1, profile.PositiveBoostSegments));
        Assert.Contains(capture.Snapshot().ActionableMissingEvidence, item => item.Contains("positive boost", StringComparison.Ordinal));
        Pass(capture, ref clock, 3, 20, boost: .02f);
        Assert.DoesNotContain(capture.Snapshot().ActionableMissingEvidence, item => item.Contains("positive boost", StringComparison.Ordinal));
    }

    [Fact]
    public void ProfileSegmentAndTransitionLimitsStayExplicitAndBounded()
    {
        var profiles = new ShiftCaptureExperimentCoverage(1_000);
        long clock = 1_000;
        for (var i = 0; i < 9; i++) Park(profiles, ref clock, "p" + i);
        Assert.Equal(8, profiles.Snapshot().Profiles.Length);
        Assert.True(profiles.Snapshot().Truncated);

        var segments = new ShiftCaptureExperimentCoverage(1_000);
        clock = 1_000;
        for (var i = 0; i < 129; i++) Pass(segments, ref clock, 3, 20);
        Assert.Equal(128, segments.Snapshot().Segments.Length);
        Assert.True(segments.Snapshot().Truncated);

        var transitions = new ShiftCaptureExperimentCoverage(1_000);
        clock = 1_000;
        for (var i = 0; i < 35; i++) Park(transitions, ref clock, i % 2 == 0 ? "a" : "b");
        Assert.Equal(32, transitions.Snapshot().IdentityTransitions.Length);
        Assert.True(transitions.Snapshot().Truncated);
    }

    private static void Pass(ShiftCaptureExperimentCoverage capture, ref long clock, int gear, float start,
        string fingerprint = "a", int car = 1, float boost = 0, bool lift = true)
    {
        for (var i = 0; i <= 20; i++)
        {
            capture.Observe(State(gear, (uint)clock, start + i * .3f, car, boost), clock, fingerprint);
            clock += 50;
        }
        if (!lift) return;
        capture.Observe(State(gear, (uint)clock, start + 6, car, boost) with { Accelerator = 0 }, clock, fingerprint);
        clock += 50;
    }

    private static void Park(ShiftCaptureExperimentCoverage capture, ref long clock, string fingerprint, int car = 1)
    {
        capture.Observe(State(1, (uint)clock, 0, car) with { Accelerator = 0 }, clock, fingerprint);
        clock += 50;
    }

    private static VehicleState State(int gear, uint time, float speed, int car = 1, float boost = 0) => new()
    {
        IsRaceOn = true,
        CarOrdinal = car,
        NumCylinders = 8,
        Gear = (TransmissionGear)gear,
        GameTimestampMilliseconds = time,
        ReceivedAtUtc = DateTimeOffset.UnixEpoch,
        Drivetrain = DrivetrainType.RearWheelDrive,
        EngineRpm = 4_000 + speed * 50,
        EngineMaximumRpm = 8_000,
        GroundSpeedMetersPerSecond = speed,
        Accelerator = 255,
        Brake = 0,
        Steering = 0,
        TorqueNm = 500,
        PowerWatts = 200_000,
        BoostPressurePsi = boost,
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 2
    };
}
