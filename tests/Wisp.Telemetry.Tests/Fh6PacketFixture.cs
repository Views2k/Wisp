using System.Buffers.Binary;

namespace Wisp.Telemetry.Tests;

internal static class Fh6PacketFixture
{
    // Keep these fixture offsets independent from Fh6PacketLayout. If the
    // production layout changes accidentally, the parser test must fail.
    private const int PacketLength = 324;
    private const int IsRaceOn = 0;
    private const int TimestampMilliseconds = 4;
    private const int EngineMaximumRpm = 8;
    private const int CurrentEngineRpm = 16;
    private const int LateralAcceleration = 20;
    private const int LongitudinalAcceleration = 28;
    private const int NormalizedSuspensionTravel = 68;
    private const int TireSlipRatio = 84;
    private const int WheelRotationSpeed = 100;
    private const int TireSlipAngle = 164;
    private const int CarOrdinal = 212;
    private const int DrivetrainType = 224;
    private const int NumCylinders = 228;
    private const int GroundSpeed = 256;
    private const int Power = 260;
    private const int Torque = 264;
    private const int TireTemperature = 268;
    private const int Accelerator = 315;
    private const int Brake = 316;
    private const int Gear = 319;
    private const int Steering = 320;

    public static byte[] Create()
    {
        var packet = new byte[PacketLength];
        WriteInt32(packet, IsRaceOn, 1);
        WriteUInt32(packet, TimestampMilliseconds, 0xDEADBEEF);
        WriteSingle(packet, EngineMaximumRpm, 8_200);
        WriteSingle(packet, CurrentEngineRpm, 5_100);
        WriteSingle(packet, LateralAcceleration, -5.2f);
        WriteSingle(packet, LongitudinalAcceleration, 0.4f);
        WriteWheels(packet, NormalizedSuspensionTravel, 0.4f, 0.5f, 0.6f, 0.7f);
        WriteWheels(packet, TireSlipRatio, 0.01f, 0.02f, 0.03f, 0.04f);
        WriteWheels(packet, WheelRotationSpeed, 101, 102, 201, 202);
        WriteWheels(packet, TireSlipAngle, 0.05f, 0.06f, 0.07f, 0.08f);
        WriteInt32(packet, CarOrdinal, 2468);
        WriteInt32(packet, DrivetrainType, 1);
        WriteInt32(packet, NumCylinders, 8);
        WriteSingle(packet, GroundSpeed, 42.25f);
        WriteSingle(packet, Power, 425_000f);
        WriteSingle(packet, Torque, 612.5f);
        WriteWheels(packet, TireTemperature, 218.5f, 221.5f, 203.5f, 206.5f);
        packet[Accelerator] = 211;
        packet[Brake] = 7;
        packet[Gear] = 5;
        packet[Steering] = unchecked((byte)-24);
        packet[323] = 0xA5;
        return packet;
    }

    public static void WriteInt32(byte[] packet, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset, sizeof(int)), value);

    public static void WriteSingle(byte[] packet, int offset, float value) =>
        WriteInt32(packet, offset, BitConverter.SingleToInt32Bits(value));

    private static void WriteUInt32(byte[] packet, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, sizeof(uint)), value);

    private static void WriteWheels(byte[] packet, int offset, float first, float second, float third, float fourth)
    {
        WriteSingle(packet, offset, first);
        WriteSingle(packet, offset + 4, second);
        WriteSingle(packet, offset + 8, third);
        WriteSingle(packet, offset + 12, fourth);
    }
}
