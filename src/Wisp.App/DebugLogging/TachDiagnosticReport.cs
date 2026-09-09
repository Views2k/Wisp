using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;

namespace Wisp.App.DebugLogging;

internal static class TachDiagnosticReport
{
    private const string NeedleRoutePolicy =
        "Raw needle routes: immediate/composition record WPF transform application; directcomposition records successful Present submissions only, excluding queue-busy and occluded attempts. " +
        "AppliedTimestamp is the sampling time, not presentation completion. Neither route proves physical display. Interval applied counts combine routes; inspect raw routes before comparing rates.";
    private const string RendererPolicy =
        "Renderer events record completed CPU-side operations, including failed and queue-busy attempts. All timestamps and duration ticks use the process Stopwatch clock; optional CPU thread ticks must use that same frequency. " +
        "Stage durations can contain waits; native setup/map timings are included in native draw, which is included in draw-stage time. Do not add nested durations. " +
        "Present submitted means accepted by the presentation API, not displayed. Sequence correlates a prepared frame and its retry operations; use completion timestamps to order operations. Sample/received/queued timestamps can repeat across attempts. " +
        "Input ages are measured at operation start, exclude missing/future origins, and do not measure physical display latency. " +
        "Thrown draw/present operations retain stage elapsed time and HRESULT, but nested native durations are unavailable and recorded as zero for those error events.";

    internal static string? SafeCaptureId(string? value) =>
        value is { Length: 32 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value : null;

    internal static TachIntervalDiagnostic? SanitizeInterval(TachIntervalDiagnostic sample)
    {
        if (SafeCaptureId(sample.CaptureId) is null || !double.IsFinite(sample.IntervalMilliseconds) ||
            sample.IntervalMilliseconds <= 0 || sample.NativeReads is null || sample.Needles is null ||
            sample.NativeReads.Length > 1024 || sample.Needles.Length > 128 ||
            sample.NativeReads.Any(read => read is null) || sample.Needles.Any(needle => needle is null) ||
            sample.Renderer is null || sample.Renderer.Length > TachDiagnostics.RendererAggregateCapacity ||
            sample.Renderer.Any(renderer => renderer is null || !ValidRendererCounts(renderer)))
            return null;

        return sample with
        {
            NativeReads = sample.NativeReads.Select(read => read with
            {
                Stage = EnumName<NativeGaugeReadStage>(read.Stage),
                Failure = EnumName<NativeGaugeReadFailure>(read.Failure),
                Cache = EnumName<NativeGaugeCacheOutcome>(read.Cache),
                Route = read.Route is "refresh" or "full" ? read.Route : "unknown"
            }).ToArray(),
            Needles = sample.Needles.Select(needle => needle with
            {
                ControlKind = needle.ControlKind is "analogue" or "digital" ? needle.ControlKind : "unknown",
                HostKind = HostKind(needle.HostKind)
            }).ToArray(),
            Renderer = sample.Renderer.Select(renderer => renderer with
            {
                Stage = TachDiagnostics.RendererStage(renderer.Stage),
                Result = TachDiagnostics.RendererResult(renderer.Result)
            }).ToArray()
        };
    }

    internal static string? SelectedCaptureId(IReadOnlyList<TachIntervalDiagnostic> intervals, TachCaptureExport? capture) =>
        capture?.CaptureId ?? intervals.OrderBy(interval => interval.TimestampUtc).LastOrDefault()?.CaptureId;

    internal static object CreateManifest(IReadOnlyList<TachIntervalDiagnostic> intervals, TachCaptureExport? capture,
        long droppedRecords, long omittedRecords) => new
        {
            schema_version = 1,
            selected_capture_id = SelectedCaptureId(intervals, capture),
            raw_capture_id = capture?.CaptureId,
            raw_capture_available = capture is not null,
            raw_lost_after_process_restart = true,
            capture_scope = "one logging-enabled period in this Wisp process; re-enabling starts a new capture",
            retained_interval_records = intervals.Count,
            retained_interval_capture_count = intervals.Select(interval => interval.CaptureId).Distinct().Count(),
            selected_interval_records = intervals.Count(interval => interval.CaptureId == SelectedCaptureId(intervals, capture)),
            capture_started_at_utc = capture?.StartedAtUtc,
            capture_started_timestamp = capture?.StartedTimestamp,
            exported_timestamp = capture?.ExportedTimestamp,
            stopwatch_frequency = capture?.StopwatchFrequency,
            timestamp_basis = "process-local Stopwatch ticks; compare only within the same capture",
            startup_window = "first 60 seconds from the first recorded value in each stream, bounded by capacity",
            startup_recent_overlap = "the two histories can contain the same records; do not concatenate without deduplication",
            native_detail_policy = "at most 60 Hz plus stage/failure/cache/route changes; one-second counters count all observed attempts",
            native_value_fields = "top-level nullable angle/blur are validated values; nested read.angle/read.blur are zero sanitization placeholders",
            native_changed_policy = "validated native angle changes counted across all observed reads; this is not displayed FPS",
            needle_route_policy = NeedleRoutePolicy,
            renderer_schema_version = 1,
            renderer_policy = RendererPolicy,
            renderer_detail_policy = "every observed completed operation, bounded startup/recent rings; no sampling. In-progress operations at export have no completion record",
            renderer_aggregate_policy = "at most 256 control/window/stage/result/HRESULT groups per collected interval; excess groups retain raw detail but omit aggregates. Counters reset each interval",
            renderer_omission_counters = "cumulative within each capture; use the latest or maximum, do not sum intervals",
            renderer_contention_omissions = capture?.RendererContentionOmissions,
            renderer_aggregate_omissions = capture?.RendererAggregateOmissions,
            renderer_invalid_omissions = capture?.RendererInvalidOmissions,
            native_context_records = capture?.NativeContexts.Length,
            native_context_policy = "active compatibility pack changes; at most the latest 32 records",
            contention_omissions = capture?.ContentionOmissions,
            native_detail_sampled_out = capture?.NativeDetailSampledOut,
            counter_omission_policy = "a producer that cannot immediately acquire the collector lock is omitted from both detail and counters",
            interval_persistence = "normally once per second; log retention, writer drops, expiry, crashes and collection gaps limit coverage",
            dropped_log_records = droppedRecords,
            omitted_export_records = omittedRecords,
            gpu_presented_fps = (double?)null,
            gpu_presentation_measured = false,
            physical_display_measured = false,
            input = capture is null ? null : Coverage(capture.InputStartup, capture.InputRecent,
                capture.InputOverwritten, 18_000, 45_000, value => value.Timestamp),
            native = capture is null ? null : Coverage(capture.NativeStartup, capture.NativeRecent,
                capture.NativeOverwritten, 6_000, 18_000, value => value.Timestamp),
            needle = capture is null ? null : Coverage(capture.NeedleStartup, capture.NeedleRecent,
                capture.NeedleOverwritten, 18_000, 45_000, value => value.AppliedTimestamp),
            lifecycle = capture is null ? null : Coverage(Array.Empty<TachLifecycleDiagnostic>(), capture.Lifecycle,
                capture.LifecycleOverwritten, 0, 1024, value => value.Timestamp),
            renderer = capture is null ? null : Coverage(capture.RendererStartup, capture.RendererRecent,
                capture.RendererOverwritten, TachDiagnostics.RendererStartupCapacity, TachDiagnostics.RendererRecentCapacity,
                value => value.CompletedTimestamp),
            build = new
            {
                diagnostic_build_id = ApplicationVersionInfo.DiagnosticBuildId,
                diagnostic_build_label = ApplicationVersionInfo.DiagnosticBuildLabel,
                baseline_version = ApplicationVersionInfo.MachineVersion,
                assembly_mvid = typeof(ApplicationVersionInfo).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
                runtime_version = Environment.Version.ToString(),
                packaged_build = ReadPackagedBuild(Path.Combine(AppContext.BaseDirectory, "build-provenance.json")),
                packaged_hash_status = "recorded by packaging; not recomputed during export"
            }
        };

    internal static string Build(IReadOnlyList<TachIntervalDiagnostic> intervals, TachCaptureExport? capture)
    {
        var id = SelectedCaptureId(intervals, capture);
        var selected = intervals.Where(interval => interval.CaptureId == id).OrderBy(interval => interval.Timestamp).ToArray();
        var text = new StringBuilder("Wisp tach diagnostic report\n");
        text.AppendLine($"Build: {ApplicationVersionInfo.DiagnosticBuildLabel ?? ApplicationVersionInfo.DisplayVersion}");
        text.AppendLine($"Selected capture: {id ?? "none"}");
        text.AppendLine("Only the selected capture contributes to the measurements below. Other retained sessions remain in tach-intervals.ndjson.");
        text.AppendLine("Game FPS, GPU presentation and the physical display are not measured. Applied/changed values are not displayed frames.");
        text.AppendLine(NeedleRoutePolicy);
        text.AppendLine(RendererPolicy);
        text.AppendLine("A steady RPM or native angle can correctly produce few changed values. These measurements alone cannot establish a renderer or Windows root cause.");
        if (capture is null)
        {
            text.AppendLine("Raw histories are unavailable: they are process-local and are lost after a Wisp restart. Retained one-second intervals may still be available.");
        }
        else
        {
            text.AppendLine($"Current capture began (UTC): {capture.StartedAtUtc:O}");
            text.AppendLine($"Monotonic clock frequency: {capture.StopwatchFrequency} ticks/second.");
            text.AppendLine($"Raw input startup/recent: {capture.InputStartup.Length}/{capture.InputRecent.Length}; recent overwritten: {capture.InputOverwritten}.");
            text.AppendLine($"Raw native startup/recent: {capture.NativeStartup.Length}/{capture.NativeRecent.Length}; recent overwritten: {capture.NativeOverwritten}.");
            text.AppendLine($"Raw needle startup/recent: {capture.NeedleStartup.Length}/{capture.NeedleRecent.Length}; recent overwritten: {capture.NeedleOverwritten}.");
            text.AppendLine($"Raw renderer startup/recent: {capture.RendererStartup.Length}/{capture.RendererRecent.Length}; recent overwritten: {capture.RendererOverwritten}.");
            text.AppendLine($"Lifecycle retained: {capture.Lifecycle.Length}; overwritten: {capture.LifecycleOverwritten}.");
            text.AppendLine($"Collector contention omissions: {capture.ContentionOmissions}; native details sampled out: {capture.NativeDetailSampledOut}.");
            text.AppendLine("Startup is the first 60 seconds per stream, subject to capacity. Startup and recent histories overlap; consult tach-manifest.json before combining them.");
        }
        text.AppendLine($"Selected persisted intervals: {selected.Length}.");
        if (selected.Length == 0)
        {
            text.AppendLine("No persisted interval for this capture. Enable Debug logging, reproduce the symptom briefly, then export from the same Wisp process.");
            return text.ToString();
        }

        var seconds = selected.Sum(interval => interval.IntervalMilliseconds) / 1000d;
        text.AppendLine($"Interval observations (UTC): {selected[0].TimestampUtc:O} to {selected[^1].TimestampUtc:O}.");
        text.AppendLine($"Represented interval duration: {Number(seconds)} seconds; intervals over 2 seconds: {selected.Count(interval => interval.IntervalMilliseconds > 2000)}.");
        text.AppendLine($"Input received/drained/accepted/rejected/UI-selected: {Total(selected, interval => interval.InputReceived)}/{Total(selected, interval => interval.InputDrained)}/{Total(selected, interval => interval.InputAccepted)}/{Total(selected, interval => interval.InputRejected)}/{Total(selected, interval => interval.UiSelected)}.");
        text.AppendLine($"Accepted input rate: {Number(selected.Sum(interval => (double)interval.InputAccepted) / Math.Max(seconds, 0.001))}/second; observed RPM changes: {Total(selected, interval => interval.RpmChanges)}; maximum accepted gap: {Number(selected.Max(interval => interval.MaximumAcceptedGapMilliseconds))} ms.");
        text.AppendLine($"Fractional worker waits/deadline already due/signaled wakeups: {Total(selected, interval => interval.FractionalWaits)}/{Total(selected, interval => interval.DeadlineAlreadyDue)}/{Total(selected, interval => interval.WaitWakeups)}; maximum wait: {Number(selected.Max(interval => interval.MaximumWaitMilliseconds))} ms.");
        AppendRenderer(text, selected);
        text.AppendLine("Native reads (all observed attempts; details are capped at 60 Hz plus state changes):");
        foreach (var group in selected.SelectMany(interval => interval.NativeReads)
                     .GroupBy(read => (read.Stage, read.Failure, read.Cache, read.Route)).Take(64))
        {
            var count = group.Sum(read => (double)read.Count);
            var mean = group.Sum(read => read.MeanReadMilliseconds * read.Count) / Math.Max(count, 1);
            text.AppendLine($"  {group.Key.Route}/{group.Key.Stage}/{group.Key.Failure}/{group.Key.Cache}: {Number(count)} attempts, {Number(mean)} ms mean, {Number(group.Max(read => read.MaximumReadMilliseconds))} ms maximum, {Number(group.Sum(read => (double)read.Changed))} validated angle changes.");
        }
        text.AppendLine("Gauge controls (use host plus control ID to distinguish the overlay and preview):");
        foreach (var group in selected.SelectMany(interval => interval.Needles)
                     .GroupBy(needle => (needle.ControlId, needle.ControlKind, needle.HostKind, needle.HostWindowHandle)).Take(128))
        {
            text.AppendLine($"  {group.Key.HostKind}/{group.Key.ControlKind} control {group.Key.ControlId}, window handle {group.Key.HostWindowHandle}: applied {Number(group.Sum(needle => (double)needle.Applied))}, changed {Number(group.Sum(needle => (double)needle.Changed))}, native/fallback/unavailable {Number(group.Sum(needle => (double)needle.Native))}/{Number(group.Sum(needle => (double)needle.Fallback))}/{Number(group.Sum(needle => (double)needle.Unavailable))}.");
            text.AppendLine($"    Maximum apply/changed gap: {Number(group.Max(needle => needle.MaximumApplyGapMilliseconds))}/{Number(group.Max(needle => needle.MaximumChangedGapMilliseconds))} ms; input/native age: {Number(group.Max(needle => needle.MaximumInputAgeMilliseconds))}/{Number(group.Max(needle => needle.MaximumNativeAgeMilliseconds))} ms.");
            text.AppendLine($"    Source switches: {Number(group.Sum(needle => (double)needle.SourceSwitches))}; at newest: {Number(group.Sum(needle => (double)needle.AtNewest))}; cumulative reseeds/starvation reseeds: {group.Max(needle => needle.Reseeds)}/{group.Max(needle => needle.StarvationReseeds)}.");
        }
        text.AppendLine("Compare times with health.ndjson and your observation. One-second observations can miss brief focus changes; a low changed count alone is not evidence of lag.");
        return text.ToString();
    }

    private static void AppendRenderer(StringBuilder text, TachIntervalDiagnostic[] selected)
    {
        var observations = selected.SelectMany(interval => interval.Renderer).ToArray();
        text.AppendLine($"Renderer cumulative omissions (contention/aggregate capacity/invalid timing): {selected.Max(interval => interval.RendererContentionOmissions)}/{selected.Max(interval => interval.RendererAggregateOmissions)}/{selected.Max(interval => interval.RendererInvalidOmissions)}.");
        if (observations.Length == 0)
        {
            text.AppendLine("No renderer-stage observations in these intervals. Older builds did not record these timings; absence is not zero rendering work.");
            return;
        }
        text.AppendLine($"Present attempts submitted/busy/occluded/error: {RendererTotal(observations, "present", "submitted")}/{RendererTotal(observations, "present", "busy")}/{RendererTotal(observations, "present", "occluded")}/{RendererTotal(observations, "present", "error")}; frame-wait timeouts: {RendererTotal(observations, "frame_wait", "timeout")}.");
        var longest = observations.Where(value => value.Count > 0)
            .OrderByDescending(value => value.MaximumMilliseconds).FirstOrDefault();
        if (longest is not null)
            text.AppendLine($"Longest observed renderer operation: {longest.Stage}/{longest.Result}, {Number(longest.MaximumMilliseconds)} ms (control {longest.ControlId}, window handle {longest.HostWindowHandle}). This identifies where time was observed, not why the operation waited.");
        text.AppendLine("Renderer stages by control and outcome (CPU-side elapsed time, including waits):");
        foreach (var group in observations.GroupBy(value => (value.ControlId, value.HostWindowHandle, value.Stage, value.Result, value.HResult)).Take(256))
        {
            var count = group.Sum(value => (double)value.Count);
            var mean = group.Sum(value => value.MeanMilliseconds * value.Count) / Math.Max(1, count);
            var queueSamples = group.Sum(value => (double)value.QueueAgeSamples);
            var queueAge = group.Sum(value => value.MeanQueueAgeMilliseconds * value.QueueAgeSamples) / Math.Max(1, queueSamples);
            text.AppendLine($"  Control {group.Key.ControlId}, window handle {group.Key.HostWindowHandle}, {group.Key.Stage}/{group.Key.Result}, HRESULT 0x{group.Key.HResult:X8}: {Number(count)} operations; {Number(mean)} ms mean, {Number(group.Max(value => value.MaximumMilliseconds))} ms maximum.");
            text.AppendLine($"    Draw commands/maps: {Number(group.Sum(value => (double)value.DrawCommands))}/{Number(group.Sum(value => (double)value.MapCount))}; total scene/setup/native draw/map/present: {Number(group.Sum(value => value.TotalSceneMilliseconds))}/{Number(group.Sum(value => value.TotalNativeSetupMilliseconds))}/{Number(group.Sum(value => value.TotalNativeDrawMilliseconds))}/{Number(group.Sum(value => value.TotalMapMilliseconds))}/{Number(group.Sum(value => value.TotalNativePresentMilliseconds))} ms; maximum single map: {Number(group.Max(value => value.MaximumMapMilliseconds))} ms.");
            text.AppendLine($"    Queue age: {Number(queueAge)} ms mean, {Number(group.Max(value => value.MaximumQueueAgeMilliseconds))} ms maximum ({Number(queueSamples)} observations); discarded queued inputs: {Number(group.Sum(value => (double)value.QueueDropped))}.");
        }
    }

    private static string RendererTotal(IEnumerable<TachRendererCounts> values, string stage, string result) =>
        Number(values.Where(value => value.Stage == stage && value.Result == result).Sum(value => (double)value.Count));

    private static bool ValidRendererCounts(TachRendererCounts value) =>
        value.Count >= 0 && value.DrawCommands >= 0 && value.MapCount >= 0 && value.QueueDropped >= 0 &&
        value.QueueAgeSamples >= 0 && value.ReceiveAgeSamples >= 0 && value.SampleAgeSamples >= 0 && value.CpuThreadSamples >= 0 &&
        new[] { value.MeanMilliseconds, value.MaximumMilliseconds, value.TotalMapMilliseconds, value.MaximumMapMilliseconds,
            value.TotalSceneMilliseconds, value.TotalNativeSetupMilliseconds, value.TotalNativeDrawMilliseconds,
            value.TotalNativePresentMilliseconds, value.MeanQueueAgeMilliseconds, value.MaximumQueueAgeMilliseconds,
            value.MeanReceiveAgeMilliseconds, value.MaximumReceiveAgeMilliseconds, value.MeanSampleAgeMilliseconds,
            value.MaximumSampleAgeMilliseconds, value.TotalCpuThreadMilliseconds }.All(number => double.IsFinite(number) && number >= 0);

    internal static object Coverage<T>(T[] startup, T[] recent, long overwritten,
        int startupCapacity, int recentCapacity, Func<T, long> timestamp) where T : struct => new
        {
            startup_records = startup.Length,
            recent_records = recent.Length,
            startup_capacity = startupCapacity,
            recent_capacity = recentCapacity,
            startup_capacity_reached = startupCapacity > 0 && startup.Length >= startupCapacity,
            recent_overwritten = overwritten,
            startup_recent_duplicate_records = Overlap(startup, recent),
            startup_first_timestamp = startup.Length == 0 ? (long?)null : timestamp(startup[0]),
            startup_last_timestamp = startup.Length == 0 ? (long?)null : timestamp(startup[^1]),
            recent_first_timestamp = recent.Length == 0 ? (long?)null : timestamp(recent[0]),
            recent_last_timestamp = recent.Length == 0 ? (long?)null : timestamp(recent[^1])
        };

    private static int Overlap<T>(IEnumerable<T> startup, IEnumerable<T> recent) where T : struct
    {
        var counts = recent.GroupBy(value => value).ToDictionary(group => group.Key, group => group.Count());
        var overlap = 0;
        foreach (var value in startup)
        {
            if (counts.TryGetValue(value, out var count) && count > 0)
            {
                counts[value] = count - 1;
                overlap++;
            }
        }
        return overlap;
    }

    internal static object? ReadPackagedBuild(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 16_384) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 16_384) return null;
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var kind = root.GetProperty("build_kind").GetString();
            var version = root.GetProperty("version").GetString();
            var revision = root.GetProperty("source_revision").GetString();
            var hash = root.GetProperty("app_sha256").GetString();
            var bytes = root.GetProperty("app_bytes").GetInt64();
            var nativeRenderer = root.GetProperty("native_renderer");
            var nativeFile = nativeRenderer.GetProperty("file").GetString();
            var nativeHash = nativeRenderer.GetProperty("sha256").GetString();
            var nativeBytes = nativeRenderer.GetProperty("bytes").GetInt64();
            var validBuildKind = kind == "release" ||
                (kind == "private" && ApplicationVersionInfo.DiagnosticBuildId is { Length: > 0 } privateId &&
                 root.TryGetProperty("private_build_id", out var packagedId) && packagedId.GetString() == privateId);
            if (root.GetProperty("schema_version").GetInt32() != 1 || !validBuildKind ||
                version != ApplicationVersionInfo.MachineVersion ||
                revision is not { Length: 40 or 64 } || !revision.All(Uri.IsHexDigit) ||
                hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) || bytes <= 0 ||
                nativeFile != "Wisp.NativeRenderer.dll" || nativeHash is not { Length: 64 } ||
                !nativeHash.All(Uri.IsHexDigit) || nativeBytes <= 0) return null;
            return new
            {
                schema_version = 1,
                build_kind = kind,
                version,
                source_revision = revision,
                source_dirty = root.GetProperty("source_dirty").GetBoolean(),
                created_at_utc = root.GetProperty("created_at_utc").GetDateTimeOffset(),
                app_sha256 = hash,
                app_bytes = bytes,
                native_renderer = new { file = nativeFile, sha256 = nativeHash, bytes = nativeBytes }
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or
            JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string EnumName<T>(string? value) where T : struct, Enum =>
        value is not null && Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) ? value : "Unknown";

    private static string HostKind(string? value) => value is "MainWindow" or "OverlayWindow" or "unhosted" ? value : "unknown";
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Total(IEnumerable<TachIntervalDiagnostic> intervals, Func<TachIntervalDiagnostic, long> value) =>
        Number(intervals.Sum(interval => (double)value(interval)));
}
