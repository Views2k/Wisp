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
    private DateTimeOffset? _lastPacketAtUtc;

    public TelemetryFreshness(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMilliseconds(750);
    }

    public void RecordPacket(DateTimeOffset receivedAtUtc)
    {
        _lastPacketAtUtc = receivedAtUtc;
    }

    public TelemetryConnectionState GetState(DateTimeOffset nowUtc)
    {
        if (_lastPacketAtUtc is null)
        {
            return TelemetryConnectionState.Waiting;
        }

        return nowUtc - _lastPacketAtUtc.Value <= _timeout
            ? TelemetryConnectionState.Connected
            : TelemetryConnectionState.Lost;
    }

    public TimeSpan? GetAge(DateTimeOffset nowUtc) =>
        _lastPacketAtUtc is null ? null : nowUtc - _lastPacketAtUtc.Value;
}
