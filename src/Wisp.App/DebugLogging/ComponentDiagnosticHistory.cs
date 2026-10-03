using System.Text.Json;
using Wisp.App.Clips;
using Wisp.App.CrashDiagnostics;

namespace Wisp.App.DebugLogging;

internal enum DiagnosticComponent { Recorder, Playback, ClipExport, Tune }
internal sealed record ComponentDiagnosticReport(DateTimeOffset TimeUtc, DiagnosticComponent Component, string Details);

// Low-frequency failures only. Callers supply reports from Wisp's allowlisted report
// factories, never exception messages, native logs, paths or disk-loaded text.
internal sealed class ComponentDiagnosticHistory
{
    internal const int MaximumReports = 8;
    internal const int MaximumReportCharacters = 16384;
    internal static ComponentDiagnosticHistory Current { get; } = new();
    private readonly object _gate = new();
    private ComponentDiagnosticReport[] _reports = [];

    internal ComponentDiagnosticReport[] Snapshot() => Volatile.Read(ref _reports).ToArray();

    internal void RecordGenerated(DiagnosticComponent component, string details)
    {
        if (!Enum.IsDefined(component) || details.Length is 0 or > MaximumReportCharacters) return;
        lock (_gate)
        {
            // Repeated reads of the same authored report cannot evict other failures.
            if (_reports.Any(report => report.Component == component && report.Details == details)) return;
            Volatile.Write(ref _reports, _reports.TakeLast(MaximumReports - 1)
                .Append(new ComponentDiagnosticReport(DateTimeOffset.UtcNow, component, details)).ToArray());
        }
    }

    internal void Clear() { lock (_gate) Volatile.Write(ref _reports, []); }

    internal void RecordPlaybackFailure(Exception error, string stage)
    {
        var safeStage = stage is "created" or "initialize-native-library" or "prepare-paused-video" or
            "prepare-playback-copy" or "player-ready" or "paused" or "playing" or "seeking" or "stopping"
            ? stage : "unrecognized";
        var code = error is OperationCanceledException ? "prepare-timeout"
            : error is LosslessMpvException native ? ClipExportFailureReport.SafeNativeCode(native.Code) : "none";
        RecordGenerated(DiagnosticComponent.Playback, JsonSerializer.Serialize(new
        {
            stage = safeStage,
            code,
            hresult = error.HResult,
            exception = CrashReportSymbols.ExceptionType(error.GetType())
        }));
    }
}
