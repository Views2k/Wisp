using Wisp.App.DebugLogging;

namespace Wisp.App.CrashDiagnostics;

// The handlers only record. Recovery remains in the operation that owns and understands the failure.
internal sealed class CrashExceptionMonitor(ICrashReportSink sink, Func<DateTimeOffset>? utcNow = null,
    Func<HealthContextSnapshot>? context = null, Func<Guid?>? runId = null)
{
    // Prevent logger recursion without dropping independent failures on other threads.
    [ThreadStatic]
    private static bool _recording;
    private readonly object _seenGate = new();
    private WeakReference<Exception>? _lastTerminatingException;

    internal void UiException(Exception exception) => Record(exception, CrashOrigin.UiDispatcher, true);
    internal void BackgroundException(object? exception, bool terminating) =>
        Record(exception as Exception, CrashOrigin.BackgroundThread, terminating);
    internal void UnobservedTaskException(Exception exception) => Record(exception, CrashOrigin.UnobservedTask, false);
    internal void UnobservedTaskException(UnobservedTaskExceptionEventArgs args) => UnobservedTaskException(args.Exception);

    private void Record(Exception? exception, CrashOrigin origin, bool terminating)
    {
        if (_recording) return;
        _recording = true;
        try
        {
            lock (_seenGate)
            {
                if (terminating && exception is not null && _lastTerminatingException?.TryGetTarget(out var previous) == true &&
                    ReferenceEquals(previous, exception)) return;
            }
            var report = CrashReport.Capture(exception, origin, terminating, utcNow?.Invoke() ?? DateTimeOffset.UtcNow) with
            { Context = CaptureContext(), RunId = runId?.Invoke() };
            if (sink.TrySave(report) && terminating && exception is not null)
            {
                lock (_seenGate) _lastTerminatingException = new(exception);
            }
        }
        catch (Exception) { } // Includes a failed logger, never a handled application exception.
        finally { _recording = false; }
    }

    private HealthContextSnapshot? CaptureContext()
    {
        try { return context?.Invoke() ?? HealthContextRecorder.Current.CaptureForReport(); }
        catch (Exception) { return null; }
    }
}
