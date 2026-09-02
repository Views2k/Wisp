using System.Buffers.Binary;
using Wisp.Core;

namespace Wisp.Telemetry;

public enum PacketParseError
{
    None,
    IncorrectLength,
    InvalidRaceFlag,
    InvalidDrivetrain,
    NonFiniteValue,
    ImplausibleValue
}

public sealed class Fh6PacketParser
{
    private const int MaximumPlausibleCylinderCount = 32;
    private const float MaximumPlausibleAbsolutePowerWatts = 100_000_000;
    private const float MaximumPlausibleAbsoluteTorqueNm = 10_000_000;
    private const float MaximumPlausibleAbsoluteBoostPsi = 200;

    public bool TryParse(
        ReadOnlySpan<byte> packet,
        DateTimeOffset receivedAtUtc,
        out VehicleState? state,
        out PacketParseError error,
        long? receivedTimestamp = null)
    {
        state = null;
        if (packet.Length != Fh6PacketLayout.PacketLength)
        {
            error = PacketParseError.IncorrectLength;
            return false;
        }

        var raceFlag = ReadInt32(packet, Fh6PacketLayout.IsRaceOn);
        if (raceFlag is not 0 and not 1)
        {
            error = PacketParseError.InvalidRaceFlag;
            return false;
        }

        var drivetrainValue = ReadInt32(packet, Fh6PacketLayout.DrivetrainType);
        if (drivetrainValue is < 0 or > 2)
        {
            error = PacketParseError.InvalidDrivetrain;
            return false;
        }

        var wheelRotation = ReadWheels(packet, Fh6PacketLayout.WheelRotationSpeed);
        var slipRatio = ReadWheels(packet, Fh6PacketLayout.TireSlipRatio);
        var slipAngle = ReadWheels(packet, Fh6PacketLayout.TireSlipAngle);
        var suspension = ReadWheels(packet, Fh6PacketLayout.NormalizedSuspensionTravel);
        var maximumRpm = ReadSingle(packet, Fh6PacketLayout.EngineMaximumRpm);
        var currentRpm = ReadSingle(packet, Fh6PacketLayout.CurrentEngineRpm);
        var lateralAcceleration = ReadSingle(packet, Fh6PacketLayout.LateralAcceleration);
        var longitudinalAcceleration = ReadSingle(packet, Fh6PacketLayout.LongitudinalAcceleration);
        var groundSpeed = ReadSingle(packet, Fh6PacketLayout.GroundSpeed);
        var powerWatts = ReadSingle(packet, Fh6PacketLayout.Power);
        var torqueNm = ReadSingle(packet, Fh6PacketLayout.Torque);
        var tireTemperatureFahrenheit = ReadWheels(packet, Fh6PacketLayout.TireTemperature);
        var boostPressurePsi = ReadSingle(packet, Fh6PacketLayout.Boost);
        var numCylinders = ReadInt32(packet, Fh6PacketLayout.NumCylinders);

        if (!wheelRotation.AreFinite() || !slipRatio.AreFinite() || !slipAngle.AreFinite() ||
            !suspension.AreFinite() || !float.IsFinite(maximumRpm) || !float.IsFinite(currentRpm) ||
            !float.IsFinite(lateralAcceleration) || !float.IsFinite(longitudinalAcceleration) ||
            !float.IsFinite(groundSpeed) || !float.IsFinite(powerWatts) || !float.IsFinite(torqueNm) ||
            !tireTemperatureFahrenheit.AreFinite() || !float.IsFinite(boostPressurePsi))
        {
            error = PacketParseError.NonFiniteValue;
            return false;
        }

        if (MathF.Abs(groundSpeed) > 500 || maximumRpm is < 0 or > 30_000 ||
            currentRpm is < 0 or > 40_000 || wheelRotation.MaximumAbsolute() > 10_000 ||
            numCylinders is < 0 or > MaximumPlausibleCylinderCount ||
            MathF.Abs(powerWatts) > MaximumPlausibleAbsolutePowerWatts ||
            MathF.Abs(torqueNm) > MaximumPlausibleAbsoluteTorqueNm ||
            MathF.Abs(boostPressurePsi) > MaximumPlausibleAbsoluteBoostPsi)
        {
            error = PacketParseError.ImplausibleValue;
            return false;
        }

        state = new VehicleState
        {
            IsRaceOn = raceFlag == 1,
            GameTimestampMilliseconds = ReadUInt32(packet, Fh6PacketLayout.TimestampMilliseconds),
            ReceivedAtUtc = receivedAtUtc,
            ReceivedTimestamp = receivedTimestamp,
            CarOrdinal = ReadInt32(packet, Fh6PacketLayout.CarOrdinal),
            Drivetrain = (DrivetrainType)drivetrainValue,
            NumCylinders = numCylinders,
            PowerWatts = powerWatts,
            TorqueNm = torqueNm,
            TireTemperatureFahrenheit = tireTemperatureFahrenheit,
            BoostPressurePsi = boostPressurePsi,
            GroundSpeedMetersPerSecond = groundSpeed,
            WheelRotationRadiansPerSecond = wheelRotation,
            TireSlipRatio = slipRatio,
            TireSlipAngle = slipAngle,
            NormalizedSuspensionTravel = suspension,
            LateralAccelerationMetersPerSecondSquared = lateralAcceleration,
            LongitudinalAccelerationMetersPerSecondSquared = longitudinalAcceleration,
            EngineRpm = currentRpm,
            EngineMaximumRpm = maximumRpm,
            Gear = Fh6GearDecoder.Decode(packet[Fh6PacketLayout.Gear]),
            Steering = unchecked((sbyte)packet[Fh6PacketLayout.Steering]),
            Accelerator = packet[Fh6PacketLayout.Accelerator],
            Brake = packet[Fh6PacketLayout.Brake]
        };

        error = PacketParseError.None;
        return true;
    }

    private static WheelValues ReadWheels(ReadOnlySpan<byte> packet, int offset) => new(
        ReadSingle(packet, offset),
        ReadSingle(packet, offset + 4),
        ReadSingle(packet, offset + 8),
        ReadSingle(packet, offset + 12));

    private static int ReadInt32(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(offset, sizeof(int)));

    private static uint ReadUInt32(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(offset, sizeof(uint)));

    private static float ReadSingle(ReadOnlySpan<byte> packet, int offset) =>
        BitConverter.Int32BitsToSingle(ReadInt32(packet, offset));
}
