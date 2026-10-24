using System.Diagnostics;
using System.Windows.Threading;

namespace Wisp.App.DebugLogging;

// A heartbeat is posted to the dashboard dispatcher every second and a timer thread watches when
// it last ran. A gap longer than StallThreshold is a stall: one breadcrumb when it is noticed and
// one with its length once the dispatcher runs again, so a report from this run shows that the
// dashboard was blocked, and for how long.
internal sealed class UiStallWatchdog : IDisposable
{
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(5);
    private readonly Action<Action> _post;
    private readonly Func<long> _timestamp;
    private readonly Action<HealthEventCode, int> _record;
    private readonly Action _beat;
    private Timer? _timer;
    private long _lastBeat;
    private int _pending, _stalled, _disposed;

    internal UiStallWatchdog(Action<Action> post, Func<long>? timestamp = null, Action<HealthEventCode, int>? record = null)
    {
        _post = post;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _record = record ?? ((code, milliseconds) => HealthContextRecorder.Current.RecordBreadcrumb(code, milliseconds));
        _beat = Beat;
        _lastBeat = _timestamp();
    }

    internal static UiStallWatchdog Start(Dispatcher dispatcher)
    {
        var watchdog = new UiStallWatchdog(action => dispatcher.BeginInvoke(DispatcherPriority.Input, action));
        watchdog._timer = new Timer(_ => watchdog.Check(), null, HeartbeatInterval, HeartbeatInterval);
        return watchdog;
    }

    internal bool Stalled => Volatile.Read(ref _stalled) != 0;

    // Timer thread: notices a stall, and posts the next heartbeat once the previous one has run.
    internal void Check()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var age = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastBeat), _timestamp());
        if (age >= StallThreshold && Interlocked.CompareExchange(ref _stalled, 1, 0) == 0)
            _record(HealthEventCode.UiStalled, Milliseconds(age));
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        try { _post(_beat); }
        catch (Exception) { Volatile.Write(ref _pending, 0); }
    }

    // Dashboard thread.
    private void Beat()
    {
        var now = _timestamp();
        var age = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastBeat), now);
        Volatile.Write(ref _lastBeat, now);
        Volatile.Write(ref _pending, 0);
        if (Interlocked.Exchange(ref _stalled, 0) != 0) _record(HealthEventCode.UiStallEnded, Milliseconds(age));
    }

    private static int Milliseconds(TimeSpan age) => (int)Math.Clamp(age.TotalMilliseconds, 0, int.MaxValue);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
    }
}
