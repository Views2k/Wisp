namespace Wisp.Telemetry;

public static class Fh6PacketLayout
{
    public const int PacketLength = 324;

    public const int IsRaceOn = 0;
    public const int TimestampMilliseconds = 4;
    public const int EngineMaximumRpm = 8;
    public const int CurrentEngineRpm = 16;
    public const int LateralAcceleration = 20;
    public const int LongitudinalAcceleration = 28;
    public const int NormalizedSuspensionTravel = 68;
    public const int TireSlipRatio = 84;
    public const int WheelRotationSpeed = 100;
    public const int TireSlipAngle = 164;
    public const int CarOrdinal = 212;
    public const int DrivetrainType = 224;
    public const int NumCylinders = 228;

    // The 324-byte Horizon packet inserts 12 Horizon-specific bytes after the
    // common 232-byte Sled prefix. The Dash fields therefore begin at 244.
    public const int HorizonExtension = 232;
    public const int GroundSpeed = 256;
    public const int Power = 260;
    public const int Torque = 264;
    public const int TireTemperature = 268;
    public const int Boost = 284;
    public const int Accelerator = 315;
    public const int Brake = 316;
    public const int Gear = 319;
    public const int Steering = 320;
}
