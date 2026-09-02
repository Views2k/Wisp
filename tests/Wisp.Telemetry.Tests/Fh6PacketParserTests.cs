using Wisp.Core;
using Xunit;

namespace Wisp.Telemetry.Tests;

public sealed class Fh6PacketParserTests
{
    private readonly Fh6PacketParser _parser = new();

    [Fact]
    public void ProductionLayoutMatchesDocumented324ByteHorizonOffsets()
    {
        Assert.Equal(324, Fh6PacketLayout.PacketLength);
        Assert.Equal(0, Fh6PacketLayout.IsRaceOn);
        Assert.Equal(4, Fh6PacketLayout.TimestampMilliseconds);
        Assert.Equal(84, Fh6PacketLayout.TireSlipRatio);
        Assert.Equal(100, Fh6PacketLayout.WheelRotationSpeed);
        Assert.Equal(212, Fh6PacketLayout.CarOrdinal);
        Assert.Equal(224, Fh6PacketLayout.DrivetrainType);
        Assert.Equal(228, Fh6PacketLayout.NumCylinders);
        Assert.Equal(256, Fh6PacketLayout.GroundSpeed);
        Assert.Equal(260, Fh6PacketLayout.Power);
        Assert.Equal(264, Fh6PacketLayout.Torque);
        Assert.Equal(268, Fh6PacketLayout.TireTemperature);
        Assert.Equal(284, Fh6PacketLayout.Boost);
        Assert.Equal(315, Fh6PacketLayout.Accelerator);
        Assert.Equal(316, Fh6PacketLayout.Brake);
        Assert.Equal(319, Fh6PacketLayout.Gear);
        Assert.Equal(320, Fh6PacketLayout.Steering);
    }

    [Fact]
    public void ParsesExact324BytePacketAtExpectedOffsets()
    {
        var receivedAt = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

        var parsed = _parser.TryParse(Fh6PacketFixture.Create(), receivedAt, out var state, out var error);

        Assert.True(parsed);
        Assert.Equal(PacketParseError.None, error);
        Assert.NotNull(state);
        Assert.True(state.IsRaceOn);
        Assert.Equal(0xDEADBEEFu, state.GameTimestampMilliseconds);
        Assert.Equal(2468, state.CarOrdinal);
        Assert.Equal(DrivetrainType.RearWheelDrive, state.Drivetrain);
        Assert.Equal(8, state.NumCylinders);
        Assert.False(state.IsElectric);
        Assert.Equal(425_000f, state.PowerWatts);
        Assert.Equal(612.5f, state.TorqueNm);
        Assert.Equal(new WheelValues(218.5f, 221.5f, 203.5f, 206.5f), state.TireTemperatureFahrenheit);
        Assert.Equal(0f, state.BoostPressurePsi);
        Assert.Equal(42.25f, state.GroundSpeedMetersPerSecond);
        Assert.Equal(-5.2f, state.LateralAccelerationMetersPerSecondSquared);
        Assert.Equal(0.4f, state.LongitudinalAccelerationMetersPerSecondSquared);
        Assert.Equal(new WheelValues(101, 102, 201, 202), state.WheelRotationRadiansPerSecond);
        Assert.Equal(TransmissionGear.Fifth, state.Gear);
        Assert.Equal(-24, state.Steering);
        Assert.Equal(receivedAt, state.ReceivedAtUtc);
        Assert.Null(state.ReceivedTimestamp);
    }

    [Fact]
    public void ParsesBoostPressureFromTheDashChannel()
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteSingle(packet, Fh6PacketLayout.Boost, 29.3437f);

        var parsed = _parser.TryParse(packet, DateTimeOffset.UtcNow, out var state, out var error);

        Assert.True(parsed);
        Assert.Equal(PacketParseError.None, error);
        Assert.NotNull(state);
        Assert.Equal(29.3437f, state.BoostPressurePsi);
    }

    [Fact]
    public void ParsesTireTemperaturesFromTheDashChannel()
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteSingle(packet, Fh6PacketLayout.TireTemperature, 251.25f);
        Fh6PacketFixture.WriteSingle(packet, Fh6PacketLayout.TireTemperature + 4, 252.75f);
        Fh6PacketFixture.WriteSingle(packet, Fh6PacketLayout.TireTemperature + 8, 233.5f);
        Fh6PacketFixture.WriteSingle(packet, Fh6PacketLayout.TireTemperature + 12, 234.5f);

        var parsed = _parser.TryParse(packet, DateTimeOffset.UtcNow, out var state, out var error);

        Assert.True(parsed);
        Assert.Equal(PacketParseError.None, error);
        Assert.NotNull(state);
        Assert.Equal(new WheelValues(251.25f, 252.75f, 233.5f, 234.5f), state.TireTemperatureFahrenheit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(9_007_199_254_740_993L)]
    public void PreservesSuppliedMonotonicReceiveTimestampExactly(long? receivedTimestamp)
    {
        var receivedAt = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

        var parsed = _parser.TryParse(Fh6PacketFixture.Create(), receivedAt, out var state, out var error,
            receivedTimestamp);

        Assert.True(parsed);
        Assert.Equal(PacketParseError.None, error);
        Assert.NotNull(state);
        Assert.Equal(receivedTimestamp, state.ReceivedTimestamp);
        Assert.Equal(receivedAt, state.ReceivedAtUtc);
        Assert.Equal(0xDEADBEEFu, state.GameTimestampMilliseconds);
    }

    [Theory]
    [InlineData(0, TransmissionGear.Reverse)]
    [InlineData(1, TransmissionGear.First)]
    [InlineData(2, TransmissionGear.Second)]
    [InlineData(3, TransmissionGear.Third)]
    [InlineData(10, TransmissionGear.Tenth)]
    [InlineData(11, TransmissionGear.Neutral)]
    [InlineData(12, TransmissionGear.Unknown)]
    [InlineData(255, TransmissionGear.Unknown)]
    public void DecodesEmpiricallyVerifiedFh6GearValues(byte rawGear, TransmissionGear expected)
    {
        Assert.Equal(expected, Fh6GearDecoder.Decode(rawGear));
    }

    [Fact]
    public void RejectsTruncatedPacket()
    {
        var packet = Fh6PacketFixture.Create()[..323];

        var parsed = _parser.TryParse(packet, DateTimeOffset.UtcNow, out var state, out var error);

        Assert.False(parsed);
        Assert.Null(state);
        Assert.Equal(PacketParseError.IncorrectLength, error);
    }

    [Fact]
    public void RejectsMalformedDrivetrain()
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteInt32(packet, Fh6PacketLayout.DrivetrainType, 99);

        var parsed = _parser.TryParse(packet, DateTimeOffset.UtcNow, out var state, out var error);

        Assert.False(parsed);
        Assert.Null(state);
        Assert.Equal(PacketParseError.InvalidDrivetrain, error);
    }

    [Fact]
    public void RejectsNonFiniteTelemetry()
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteSingle(packet, Fh6PacketLayout.GroundSpeed, float.NaN);

        var parsed = _parser.TryParse(packet, DateTimeOffset.UtcNow, out _, out var error);

        Assert.False(parsed);
        Assert.Equal(PacketParseError.NonFiniteValue, error);
    }

    [Fact]
    public void ParsesZeroCylinderElectricPowerAndRegeneration()
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteInt32(packet, Fh6PacketLayout.NumCylinders, 0);
        Fh6PacketFixture.WriteSingle(packet, Fh6PacketLayout.Power, -84_500f);
        Fh6PacketFixture.WriteSingle(packet, Fh6PacketLayout.Torque, -310.25f);

        var parsed = _parser.TryParse(packet, DateTimeOffset.UtcNow, out var state, out var error);

        Assert.True(parsed);
        Assert.Equal(PacketParseError.None, error);
        Assert.NotNull(state);
        Assert.True(state.IsElectric);
        Assert.Equal(0, state.NumCylinders);
        Assert.Equal(-84_500f, state.PowerWatts);
        Assert.Equal(-310.25f, state.TorqueNm);
    }

    [Theory]
    [InlineData(Fh6PacketLayout.Power)]
    [InlineData(Fh6PacketLayout.Torque)]
    [InlineData(Fh6PacketLayout.TireTemperature)]
    [InlineData(Fh6PacketLayout.Boost)]
    public void RejectsNonFinitePowertrainTelemetry(int offset)
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteSingle(packet, offset, float.NaN);

        var parsed = _parser.TryParse(packet, DateTimeOffset.UtcNow, out var state, out var error);

        Assert.False(parsed);
        Assert.Null(state);
        Assert.Equal(PacketParseError.NonFiniteValue, error);
    }

    [Theory]
    [InlineData(Fh6PacketLayout.NumCylinders, -1)]
    [InlineData(Fh6PacketLayout.NumCylinders, 33)]
    public void RejectsImplausibleCylinderCounts(int offset, int value)
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteInt32(packet, offset, value);

        var parsed = _parser.TryParse(packet, DateTimeOffset.UtcNow, out var state, out var error);

        Assert.False(parsed);
        Assert.Null(state);
        Assert.Equal(PacketParseError.ImplausibleValue, error);
    }

    [Theory]
    [InlineData(Fh6PacketLayout.Power, 101_000_000f)]
    [InlineData(Fh6PacketLayout.Torque, 11_000_000f)]
    public void RejectsImplausiblePowertrainTelemetry(int offset, float value)
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteSingle(packet, offset, value);

        var parsed = _parser.TryParse(packet, DateTimeOffset.UtcNow, out var state, out var error);

        Assert.False(parsed);
        Assert.Null(state);
        Assert.Equal(PacketParseError.ImplausibleValue, error);
    }
}
