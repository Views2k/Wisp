using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wisp.App.Clips;

// Only fixed tokens and numeric evidence cross the native diagnostic boundary.
// Raw stderr, paths, process identities and component message strings are never retained.
internal sealed record RecorderFailureDiagnostic(string Reason, string Stage, uint HResult,
    ulong VideoPackets, ulong AudioPackets, uint SubmittedFrames, long? SchedulerLag100ns, long? SourceAge100ns,
    long? LocalFrameAge100ns)
{
    internal const int MaximumBytes = 4096;
    private static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
    {
        "not_started", "unknown", "storage_preflight", "runtime_initialize", "capture_initialize",
        "conversion_initialize", "video_initialize", "audio_initialize", "capture_start", "capture_submit",
        "capture_freshness", "video_output", "video_spool_append", "audio_output", "audio_spool_append",
        "audio_source", "audio_encode", "audio_timeline", "aac_initialize", "spool_initialize", "spool_bootstrap",
        "capture_target", "video_pump", "video_schedule", "video_submit", "startup_readiness", "spool_health",
        "save_destination", "save_retain", "save_create", "save_worker", "save_begin", "save_finalize", "host_configuration", "host_loop", "host_exception"
    };

    internal static RecorderFailureDiagnostic? Parse(ReadOnlyMemory<byte> line)
    {
        if (line.Length is 0 or > MaximumBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(line, new() { MaxDepth = 3 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) if (!names.Add(property.Name)) return null;
            if (root.GetProperty("mode").GetString() != "recorder_failure" || root.GetProperty("v").GetInt32() != 1) return null;
            var reason = root.GetProperty("reason").GetString() ?? "";
            var stage = root.GetProperty("stage").GetString() ?? "";
            if (!RecorderProtocol.Reasons.Contains(reason) || !Stages.Contains(stage)) return null;
            long? localFrameAge = null;
            if (root.TryGetProperty("localFrameAgeKnown", out var localKnown))
            {
                var age = root.GetProperty("localFrameAge100ns").GetInt64();
                if (localKnown.GetBoolean()) localFrameAge = age;
            }
            else if (root.TryGetProperty("localFrameAge100ns", out _)) return null;
            return new(reason, stage, root.GetProperty("hr").GetUInt32(),
                root.GetProperty("videoPackets").GetUInt64(), root.GetProperty("audioPackets").GetUInt64(),
                root.GetProperty("submittedFrames").GetUInt32(),
                root.GetProperty("schedulerLagKnown").GetBoolean() ? root.GetProperty("schedulerLag100ns").GetInt64() : null,
                root.GetProperty("sourceAgeKnown").GetBoolean() ? root.GetProperty("sourceAge100ns").GetInt64() : null,
                localFrameAge);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return null; }
    }
}

internal sealed record ClipStorageDiagnostic(string Stage, int HResult)
{
    internal static ClipStorageDiagnostic? From(RecorderClientException? error) =>
        error?.StorageHResult is { } hr && error.StorageStage is
            "validate_destination" or "open_private_media" or "private_media_identity" or "write_recovery_record" or
            "open_destination" or "create_publication_temporary" or "copy_media" or "verify_copy" or
            "rename_publication" or "published_identity" or "reopen_publication"
            ? new(error.StorageStage, hr) : null;
}

internal static class ClipFailureReport
{
    internal static string Build(string reason, ClipRecordingSpec? recording, RecorderFailureDiagnostic? diagnostic,
        ClipStorageDiagnostic? storage = null)
    {
        var safeReason = diagnostic?.Reason ?? (RecorderProtocol.Reasons.Contains(reason) ||
            reason is "helper_start_failed" or "helper_timeout" or "helper_exited" or "helper_shutdown_failed" or
                "buffer_storage_unavailable" or "buffer_storage_full" or "clip_storage_full" or "clip_publish_failed" ? reason : "recorder_failed");
        var report = new StringBuilder("Wisp Clips error report\n");
        report.AppendLine($"Version: {ApplicationVersionInfo.MachineVersion}");
        var build = ApplicationVersionInfo.DiagnosticBuildId;
        if (build is { Length: > 0 and <= 96 } && build.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            report.AppendLine($"Build: {build}");
        report.AppendLine($"Reason: {safeReason}");
        if (recording is not null)
            report.AppendLine(FormattableString.Invariant($"Settings: {recording.LengthSeconds}s, {recording.ResolutionHeight}p, {recording.FrameRate}fps, quality {recording.Quality}, video: {(recording.LosslessVideo ? "lossless" : "compressed")}, audio: {(recording.CaptureSystemAudio ? "system playback" : "Forza only")}"));
        if (storage is not null)
        {
            report.AppendLine($"Storage stage: {storage.Stage}");
            report.AppendLine($"Storage HRESULT: 0x{storage.HResult.ToString("X8", CultureInfo.InvariantCulture)}");
        }
        if (diagnostic is null) report.AppendLine("Native detail: not available");
        else
        {
            report.AppendLine($"Stage: {diagnostic.Stage}");
            report.AppendLine($"HRESULT: 0x{diagnostic.HResult.ToString("X8", CultureInfo.InvariantCulture)}");
            report.AppendLine(FormattableString.Invariant($"Frames submitted: {diagnostic.SubmittedFrames}; video packets: {diagnostic.VideoPackets}; audio packets: {diagnostic.AudioPackets}"));
            if (diagnostic.SchedulerLag100ns is { } lag) report.AppendLine(FormattableString.Invariant($"Scheduler lag (100ns): {lag}"));
            if (diagnostic.SourceAge100ns is { } age) report.AppendLine(FormattableString.Invariant($"Source frame age (100ns): {age}"));
            if (diagnostic.LocalFrameAge100ns is { } localAge) report.AppendLine(FormattableString.Invariant($"Local frame age (100ns): {localAge}"));
        }
        return report.ToString();
    }
}
