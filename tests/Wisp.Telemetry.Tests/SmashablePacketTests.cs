using Xunit;

namespace Wisp.Telemetry.Tests;

public sealed class SmashablePacketTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(2.5f, 120f)]
    [InlineData(500f, 10_000_000f)]
    public void DocumentedHorizonImpactFieldsKeepIndependentDashOffsets(float loss, float mass)
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteInt32(packet, 232, 37);
        Fh6PacketFixture.WriteSingle(packet, 236, loss);
        Fh6PacketFixture.WriteSingle(packet, 240, mass);
        Fh6PacketFixture.WriteSingle(packet, 244, 180.5f);
        Fh6PacketFixture.WriteSingle(packet, 248, 20.25f);
        Fh6PacketFixture.WriteSingle(packet, 252, -30f);
        Assert.True(new Fh6PacketParser().TryParse(packet, DateTimeOffset.UtcNow, out var state, out var error));
        Assert.Equal(PacketParseError.None, error);
        Assert.Equal(loss, state!.SmashableVelocityLossMetersPerSecond);
        Assert.Equal(mass, state.SmashableMassKilograms);
        Assert.Equal(180.5f, state.Lap!.Position.X);
        Assert.Equal(20.25f, state.Lap.Position.Y);
        Assert.Equal(-30f, state.Lap.Position.Z);
        Assert.Equal(42.25f, state.GroundSpeedMetersPerSecond);
        Assert.Equal(236, Fh6PacketLayout.SmashableVelocityLoss);
        Assert.Equal(240, Fh6PacketLayout.SmashableMass);
    }

    [Theory]
    [InlineData(float.NaN, 10f)]
    [InlineData(2f, float.PositiveInfinity)]
    [InlineData(-1f, 10f)]
    [InlineData(501f, 10f)]
    [InlineData(2f, -1f)]
    [InlineData(2f, 10_000_001f)]
    public void InvalidOptionalImpactDataDoesNotDiscardHealthyTelemetry(float loss, float mass)
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteSingle(packet, 236, loss);
        Fh6PacketFixture.WriteSingle(packet, 240, mass);
        Assert.True(new Fh6PacketParser().TryParse(packet, DateTimeOffset.UtcNow, out var state, out var error));
        Assert.Equal(PacketParseError.None, error);
        Assert.Null(state!.SmashableVelocityLossMetersPerSecond);
        Assert.Null(state.SmashableMassKilograms);
        Assert.Equal(42.25f, state.GroundSpeedMetersPerSecond);
    }
}
