using Wisp.App.CrashDiagnostics;

namespace Wisp.App;

// The three ways a gauge update pass starts on the dashboard thread.
internal enum UiPassKind { Packet, Composition, Timer }

// The gauge update loop runs on the dashboard thread from a timer, the composition callback and
// packet activations. One failed pass is reported and skipped; the loop keeps going. After
// MaximumConsecutiveFailures of one pass kind with no clean pass of that kind between them the loop
// stops, so a broken state cannot report once per frame, and the dashboard says so. The kinds are
// counted apart: a timer pass that keeps failing stops the loop even while packet passes succeed.
internal sealed class UiUpdateGuard
{
    internal const int MaximumConsecutiveFailures = 5;
    private readonly int[] _consecutiveFailures = new int[3];

    internal bool Stopped { get; private set; }
    internal int ConsecutiveFailures(UiPassKind kind) => _consecutiveFailures[(int)kind];

    // Failures the loop cannot recover from end the process as before. A dead render thread is one:
    // nothing draws again in this process, and the next launch needs the software-rendering request
    // that only the unhandled-exception path makes.
    internal static bool IsFatal(Exception error) =>
        error is OutOfMemoryException or StackOverflowException || DashboardRenderingFallback.IsRenderThreadFailure(error);

    // Counts a failed pass; true when the loop must stop.
    internal bool Failed(UiPassKind kind)
    {
        if (Stopped) return true;
        if (++_consecutiveFailures[(int)kind] >= MaximumConsecutiveFailures) Stopped = true;
        return Stopped;
    }

    internal void Succeeded(UiPassKind kind)
    {
        if (!Stopped) _consecutiveFailures[(int)kind] = 0;
    }
}
