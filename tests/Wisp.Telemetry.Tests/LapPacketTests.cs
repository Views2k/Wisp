using System.Buffers.Binary;
using Xunit;

namespace Wisp.Telemetry.Tests;

public sealed class LapPacketTests
{
    [Fact]
    public void ParsesOptionalDashLapChannelsAtIndependentOffsets()
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteSingle(packet, 244, 123.5f);
        Fh6PacketFixture.WriteSingle(packet, 248, -12.25f);
        Fh6PacketFixture.WriteSingle(packet, 252, 543.75f);
        Fh6PacketFixture.WriteSingle(packet, 300, 62.345f);
        Fh6PacketFixture.WriteSingle(packet, 304, 21.25f);
        Fh6PacketFixture.WriteSingle(packet, 308, 146.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(312, 2), 3);
        packet[314] = 2;
        Assert.True(new Fh6PacketParser().TryParse(packet, DateTimeOffset.UtcNow, out var state, out _));
        var lap = Assert.IsType<Wisp.Core.LapTelemetry>(state!.Lap);
        Assert.Equal(new Wisp.Core.LapPosition(123.5f, -12.25f, 543.75f), lap.Position);
        Assert.Equal(62.345f, lap.LastLapSeconds);
        Assert.Equal(21.25f, lap.CurrentLapSeconds);
        Assert.Equal(146, lap.RaceSeconds);
        Assert.Equal(3, lap.LapNumber);
        Assert.Equal(2, lap.RacePosition);
        Assert.Equal(42.25f, state.GroundSpeedMetersPerSecond);
    }

    [Theory]
    [InlineData(244, float.NaN)]
    [InlineData(248, float.PositiveInfinity)]
    [InlineData(252, 100_000_000)]
    [InlineData(300, -1)]
    [InlineData(304, float.NaN)]
    [InlineData(308, float.PositiveInfinity)]
    public void BadLapChannelDoesNotBreakExistingGauges(int offset, float value)
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteSingle(packet, offset, value);
        Assert.True(new Fh6PacketParser().TryParse(packet, DateTimeOffset.UtcNow, out var state, out _));
        Assert.Null(state!.Lap);
        Assert.Equal(5100, state.EngineRpm);
    }
}
