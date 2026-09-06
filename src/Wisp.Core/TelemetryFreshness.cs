using System.Diagnostics;

namespace Wisp.Core;

public enum TelemetryConnectionState
{
    Waiting,
    Connected,
    Lost
}

public sealed class TelemetryFreshness
{
    private readonly TimeSpan _timeout;
    private long? _lastPacketTimestamp;

    public TelemetryFreshness(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMilliseconds(750);
    }

    public void RecordPacket(long? receivedTimestamp)
    {
        // Missing arrival evidence cannot establish or extend freshness.
        if (receivedTimestamp is > 0)
        {
            _lastPacketTimestamp = receivedTimestamp;
        }
    }

    public TelemetryConnectionState GetState(long nowTimestamp)
    {
        if (_lastPacketTimestamp is null)
        {
            return TelemetryConnectionState.Waiting;
        }

        return GetAge(nowTimestamp) is { } age && age <= _timeout
            ? TelemetryConnectionState.Connected
            : TelemetryConnectionState.Lost;
    }

    public TimeSpan? GetAge(long nowTimestamp) =>
        _lastPacketTimestamp is not { } receivedTimestamp || nowTimestamp < receivedTimestamp
            ? null
            : Stopwatch.GetElapsedTime(receivedTimestamp, nowTimestamp);
}
