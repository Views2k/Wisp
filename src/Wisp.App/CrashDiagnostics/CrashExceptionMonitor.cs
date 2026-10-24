using System.Text;
using Wisp.App.DebugLogging;

namespace Wisp.App.CrashDiagnostics;

// The handlers only record. Recovery remains in the operation that owns and understands the failure.
internal sealed class CrashExceptionMonitor(ICrashReportSink sink, Func<DateTimeOffset>? utcNow = null,
    Func<HealthContextSnapshot>? context = null, Func<Guid?>? runId = null)
{
    // A recovered failure that repeats on every pass would fill the store with copies of one report
    // and push out the reports of other failures. One report per kind and minute keeps that room.
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);

    // Prevent logger recursion without dropping independent failures on other threads.
    [ThreadStatic]
    private static bool _recording;
    private readonly object _seenGate = new();
    private readonly Dictionary<string, DateTimeOffset> _recentRecovered = new(StringComparer.Ordinal);
    private WeakReference<Exception>? _lastTerminatingException;

    internal void UiException(Exception exception) => Record(exception, CrashOrigin.UiDispatcher, true);
    // An operation on the dashboard thread caught its failure and carried on; the report still reaches diagnostics.
    internal void RecoveredUiException(Exception exception, string? note = null) => Record(exception, CrashOrigin.UiDispatcher, false, note);
    internal void BackgroundException(object? exception, bool terminating) =>
        Record(exception as Exception, CrashOrigin.BackgroundThread, terminating);
    internal void UnobservedTaskException(Exception exception, string? note = null) =>
        Record(exception, CrashOrigin.UnobservedTask, false, note);
    internal void UnobservedTaskException(UnobservedTaskExceptionEventArgs args) => UnobservedTaskException(args.Exception);

    private void Record(Exception? exception, CrashOrigin origin, bool terminating, string? note = null)
    {
        if (_recording) return;
        _recording = true;
        try
        {
            var now = utcNow?.Invoke() ?? DateTimeOffset.UtcNow;
            var repeatKey = !terminating && exception is not null ? RepeatKey(origin, exception) : null;
            lock (_seenGate)
            {
                if (terminating && exception is not null && _lastTerminatingException?.TryGetTarget(out var previous) == true &&
                    ReferenceEquals(previous, exception)) return;
                if (repeatKey is not null && _recentRecovered.TryGetValue(repeatKey, out var last) &&
                    now - last is { Ticks: >= 0 } age && age < RepeatWindow) return;
            }
            var report = CrashReport.Capture(exception, origin, terminating, now) with
            { Context = CaptureContext(), RunId = runId?.Invoke(), Note = note };
            if (!sink.TrySave(report)) return;
            lock (_seenGate)
            {
                if (terminating && exception is not null) _lastTerminatingException = new(exception);
                if (repeatKey is null) return;
                foreach (var entry in _recentRecovered.Where(entry => now - entry.Value is not { Ticks: >= 0 } age || age >= RepeatWindow).ToArray())
                    _recentRecovered.Remove(entry.Key);
                _recentRecovered[repeatKey] = now;
            }
        }
        catch (Exception) { } // Includes a failed logger, never a handled application exception.
        finally { _recording = false; }
    }

    // The origin and the exception types down the chain; messages and stacks vary between copies of one failure.
    private static string RepeatKey(CrashOrigin origin, Exception exception)
    {
        var key = new StringBuilder(origin.ToString());
        for (var error = exception; error is not null; error = error.InnerException) key.Append('|').Append(error.GetType().FullName);
        return key.ToString();
    }

    private HealthContextSnapshot? CaptureContext()
    {
        try { return context?.Invoke() ?? HealthContextRecorder.Current.CaptureForReport(); }
        catch (Exception) { return null; }
    }
}
