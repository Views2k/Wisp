using System.Collections.Immutable;
using System.Text.Json;

namespace Wisp.App.Supplementary;

internal sealed record SupplementaryDiagnosticObservation(DateTimeOffset At, string Kind, string Feature,
    string Outcome, string? Stage, double? Value, double? DurationMs, SupplementaryMeasurements? Measurements, string? Reason,
    SupplementaryIncident? Incident = null, NativeGameVerification? GameVerification = null,
    SupplementaryUpdateStatus? UpdateStatus = null, string? Platform = null, string? GameBuild = null,
    string? RenderMode = null, string? ForegroundState = null, long? SessionAgeMs = null,
    SupplementarySessionSummary? SessionSummary = null, SupplementaryActivitySummary? ActivitySummary = null, SupplementaryRelatedRun? RelatedRun = null);

internal sealed record SupplementaryDiagnosticHistory(int SchemaVersion, DateTimeOffset From, DateTimeOffset To,
    int Omitted, SupplementaryEnvironment? Environment, ImmutableArray<SupplementaryDiagnosticObservation> Observations, SupplementaryDiagnosticDetail? Detail = null)
{
    internal const int MaximumBytes = 1536 * 1024;
    internal bool IsValid(DateTimeOffset observedAt) => SchemaVersion is 1 or 2 && From.Offset == TimeSpan.Zero && To.Offset == TimeSpan.Zero &&
        From <= To && To - From <= TimeSpan.FromMinutes(20) && To <= observedAt + TimeSpan.FromSeconds(1) && observedAt - To <= TimeSpan.FromMinutes(5) &&
        Omitted >= 0 && !Observations.IsDefault && Observations.Length <= 2048 &&
        (Environment is null || Environment.IsValid(To)) && Observations.All(p => p is not null && p.At.Offset == TimeSpan.Zero && p.At >= From && p.At <= To &&
            SupplementarySchema.Kinds.Contains(p.Kind) && SupplementarySchema.Features.Contains(p.Feature) &&
            SupplementarySchema.Outcomes.Contains(p.Outcome) && (p.Stage is null || SupplementarySchema.Stages.Contains(p.Stage)) &&
            SupplementarySchema.Number(p.Value, 1_000_000_000_000) && SupplementarySchema.Number(p.DurationMs, 604_800_000) &&
            (p.Measurements is null || p.Measurements.IsValid) && (p.Reason is null || SupplementarySchema.Token(p.Reason, 96)) &&
            (p.Incident is null || SupplementaryIncidentSchema.Valid(p.Incident)) &&
            (p.GameVerification is null || p.GameVerification.IsValid) && (p.UpdateStatus is null || p.UpdateStatus.IsValid) &&
            (p.Platform is null || SupplementarySchema.Platforms.Contains(p.Platform)) &&
            (p.GameBuild is null || SupplementarySchema.NumericVersion(p.GameBuild, app: false)) &&
            (p.RenderMode is null or "cpu" or "gpu") && (p.ForegroundState is null or "game" or "wisp" or "other" or "unknown") &&
            (p.SessionAgeMs is null or >= 0 and <= 1_000_000_000_000) &&
            (p.SessionSummary is null || p.SessionSummary.IsValid) && (p.ActivitySummary is null || p.ActivitySummary.IsValid) &&
            (p.RelatedRun is null || p.Kind == "exception" && SupplementarySchema.Uuid4(p.RelatedRun.SessionId) &&
                SupplementarySchema.NumericVersion(p.RelatedRun.AppVersion, app: true) && SupplementarySchema.BuildIdentity(p.RelatedRun.BuildId) && SupplementarySchema.Channels.Contains(p.RelatedRun.Channel))) &&
        (Detail is null || SchemaVersion == 2 && Detail.IsValid(From, To)) &&
        JsonSerializer.SerializeToUtf8Bytes(this, SupplementarySchema.Json).Length <= MaximumBytes;
}

// Retains the existing background summaries; it never samples game, renderer or device state.
internal sealed class SupplementaryDiagnosticBuffer(TimeProvider? clock = null)
{
    private const int MaximumObservations = 2048;
    private readonly SupplementaryDiagnosticDetailBuffer _detail = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly PriorityQueue<SupplementaryDiagnosticObservation, (long At, long Sequence)> _observations = new(MaximumObservations + 1);
    private readonly SortedDictionary<long, int> _omitted = new();
    private SupplementaryEnvironment? _environment;
    private long _sequence;
    internal int Count { get { lock (_gate) return _observations.Count; } }
    internal void Clear()
    {
        lock (_gate) { _detail.Clear(); _observations.Clear(); _omitted.Clear(); _environment = null; _sequence = 0; }
    }

    internal void ObserveContext(DebugLogging.HealthContextSnapshot context, DebugLogging.ComponentDiagnosticReport[] components,
        SupplementaryEnvironment? environment = null, SupplementaryDiagnosticSettings? settings = null)
    {
        lock (_gate) _detail.Observe(_clock.GetUtcNow(), context, components, environment, settings);
    }

    internal void Observe(SupplementaryEvent value)
    {
        if (value.RelatedRun is not null && value.Kind != "exception" || value.ObservedAt.Offset != TimeSpan.Zero) return;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var cutoff = WindowStart(now);
            Trim(cutoff);
            // Future observations cannot enter a current attachment, and late summaries
            // cannot push newer evidence out of the buffer merely by arriving last.
            if (value.ObservedAt < cutoff || value.ObservedAt > now) return;
            if (value.Environment is { } environment &&
                (_environment is null || environment.CollectedAt >= _environment.CollectedAt)) _environment = environment;
            var point = new SupplementaryDiagnosticObservation(value.ObservedAt, value.Kind, value.Feature, value.Outcome, value.Stage,
                value.Value, value.DurationMs, value.Measurements, value.Incident?.Reason, value.Incident, value.GameVerification,
                value.UpdateStatus, value.Platform, value.GameBuild, value.RenderMode, value.ForegroundState, value.SessionAgeMs,
                value.SessionSummary, value.ActivitySummary, value.RelatedRun);
            _observations.Enqueue(point, (point.At.UtcTicks, _sequence++));
            if (_observations.Count > MaximumObservations)
            {
                var removed = _observations.Dequeue();
                var second = removed.At.UtcTicks / TimeSpan.TicksPerSecond;
                _omitted.TryGetValue(second, out var count);
                _omitted[second] = (int)Math.Min(int.MaxValue, (long)count + 1);
            }
        }
    }

    internal SupplementaryDiagnosticHistory Snapshot(DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        var cutoff = WindowStart(now);
        (SupplementaryDiagnosticObservation Element, (long At, long Sequence) Priority)[] points;
        SupplementaryEnvironment? environment;
        int omitted;
        lock (_gate)
        {
            Trim(cutoff);
            points = _observations.UnorderedItems.Where(p => p.Element.At <= now).ToArray();
            environment = _environment;
            omitted = (int)Math.Min(int.MaxValue, _omitted.Where(p => p.Key <= now.UtcTicks / TimeSpan.TicksPerSecond).Sum(p => (long)p.Value));
        }
        // Environment validation, ordering and payload sizing are attachment work only.
        // None holds the background collector lock or runs on telemetry/render callbacks.
        var history = new SupplementaryDiagnosticHistory(2, cutoff, now, omitted,
            environment?.IsValid(now) == true ? environment : null,
            points.OrderBy(p => p.Priority).Select(p => p.Element).ToImmutableArray(), _detail.Snapshot(cutoff, now));
        if (PayloadBytes(history) <= SupplementaryDiagnosticHistory.MaximumBytes) return history;

        // Find the largest suffix that fits, with a bounded binary search.
        var low = 0;
        var high = history.Observations.Length;
        while (low < high)
        {
            var remove = low + (high - low) / 2;
            if (PayloadBytes(WithoutOldest(history, remove)) <= SupplementaryDiagnosticHistory.MaximumBytes) high = remove;
            else low = remove + 1;
        }
        return WithoutOldest(history, low);
    }

    private static SupplementaryDiagnosticHistory WithoutOldest(SupplementaryDiagnosticHistory history, int count) => history with
    {
        Observations = history.Observations.RemoveRange(0, count),
        Omitted = (int)Math.Min(int.MaxValue, (long)history.Omitted + count)
    };

    private static int PayloadBytes(SupplementaryDiagnosticHistory history) =>
        JsonSerializer.SerializeToUtf8Bytes(history, SupplementarySchema.Json).Length;

    // Whole-second buckets bound omission metadata to 1,201 counters. Rounding the
    // window's start up keeps every reported observation within the last 20 minutes.
    private static DateTimeOffset WindowStart(DateTimeOffset now)
    {
        var ticks = Math.Max(0, now.UtcTicks - TimeSpan.FromMinutes(20).Ticks);
        return new DateTimeOffset(((ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond) * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    private void Trim(DateTimeOffset cutoff)
    {
        while (_observations.TryPeek(out var point, out _) && point.At < cutoff) _observations.Dequeue();
        var second = cutoff.UtcTicks / TimeSpan.TicksPerSecond;
        while (_omitted.Count > 0 && _omitted.First().Key < second) _omitted.Remove(_omitted.First().Key);
    }
}
