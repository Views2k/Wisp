using System.Collections.Immutable;
using Wisp.App.Clips;
using Wisp.App.CrashDiagnostics;
using Wisp.App.Supplementary;
using Wisp.Core;

namespace Wisp.App.DebugLogging;

// Project explicitly selected numeric fields. Never serialize a local diagnostic report or exception.
internal static class SupplementaryIncidentCapture
{
    internal static SupplementaryIncident Crash(CrashReport report)
    {
        var safe = report.Sanitize();
        var category = safe?.Origin == CrashOrigin.WindowsApplicationFault ? "windows-fault" :
            safe?.Origin == CrashOrigin.UnexpectedExit ? "unexpected-exit" : "managed-exception";
        return SupplementaryIncidentSchema.Create(category,
            code: safe?.RecoveredExit?.Fault?.ExceptionCode ?? (safe?.Exceptions.FirstOrDefault() is { } first ? unchecked((uint)first.HResult) : null),
            exceptions: Exceptions(safe?.Exceptions), context: Context(safe?.Context, report.TimeUtc));
    }

    internal static SupplementaryIncident? Export(Exception error, DateTimeOffset now)
    {
        try { return ExportCore(error, now); }
        catch (Exception) { return null; } // Optional evidence cannot replace the original operation failure.
    }

    private static SupplementaryIncident ExportCore(Exception error, DateTimeOffset now)
    {
        var stage = error.Data["wisp-export-stage"] is string s && s is "source-inspection" or "source-validation" or "encoding" or
            "output-inspection" or "output-validation" ? s : "library-copy";
        var reason = error.Data["wisp-export-reason"] is string r && SupplementaryIncidentSchema.Reasons.Contains(r) ? r : "unknown";
        if (reason == "unknown") reason = error switch
        {
            TimeoutException => "timeout",
            UnauthorizedAccessException => "access-denied",
            System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException => "file-unavailable",
            System.IO.InvalidDataException => "validation",
            NotSupportedException => "unsupported-format",
            System.IO.IOException => "storage",
            LosslessMpvException => "native-failure",
            _ => "unknown"
        };
        return SupplementaryIncidentSchema.Create("export", reason, stage, unchecked((uint)error.HResult),
            [new(CrashReportSymbols.ExceptionType(error.GetType()), unchecked((uint)error.HResult), [])],
            Context(HealthContextRecorder.Current.Snapshot(), now));
    }

    internal static SupplementaryIncident Recorder(string reason, RecorderFailureDiagnostic? diagnostic, ClipStorageDiagnostic? storage, DateTimeOffset now)
    {
        reason = diagnostic?.Reason ?? reason;
        if (!SupplementaryIncidentSchema.Reasons.Contains(reason)) reason = "unknown";
        var stage = storage?.Stage ?? diagnostic?.Stage;
        if (stage is not null && !SupplementaryIncidentSchema.FailureStages.Contains(stage)) stage = null;
        var evidence = diagnostic is null ? null : new SupplementaryRecorderEvidence(
            Count(diagnostic.VideoPackets), Count(diagnostic.AudioPackets), diagnostic.SubmittedFrames,
            Ms(diagnostic.SchedulerLag100ns / 10000d), Ms(diagnostic.SourceAge100ns / 10000d), Ms(diagnostic.LocalFrameAge100ns / 10000d));
        return SupplementaryIncidentSchema.Create("recorder", reason, stage,
            storage is not null ? unchecked((uint)storage.HResult) : diagnostic?.HResult,
            context: Context(HealthContextRecorder.Current.Snapshot(), now), recorder: evidence);
    }

    internal static void RecordRecorder(string reason, RecorderFailureDiagnostic? diagnostic = null, ClipStorageDiagnostic? storage = null)
    {
        if (SupplementaryObservations.Observer is null || !SupplementaryIncidentSchema.Reasons.Contains(diagnostic?.Reason ?? reason)) return;
        try
        {
            var incident = Recorder(reason, diagnostic, storage, DateTimeOffset.UtcNow);
            SupplementaryObservations.Record("clips", "clips", SupplementaryIncidentSchema.RecorderOutcome(incident.Reason),
                SupplementaryIncidentSchema.RecorderStage(incident.Reason, incident.FailureStage), incident: incident);
        }
        catch (Exception) { } // Native pipe draining and recorder recovery keep their original behavior.
    }

    internal static SupplementaryIncident Native(HealthContextSample sample, HealthContextSnapshot context) =>
        SupplementaryIncidentSchema.Create(sample.NativeStatus == NativeAssistProviderStatus.AccessDenied ? "native-access" : "native-unsupported",
            sample.NativeStatus == NativeAssistProviderStatus.AccessDenied ? "access-denied" : "unsupported-build",
            context: Context(context, sample.TimestampUtc));

    internal static SupplementaryIncident Breadcrumb(string category, HealthBreadcrumb breadcrumb, HealthContextSnapshot context) =>
        SupplementaryIncidentSchema.Create(category, code: breadcrumb.ErrorCode == 0 ? null : unchecked((uint)breadcrumb.ErrorCode),
            context: Context(context, breadcrumb.TimestampUtc));

    internal static ImmutableArray<SupplementaryIncidentException>? Exceptions(CrashExceptionInfo[]? errors)
    {
        if (errors is null || errors.Length == 0) return null;
        return errors.Take(3).Select(e => new SupplementaryIncidentException(CrashReportSymbols.SafeExceptionType(e.Type), unchecked((uint)e.HResult),
            (e.Methods ?? []).Where(m => SupplementaryIncidentSchema.Methods.Contains(m) && CrashReportSymbols.IsKnownMethod(m))
                .Distinct(StringComparer.Ordinal).Take(4).ToImmutableArray())).ToImmutableArray();
    }

    internal static ImmutableArray<SupplementaryIncidentContext>? Context(HealthContextSnapshot? snapshot, DateTimeOffset observedAt)
    {
        if (snapshot is null || snapshot.Samples.IsDefault || snapshot.Samples.Length > HealthContextRecorder.SampleCapacity) return null;
        var samples = snapshot.Samples.Where(s => s.TimestampUtc <= observedAt && observedAt - s.TimestampUtc <= TimeSpan.FromSeconds(30))
            .OrderBy(s => s.TimestampUtc).GroupBy(s => (int)(observedAt - s.TimestampUtc).TotalMilliseconds).Select(g => g.First()).ToArray();
        if (samples.Length == 0) return null;
        var selected = samples.Length <= 3 ? samples : new[] { samples[0], samples[samples.Length / 2], samples[^1] };
        return selected.Select(s => new SupplementaryIncidentContext((int)(observedAt - s.TimestampUtc).TotalMilliseconds,
            s.ListenerError ? "error" : !s.ListenerRunning ? "idle" : s.PacketAgeMs is >= 0 and <= 300 ? "fresh" :
            s.PacketAgeMs is >= 0 ? "stale" : "unknown", Number(s.CpuPercent, 100), Count(s.WorkingSetBytes), Count(s.ManagedHeapBytes),
            Ms(s.PacketAgeMs), Ms(s.UiHeartbeatAgeMs), Ms(s.NativeAgeMs))).ToImmutableArray();
    }
    private static long? Count(ulong value) => value <= 1_000_000_000_000 ? (long)value : null;
    private static long? Count(long value) => value is >= 0 and <= 1_000_000_000_000 ? value : null;
    private static double? Number(double? value, double maximum) => value is { } v && double.IsFinite(v) && v >= 0 && v <= maximum ? v : null;
    private static double? Ms(double? value) => Number(value, 60000);
}
