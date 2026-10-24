namespace Wisp.App;

// The gauge update loop runs on the dashboard thread from a timer, the composition callback and
// packet activations. One failed pass is reported and skipped; the loop keeps going. After
// MaximumConsecutiveFailures with no clean pass between them the loop stops, so a broken state
// cannot report once per frame, and the dashboard says so.
internal sealed class UiUpdateGuard
{
    internal const int MaximumConsecutiveFailures = 5;
    private int _consecutiveFailures;

    internal bool Stopped { get; private set; }
    internal int ConsecutiveFailures => _consecutiveFailures;

    // Failures the loop cannot recover from end the process as before.
    internal static bool IsFatal(Exception error) => error is OutOfMemoryException or StackOverflowException;

    // Counts a failed pass; true when the loop must stop.
    internal bool Failed()
    {
        if (Stopped) return true;
        _consecutiveFailures++;
        if (_consecutiveFailures >= MaximumConsecutiveFailures) Stopped = true;
        return Stopped;
    }

    internal void Succeeded()
    {
        if (!Stopped) _consecutiveFailures = 0;
    }
}
