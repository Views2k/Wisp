namespace Wisp.App;

// When the receive loop ends on an unexpected socket error the port stays unbound until Wisp is
// restarted. Bounded restarts with growing delays bring telemetry back on their own; after the
// budget the dashboard keeps showing the listener error.
internal sealed class TelemetryListenerRecovery
{
    internal const int MaximumAttempts = 5;
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
    // Running this long after a restart proves the fix; the next failure gets a full budget.
    internal static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(30);
    private int _attempts;
    private DateTimeOffset? _nextAttemptAt;

    internal int Attempts => _attempts;
    internal bool Exhausted => _attempts >= MaximumAttempts;

    // True when a restart should begin now. The delays grow 1, 2, 4, 8 and 16 s between attempts.
    internal bool ShouldRestart(DateTimeOffset now)
    {
        if (Exhausted || _nextAttemptAt is { } next && now < next) return false;
        _attempts++;
        _nextAttemptAt = now + InitialDelay * (1 << (_attempts - 1));
        return true;
    }

    internal void Reset()
    {
        _attempts = 0;
        _nextAttemptAt = null;
    }
}
