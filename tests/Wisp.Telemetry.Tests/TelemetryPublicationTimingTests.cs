using Wisp.Core;
using Xunit;

namespace Wisp.Telemetry.Tests;

public sealed class TelemetryPublicationTimingTests
{
    [Fact]
    public void ExactReferenceIsRequiredAndOverwrittenEvidenceIsUnavailable()
    {
        var ledger = new TelemetryPublicationTimingLedger();
        var original = State(100);
        ledger.Record(original, new(100, 101, 102, 103));
        Assert.True(ledger.TryGet(original, out var timing));
        Assert.Equal(new(100, 101, 102, 103), timing);
        Assert.False(ledger.TryGet(original with { }, out _));
        for (var i = 0; i < 8; i++)
        {
            var timestamp = 200 + i * 10;
            ledger.Record(State(timestamp), new(timestamp, timestamp + 1, timestamp + 2, timestamp + 3));
        }
        Assert.False(ledger.TryGet(original, out _));
    }

    [Theory]
    [InlineData(0, 1, 2, 3)]
    [InlineData(100, 99, 101, 102)]
    [InlineData(100, 101, 100, 102)]
    [InlineData(100, 101, 102, 101)]
    [InlineData(99, 101, 102, 103)]
    public void InvalidOrMismatchedClockEvidenceIsRejected(long received, long started, long parsed, long published)
    {
        var ledger = new TelemetryPublicationTimingLedger();
        var state = State(100);
        ledger.Record(state, new(received, started, parsed, published));
        Assert.False(ledger.TryGet(state, out _));
    }

    [Fact]
    public async Task ConcurrentBoundedReadsNeverCombineDifferentPublicationSlots()
    {
        var ledger = new TelemetryPublicationTimingLedger();
        var states = Enumerable.Range(0, 32).Select(i => State(100 + i * 10)).ToArray();
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 100000; i++)
            {
                var state = states[i % states.Length]; var t = state.ReceivedTimestamp!.Value;
                ledger.Record(state, new(t, t + 1, t + 2, t + 3));
            }
        }, TestContext.Current.CancellationToken);
        for (var i = 0; i < 100000; i++)
        {
            var state = states[i % states.Length]; var t = state.ReceivedTimestamp!.Value;
            if (ledger.TryGet(state, out var timing)) Assert.Equal(new(t, t + 1, t + 2, t + 3), timing);
        }
        await writer;
    }

    [Fact]
    public void HotCorrelationDoesNotAllocateOrRetainAnUnboundedHistory()
    {
        var ledger = new TelemetryPublicationTimingLedger(); var state = State(100);
        var timing = new TelemetryPublicationTiming(100, 101, 102, 103);
        for (var i = 0; i < 100; i++) { ledger.Record(state, timing); ledger.TryGet(state, out _); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100000; i++) { ledger.Record(state, timing); ledger.TryGet(state, out _); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static VehicleState State(long received)
    {
        Assert.True(new Fh6PacketParser().TryParse(Fh6PacketFixture.Create(), DateTimeOffset.UtcNow,
            out var state, out _, received));
        return state!;
    }
}
