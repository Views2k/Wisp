using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wisp.App.Supplementary;

internal sealed record SupplementaryEvent(int SchemaVersion, Guid EventId, Guid SessionId, Guid InstallationId,
    DateTimeOffset ObservedAt, string AppVersion, string BuildId, string Channel, string Kind, string Feature,
    string Outcome, string Platform, double? DurationMs = null, double? Value = null, string? GameBuild = null,
    string? Stage = null, SupplementaryMeasurements? Measurements = null, string? RenderMode = null,
    SupplementaryRelatedRun? RelatedRun = null, SupplementarySessionSummary? SessionSummary = null,
    long? SessionAgeMs = null, SupplementaryIncident? Incident = null);

// Cumulative observed intervals in this reporting run; idle means no fresh parsed telemetry, not player inactivity.
internal sealed record SupplementarySessionSummary(long ObservedOpenMs, long IdleMs, long ConnectedMs,
    long StateUnknownMs, long UnobservedMs, long SessionAgeMs)
{
    internal bool IsValid => ObservedOpenMs is >= 0 and <= 1_000_000_000_000 &&
        IdleMs is >= 0 and <= 1_000_000_000_000 && ConnectedMs is >= 0 and <= 1_000_000_000_000 &&
        StateUnknownMs is >= 0 and <= 1_000_000_000_000 && UnobservedMs is >= 0 and <= 1_000_000_000_000 &&
        SessionAgeMs is >= 0 and <= 1_000_000_000_000 &&
        IdleMs + ConnectedMs + StateUnknownMs == ObservedOpenMs && ObservedOpenMs + UnobservedMs <= SessionAgeMs;
}

// Historical evidence keeps its original build identity without reopening a previous run's delivery sequence.
internal sealed record SupplementaryRelatedRun(Guid SessionId, string AppVersion, string BuildId, string Channel);

internal sealed record SupplementaryMeasurements(int Count, double MinMs, double P50Ms, double P95Ms, double P99Ms,
    double MaxMs, double AverageMs, ImmutableArray<int>? Histogram = null)
{
    internal static readonly ImmutableArray<double> BucketUpperBounds = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 60000];
    internal bool IsValid => Count is > 0 and <= 1_000_000 &&
        new[] { MinMs, P50Ms, P95Ms, P99Ms, MaxMs, AverageMs }.All(v => double.IsFinite(v) && v is >= 0 and <= 60000) &&
        MinMs <= P50Ms && P50Ms <= P95Ms && P95Ms <= P99Ms && P99Ms <= MaxMs && AverageMs >= MinMs && AverageMs <= MaxMs &&
        (Histogram is null || Histogram is { IsDefault: false, Length: 16 } buckets &&
            buckets.All(v => v is >= 0 and <= 1_000_000) && buckets.Sum(v => (long)v) == Count);
}

internal sealed record SupplementarySupportDiagnostics(string Platform, string ConnectionState, string ErrorCategory,
    string? GameBuild = null);

internal sealed record SupplementarySupportReport(int SchemaVersion, Guid EventId, Guid SessionId, Guid InstallationId,
    DateTimeOffset ObservedAt, string AppVersion, string BuildId, string Channel, string Category, string Title,
    string Message, SupplementarySupportDiagnostics? Diagnostics = null);

internal enum SupplementaryRequestStatus { Success, Unconfigured, Disabled, Invalid, Cancelled, TimedOut, Unavailable, Rejected, DailyLimit }
internal sealed record SupplementarySupportResult(SupplementaryRequestStatus Status, string? Reference = null, DateTimeOffset? NextAllowedAt = null);

// Automatic events contain only compiled categories and bounded scalar measurements. No dictionary extension bag.
internal static partial class SupplementarySchema
{
    internal const int MaximumBatchBytes = 64 * 1024;
    internal const int MaximumBatchEvents = 64;
    internal const int MaximumSupportBytes = 32 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 12,
        Converters = { new UtcConverter() }
    };
    internal static readonly HashSet<string> Channels = new(StringComparer.Ordinal) { "stable", "private" };
    internal static readonly HashSet<string> Platforms = new(StringComparer.Ordinal) { "unknown", "steam", "store" };
    internal static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "heartbeat", "session-start", "session-end", "first-launch", "feature", "setup", "update", "connection",
        "udp-summary", "exception", "startup", "resource", "clips", "settings", "saved-data"
    };
    internal static readonly HashSet<string> Features = new(StringComparer.Ordinal)
        { "app", "hud", "clips", "runs", "tune", "drift", "profiles", "settings", "setup", "update", "native", "telemetry" };
    internal static readonly HashSet<string> Outcomes = new(StringComparer.Ordinal)
        { "none", "attempt", "success", "failure", "cancelled", "unsupported", "timeout", "unknown", "detected", "fresh", "idle", "stale" };
    internal static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
    {
        "launch", "ready", "game-detection", "binding", "validation", "data-out", "hud-ready", "setup-start", "setup-step",
        "setup-complete", "prompt", "chosen", "download", "installed", "capture-init", "capture", "audio", "buffer", "save",
        "encode", "decode", "playback", "export", "load", "migrate", "import", "recovery", "cpu", "memory", "telemetry-age",
        "reception-gap", "packet-count", "parse-failure", "unhandled", "renderer-queue", "native-read", "dispatcher",
        "composition-callback", "renderer-submit", "working-set", "managed-heap", "ui-delay", "render-submit",
        "frame-wait", "render-work", "scene-build", "present-call", "submission-interval", "queue-to-submit", "receive-to-submit",
        "compositor-update", "retry-wait", "measurement-dropped", "accepted-packets", "rejected-packets", "native-attempts",
        "native-failures", "renderer-busy", "renderer-occluded", "renderer-failures", "renderer-submissions", "queue-dropped", "collector-failures",
        "native-age", "ui-heartbeat-age", "composition-maximum-gap", "fatal-exception", "continuing-exception", "unexpected-exit", "windows-fault",
        "create", "apply", "rename", "delete", "first-telemetry", "runtime-ready", "check", "install-handoff",
        "presentation-requested", "setup-welcome", "setup-connection", "setup-display", "setup-appearance"
    };
    internal static readonly HashSet<string> CountStages = new(StringComparer.Ordinal)
    { "measurement-dropped", "accepted-packets", "rejected-packets", "native-attempts", "native-failures", "renderer-busy",
        "renderer-occluded", "renderer-failures", "renderer-submissions", "queue-dropped", "collector-failures", "packet-count", "parse-failure" };

    internal static bool Valid(SupplementaryEvent? value, DateTimeOffset now) => value is not null &&
        Identity(value.SchemaVersion, value.EventId, value.SessionId, value.InstallationId, value.ObservedAt,
            value.AppVersion, value.BuildId, value.Channel, now) && Kinds.Contains(value.Kind) && Features.Contains(value.Feature) &&
        Outcomes.Contains(value.Outcome) && Platforms.Contains(value.Platform) &&
        Number(value.DurationMs, 604_800_000) && Number(value.Value, 1_000_000_000_000) &&
        (value.GameBuild is null || NumericVersion(value.GameBuild, app: false)) &&
        (value.Stage is null || Stages.Contains(value.Stage)) && (value.RenderMode is null or "cpu" or "gpu") &&
        (value.SessionSummary is null || value.Kind == "heartbeat" && value.SessionSummary.IsValid) &&
        (value.Incident is null || (value.Kind == "exception" || value.Outcome is "failure" or "unsupported" or "timeout" or "unknown") &&
            value.Outcome is not ("success" or "attempt") && SupplementaryIncidentSchema.Valid(value.Incident)) &&
        (value.SessionAgeMs is null || value.Kind == "resource" && value.SessionAgeMs is >= 0 and <= 1_000_000_000_000 &&
            value.Stage is "cpu" or "working-set" or "managed-heap" && value.Value is not null &&
            value.DurationMs is null && value.Measurements is null) &&
        (value.RelatedRun is not { } prior || value.Kind == "exception" && Uuid4(prior.SessionId) &&
            NumericVersion(prior.AppVersion, app: true) && BuildIdentity(prior.BuildId) && Channels.Contains(prior.Channel)) &&
        (value.Measurements is null || value.Measurements.IsValid && !CountStages.Contains(value.Stage ?? "") &&
            value.Stage is not ("memory" or "working-set" or "managed-heap"));

    internal static bool Valid(SupplementarySupportReport? value, DateTimeOffset now) => value is not null &&
        Identity(value.SchemaVersion, value.EventId, value.SessionId, value.InstallationId, value.ObservedAt,
            value.AppVersion, value.BuildId, value.Channel, now) && value.Category is "bug" or "suggestion" or "feedback" &&
        PlainText(value.Title, 100, multiline: false) && PlainText(value.Message, 4000, multiline: true) &&
        IsRedacted(value.Title) && IsRedacted(value.Message) &&
        (value.Diagnostics is not { } d || Platforms.Contains(d.Platform) &&
            d.ConnectionState is "unknown" or "idle" or "detected" or "fresh" or "stale" &&
            d.ErrorCategory is "none" or "connection" or "validation" or "unsupported" or "clips" or "settings" or "saved-data" or "unexpected" &&
            (d.GameBuild is null || NumericVersion(d.GameBuild, app: false)));

    internal static SupplementarySupportReport RedactForPreview(SupplementarySupportReport report) => report with
        { Title = Redact(report.Title), Message = Redact(report.Message) };

    // This is a minimization boundary, not a claim that arbitrary prose can be proven free of personal data.
    // Callers must display this exact result for deliberate review before submission. Raw input is never queued.
    internal static string Redact(string value)
    {
        if (value.Length > 16_384) return "[REDACTED: text exceeds the supported preview limit]";
        try { return SensitivePatterns().Replace(value, "[REDACTED]"); }
        catch (RegexMatchTimeoutException) { return "[REDACTED: text could not be safely previewed]"; }
    }
    private static bool IsRedacted(string value) => Redact(value) == value;
    private static bool Identity(int schema, Guid eventId, Guid sessionId, Guid installationId, DateTimeOffset observed,
        string app, string build, string channel, DateTimeOffset now) => schema == 1 && Uuid4(eventId) && Uuid4(sessionId) &&
        Uuid4(installationId) && observed.Offset == TimeSpan.Zero && observed >= now - TimeSpan.FromHours(24) &&
        observed <= now + TimeSpan.FromMinutes(5) && NumericVersion(app, app: true) && BuildIdentity(build) && Channels.Contains(channel);
    internal static bool Uuid4(Guid value)
    {
        var bytes = value.ToByteArray();
        return value != Guid.Empty && (bytes[7] >> 4) == 4 && (bytes[8] & 0xC0) == 0x80;
    }
    internal static bool Number(double? value, double maximum) => value is null || double.IsFinite(value.Value) && value >= 0 && value <= maximum;
    internal static bool Token(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    internal static bool BuildIdentity(string? value) => Token(value, 64) && char.IsAsciiLetterOrDigit(value![0]);
    internal static bool NumericVersion(string? value, bool app)
    {
        if (value is not { Length: > 0 and <= 32 }) return false;
        var parts = value.Split('.');
        return (app ? parts.Length == 3 : parts.Length is 3 or 4) && parts.All(p => p.Length > 0 && p.Length <= (app ? 5 : 9) &&
            (p.Length == 1 || p[0] != '0') && p.All(char.IsAsciiDigit) && int.TryParse(p, out _));
    }
    internal static bool PlainText(string? value, int maximum, bool multiline) => value is { Length: > 0 } &&
        value.Length <= maximum && !string.IsNullOrWhiteSpace(value) && value.All(c =>
            c is not ('<' or '>') && (!char.IsControl(c) || multiline && c is '\n' or '\r' or '\t'));

    [GeneratedRegex(@"(?ix)(?:\b(?:bearer|token|api[_-]?key|password|secret|authorization)\s*[:=]\s*[^\s,;]+)|(?:\b(?:sk-|gh[pousr]_|github_pat_)[A-Za-z0-9_\-]{8,})|(?:[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})|(?:\b(?:\d{1,3}\.){3}\d{1,3}\b)|(?:\b[0-9A-F]{2}(?:[:-][0-9A-F]{2}){5}\b)|(?:[A-Z]:[\\/][^\r\n\t]+)|(?:\\\\[^\s]+)|(?:(?:/home/|/Users/|/srv/)[^\s]+)|(?:https?://[^\s]+)|(?:\b(?:[0-9A-F]{1,4}:){2,}[0-9A-F:]{0,39}\b)|(?:\+?\d[\d ()\-]{7,}\d)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex SensitivePatterns();

    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDateTimeOffset();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }
}
