using Xunit;

namespace Wisp.Core.Tests;

public sealed class ShiftCalibrationPartialTests
{
    [Fact]
    public void MissingLaunchGearDoesNotBlockConfirmedHigherGearTargets()
    {
        var result = Collect(observeCut: true, confirmationTorque: 500);
        Assert.True(result.Ready, result.Reason);
        Assert.False(result.Gears[0].HasEstimatedTarget);
        Assert.Equal(AccelerationShiftStatus.InsufficientCurveDomain, result.Gears[0].Status);
        Assert.True(result.Gears[1].HasEstimatedTarget);
        Assert.Equal(AccelerationShiftStatus.MeasuredUpperBoundary, result.Gears[1].Status);
        Assert.False(result.Gears[1].OperatingCeilingVerified);
        Assert.True(result.ConfirmingUpshifts > 0);
        Assert.True(ShiftCalibrationSession.TryRestore(result.Context, result.Profile!.Samples,
            result.EmpiricalUpperRpm, result.ConfirmingUpshifts, result.AcceptedSamples, out var restored));
        Assert.False(restored!.Gears[0].HasEstimatedTarget);
        Assert.Equal(result.Gears[1].EstimatedTargetRpm, restored.Gears[1].EstimatedTargetRpm);
    }

    [Theory]
    [InlineData(false, 500)]
    [InlineData(true, 350)]
    public void PartialCoverageStillRequiresMeasuredBoundaryAndIndependentConfirmation(bool observeCut, double confirmationTorque)
    {
        var result = Collect(observeCut, confirmationTorque);
        Assert.False(result.Ready);
        if (!observeCut) Assert.All(result.Gears, gear => Assert.False(gear.HasEstimatedTarget));
        else Assert.Equal(0, result.ConfirmingUpshifts);
    }

    private static ShiftCalibrationResult Collect(bool observeCut, double confirmationTorque)
    {
        var context = new ShiftCalibrationContext(100, "partial-test", [4d, 2, 1.8, 1.6, 1.4, 1.2, 1],
            configuredOperatingCeilingRpm: 8000);
        var session = new ShiftCalibrationSession(context, 1000);
        long time = 1;
        void Add(int gear, int rpm, double torque, bool limiter = false)
        {
            var state = TestVehicleState.Create() with
            {
                CarOrdinal = 100,
                NumCylinders = 8,
                Gear = (TransmissionGear)gear,
                EngineRpm = rpm,
                TorqueNm = (float)torque,
                PowerWatts = (float)(torque * rpm * Math.PI / 30),
                Accelerator = 255,
                ReceivedTimestamp = time,
                GameTimestampMilliseconds = (uint)time
            };
            session.Observe(state, context.Fingerprint, time, limiter);
            time += 10;
        }
        for (var rpm = 5500; rpm <= 7980; rpm += 20) Add(3, rpm, 500);
        if (observeCut)
            for (var i = 0; i < 18; i++) Add(3, 7950, 0, limiter: true);
        for (var rpm = 6500; rpm <= 7900; rpm += 20) Add(4, rpm, confirmationTorque);
        return session.Evaluate();
    }
}
