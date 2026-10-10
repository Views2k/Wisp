using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wisp.App.Supplementary;

internal sealed record SupplementaryIncidentException(string Type, uint Code, ImmutableArray<string> Methods);
internal sealed record SupplementaryIncidentContext(int AgeMs, string ConnectionState, double? CpuPercent = null,
    long? WorkingSetBytes = null, long? ManagedHeapBytes = null, double? PacketAgeMs = null,
    double? UiHeartbeatAgeMs = null, double? NativeAgeMs = null);
internal sealed record SupplementaryRecorderEvidence(long? VideoPackets = null, long? AudioPackets = null,
    long? SubmittedFrames = null, double? SchedulerLagMs = null, double? SourceAgeMs = null, double? LocalFrameAgeMs = null);
internal sealed record SupplementaryIncident(int SchemaVersion, string Category, string Reason, string Signature,
    string? FailureStage = null, uint? Code = null, ImmutableArray<SupplementaryIncidentException>? Exceptions = null,
    ImmutableArray<SupplementaryIncidentContext>? Context = null, SupplementaryRecorderEvidence? Recorder = null);

// Only this catalog crosses the automatic incident boundary. Context is evidence observed before a
// failure, never an attribution of its cause. New symbols/categories require the matching service catalog.
internal static class SupplementaryIncidentSchema
{
    internal const int MaximumBytes = 8192;
    internal static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    { "managed-exception", "windows-fault", "unexpected-exit", "recorder", "export", "native-access", "native-unsupported", "renderer", "collector", "saved-data" };
    internal static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
    {
        "unknown", "access-denied", "unsupported-build", "unsupported_os", "unsupported_gpu", "unsupported_format",
        "capture_failed", "encoder_failed", "audio_failed", "audio_capture_failed", "audio_unavailable", "buffer_full",
        "no_keyframe", "not_ready", "save_in_progress", "storage_failed", "mux_failed", "protocol_error",
        "capture_stale", "capture_reconnecting", "encoder_reconnecting", "audio_reconnecting", "scheduler_late",
        "cleanup_failed", "lossless_storage_low", "lossless_encoder_unsupported", "hdr_encoder_unsupported",
        "helper_start_failed", "helper_timeout", "helper_exited", "helper_shutdown_failed", "buffer_storage_unavailable",
        "buffer_storage_full", "clip_storage_full", "clip_publish_failed", "track-count", "track-codec", "video-track-missing",
        "dimensions", "audio-track", "duration", "frame-rate", "color-primaries", "source-pixel-format", "output-pixel-format",
        "export-size-limit", "output-empty", "output-color-transfer", "source-color-transfer", "export-stopped-before-end",
        "export-timeout", "preparation-timeout", "encoder-unavailable", "media-queue-full", "timeout", "disk-full", "size-limit",
        "file-unavailable", "validation", "unsupported-format", "storage", "native-failure"
    };
    internal static readonly HashSet<string> FailureStages = new(StringComparer.Ordinal)
    {
        "not_started", "unknown", "storage_preflight", "runtime_initialize", "capture_initialize", "conversion_initialize",
        "video_initialize", "audio_initialize", "capture_start", "capture_submit", "capture_freshness", "video_output",
        "video_spool_append", "audio_output", "audio_spool_append", "audio_source", "audio_encode", "audio_timeline",
        "aac_initialize", "spool_initialize", "spool_bootstrap", "capture_target", "video_pump", "video_schedule", "video_submit",
        "startup_readiness", "spool_health", "save_destination", "save_retain", "save_create", "save_worker", "save_begin",
        "save_finalize", "host_configuration", "host_loop", "host_exception", "video_cleanup", "validate_destination",
        "open_private_media", "private_media_identity", "write_recovery_record", "open_destination", "create_publication_temporary",
        "copy_media", "verify_copy", "rename_publication", "published_identity", "reopen_publication", "source-inspection",
        "source-validation", "encoding", "output-inspection", "output-validation", "library-copy"
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
        ImmutableArray<SupplementaryIncidentContext>? context = null, SupplementaryRecorderEvidence? recorder = null)
    {
        var incident = new SupplementaryIncident(1, category, reason, "", failureStage, code, exceptions, context, recorder);
        return incident with { Signature = Signature(incident) };
    }

    internal static bool Valid(SupplementaryIncident value) => value.SchemaVersion == 1 && Categories.Contains(value.Category) &&
        Reasons.Contains(value.Reason) && (value.FailureStage is null || FailureStages.Contains(value.FailureStage)) &&
        (value.Exceptions is null || value.Exceptions is { IsDefault: false, Length: > 0 and <= 3 } exceptions && exceptions.All(e =>
            e is not null && ExceptionTypes.Contains(e.Type) && !e.Methods.IsDefault && e.Methods.Length <= 4 &&
            e.Methods.All(Methods.Contains) && e.Methods.Distinct(StringComparer.Ordinal).Count() == e.Methods.Length)) &&
        (value.Context is null || value.Context is { IsDefault: false, Length: > 0 and <= 3 } context && context.All(ValidContext) &&
            context.Select(c => c.AgeMs).SequenceEqual(context.Select(c => c.AgeMs).Distinct().OrderDescending())) &&
        (value.Recorder is null || value.Category == "recorder" && ValidRecorder(value.Recorder)) &&
        value.Signature is { Length: 64 } && value.Signature.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        value.Signature == Signature(value) && JsonSerializer.SerializeToUtf8Bytes(value, SupplementarySchema.Json).Length <= MaximumBytes;

    private static bool ValidContext(SupplementaryIncidentContext c) => c is not null && c.AgeMs is >= 0 and <= 30000 &&
        c.ConnectionState is "unknown" or "idle" or "fresh" or "stale" or "error" &&
        SupplementarySchema.Number(c.CpuPercent, 100) && Count(c.WorkingSetBytes) && Count(c.ManagedHeapBytes) &&
        Milliseconds(c.PacketAgeMs) && Milliseconds(c.UiHeartbeatAgeMs) && Milliseconds(c.NativeAgeMs);
    private static bool ValidRecorder(SupplementaryRecorderEvidence r) => Count(r.VideoPackets) && Count(r.AudioPackets) &&
        Count(r.SubmittedFrames) && Milliseconds(r.SchedulerLagMs) && Milliseconds(r.SourceAgeMs) && Milliseconds(r.LocalFrameAgeMs);
    private static bool Count(long? value) => value is null or >= 0 and <= 1_000_000_000_000;
    private static bool Milliseconds(double? value) => SupplementarySchema.Number(value, 60000);

    // Domain-separated UTF-8, LF delimiters, invariant unsigned decimal; excludes changing sample measurements.
    internal static string Signature(SupplementaryIncident value)
    {
        var text = new StringBuilder("wisp-tools-incident-v1\n").Append(value.Category).Append('\n').Append(value.Reason).Append('\n')
            .Append(value.FailureStage ?? "").Append('\n').Append(value.Code?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\n');
        if (value.Exceptions is { IsDefault: false } exceptions) foreach (var exception in exceptions)
            {
                text.Append(exception.Type).Append('\n').Append(exception.Code.ToString(CultureInfo.InvariantCulture)).Append('\n');
                foreach (var method in exception.Methods) text.Append(method).Append('\n');
                text.Append('\n');
            }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    internal static string RecorderStage(string reason, string? stage) =>
        reason is "audio_failed" or "audio_capture_failed" or "audio_unavailable" or "audio_reconnecting" ||
        stage is "audio_initialize" or "audio_output" or "audio_spool_append" or "audio_source" or "audio_encode" or "audio_timeline" or "aac_initialize" ? "audio" :
        reason is "encoder_failed" or "encoder_reconnecting" or "unsupported_gpu" or "lossless_encoder_unsupported" or "hdr_encoder_unsupported" ||
        stage is "video_initialize" or "video_output" or "video_pump" or "video_schedule" or "video_submit" or "video_cleanup" ? "encode" :
        stage is "save_destination" or "save_retain" or "save_create" or "save_worker" or "save_begin" or "save_finalize" ||
        reason is "clip_storage_full" or "clip_publish_failed" or "mux_failed" ? "save" : "capture";
    internal static string RecorderOutcome(string reason) => reason is "unsupported_os" or "unsupported_gpu" or "unsupported_format" or
        "lossless_encoder_unsupported" or "hdr_encoder_unsupported" or "audio_unavailable" ? "unsupported" :
        reason == "helper_timeout" ? "timeout" : "failure";
}
