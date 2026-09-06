using System.Diagnostics;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class TelemetryFreshnessTests
{
    private static readonly long Origin = Stopwatch.Frequency * 10;

    [Fact]
    public void TransitionsFromConnectedToLostAfterTimeout()
    {
        var freshness = new TelemetryFreshness(TimeSpan.FromMilliseconds(750));

        Assert.Equal(TelemetryConnectionState.Waiting, freshness.GetState(Origin));
        Assert.Null(freshness.GetAge(Origin));
        freshness.RecordPacket(Origin);
        Assert.Equal(TelemetryConnectionState.Connected, freshness.GetState(AtMilliseconds(749)));
        Assert.Equal(TelemetryConnectionState.Connected, freshness.GetState(AtMilliseconds(750)));
        Assert.Equal(TelemetryConnectionState.Lost, freshness.GetState(AtMilliseconds(751)));
        Assert.Equal(TimeSpan.FromMilliseconds(751), freshness.GetAge(AtMilliseconds(751)));
    }

    [Fact]
    public void StoppedInputStaysLostUntilANewArrivalReconnects()
    {
        var freshness = new TelemetryFreshness(TimeSpan.FromMilliseconds(300));
        freshness.RecordPacket(Origin);

        Assert.Equal(TelemetryConnectionState.Lost, freshness.GetState(AtMilliseconds(301)));
        Assert.Equal(TelemetryConnectionState.Lost, freshness.GetState(AtMilliseconds(10_000)));

        freshness.RecordPacket(AtMilliseconds(10_000));

        Assert.Equal(TelemetryConnectionState.Connected, freshness.GetState(AtMilliseconds(10_000)));
        Assert.Equal(TimeSpan.Zero, freshness.GetAge(AtMilliseconds(10_000)));
        Assert.Equal(TelemetryConnectionState.Lost, freshness.GetState(AtMilliseconds(10_301)));
    }

    [Theory]
    [InlineData(-24)]
    [InlineData(24)]
    public void WallClockChangesDoNotAlterArrivalExpiry(int wallClockShiftHours)
    {
        var freshness = new TelemetryFreshness(TimeSpan.FromMilliseconds(300));
        var packet = TestVehicleState.Create() with
        {
            ReceivedAtUtc = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero),
            ReceivedTimestamp = Origin
        };
        freshness.RecordPacket(packet.ReceivedTimestamp);

        var next = packet with
        {
            ReceivedAtUtc = packet.ReceivedAtUtc.AddHours(wallClockShiftHours),
            ReceivedTimestamp = AtMilliseconds(100)
        };
        freshness.RecordPacket(next.ReceivedTimestamp);

        Assert.Equal(TelemetryConnectionState.Connected, freshness.GetState(AtMilliseconds(399)));
        Assert.Equal(TelemetryConnectionState.Lost, freshness.GetState(AtMilliseconds(401)));
        Assert.Equal(TimeSpan.FromMilliseconds(301), freshness.GetAge(AtMilliseconds(401)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void MissingOrInvalidArrivalCannotEstablishFreshness(long? receivedTimestamp)
    {
        var freshness = new TelemetryFreshness();
        freshness.RecordPacket(receivedTimestamp);

        Assert.Equal(TelemetryConnectionState.Waiting, freshness.GetState(Origin));
        Assert.Null(freshness.GetAge(Origin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void MissingOrInvalidArrivalCannotExtendExistingFreshness(long? receivedTimestamp)
    {
        var freshness = new TelemetryFreshness(TimeSpan.FromMilliseconds(300));
        freshness.RecordPacket(Origin);
        freshness.RecordPacket(receivedTimestamp);

        Assert.Equal(TelemetryConnectionState.Connected, freshness.GetState(AtMilliseconds(299)));
        Assert.Equal(TelemetryConnectionState.Lost, freshness.GetState(AtMilliseconds(301)));
        Assert.Equal(TimeSpan.FromMilliseconds(301), freshness.GetAge(AtMilliseconds(301)));

        freshness.RecordPacket(receivedTimestamp);

        Assert.Equal(TelemetryConnectionState.Lost, freshness.GetState(AtMilliseconds(1_000)));
    }

    [Fact]
    public void ObservationBeforeArrivalFailsClosed()
    {
        var freshness = new TelemetryFreshness();
        freshness.RecordPacket(Origin);

        Assert.Equal(TelemetryConnectionState.Lost, freshness.GetState(Origin - 1));
        Assert.Null(freshness.GetAge(Origin - 1));
    }

    private static long AtMilliseconds(int milliseconds) =>
        Origin + Stopwatch.Frequency * milliseconds / 1_000;
}
