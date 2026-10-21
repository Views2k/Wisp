using System.Windows.Threading;
using Wisp.App.DebugLogging;

namespace Wisp.App;

// WM_DWMCOMPOSITIONCHANGED can arrive while DWM is still restarting, so re-applying an overlay's
// composition may fail transiently. A failure is recorded once per episode and retried on the
// window's dispatcher; it never leaves the window procedure.
internal sealed class OverlayCompositionRecovery
{
    internal const int MaximumRetries = 5;
    internal static readonly TimeSpan DefaultFirstRetryDelay = TimeSpan.FromMilliseconds(500);
    private const int UnspecifiedFailure = unchecked((int)0x80004005); // E_FAIL
    private readonly Func<IntPtr, int> _apply;
    private readonly HealthContextRecorder _health;
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly TimeSpan _firstRetryDelay;
    private readonly Dictionary<IntPtr, PendingWindow> _pending = new();
    private readonly object _gate = new();
    private long _generation;
    private bool _reported;

    internal OverlayCompositionRecovery(Func<IntPtr, int> apply, HealthContextRecorder? health = null,
        Action<TimeSpan, Action>? schedule = null, TimeSpan? firstRetryDelay = null)
    {
        _apply = apply;
        _health = health ?? HealthContextRecorder.Current;
        _schedule = schedule ?? ScheduleOnCurrentDispatcher;
        _firstRetryDelay = firstRetryDelay ?? DefaultFirstRetryDelay;
    }

    internal int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    // A new composition-changed message restarts the window's retry budget.
    internal void Reapply(IntPtr handle) => Attempt(handle, generation: null);

    internal void Forget(IntPtr handle)
    {
        lock (_gate)
        {
            if (_pending.Remove(handle) && _pending.Count == 0) _reported = false;
        }
    }

    private void Retry(IntPtr handle, long generation)
    {
        lock (_gate)
        {
            if (!IsCurrent(handle, generation)) return;
        }
        Attempt(handle, generation);
    }

    private void Attempt(IntPtr handle, long? generation)
    {
        var result = TryApply(handle);
        bool report;
        TimeSpan? delay = null;
        long scheduledGeneration;
        lock (_gate)
        {
            if (generation is { } expected && !IsCurrent(handle, expected)) return;
            _pending.TryGetValue(handle, out var pending);
            if (result >= 0)
            {
                if (_pending.Remove(handle) && _pending.Count == 0) _reported = false;
                return;
            }

            if (pending is null) _pending[handle] = pending = new PendingWindow();
            if (generation is null)
            {
                // Never reused, even by a later entry for the same or a recycled handle.
                pending.Generation = ++_generation;
                pending.Retries = 0;
            }
            report = !_reported;
            _reported = true;
            if (pending.Retries < MaximumRetries)
            {
                delay = _firstRetryDelay * (1 << pending.Retries);
                pending.Retries++;
            }
            scheduledGeneration = pending.Generation;
        }

        if (report) _health.RecordRendererFailure(result, HealthFailureStage.RendererHost);
        if (delay is { } retryDelay) _schedule(retryDelay, () => Retry(handle, scheduledGeneration));
    }

    // A retry whose window recovered, closed or received a newer message is stale.
    private bool IsCurrent(IntPtr handle, long generation) =>
        _pending.TryGetValue(handle, out var pending) && pending.Generation == generation;

    private int TryApply(IntPtr handle)
    {
        try { return _apply(handle); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { return error.HResult < 0 ? error.HResult : UnspecifiedFailure; }
    }

    private static void ScheduleOnCurrentDispatcher(TimeSpan delay, Action retry)
    {
        if (Dispatcher.FromThread(Thread.CurrentThread) is not { HasShutdownStarted: false } dispatcher) return;
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            retry();
        };
        timer.Start();
    }

    private sealed class PendingWindow
    {
        public long Generation;
        public int Retries;
    }
}
