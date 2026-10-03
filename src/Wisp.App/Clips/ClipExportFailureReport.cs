using System.Globalization;
using System.IO;
using System.Security;
using System.Text.Json;

namespace Wisp.App.Clips;

internal sealed record ClipExportFailureReport(string Message, string Details)
{
    internal static ClipExportFailureReport Create(Exception error, ClipEntry clip, ClipExportFormat format)
    {
        var stage = error.Data["wisp-export-stage"] is string value && value is
            "source-inspection" or "source-validation" or "encoding" or "output-inspection" or "output-validation"
            ? value : "library-copy";
        var reason = error.Data["wisp-export-reason"] is string code && code is
            "track-count" or "track-codec" or "video-track-missing" or "dimensions" or "audio-track" or "duration" or
            "frame-rate" or "color-primaries" or "source-pixel-format" or "output-pixel-format" or
            "export-size-limit" or "output-empty" or "output-color-transfer" or "source-color-transfer" or
            "export-stopped-before-end" or "export-timeout" or "preparation-timeout" or "encoder-unavailable"
            ? code : "unspecified";
        var nativeCode = error is LosslessMpvException native ? SafeNativeCode(native.Code) : "none";
        var category = nativeCode == "packet-queue-full" ? "media-queue-full"
            : error is TimeoutException ? "timeout"
            : error.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) ? "disk-full"
            : error is UnauthorizedAccessException or SecurityException || error.HResult == unchecked((int)0x80070005) ? "access-denied"
            : reason == "export-size-limit" ? "size-limit"
            : error is FileNotFoundException or DirectoryNotFoundException ? "file-unavailable"
            : error is InvalidDataException ? "validation"
            : error is NotSupportedException ? "unsupported-format"
            : error is IOException ? "storage"
            : error is LosslessMpvException ? "native-failure" : "unknown";
        var message = category switch
        {
            "media-queue-full" => "Export failed because the media queue filled. Copy the export details to report this.",
            "timeout" => "Export timed out while preparing or converting the clip. Copy the export details before trying again.",
            "disk-full" => "Export failed because a drive is full. Free space on the clip-library and destination drives, then try again.",
            "access-denied" => "Export failed because file access was denied. Check folder permissions, then try again.",
            "size-limit" => "Export stopped at Wisp's file size limit. Choose Original MP4 to copy the recording.",
            "file-unavailable" => "Export failed because a required file or folder is unavailable. Check that the drive and clip are still available.",
            "validation" => "Export failed its media checks. Copy the export details to report this.",
            "unsupported-format" => "This clip's format could not be converted. Choose Original MP4 to copy the recording.",
            "storage" => "Export failed while accessing files. Check folder access and free space, then try again.",
            _ => format == ClipExportFormat.Compatible
                ? "The compatible copy could not be created. Copy the export details, or choose Original MP4."
                : "The clip could not be exported. Copy the export details to report this."
        };
        // Only authored codes and numeric media metadata enter this report.
        // Exception messages, inner exceptions, paths and native logs can contain private data.
        var details = JsonSerializer.Serialize(new
        {
            report = "wisp-clip-export",
            version = ApplicationVersionInfo.MachineVersion,
            diagnosticBuildId = SafeBuildId(ApplicationVersionInfo.DiagnosticBuildId),
            format = format == ClipExportFormat.Compatible ? "compatible" : "original",
            stage,
            category,
            reason,
            nativeCode,
            hresult = error.HResult,
            clip = new
            {
                lossless = clip.Media.LosslessVideo,
                hdr = clip.Media.HdrVideo,
                width = clip.Media.Width,
                height = clip.Media.Height,
                frameRate = clip.Media.FrameRate,
                hasAudio = clip.Media.HasAudio,
                fileBytes = clip.Media.FileBytes,
                durationSeconds = clip.DurationSeconds
            }
        }, new JsonSerializerOptions { WriteIndented = true });
        DebugLogging.ComponentDiagnosticHistory.Current.RecordGenerated(DebugLogging.DiagnosticComponent.ClipExport, details);
        return new(message + " The original clip is kept.", details);
    }

    private static string SafeBuildId(string? value) => value is null ? "public"
        : value.Length is > 0 and <= 80 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? value : "unrecognized";

    internal static string SafeNativeCode(string code)
    {
        if (code is "packet-queue-full" or "decode-failed" or "decoder-error" or "event-queue-overflow" or
            "event-batch-limit" or "export-event-overflow" or "export-file-ended-with-error" or
            "export-native-error" or "export-event-batch-limit") return code;
        var separator = code.IndexOf('/');
        if (separator <= 0 || !int.TryParse(code.AsSpan(separator + 1), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var result) || result >= 0) return "unrecognized";
        var action = code[..separator];
        if (action is not ("initialize" or "request-logs" or "command-loadfile" or "command-quit" or "command-seek" or
            "property-volume" or "property-mute" or "property-pause" or "video-window" or "option-d3d11-output-csp" or
            "option-config" or "option-load-scripts" or "option-ytdl" or "option-terminal" or "option-input-default-bindings" or
            "option-input-vo-keyboard" or "option-input-terminal" or "option-input-cursor" or "option-osc" or "option-osd-level" or
            "option-access-references" or "option-sub-auto" or "option-audio-file-auto" or "option-keep-open" or
            "option-resume-playback" or "option-stop-screensaver" or "option-vo" or "option-ao" or "option-gpu-api" or "option-gpu-context" or
            "option-vid" or "option-aid" or "option-sid" or "option-force-window" or
            "option-hwdec" or "option-framedrop" or "option-cache" or "option-demuxer-max-bytes" or
            "option-cache-on-disk" or "option-cache-secs" or "option-cache-pause" or "option-cache-pause-initial" or
            "option-cache-pause-wait" or "option-demuxer-readahead-secs" or "option-screenshot-sw" or
            "option-demuxer-max-back-bytes" or "option-save-position-on-quit" or "option-o" or "option-of" or
            "option-ofopts" or "option-ovc" or "option-ovcopts" or "option-oac" or "option-oacopts" or
            "option-ocopy-metadata" or "option-pause" or "option-mute" or "option-volume" or "option-idle" or "option-audio-display" or
            "option-vd-lavc-threads" or "option-vd-queue-enable" or "option-vf")) return "unrecognized";
        return action + "/" + result.ToString(CultureInfo.InvariantCulture);
    }
}
