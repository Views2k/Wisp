using Wisp.Core;

namespace Wisp.Core.Tests;

internal static class TestVehicleState
{
    public static VehicleState Create(
        int carOrdinal = 100,
        DrivetrainType drivetrain = DrivetrainType.RearWheelDrive,
        float groundSpeed = 30,
        WheelValues? wheelSpeed = null,
        WheelValues? slipRatio = null,
        WheelValues? slipAngle = null,
        float acceleration = 0,
        byte brake = 0) => new()
        {
            IsRaceOn = true,
            GameTimestampMilliseconds = 1234,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            CarOrdinal = carOrdinal,
            Drivetrain = drivetrain,
            GroundSpeedMetersPerSecond = groundSpeed,
            WheelRotationRadiansPerSecond = wheelSpeed ?? new WheelValues(100, 100, 100, 100),
            TireSlipRatio = slipRatio ?? new WheelValues(0.02f, 0.02f, 0.02f, 0.02f),
            TireSlipAngle = slipAngle ?? new WheelValues(0.02f, 0.02f, 0.02f, 0.02f),
            NormalizedSuspensionTravel = new WheelValues(0.5f, 0.5f, 0.5f, 0.5f),
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = acceleration,
            EngineRpm = 4_000,
            EngineMaximumRpm = 8_000,
            Gear = TransmissionGear.Fourth,
            Steering = 0,
            Accelerator = 90,
            Brake = brake
        };
}
