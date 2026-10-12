using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wisp.App.Supplementary;

internal sealed record SupplementaryIncidentException(string Type, uint Code, ImmutableArray<string> Methods);
internal sealed record SupplementaryIncidentContext(int AgeMs, string ConnectionState, double? CpuPercent = null,
    long? WorkingSetBytes = null, long? ManagedHeapBytes = null, double? PacketAgeMs = null,
    double? UiHeartbeatAgeMs = null, double? NativeAgeMs = null, string? Foreground = null,
    string? GameBuild = null, string? GameVerification = null, string? RecorderState = null,
    string? RenderMode = null, bool? GameActive = null, bool? NativeExpected = null, bool? OverlayExpectedVisible = null);
internal sealed record SupplementaryUpdateEvidence(string Recovery, uint? InstallerExitCode = null);
internal sealed record SupplementaryCrashEvidence(string Origin, string? Lookup = null, string? Module = null);
internal sealed record SupplementaryRecorderComponent(string Component, string Reason, uint Code);
internal sealed record SupplementaryRecorderEvidence(long? VideoPackets = null, long? AudioPackets = null,
    long? SubmittedFrames = null, double? SchedulerLagMs = null, double? SourceAgeMs = null, double? LocalFrameAgeMs = null,
    ImmutableArray<SupplementaryRecorderComponent>? Components = null, uint? AdapterVendorId = null,
    uint? AdapterDeviceId = null, uint? HardwareCandidates = null, string? Resolution = null);
internal sealed record SupplementaryIncident(int SchemaVersion, string Category, string Reason, string Signature,
    string? FailureStage = null, uint? Code = null, ImmutableArray<SupplementaryIncidentException>? Exceptions = null,
    ImmutableArray<SupplementaryIncidentContext>? Context = null, SupplementaryRecorderEvidence? Recorder = null,
    SupplementaryCrashEvidence? Crash = null, SupplementaryUpdateEvidence? Update = null);

// Only this catalog crosses the automatic incident boundary. Context is evidence observed before a
// failure, never an attribution of its cause. New symbols/categories require the matching service catalog.
internal static class SupplementaryIncidentSchema
{
    internal const int MaximumBytes = 8192;
    internal static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    { "managed-exception", "windows-fault", "unexpected-exit", "recorder", "export", "native-access", "native-unsupported", "renderer", "collector", "saved-data", "tune",
        "settings", "update", "telemetry", "playback"
    };
    internal static readonly HashSet<string> UpdateFailureReasons = new(StringComparer.Ordinal)
    { "update-application-path", "update-arguments", "update-helper-path", "update-installed-app-invalid", "update-installer-exit-failed", "update-installer-hash", "update-installer-hash-mismatch", "update-installer-not-pe", "update-installer-path", "update-installer-product-mismatch", "update-installer-read-failed", "update-installer-size", "update-installer-size-mismatch", "update-installer-start-failed", "update-installer-stop-failed", "update-installer-timeout", "update-installer-version-mismatch", "update-install-registration", "update-parent-exited", "update-parent-identity", "update-parent-inspection", "update-parent-process", "update-parent-timeout", "update-ready-event", "update-ready-signal", "update-request-json", "update-request-path", "update-request-root", "update-request-size", "update-restart-failed", "update-source-version", "update-target-version", "update-unexpected-failure" };
    internal static readonly HashSet<string> UpdateRecoveries = new(StringComparer.Ordinal)
    { "unknown", "application-still-running", "restarted", "deferred", "no-verified-installation", "restart-failed" };
    internal static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
    {
        "exception-raised", "windows-application-fault", "shutdown-incomplete",
        "classification-missing", "unknown", "access-denied", "unsupported-build", "unsupported_os", "unsupported_gpu", "unsupported_format",
        "capture_failed", "encoder_failed", "audio_failed", "audio_capture_failed", "audio_unavailable", "buffer_full",
        "no_keyframe", "not_ready", "save_in_progress", "storage_failed", "mux_failed", "protocol_error",
        "capture_stale", "capture_reconnecting", "encoder_reconnecting", "audio_reconnecting", "scheduler_late",
        "cleanup_failed", "lossless_storage_low", "lossless_encoder_unsupported", "hdr_encoder_unsupported",
        "helper_start_failed", "helper_timeout", "helper_exited", "helper_shutdown_failed", "buffer_storage_unavailable",
        "buffer_storage_full", "clip_storage_full", "clip_publish_failed", "track-count", "track-codec", "video-track-missing",
        "dimensions", "audio-track", "duration", "frame-rate", "color-primaries", "source-pixel-format", "output-pixel-format",
        "export-size-limit", "output-empty", "output-color-transfer", "source-color-transfer", "export-stopped-before-end",
        "export-timeout", "preparation-timeout", "encoder-unavailable", "media-queue-full", "timeout", "disk-full", "size-limit",
        "file-unavailable", "validation", "unsupported-format", "storage", "native-failure",
        "compatibility-unavailable", "layout-unverified", "session-changed", "session-unverified", "tune-decode-failed", "tune-read-failed",
        "update-verification-failed", "port-in-use", "socket-failed", "http-status", "network-name-resolution", "network-connection", "network-tls", "network-response", "network-request", "library-full", "path-too-long", "invalid-data", "file-in-use", "managed-operation-failed", "listener-stopped", "packets-rejected", "sequence-incomplete", "no-datagrams"
    };
    internal static readonly HashSet<string> FailureStages = new(StringComparer.Ordinal)
    {
        "report-classification", "buffer-open",
        "crash-ui", "crash-background", "crash-task", "crash-recovered", "crash-windows",
        "helper-launch", "helper-config", "helper-start", "helper-pause", "helper-resume", "helper-save", "helper-stop", "helper-protocol", "helper-exit", "helper-state",
        "not_started", "unknown", "storage_preflight", "runtime_initialize", "capture_initialize", "conversion_initialize",
        "video_initialize", "audio_initialize", "capture_start", "capture_submit", "capture_freshness", "video_output",
        "video_spool_append", "audio_output", "audio_spool_append", "audio_source", "audio_encode", "audio_timeline",
        "aac_initialize", "spool_initialize", "spool_bootstrap", "capture_target", "video_pump", "video_schedule", "video_submit",
        "startup_readiness", "spool_health", "save_destination", "save_retain", "save_create", "save_worker", "save_begin",
        "save_finalize", "host_configuration", "host_loop", "host_exception", "video_cleanup", "validate_destination",
        "open_private_media", "private_media_identity", "write_recovery_record", "open_destination", "create_publication_temporary",
        "copy_media", "verify_copy", "rename_publication", "published_identity", "reopen_publication", "source-inspection",
        "source-validation", "encoding", "output-inspection", "output-validation", "library-copy",
        "capture_pause", "capture_resume", "save_storage_preflight",
        "tune-open", "tune-session", "tune-read", "tune-decode", "tune-current",
        "runs-start", "settings-load", "settings-save", "update-check", "update-download", "update-install-handoff", "update-installed", "setup-data-out", "telemetry-binding", "telemetry-ready", "runs-load", "runs-save", "runs-import", "runs-export", "runs-delete", "runs-recovery", "tune-load", "tune-save", "tune-delete", "profiles-create", "profiles-save", "profiles-rename", "profiles-delete", "profiles-apply", "clips-load", "clips-save", "clips-rename", "clips-delete", "clips-import", "clips-recovery", "clips-decode", "clips-playback", "clips-chosen",
        "renderer-state", "renderer-create", "renderer-resize", "renderer-position", "renderer-resume", "renderer-visibility", "renderer-frame-wait", "renderer-draw", "renderer-present", "renderer-motion", "renderer-retry-wait", "renderer-host", "runs-map", "health-collection"
    };
    internal static readonly HashSet<string> ExceptionTypes = new(StringComparer.Ordinal)
    {
        "Exception", "AggregateException", "ArgumentException", "ArgumentNullException", "ArgumentOutOfRangeException",
        "InvalidOperationException", "ObjectDisposedException", "NullReferenceException", "IndexOutOfRangeException",
        "InvalidCastException", "NotSupportedException", "NotImplementedException", "FormatException", "OverflowException",
        "IOException", "InvalidDataException", "FileNotFoundException", "DirectoryNotFoundException", "FileLoadException",
        "UnauthorizedAccessException", "Win32Exception", "SocketException", "COMException", "SEHException",
        "AccessViolationException", "OutOfMemoryException", "StackOverflowException", "OperationCanceledException",
        "TaskCanceledException", "TimeoutException", "DllNotFoundException", "EntryPointNotFoundException", "TypeLoadException",
        "TypeInitializationException", "ApplicationUpdateHelperUnavailableException", "LosslessMpvException", "RecorderClientException",
        "NativeCompatibilityEnvelopeException", "RejectedResponseException", "ByteLimitException", "TuneLayoutException",
        "TuneChangedException", "TuneAssetValidationException", "TuneAssetStreamValidationException", "RunLibraryFullException", "UpdateSecurityException"
    };
    internal static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "Wisp.App.Clips.CompatibleClipExporter.ExportAsync", "Wisp.App.Clips.CompatibleClipExporter.Inspect",
        "Wisp.App.Clips.CompatibleClipExporter.Validate", "Wisp.App.Clips.ClipRecorderService.SaveAsync",
        "Wisp.App.Clips.ClipRecorderService.SetEnabledAsync", "Wisp.App.Clips.ClipRecorderService.ReconcileAsync",
        "Wisp.App.Clips.ClipRecorderService.CloseSessionAsync", "Wisp.App.Clips.RecorderProcessClient.OpenAsync",
        "Wisp.App.Clips.RecorderProcessClient.SaveAsync", "Wisp.App.Clips.RecorderProcessClient.ReadOutputAsync",
        "Wisp.App.Clips.RecorderProcessClient.DrainErrorAsync", "Wisp.App.Clips.ClipsViewModel.Operation",
        "Wisp.App.Tunes.TuneViewModel.RefreshCoreAsync", "Wisp.App.Tunes.TuneViewModel.LoadSelectedAsync"
    };

    internal static SupplementaryIncident Create(string category, string reason = "unknown", string? failureStage = null,
        uint? code = null, ImmutableArray<SupplementaryIncidentException>? exceptions = null,
        ImmutableArray<SupplementaryIncidentContext>? context = null, SupplementaryRecorderEvidence? recorder = null,
        SupplementaryCrashEvidence? crash = null, SupplementaryUpdateEvidence? update = null)
    {
        var incident = new SupplementaryIncident(crash is not null || update is not null || ExtendedRecorder(recorder) ? 3 : recorder?.Components is null ? 1 : 2, category, reason, "", failureStage, code, exceptions, context, recorder, crash, update);
        return incident with { Signature = Signature(incident) };
    }

    internal static bool Valid(SupplementaryIncident value) => value.SchemaVersion is 1 or 2 or 3 && (value.SchemaVersion >= 2 || value.Recorder?.Components is null) && Categories.Contains(value.Category) &&
        (value.Crash is null || value.SchemaVersion == 3 && ValidCrashOrigin(value.Category, value.Crash.Origin) &&
            value.Crash.Lookup is null or "no-match" or "matched" or "access-denied" or "timeout" or "read-failed" or "scan-limit" &&
            (value.Crash.Module is null || CrashDiagnostics.WindowsFaultEventReader.SafeModule(value.Crash.Module) == value.Crash.Module)) &&
        (value.Update is null || value.SchemaVersion == 3 && value.Category == "update" && UpdateRecoveries.Contains(value.Update.Recovery)) &&
        (Reasons.Contains(value.Reason) || value.Category == "update" && UpdateFailureReasons.Contains(value.Reason)) && (value.FailureStage is null || FailureStages.Contains(value.FailureStage)) &&
        (value.Exceptions is null || value.Exceptions is { IsDefault: false, Length: > 0 and <= 3 } exceptions && exceptions.All(e =>
            e is not null && ExceptionTypes.Contains(e.Type) && !e.Methods.IsDefault && e.Methods.Length <= 4 &&
            e.Methods.All(ValidMethod) && e.Methods.Distinct(StringComparer.Ordinal).Count() == e.Methods.Length)) &&
        (value.Context is null || value.Context is { IsDefault: false, Length: > 0 and <= 3 } context &&
            context.All(c => ValidContext(c, value.Category is "unexpected-exit" or "windows-fault" ? 86400000 : 30000)) &&
            context.Select(c => c.AgeMs).SequenceEqual(context.Select(c => c.AgeMs).Distinct().OrderDescending())) &&
        (value.Recorder is null || value.Category == "recorder" && ValidRecorder(value.Recorder) &&
            (!ExtendedRecorder(value.Recorder) || value.SchemaVersion == 3) &&
            (value.Recorder.Resolution is null || value.Recorder.Resolution == "sdr-fallback" && value.Reason == "hdr_encoder_unsupported" && value.FailureStage == "video_initialize" ||
                value.Recorder.Resolution == "video-only" && value.Reason == "audio_unavailable" && value.FailureStage == "audio_initialize")) &&
        value.Signature is { Length: 64 } && value.Signature.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        value.Signature == Signature(value) && JsonSerializer.SerializeToUtf8Bytes(value, SupplementarySchema.Json).Length <= MaximumBytes;

    private static bool ValidContext(SupplementaryIncidentContext c, int maximumAge) => c is not null && c.AgeMs >= 0 && c.AgeMs <= maximumAge &&
        c.ConnectionState is "unknown" or "idle" or "fresh" or "stale" or "error" &&
        SupplementarySchema.Number(c.CpuPercent, 100) && Count(c.WorkingSetBytes) && Count(c.ManagedHeapBytes) &&
        Milliseconds(c.PacketAgeMs) && Milliseconds(c.UiHeartbeatAgeMs) && Milliseconds(c.NativeAgeMs) &&
        c.Foreground is null or "unknown" or "game" or "wisp" or "other" or "none" &&
        (c.GameBuild is null || c.GameBuild == DebugLogging.HealthContextRecorder.SafeGameVersion(c.GameBuild)) &&
        c.GameVerification is null or "verified" or "unverified" or "unavailable" or "not-running" &&
        c.RecorderState is null or "disabled" or "waiting" or "preparing" or "buffering" or "saving" or "stopping" or "reconnecting" or "unavailable" or "error" or "paused" &&
        c.RenderMode is null or "cpu" or "gpu";
    private static bool ValidCrashOrigin(string category, string origin) => category switch
    {
        "managed-exception" => origin is "ui" or "background" or "task",
        "windows-fault" => origin == "windows",
        "unexpected-exit" => origin == "recovered",
        _ => false
    };
    private static bool ValidRecorder(SupplementaryRecorderEvidence r) => Count(r.VideoPackets) && Count(r.AudioPackets) &&
        (r.AdapterVendorId is null) == (r.AdapterDeviceId is null) &&
        Count(r.SubmittedFrames) && Milliseconds(r.SchedulerLagMs) && Milliseconds(r.SourceAgeMs) && Milliseconds(r.LocalFrameAgeMs) &&
        (r.Components is null || r.Components is { IsDefault: false, Length: > 0 and <= 7 } components &&
            components.All(c => c is not null && RecorderComponentCatalog.Components.Contains(c.Component, StringComparer.Ordinal) &&
                RecorderComponentCatalog.Reasons.Contains(c.Reason) && (c.Code & 0x80000000u) != 0) &&
            components.Select(c => c.Component).SequenceEqual(components.Select(c => c.Component).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
    private static bool ExtendedRecorder(SupplementaryRecorderEvidence? r) => r is not null &&
        (r.AdapterVendorId is not null || r.AdapterDeviceId is not null || r.HardwareCandidates is not null || r.Resolution is not null);
    private static bool Count(long? value) => value is null or >= 0 and <= 1_000_000_000_000;
    private static bool Milliseconds(double? value) => SupplementarySchema.Number(value, 60000);

    internal static bool ValidMethod(string value) => Methods.Contains(value) || value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
    internal static string MethodId(string compiledSymbol) => "sha256:" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("wisp-tools-method-v1\n" + compiledSymbol))).ToLowerInvariant();

    // Domain-separated UTF-8, LF delimiters, invariant unsigned decimal; excludes changing sample measurements.
    internal static string Signature(SupplementaryIncident value)
    {
        var text = new StringBuilder($"wisp-tools-incident-v{value.SchemaVersion}\n").Append(value.Category).Append('\n').Append(value.Reason).Append('\n')
            .Append(value.FailureStage ?? "").Append('\n').Append(value.Code?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\n');
        if (value.Exceptions is { IsDefault: false } exceptions) foreach (var exception in exceptions)
            {
                text.Append(exception.Type).Append('\n').Append(exception.Code.ToString(CultureInfo.InvariantCulture)).Append('\n');
                foreach (var method in exception.Methods) text.Append(method).Append('\n');
                text.Append('\n');
            }
        if (value.SchemaVersion >= 2 && value.Recorder?.Components is { IsDefault: false } components)
            foreach (var component in components)
                text.Append(component.Component).Append('\n').Append(component.Reason).Append('\n')
                    .Append(component.Code.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (value.SchemaVersion == 3 && value.Crash is { } crash)
            text.Append(crash.Origin).Append('\n').Append(crash.Module ?? "").Append('\n');
        if (value.SchemaVersion == 3 && value.Update is { } update)
            text.Append(update.InstallerExitCode?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\n');
        if (value.SchemaVersion == 3 && value.Recorder?.Resolution is { } resolution)
            text.Append(resolution).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    internal static string RecorderStage(string reason, string? stage) =>
        reason is "audio_failed" or "audio_capture_failed" or "audio_unavailable" or "audio_reconnecting" ||
        stage is "audio_initialize" or "audio_output" or "audio_spool_append" or "audio_source" or "audio_encode" or "audio_timeline" or "aac_initialize" ? "audio" :
        reason is "encoder_failed" or "encoder_reconnecting" or "unsupported_gpu" or "lossless_encoder_unsupported" or "hdr_encoder_unsupported" ||
        stage is "video_initialize" or "video_output" or "video_pump" or "video_schedule" or "video_submit" or "video_cleanup" ? "encode" :
        stage is "save_destination" or "save_retain" or "save_create" or "save_worker" or "save_begin" or "save_finalize" or "save_storage_preflight" ||
        reason is "clip_storage_full" or "clip_publish_failed" or "mux_failed" ? "save" :
        reason is "buffer_full" or "buffer_storage_full" or "buffer_storage_unavailable" or "lossless_storage_low" ||
        stage is "storage_preflight" or "spool_initialize" or "spool_bootstrap" or "spool_health" or "video_spool_append" ? "buffer" : "capture";
    internal static string RecorderOutcome(string reason) => reason is "unsupported_os" or "unsupported_gpu" or "unsupported_format" or
        "lossless_encoder_unsupported" or "hdr_encoder_unsupported" or "audio_unavailable" ? "unsupported" :
        reason == "helper_timeout" ? "timeout" : "failure";
}
