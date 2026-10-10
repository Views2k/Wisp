using System.Diagnostics;
using System.IO;
using Wisp.App.Clips;
using Wisp.App.DebugLogging;
using Wisp.App.Supplementary;
using Wisp.Core;

namespace Wisp.App;

public partial class App
{
    private readonly object _supplementaryGate = new();
    private SupplementaryClient? _supplementaryClient;
    private bool _supplementaryStopped;
    private Guid _reportingSessionId, _reportingInstallationId;
    private readonly Queue<SupplementarySignal> _supplementarySignals = new(32);
    private sealed record SupplementarySignal(string Kind, string Feature, string Outcome, string? Stage, double? DurationMs,
        DateTimeOffset ObservedAt, SupplementaryIncident? Incident);
    private long _supplementaryStartedAt;
    private string _supplementaryVersion = "0.0.0", _supplementaryBuild = "unconfigured", _supplementaryChannel = "stable";
    private HashSet<DateTimeOffset> _reportedHealthTimes = [];
    private readonly HashSet<DateTimeOffset> _reportedNativeHealthTimes = [];
    private HashSet<HealthBreadcrumb> _reportedBreadcrumbs = [];
    private HashSet<Guid> _reportedCrashes = [];
    private HealthContextSample? _previousReportedHealth;
    private long _previousDroppedEvents;
    private string? _reportedConnectionState;
    private NativeAssistProviderStatus? _reportedNativeStatus;
    private string _reportingPlatform = "unknown";
    private string? _reportingGameBuild;
    private bool _supplementaryReadyReported;
    private long _reportedWorkingSetAge = -1, _reportedManagedHeapAge = -1, _reportedCpuAge = -1;
    internal event EventHandler? SupplementaryContentChanged;

    private void NotifySupplementaryContentChanged()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try { Dispatcher.BeginInvoke(new Action(() => SupplementaryContentChanged?.Invoke(this, EventArgs.Empty))); }
        catch (InvalidOperationException) { }
    }

    internal bool SupplementaryAvailable
    {
        get { lock (_supplementaryGate) return _supplementaryClient?.IsReportingEnabled == true; }
    }

    internal (SupplementaryContentSnapshot Content, SupplementaryAudienceContext Audience) GetSupplementaryContent()
    {
        lock (_supplementaryGate)
            return (_supplementaryClient?.Content ?? SupplementaryContentSnapshot.Empty,
                new(_supplementaryVersion, _supplementaryChannel, _reportingPlatform, _reportingGameBuild));
    }

    internal SupplementarySupportReport? CreateSupplementarySupportPreview(string category, string title, string message, bool includeDiagnostics)
    {
        lock (_supplementaryGate)
        {
            if (_supplementaryClient?.IsReportingEnabled != true) return null;
            var connection = _reportedConnectionState is "idle" or "detected" or "fresh" or "stale" ? _reportedConnectionState : "unknown";
            return SupplementarySchema.RedactForPreview(new(1, Guid.NewGuid(), _reportingSessionId, _reportingInstallationId,
                DateTimeOffset.UtcNow, _supplementaryVersion, _supplementaryBuild, _supplementaryChannel, category, title, message,
                includeDiagnostics ? new(_reportingPlatform, connection, "none", _reportingGameBuild) : null));
        }
    }

    internal Task<SupplementarySupportResult> SubmitSupplementarySupportAsync(SupplementarySupportReport reviewed, CancellationToken cancellation = default)
    {
        lock (_supplementaryGate)
        {
            if (_supplementaryClient is not { } client) return Task.FromResult(new SupplementarySupportResult(SupplementaryRequestStatus.Unconfigured));
            if (reviewed.SessionId != _reportingSessionId || reviewed.InstallationId != _reportingInstallationId ||
                reviewed.AppVersion != _supplementaryVersion || reviewed.BuildId != _supplementaryBuild || reviewed.Channel != _supplementaryChannel)
                return Task.FromResult(new SupplementarySupportResult(SupplementaryRequestStatus.Invalid));
            return client.SubmitSupportAsync(reviewed, cancellation);
        }
    }

    private void StartSupplementaryReporting()
    {
        ToolsPerformanceRecorder.Current.Enabled = false;
        if (!SupplementaryRuntime.IsConfigured) return;
        SupplementaryObservations.Observer = ObserveSupplementaryOperation;
        _controller?.InitializeSupplementaryObservations();
        _supplementaryStartedAt = Stopwatch.GetTimestamp();
        SupplementarySessionRecorder.Current.Start();
        SupplementaryActivityRecorder.Current.Start();
        _supplementaryVersion = ApplicationVersionInfo.MachineVersion;
        _supplementaryChannel = ApplicationVersionInfo.IsPrivateCandidate || ApplicationVersionInfo.DiagnosticBuildId is not null ? "private" : "stable";
        _supplementaryBuild = SupplementarySchema.BuildIdentity(ApplicationVersionInfo.DiagnosticBuildId)
            ? ApplicationVersionInfo.DiagnosticBuildId! : typeof(App).Assembly.ManifestModule.ModuleVersionId.ToString("N");
        var runId = _runExitMarker?.RunId;
        // File reads, identity creation and network initialization never hold the startup dispatcher.
        _ = Task.Run(() =>
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "Supplementary");
                var identity = SupplementaryIdentityStore.LoadOrCreate(directory);
                if (identity is null) { SupplementarySessionRecorder.Current.Enabled = false; SupplementaryActivityRecorder.Current.Enabled = false; return; }
                var content = new SupplementaryContentStore(new(SupplementaryRuntime.PublicKeys), Path.Combine(directory, "Content"));
                var client = new SupplementaryClient(SupplementaryRuntime.Configuration, content, collect: CollectSupplementaryObservations,
                    contentChanged: NotifySupplementaryContentChanged);
                lock (_supplementaryGate)
                {
                    if (_supplementaryStopped) { client.Dispose(); return; }
                    _reportingInstallationId = identity.Id;
                    _reportingSessionId = runId is { } id && SupplementarySchema.Uuid4(id) ? id : Guid.NewGuid();
                    _supplementaryClient = client;
                    EnqueueSupplementary(client, "session-start", "app", "none", "launch");
                    // Existing installations upgrading to reporting are not falsely called new Wisp installations.
                    foreach (var signal in _supplementarySignals)
                        EnqueueSupplementary(client, signal.Kind, signal.Feature, signal.Outcome, signal.Stage, signal.DurationMs,
                            observedAt: signal.ObservedAt, incident: signal.Incident);
                    _supplementarySignals.Clear();
                    ToolsPerformanceRecorder.Current.Enabled = client.IsReportingEnabled;
                    SupplementarySessionRecorder.Current.Enabled = client.IsReportingEnabled;
                    SupplementaryActivityRecorder.Current.Enabled = client.IsReportingEnabled;
                    client.Start();
                }
                NotifySupplementaryContentChanged();
                _ = client.Completion.ContinueWith(_ =>
                    { ToolsPerformanceRecorder.Current.Enabled = false; SupplementarySessionRecorder.Current.Enabled = false; SupplementaryActivityRecorder.Current.Enabled = false; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception)
            { ToolsPerformanceRecorder.Current.Enabled = false; SupplementarySessionRecorder.Current.Enabled = false; SupplementaryActivityRecorder.Current.Enabled = false; } // Supplementary service has no authority over startup.
        });
    }

    private void RecordSupplementarySignal(string kind, string feature, string outcome, string? stage = null, double? durationMs = null,
        SupplementaryIncident? incident = null, DateTimeOffset? observedAt = null)
    {
        if (!SupplementaryRuntime.IsConfigured) return;
        lock (_supplementaryGate)
        {
            if (_supplementaryStopped) return;
            if (_supplementaryClient is { } client) EnqueueSupplementary(client, kind, feature, outcome, stage, durationMs,
                observedAt: observedAt, incident: incident);
            else if (_supplementarySignals.Count < 32) _supplementarySignals.Enqueue(new(kind, feature, outcome, stage, durationMs,
                observedAt ?? DateTimeOffset.UtcNow, incident));
        }
    }

    private void ObserveSupplementaryOperation(SupplementaryObservation observation) =>
        RecordSupplementarySignal(observation.Kind, observation.Feature, observation.Outcome, observation.Stage, observation.DurationMs,
            observation.Incident, observation.ObservedAt);

    private void RecordSupplementaryRuntimeReady()
    {
        RecordSupplementarySignal("connection", "telemetry", "success", "binding");
        if (_supplementaryReadyReported) return;
        _supplementaryReadyReported = true;
        RecordSupplementarySignal("startup", "app", "success", "runtime-ready", _supplementaryStartedAt == 0 ? null :
            Stopwatch.GetElapsedTime(_supplementaryStartedAt).TotalMilliseconds);
    }

    private void StopSupplementaryReporting()
    {
        SupplementaryObservations.Observer = null;
        _controller?.DisposeSupplementaryObservations();
        ToolsPerformanceRecorder.Current.Enabled = false;
        SupplementarySessionRecorder.Current.Enabled = false;
        SupplementaryActivityRecorder.Current.Enabled = false;
        lock (_supplementaryGate)
        {
            _supplementaryStopped = true;
            _supplementarySignals.Clear();
            _supplementaryClient?.Dispose();
            _supplementaryClient = null;
        }
        // No exit flush: a missing final heartbeat is not classified as a crash or an exact session ending.
    }

    private void CollectSupplementaryObservations(SupplementaryClient client)
    {
        lock (_supplementaryGate)
        {
            ToolsPerformanceRecorder.Current.Enabled = !_supplementaryStopped && client.IsReportingEnabled;
            SupplementarySessionRecorder.Current.Enabled = !_supplementaryStopped && client.IsReportingEnabled;
            SupplementaryActivityRecorder.Current.Enabled = !_supplementaryStopped && client.IsReportingEnabled;
        }
        if (!client.IsReportingEnabled)
        {
            _ = ToolsPerformanceRecorder.Current.Drain();
            _reportedHealthTimes.Clear(); _reportedNativeHealthTimes.Clear(); _reportedBreadcrumbs.Clear(); _previousReportedHealth = null;
            return;
        }
        var context = HealthContextRecorder.Current.Snapshot();
        var samples = context.Samples.Where(s => !_reportedHealthTimes.Contains(s.TimestampUtc)).ToArray();
        _reportedHealthTimes = context.Samples.Select(s => s.TimestampUtc).ToHashSet();
        var latest = samples.LastOrDefault();
        var now = DateTimeOffset.UtcNow;
        if (latest is not null)
        {
            var audienceChanged = false;
            lock (_supplementaryGate)
            {
                var previous = (_reportingPlatform, _reportingGameBuild);
                _reportingPlatform = latest.NativeGamePlatform switch { DiagnosticGamePlatform.Steam => "steam", DiagnosticGamePlatform.XboxStore => "store", _ => "unknown" };
                _reportingGameBuild = SupplementarySchema.NumericVersion(latest.NativeGameVersion, false) ? latest.NativeGameVersion : null;
                audienceChanged = previous != (_reportingPlatform, _reportingGameBuild);
            }
            if (audienceChanged) NotifySupplementaryContentChanged();
        }
        var freshSnapshot = latest is not null && latest.TimestampUtc <= now + TimeSpan.FromMinutes(5) && now - latest.TimestampUtc <= TimeSpan.FromSeconds(10);
        var connection = !freshSnapshot ? "unknown" : latest!.ListenerError ? "failure"
            : latest.ListenerRunning && latest.PacketAgeMs is >= 0 and <= 300 ? "fresh"
            : latest.NativeStatus is NativeAssistProviderStatus.Ready or NativeAssistProviderStatus.UnsupportedBuild or NativeAssistProviderStatus.AccessDenied ? "detected"
            : latest.ListenerRunning && latest.PacketAgeMs is not null ? "stale" : "idle";
        EnqueueSupplementary(client, "heartbeat", "app", connection, "ready", sessionSummary: SupplementarySessionRecorder.Current.Snapshot(),
            activitySummary: SupplementaryActivityRecorder.Current.Snapshot());
        if (_reportedConnectionState != connection)
        {
            // This is an observed state transition. It is not an invented connection attempt denominator.
            if (EnqueueSupplementary(client, "connection", "telemetry", connection, "data-out"))
                _reportedConnectionState = connection;
        }
        if (samples.Length > 0) CollectSupplementaryHealth(client, samples);
        _reportedNativeHealthTimes.IntersectWith(context.Samples.Select(sample => sample.TimestampUtc));
        foreach (var sample in context.Samples.Where(sample => !_reportedNativeHealthTimes.Contains(sample.TimestampUtc)))
        {
            if (_reportedNativeStatus != sample.NativeStatus && sample.NativeStatus is NativeAssistProviderStatus.AccessDenied or NativeAssistProviderStatus.UnsupportedBuild)
                if (!EnqueueSupplementary(client, "connection", "native", sample.NativeStatus == NativeAssistProviderStatus.AccessDenied ? "failure" : "unsupported",
                    "validation", observedAt: sample.TimestampUtc, incident: SupplementaryIncidentCapture.Native(sample, context),
                    platform: sample.NativeGamePlatform switch { DiagnosticGamePlatform.Steam => "steam", DiagnosticGamePlatform.XboxStore => "store", _ => "unknown" },
                    gameBuild: sample.NativeGameVersion, historical: true)) break;
            _reportedNativeStatus = sample.NativeStatus;
            _reportedNativeHealthTimes.Add(sample.TimestampUtc);
        }
        CollectSupplementarySessionResources(client);
        foreach (var distribution in ToolsPerformanceRecorder.Current.Drain()) CollectSupplementaryPerformance(client, distribution);
        _reportedBreadcrumbs.IntersectWith(context.Breadcrumbs);
        foreach (var breadcrumb in context.Breadcrumbs.Where(b => !_reportedBreadcrumbs.Contains(b)))
            if (CollectSupplementaryBreadcrumb(client, breadcrumb)) _reportedBreadcrumbs.Add(breadcrumb);
        CollectSupplementaryCrashes(client);
        var dropped = client.DroppedEvents;
        if (dropped > _previousDroppedEvents &&
            EnqueueSupplementary(client, "resource", "app", "unknown", "measurement-dropped", value: dropped - _previousDroppedEvents))
            _previousDroppedEvents = dropped;
    }

    private void CollectSupplementarySessionResources(SupplementaryClient client)
    {
        if (SupplementarySessionRecorder.Current.LatestResource() is not { } sample) return;
        // These are actual point observations, separate from the unaged interval mean/maximum summaries.
        // The server groups them by fixed elapsed-session age bands, never by raw per-millisecond keys.
        if (sample.SessionAgeMs > _reportedWorkingSetAge && EnqueueSupplementary(client, "resource", "app", "none", "working-set", value: sample.WorkingSetBytes,
                sessionAgeMs: sample.SessionAgeMs, observedAt: sample.ObservedAt, platform: sample.Platform, gameBuild: sample.GameBuild, historical: true))
            _reportedWorkingSetAge = sample.SessionAgeMs;
        if (sample.SessionAgeMs > _reportedManagedHeapAge && EnqueueSupplementary(client, "resource", "app", "none", "managed-heap", value: sample.ManagedHeapBytes,
                sessionAgeMs: sample.SessionAgeMs, observedAt: sample.ObservedAt, platform: sample.Platform, gameBuild: sample.GameBuild, historical: true))
            _reportedManagedHeapAge = sample.SessionAgeMs;
        if (sample.CpuPercent is { } cpu && sample.SessionAgeMs > _reportedCpuAge && EnqueueSupplementary(client, "resource", "app", "none", "cpu", value: cpu,
                sessionAgeMs: sample.SessionAgeMs, observedAt: sample.ObservedAt, platform: sample.Platform, gameBuild: sample.GameBuild, historical: true))
            _reportedCpuAge = sample.SessionAgeMs;
    }

    private void CollectSupplementaryHealth(SupplementaryClient client, HealthContextSample[] samples)
    {
        // Counter rates have a measured interval only after a previous snapshot; first totals carry no invented duration.
        var intervalMs = _previousReportedHealth is { } baseline ? (samples[^1].TimestampUtc - baseline.TimestampUtc).TotalMilliseconds : (double?)null;
        if (intervalMs is <= 0 or > 604800000) intervalMs = null;
        long accepted = 0, rejected = 0, nativeAttempts = 0, nativeFailures = 0, submissions = 0, busy = 0, occluded = 0, failures = 0, queueDropped = 0, collectorFailures = 0;
        long received = 0, drained = 0, processed = 0, gen2 = 0;
        foreach (var s in samples)
        {
            var previous = _previousReportedHealth;
            accepted += Delta(s.AcceptedPackets, previous?.AcceptedPackets);
            rejected += Delta(s.RejectedPackets, previous?.RejectedPackets);
            received += Delta(s.ReceivedDatagrams, previous?.ReceivedDatagrams);
            drained += Delta(s.DrainedDatagrams, previous?.DrainedDatagrams);
            processed += Delta(s.ProcessedPackets, previous?.ProcessedPackets);
            gen2 += Delta(s.Gen2Collections, previous?.Gen2Collections);
            nativeAttempts += Delta(s.NativeReadAttempts, previous?.NativeReadAttempts);
            nativeFailures += Delta(s.NativeReadFailures, previous?.NativeReadFailures);
            submissions += Delta(s.Renderer.Submissions, previous?.Renderer.Submissions);
            busy += Delta(s.Renderer.BusyResults, previous?.Renderer.BusyResults);
            occluded += Delta(s.Renderer.OccludedResults, previous?.Renderer.OccludedResults);
            failures += Delta(s.Renderer.Failures, previous?.Renderer.Failures);
            queueDropped += Delta(s.Renderer.QueueDropped, previous?.Renderer.QueueDropped);
            collectorFailures += Delta(s.CollectorFailures, previous?.CollectorFailures);
            _previousReportedHealth = s;
        }
        foreach (var (stage, value, feature) in new (string, long, string)[] { ("accepted-packets", accepted, "telemetry"), ("rejected-packets", rejected, "telemetry"),
            ("native-attempts", nativeAttempts, "native"), ("native-failures", nativeFailures, "native"), ("renderer-submissions", submissions, "hud"),
            ("renderer-busy", busy, "hud"), ("renderer-occluded", occluded, "hud"), ("renderer-failures", failures, "hud"), ("queue-dropped", queueDropped, "hud"), ("collector-failures", collectorFailures, "app"),
            ("received-datagrams", received, "telemetry"), ("drained-datagrams", drained, "telemetry"), ("processed-packets", processed, "telemetry"),
            ("gc-gen2-collections", gen2, "app"), ("health-samples", samples.Length, "app"), ("dispatcher-pending-samples", samples.Count(s => s.DispatcherProbePending), "app") })
            EnqueueSupplementary(client, feature == "telemetry" ? "udp-summary" : "resource", feature, "none", stage, durationMs: intervalMs, value: value);
        var cpu = samples.Where(s => s.CpuPercent.HasValue).Select(s => s.CpuPercent!.Value).ToArray();
        if (cpu.Length > 0) EnqueueSupplementary(client, "resource", "app", "none", "cpu", value: cpu.Average());
        EnqueueSupplementary(client, "resource", "app", "none", "working-set", value: samples.Max(s => s.WorkingSetBytes));
        EnqueueSupplementary(client, "resource", "app", "none", "managed-heap", value: samples.Max(s => s.ManagedHeapBytes));
        CollectSupplementarySampleDistribution(client, "telemetry-age", samples.Where(s => s.ListenerRunning && s.GameActive).Select(s => s.PacketAgeMs));
        CollectSupplementarySampleDistribution(client, "native-age", samples.Where(s => s.NativeExpected).Select(s => s.NativeAgeMs));
        CollectSupplementarySampleDistribution(client, "dispatcher", samples.Select(s => s.DispatcherDelayMs));
        CollectSupplementarySampleDistribution(client, "ui-heartbeat-age", samples.Select(s => s.UiHeartbeatAgeMs));
        CollectSupplementarySampleDistribution(client, "composition-maximum-gap", samples.Where(s => s.OverlayExpectedVisible).Select(s => (double?)s.CompositionMaximumGapMs));
        CollectSupplementarySampleDistribution(client, "health-collection-gap", samples.Select(s => (double?)s.CollectionGapMs));
        CollectSupplementarySampleDistribution(client, "composition-callback-age", samples.Where(s => s.OverlayExpectedVisible).Select(s => s.CompositionCallbackAgeMs));
    }

    private void CollectSupplementarySampleDistribution(SupplementaryClient client, string stage, IEnumerable<double?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        var samples = present.Where(v => v is >= 0 and <= 60000 && double.IsFinite(v)).ToArray();
        if (present.Length > samples.Length)
            EnqueueSupplementary(client, "resource", "app", "unknown", "measurement-dropped", value: present.Length - samples.Length);
        if (samples.Length == 0) return;
        if (SupplementaryPerformanceProjection.SampledMeasurements(samples) is { } measurement)
            EnqueueSupplementary(client, "resource", "app", "none", stage, measurements: measurement);
    }

    private void CollectSupplementaryPerformance(SupplementaryClient client, ToolsPerformanceDistribution d)
    {
        var stage = SupplementaryPerformanceProjection.Stage(d.Metric);
        if (stage is null) return;
        var mode = SupplementaryPerformanceProjection.HasRenderMode(d.Metric) ? d.CpuRendering ? "cpu" : "gpu" : null;
        var feature = SupplementaryPerformanceProjection.Feature(d.Metric);
        if (d.Dropped > 0) EnqueueSupplementary(client, "resource", feature, "unknown", "measurement-dropped", value: d.Dropped, mode: mode);
        if (d.Count <= 0) return;
        var measurement = SupplementaryPerformanceProjection.Measurements(d);
        if (measurement is null)
        { EnqueueSupplementary(client, "resource", feature, "unknown", "measurement-dropped", value: d.Count, mode: mode); return; }
        EnqueueSupplementary(client, "resource", feature, "none", stage, measurements: measurement, mode: mode);
    }

    private bool CollectSupplementaryBreadcrumb(SupplementaryClient client, HealthBreadcrumb b)
    {
        var mapped = b.Code switch
        {
            HealthEventCode.RendererFailed => ("resource", "hud", "failure", "present-call"),
            HealthEventCode.HealthCollectionFailed => ("resource", "app", "failure", "collector-failures"),
            HealthEventCode.LapReviewFailed => ("saved-data", "runs", "failure", "load"),
            HealthEventCode.RecorderChanged when b.RecorderState == ClipRecorderState.Preparing => ("clips", "clips", "attempt", "capture-init"),
            HealthEventCode.RecorderChanged when b.RecorderState == ClipRecorderState.Buffering => ("clips", "clips", "success", "buffer"),
            HealthEventCode.RecorderChanged when b.RecorderState == ClipRecorderState.Error => ("clips", "clips", "failure", "capture"),
            _ => default
        };
        if (mapped.Item1 is not null)
        {
            var category = b.Code switch
            {
                HealthEventCode.RendererFailed => "renderer",
                HealthEventCode.HealthCollectionFailed => "collector",
                HealthEventCode.LapReviewFailed => "saved-data",
                _ => null
            };
            return EnqueueSupplementary(client, mapped.Item1, mapped.Item2!, mapped.Item3!, mapped.Item4,
                value: b.ErrorCode == 0 ? null : unchecked((uint)b.ErrorCode), observedAt: b.TimestampUtc,
                incident: category is null ? null : SupplementaryIncidentCapture.Breadcrumb(category, b, HealthContextRecorder.Current.Snapshot()));
        }
        return true; // Unmapped local breadcrumbs are intentionally outside the automatic schema.
    }

    private void CollectSupplementaryCrashes(SupplementaryClient client)
    {
        if (!_crashReports.TryReadAll(out var reports)) return;
        _reportedCrashes.IntersectWith(reports.Select(report => report.Id));
        foreach (var report in reports.Where(r => !_reportedCrashes.Contains(r.Id)))
        {
            // Only the explicit incident projection is uploaded; the complete local report stays local.
            if (report.TimeUtc < DateTimeOffset.UtcNow - TimeSpan.FromDays(1)) continue;
            var stage = report.Origin == CrashDiagnostics.CrashOrigin.UnexpectedExit ? "unexpected-exit"
                : report.Origin == CrashDiagnostics.CrashOrigin.WindowsApplicationFault ? "windows-fault"
                : report.IsTerminating ? "fatal-exception" : "continuing-exception";
            // A report without a recorded originating run cannot be safely attributed to this session.
            if (report.RunId is not { } runId || !SupplementarySchema.Uuid4(runId)) continue;
            var prior = report.Context?.Samples.LastOrDefault();
            if (EnqueueSupplementary(client, "exception", "app", stage == "unexpected-exit" ? "unknown" : "failure", stage,
                value: report.Exceptions.FirstOrDefault() is { } error ? unchecked((uint)error.HResult) : null,
                eventId: report.Id, observedAt: report.TimeUtc,
                relatedRun: runId == _reportingSessionId ? null : new(runId, report.WispVersion,
                    SupplementarySchema.BuildIdentity(report.PrivateBuildId) ? report.PrivateBuildId! : report.ModuleVersionId?.ToString("N") ?? "unknown",
                    report.PrivateBuildId is not null ? "private" : "stable"),
                platform: prior?.NativeGamePlatform switch { DiagnosticGamePlatform.Steam => "steam", DiagnosticGamePlatform.XboxStore => "store", _ => "unknown" },
                gameBuild: prior?.NativeGameVersion, historical: true, incident: SupplementaryIncidentCapture.Crash(report)))
                _reportedCrashes.Add(report.Id);
        }
    }

    private bool EnqueueSupplementary(SupplementaryClient client, string kind, string feature, string outcome, string? stage,
        double? durationMs = null, double? value = null, SupplementaryMeasurements? measurements = null, string? mode = null,
        Guid? eventId = null, DateTimeOffset? observedAt = null, SupplementaryRelatedRun? relatedRun = null,
        string? platform = null, string? gameBuild = null, bool historical = false,
        SupplementarySessionSummary? sessionSummary = null, long? sessionAgeMs = null, SupplementaryIncident? incident = null,
        SupplementaryActivitySummary? activitySummary = null)
    {
        return client.TryEnqueue(new(1, eventId ?? Guid.NewGuid(), _reportingSessionId, _reportingInstallationId,
            observedAt ?? DateTimeOffset.UtcNow, _supplementaryVersion, _supplementaryBuild,
            _supplementaryChannel, kind, feature, outcome, platform ?? _reportingPlatform, durationMs, value,
            historical ? gameBuild : _reportingGameBuild, stage, measurements, mode, relatedRun, sessionSummary, sessionAgeMs, incident, activitySummary));
    }
    private static long Delta(long current, long? previous) => current >= (previous ?? 0) ? current - (previous ?? 0) : current;
}
