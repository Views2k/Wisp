using Xunit;

namespace Wisp.Core.Tests;

public sealed class ShiftCalibrationGripTests
{
    private const string Fingerprint = "seven-speed-test";

    [Theory]
    [InlineData(.3f)]
    [InlineData(-.3f)]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void BelowGripLimitPullInThirdAndFourthCalibratesAllSevenGears(float normalizedSlip)
    {
        var session = Session();
        long time = 1;
        // Unusable launch gears do not poison a later independent rolling pull.
        Pull(session, ref time, 1, 1200, 8500, 1.3f);
        Pull(session, ref time, 2, 1200, 8500, 1.3f);
        Pull(session, ref time, 3, 1200, 8500, normalizedSlip);
        Pull(session, ref time, 4, 5800, 7800, normalizedSlip);
        var result = session.Evaluate();
        Assert.True(result.Ready, result.Reason);
        Assert.Equal(new[] { 3, 4 }, result.RecordedGears);
        Assert.Equal(3, result.CurveGear);
        Assert.Equal(7, result.Gears.Count);
        Assert.All(result.Gears.Take(6), gear => Assert.True(gear.HasEstimatedTarget));
        Assert.Equal(AccelerationShiftStatus.NoNextGear, result.Gears[6].Status);
    }

    [Theory]
    [InlineData(1.01f)]
    [InlineData(-1.01f)]
    public void BeyondNormalizedGripLimitDoesNotSupplyCurve(float normalizedSlip)
    {
        var session = Session();
        long time = 1;
        Pull(session, ref time, 3, 1200, 8500, normalizedSlip);
        Pull(session, ref time, 4, 5800, 7800, normalizedSlip);
        var result = session.Evaluate();
        Assert.False(result.Ready);
        Assert.Null(result.Profile);
        Assert.Empty(result.RecordedGears);
        Assert.Contains("wheelspin", result.Reason);
    }

    [Theory]
    [InlineData("grip")]
    [InlineData("throttle")]
    [InlineData("metadata")]
    public void ShortFragmentsExplainTheActualInterruption(string cause)
    {
        var session = Session();
        long time = 1;
        for (var i = 0; i < 8; i++)
        {
            Pull(session, ref time, 3, 2000, 2500, .02f);
            if (cause == "metadata") session.BreakObservation();
            else session.Observe(State(time, 3, 2520, cause == "grip" ? 1.3f : .02f) with
            {
                Accelerator = cause == "throttle" ? (byte)100 : (byte)255
            }, Fingerprint, time);
            time += 10;
        }
        var result = session.Evaluate();
        Assert.Null(result.Profile);
        Assert.Contains("Last interruption:", result.Reason);
        Assert.Contains(cause switch
        {
            "grip" => "wheelspin",
            "throttle" => "Keep full throttle",
            _ => "associated car configuration"
        }, result.Reason);
        Assert.DoesNotContain("longer", result.Reason);
    }

    private static ShiftCalibrationSession Session() => new(new(1229, Fingerprint,
        [4d, 3, 2.4, 1.9, 1.5, 1.2, 1], configuredOperatingCeilingRpm: 9000), 1000);

    private static void Pull(ShiftCalibrationSession session, ref long time, int gear, int first, int last, float slip)
    {
        for (var rpm = first; rpm <= last; rpm += 20)
        {
            session.Observe(State(time, gear, rpm, slip), Fingerprint, time);
            time += 10;
        }
    }

    private static VehicleState State(long time, int gear, int rpm, float slip) => TestVehicleState.Create(1229) with
    {
        ReceivedTimestamp = time,
        ReceivedAtUtc = DateTimeOffset.UnixEpoch,
        GameTimestampMilliseconds = (uint)time,
        NumCylinders = 3,
        Gear = (TransmissionGear)gear,
        EngineRpm = rpm,
        Accelerator = 255,
        TorqueNm = 1000 - rpm / 10f,
        PowerWatts = (1000 - rpm / 10f) * rpm * MathF.PI / 30,
        TireSlipRatio = new(slip, slip, slip, slip)
    };
}
