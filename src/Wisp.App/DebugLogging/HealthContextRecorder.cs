using System.Collections.Immutable;
using System.Diagnostics;
using Wisp.App.Clips;
using Wisp.Core;
using Wisp.App.Supplementary;

namespace Wisp.App.DebugLogging;

internal enum HealthEventCode
{
    ApplicationStarted, ApplicationStopping, RecorderChanged, PlayerChanged,
    ClipSelected, ClipClosed, ClipExportStarted, ClipExportFinished,
    ClipActionFailed, TuneReadFailed, SettingsSaveFailed, RendererFailed, HealthCollectionFailed, LapReviewFailed
}

internal enum HealthPlayerState { Closed, Preparing, ReadyPaused, Playing, Buffering, Ended, Failed }
internal enum HealthPresentResult { Submitted, Busy, Occluded, Failed }

internal sealed record HealthBreadcrumb(DateTimeOffset TimestampUtc, HealthEventCode Code,
    int ErrorCode, ClipRecorderState? RecorderState, HealthPlayerState? PlayerState);

internal sealed record HealthRendererSample(long PresentCalls, long Submissions, long BusyResults,
    long OccludedResults, long Failures, long QueueDropped, int LastErrorCode,
    double RendererSubmissionsPerSecond, double? RendererQueuedToSubmitAgeMs,
    double? MaximumQueuedToSubmitAgeMs);

internal sealed record HealthContextSample
{
    public DateTimeOffset TimestampUtc { get; init; }
    public double CollectionGapMs { get; init; }
    public double IncomingDatagramsPerSecond { get; init; }
    public double ProcessedPacketsPerSecond { get; init; }
    public long AcceptedPackets { get; init; }
    public long RejectedPackets { get; init; }
    public double? PacketAgeMs { get; init; }
    public bool ListenerRunning { get; init; }
    public bool ListenerError { get; init; }
    public bool GameActive { get; init; }
    public bool GameTimestampAdvancing { get; init; }
    public bool OverlayExpectedVisible { get; init; }
    public double? UiHeartbeatAgeMs { get; init; }
    public double? DispatcherDelayMs { get; init; }
    public bool DispatcherProbePending { get; init; }
    public double CompositionCallbacksPerSecond { get; init; }
    public double? CompositionCallbackAgeMs { get; init; }
    public double CompositionMaximumGapMs { get; init; }
    public bool NativeExpected { get; init; }
    public NativeAssistProviderStatus NativeStatus { get; init; }
    public string? NativeGameVersion { get; init; }
    public DiagnosticGamePlatform? NativeGamePlatform { get; init; }
    public int? NativeCompatibilityRevision { get; init; }
    public long NativeReadAttempts { get; init; }
    public long NativeReadFailures { get; init; }
    public double? NativeAgeMs { get; init; }
    public double? CpuPercent { get; init; }
    public long WorkingSetBytes { get; init; }
    public long ManagedHeapBytes { get; init; }
    public int Gen2Collections { get; init; }
    public long CollectorFailures { get; init; }
    public HealthRendererSample Renderer { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, null, null);
}

internal sealed record HealthContextSnapshot(DateTimeOffset CapturedAtUtc,
    ImmutableArray<HealthContextSample> Samples, ImmutableArray<HealthBreadcrumb> Breadcrumbs,
    long DroppedBreadcrumbs = 0);

// Recent context only. No file writes, game reads, identifiers, paths or free-form text.
internal sealed class HealthContextRecorder
{
    internal const int SampleCapacity = 60;
    internal const int BreadcrumbCapacity = 96;
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);
    internal static HealthContextRecorder Current { get; } = new();
    private readonly Func<long> _timestamp;
    private readonly Queue<HealthContextSample> _samples = new(SampleCapacity);
    private readonly BreadcrumbSlot[] _breadcrumbs = Enumerable.Range(0, BreadcrumbCapacity).Select(_ => new BreadcrumbSlot()).ToArray();
    private readonly object _sampleGate = new();
    private HealthContextSnapshot _snapshot = new(DateTimeOffset.UtcNow, [], []);
    private long _lastSample, _previousSubmissions, _breadcrumbSequence, _droppedBreadcrumbs;
    private long _presentCalls, _submissions, _busy, _occluded, _failures, _queueDropped;
    private long _queueAgeTicks, _maximumQueueAgeTicks, _queueAgeSamples;
    private int _lastErrorCode;

    internal HealthContextRecorder(Func<long>? timestamp = null) => _timestamp = timestamp ?? Stopwatch.GetTimestamp;

    // Safe for a fatal handler: a prebuilt immutable reference, no lock, allocation or dispatcher.
    internal HealthContextSnapshot Snapshot() => Volatile.Read(ref _snapshot);

    // Explicit export/failure capture only: at most 96 atomic slots, no UI invocation or locks.
    // Samples keep their original timestamps; recent transitions need not wait for the sampler.
    internal HealthContextSnapshot CaptureForReport()
    {
        var current = Snapshot();
        var report = current with
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Breadcrumbs = ReadBreadcrumbs(),
            DroppedBreadcrumbs = Interlocked.Read(ref _droppedBreadcrumbs)
        };
        return IsValidSnapshot(report) ? report : current;
    }

    // These hooks are allocation-free after initialization. Contended event slots are dropped,
    // never waited on; the drop count accompanies the next snapshot.
    internal void RecordBreadcrumb(HealthEventCode code, int errorCode = 0,
        ClipRecorderState? recorderState = null, HealthPlayerState? playerState = null)
    {
        if ((uint)code > (uint)HealthEventCode.HealthCollectionFailed ||
            recorderState is { } checkedRecorder && (uint)checkedRecorder > (uint)ClipRecorderState.Paused ||
            playerState is { } checkedPlayer && (uint)checkedPlayer > (uint)HealthPlayerState.Failed) return;
        var sequence = Interlocked.Increment(ref _breadcrumbSequence);
        var slot = _breadcrumbs[(int)((sequence - 1) % BreadcrumbCapacity)];
        if (Interlocked.CompareExchange(ref slot.Writing, 1, 0) != 0)
        { Interlocked.Increment(ref _droppedBreadcrumbs); return; }
        try
        {
            if (Volatile.Read(ref slot.Sequence) >= sequence)
            { Interlocked.Increment(ref _droppedBreadcrumbs); return; }
            Volatile.Write(ref slot.Sequence, 0);
            slot.UtcTicks = DateTimeOffset.UtcNow.UtcTicks;
            slot.Code = (int)code; slot.ErrorCode = errorCode;
            slot.RecorderState = recorderState is { } recorder ? (int)recorder : -1;
            slot.PlayerState = playerState is { } player ? (int)player : -1;
            Volatile.Write(ref slot.Sequence, sequence);
        }
        finally { Volatile.Write(ref slot.Writing, 0); }
    }

    internal void RecordPresent(HealthPresentResult result, long queuedAt, long submittedAt)
    {
        Interlocked.Increment(ref _presentCalls);
        if (result == HealthPresentResult.Busy) { Interlocked.Increment(ref _busy); return; }
        if (result == HealthPresentResult.Occluded) { Interlocked.Increment(ref _occluded); return; }
        if (result != HealthPresentResult.Submitted) return;
        Interlocked.Increment(ref _submissions);
        if (queuedAt <= 0 || submittedAt < queuedAt) return;
        var age = submittedAt - queuedAt;
        Interlocked.Exchange(ref _queueAgeTicks, age);
        Interlocked.Increment(ref _queueAgeSamples);
        UpdateMaximum(ref _maximumQueueAgeTicks, age);
    }

    internal void RecordRendererFailure(int hresult)
    {
        Interlocked.Increment(ref _failures);
        Volatile.Write(ref _lastErrorCode, hresult);
        RecordBreadcrumb(HealthEventCode.RendererFailed, hresult);
    }

    internal void RecordQueueDrop(int count)
    { if (count > 0) Interlocked.Add(ref _queueDropped, count); }

    internal void RecordSample(DebugHealthSample sample)
    {
        lock (_sampleGate)
        {
            var now = _timestamp();
            if (_lastSample != 0 && now >= _lastSample && now - _lastSample < Stopwatch.Frequency * 2) return;
            var seconds = _lastSample != 0 && now > _lastSample ? (now - _lastSample) / (double)Stopwatch.Frequency : 0;
            var submissions = Interlocked.Read(ref _submissions);
            var queueSamples = Interlocked.Read(ref _queueAgeSamples);
            var maximumAge = Interlocked.Exchange(ref _maximumQueueAgeTicks, 0);
            var renderer = new HealthRendererSample(
                Interlocked.Read(ref _presentCalls), submissions, Interlocked.Read(ref _busy),
                Interlocked.Read(ref _occluded), Interlocked.Read(ref _failures), Interlocked.Read(ref _queueDropped),
                Volatile.Read(ref _lastErrorCode), seconds > 0 ? Number((submissions - _previousSubmissions) / seconds) : 0,
                queueSamples > 0 ? Number(Interlocked.Read(ref _queueAgeTicks) * 1000d / Stopwatch.Frequency) : null,
                maximumAge > 0 ? Number(maximumAge * 1000d / Stopwatch.Frequency) : null);
            _previousSubmissions = submissions;
            _lastSample = now;
            var safe = new HealthContextSample
            {
                TimestampUtc = sample.TimestampUtc.ToUniversalTime(),
                CollectionGapMs = Number(sample.CollectionGapMilliseconds),
                IncomingDatagramsPerSecond = Number(sample.IncomingHz),
                ProcessedPacketsPerSecond = Number(sample.ProcessedHz),
                AcceptedPackets = Math.Max(0, sample.AcceptedPackets),
                RejectedPackets = Math.Max(0, sample.RejectedPackets),
                PacketAgeMs = Optional(sample.PacketAgeMilliseconds),
                ListenerRunning = sample.ListenerRunning,
                ListenerError = sample.ListenerError,
                GameActive = sample.RaceOn,
                GameTimestampAdvancing = sample.GameTimestampAdvancing,
                OverlayExpectedVisible = sample.OverlayExpectedVisible,
                UiHeartbeatAgeMs = Optional(sample.UiHeartbeatAgeMilliseconds),
                DispatcherDelayMs = Optional(sample.DispatcherDelayMilliseconds),
                DispatcherProbePending = sample.DispatcherProbePending,
                CompositionCallbacksPerSecond = Number(sample.CompositionHz),
                CompositionCallbackAgeMs = Optional(sample.CompositionAgeMilliseconds),
                CompositionMaximumGapMs = Number(sample.CompositionMaximumGapMilliseconds),
                NativeExpected = sample.NativeExpected,
                NativeStatus = Enum.TryParse<NativeAssistProviderStatus>(sample.NativeStatus, out var status) && Enum.IsDefined(status)
                    ? status : NativeAssistProviderStatus.Unavailable,
                NativeGameVersion = SafeGameVersion(sample.NativeGameVersion),
                NativeGamePlatform = sample.NativeGamePlatform is { } platform && Enum.IsDefined(platform) ? platform : null,
                NativeCompatibilityRevision = sample.NativeCompatibilityRevision is > 0 ? sample.NativeCompatibilityRevision : null,
                NativeReadAttempts = Math.Max(0, sample.NativeReadAttempts),
                NativeReadFailures = Math.Max(0, sample.NativeReadFailures),
                NativeAgeMs = Optional(sample.NativeAgeMilliseconds),
                CpuPercent = Optional(sample.WispCpuPercent) is { } cpu ? Math.Min(100, cpu) : null,
                WorkingSetBytes = Math.Max(0, sample.WorkingSetBytes),
                ManagedHeapBytes = Math.Max(0, sample.ManagedHeapBytes),
                Gen2Collections = Math.Max(0, sample.Gen2Collections),
                CollectorFailures = Math.Max(0, sample.CollectorFailures),
                Renderer = renderer
            };
            SupplementarySessionRecorder.Current.RecordSample(safe.ListenerRunning, safe.ListenerError, safe.PacketAgeMs,
                safe.CpuPercent, safe.WorkingSetBytes, safe.ManagedHeapBytes,
                safe.NativeGamePlatform switch { DiagnosticGamePlatform.Steam => "steam", DiagnosticGamePlatform.XboxStore => "store", _ => "unknown" },
                safe.NativeGameVersion);
            if (_samples.Count == SampleCapacity) _samples.Dequeue();
            _samples.Enqueue(safe);
            Volatile.Write(ref _snapshot, new(safe.TimestampUtc, _samples.ToImmutableArray(), ReadBreadcrumbs(),
                Interlocked.Read(ref _droppedBreadcrumbs)));
        }
    }

    internal static bool IsValidSnapshot(HealthContextSnapshot? value) => value is not null &&
        Utc(value.CapturedAtUtc) && !value.Samples.IsDefault && value.Samples.Length <= SampleCapacity &&
        !value.Breadcrumbs.IsDefault && value.Breadcrumbs.Length <= BreadcrumbCapacity && value.DroppedBreadcrumbs >= 0 &&
        value.Samples.All(ValidSample) && value.Breadcrumbs.All(entry => entry is not null && Utc(entry.TimestampUtc) &&
            Enum.IsDefined(entry.Code) && (entry.RecorderState is null || Enum.IsDefined(entry.RecorderState.Value)) &&
            (entry.PlayerState is null || Enum.IsDefined(entry.PlayerState.Value)));

    private static bool ValidSample(HealthContextSample? s) => s is not null && Utc(s.TimestampUtc) &&
        Valid(s.CollectionGapMs) && Valid(s.IncomingDatagramsPerSecond) && Valid(s.ProcessedPacketsPerSecond) &&
        s.AcceptedPackets >= 0 && s.RejectedPackets >= 0 && Valid(s.PacketAgeMs) && Valid(s.UiHeartbeatAgeMs) &&
        Valid(s.DispatcherDelayMs) && Valid(s.CompositionCallbacksPerSecond) && Valid(s.CompositionCallbackAgeMs) &&
        Valid(s.CompositionMaximumGapMs) && Enum.IsDefined(s.NativeStatus) && s.NativeReadAttempts >= 0 && s.NativeReadFailures >= 0 &&
        s.NativeGameVersion == SafeGameVersion(s.NativeGameVersion) &&
        (s.NativeGamePlatform is null || Enum.IsDefined(s.NativeGamePlatform.Value)) &&
        (s.NativeCompatibilityRevision is null or > 0) &&
        Valid(s.NativeAgeMs) && Valid(s.CpuPercent) && (s.CpuPercent is null || s.CpuPercent <= 100) &&
        s.WorkingSetBytes >= 0 && s.ManagedHeapBytes >= 0 && s.Gen2Collections >= 0 && s.CollectorFailures >= 0 &&
        s.Renderer is { } r && r.PresentCalls >= 0 && r.Submissions >= 0 && r.BusyResults >= 0 && r.OccludedResults >= 0 &&
        r.Failures >= 0 && r.QueueDropped >= 0 && Valid(r.RendererSubmissionsPerSecond) &&
        Valid(r.RendererQueuedToSubmitAgeMs) && Valid(r.MaximumQueuedToSubmitAgeMs);

    private ImmutableArray<HealthBreadcrumb> ReadBreadcrumbs()
    {
        var result = new List<(long Sequence, HealthBreadcrumb Value)>(BreadcrumbCapacity);
        foreach (var slot in _breadcrumbs)
        {
            if (Volatile.Read(ref slot.Writing) != 0) continue;
            var sequence = Volatile.Read(ref slot.Sequence);
            if (sequence <= 0) continue;
            var ticks = slot.UtcTicks; var code = slot.Code; var error = slot.ErrorCode;
            var recorder = slot.RecorderState; var player = slot.PlayerState;
            if (Volatile.Read(ref slot.Sequence) != sequence || Volatile.Read(ref slot.Writing) != 0) continue;
            result.Add((sequence, new(new DateTimeOffset(ticks, TimeSpan.Zero), (HealthEventCode)code, error,
                recorder >= 0 ? (ClipRecorderState)recorder : null, player >= 0 ? (HealthPlayerState)player : null)));
        }
        return result.OrderBy(item => item.Sequence).Select(item => item.Value).ToImmutableArray();
    }

    private static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value.Ticks > 0;
    internal static string? SafeGameVersion(string? value) => value is { Length: > 0 and <= 32 } &&
        value.All(character => char.IsAsciiDigit(character) || character == '.') && Version.TryParse(value, out _) ? value : null;
    private static bool Valid(double? value) => value is null || double.IsFinite(value.Value) && value >= 0 && value <= 1e15;
    private static double Number(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1e15) : 0;
    private static double? Optional(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? Math.Min(number, 1e15) : null;
    private static void UpdateMaximum(ref long target, long value)
    {
        var observed = Volatile.Read(ref target);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref target, value, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }

    private sealed class BreadcrumbSlot
    {
        internal int Writing, Code, ErrorCode, RecorderState, PlayerState;
        internal long Sequence, UtcTicks;
    }
}
