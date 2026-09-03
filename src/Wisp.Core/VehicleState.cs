namespace Wisp.Core;

public enum DrivetrainType
{
    FrontWheelDrive = 0,
    RearWheelDrive = 1,
    AllWheelDrive = 2
}

public enum TransmissionGear
{
    Unknown = -2,
    Reverse = -1,
    Neutral = 0,
    First = 1,
    Second = 2,
    Third = 3,
    Fourth = 4,
    Fifth = 5,
    Sixth = 6,
    Seventh = 7,
    Eighth = 8,
    Ninth = 9,
    Tenth = 10
}

public readonly record struct WheelValues(float FrontLeft, float FrontRight, float RearLeft, float RearRight)
{
    public float MaximumAbsolute() => MathF.Max(
        MathF.Max(MathF.Abs(FrontLeft), MathF.Abs(FrontRight)),
        MathF.Max(MathF.Abs(RearLeft), MathF.Abs(RearRight)));

    public bool AreFinite() =>
        float.IsFinite(FrontLeft) && float.IsFinite(FrontRight) &&
        float.IsFinite(RearLeft) && float.IsFinite(RearRight);
}

public sealed record VehicleState
{
    public required bool IsRaceOn { get; init; }
    public required uint GameTimestampMilliseconds { get; init; }
    public required DateTimeOffset ReceivedAtUtc { get; init; }
    // Optional local Stopwatch ticks, not a field from the FH6 packet.
    public long? ReceivedTimestamp { get; init; }
    public required int CarOrdinal { get; init; }
    public required DrivetrainType Drivetrain { get; init; }
    public int NumCylinders { get; init; } = -1;
    public float PowerWatts { get; init; }
    public float TorqueNm { get; init; }
    public float BoostPressurePsi { get; init; }
    public WheelValues TireTemperatureFahrenheit { get; init; }
    public bool IsElectric => NumCylinders == 0;
    public required float GroundSpeedMetersPerSecond { get; init; }
    public required WheelValues WheelRotationRadiansPerSecond { get; init; }
    public required WheelValues TireSlipRatio { get; init; }
    public required WheelValues TireSlipAngle { get; init; }
    public required WheelValues NormalizedSuspensionTravel { get; init; }
    public required float LateralAccelerationMetersPerSecondSquared { get; init; }
    public required float LongitudinalAccelerationMetersPerSecondSquared { get; init; }
    public required float EngineRpm { get; init; }
    public required float EngineMaximumRpm { get; init; }
    public required TransmissionGear Gear { get; init; }
    public required sbyte Steering { get; init; }
    public required byte Accelerator { get; init; }
    public required byte Brake { get; init; }
}
