using Xunit;

namespace Wisp.Core.Tests;

public sealed class TelemetryFreshnessTests
{
    [Fact]
    public void TransitionsFromConnectedToLostAfterTimeout()
    {
        var origin = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var freshness = new TelemetryFreshness(TimeSpan.FromMilliseconds(750));

        Assert.Equal(TelemetryConnectionState.Waiting, freshness.GetState(origin));
        freshness.RecordPacket(origin);
        Assert.Equal(TelemetryConnectionState.Connected, freshness.GetState(origin.AddMilliseconds(749)));
        Assert.Equal(TelemetryConnectionState.Lost, freshness.GetState(origin.AddMilliseconds(751)));
    }
}
