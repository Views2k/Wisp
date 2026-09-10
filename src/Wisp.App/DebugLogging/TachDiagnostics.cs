using System.Diagnostics;
using Wisp.Core;
using Wisp.Telemetry;

namespace Wisp.App.DebugLogging;

internal readonly record struct TachNeedleDiagnostic
{
    public int ControlId { get; init; }
    public string ControlKind { get; init; }
    public string HostKind { get; init; }
    public long HostWindowHandle { get; init; }
    public string Route { get; init; }
    public string Source { get; init; }
    public bool IsLoaded { get; init; }
    public bool IsVisible { get; init; }
    public bool IsLive { get; init; }
    public bool NeedleVisible { get; init; }
    public int CarOrdinal { get; init; }
    public uint GameTimestampMilliseconds { get; init; }
    public long? ReceivedTimestamp { get; init; }
    public long NativeObservedTimestamp { get; init; }
    public long AppliedTimestamp { get; init; }
    public double RawRpm { get; init; }
    public double? AppliedRpm { get; init; }
    public double? AppliedFraction { get; init; }
    public double? Angle { get; init; }
    public double? Blur { get; init; }
    public double PlaybackDelayMilliseconds { get; init; }
    public double PlaybackTargetDelayMilliseconds { get; init; }
    public bool PlaybackAtNewest { get; init; }
    public int BufferedSamples { get; init; }
    public long ReseedCount { get; init; }
    public long StarvationReseedCount { get; init; }
}

internal readonly record struct TachInputDiagnostic(
    string Route, long Timestamp, long ReceivedTimestamp, long Sequence,
    uint GameTimestampMilliseconds, double Rpm, double MaximumRpm, int CarOrdinal,
    bool RaceOn, string ParseError);

internal readonly record struct TachNativeDiagnostic(
    long Timestamp, ulong Generation, int CarOrdinal, string Route,
    NativeGaugeReadDiagnostics Read, double? Angle, double? Blur);

internal readonly record struct TachLifecycleDiagnostic(
    long Timestamp, int ControlId, string ControlKind, string HostKind, long HostWindowHandle,
    bool IsLoaded, bool IsVisible, bool IsLive);

internal readonly record struct TachRendererDiagnostic
{
    public int ControlId { get; init; }
    public uint? NativeThreadId { get; init; }
    public long HostWindowHandle { get; init; }
    public long Sequence { get; init; }
    public string Stage { get; init; }
    public string Result { get; init; }
    public long StartedTimestamp { get; init; }
    public long CompletedTimestamp { get; init; }
    public long? SampleTimestamp { get; init; }
    public long? ReceivedTimestamp { get; init; }
    public long? QueuedTimestamp { get; init; }
    public int DrawCommands { get; init; }
    public int MapCount { get; init; }
    public long MapTicks { get; init; }
    public long MaxMapTicks { get; init; }
    public long SceneTicks { get; init; }
    public long NativeSetupTicks { get; init; }
    public long NativeDrawTicks { get; init; }
    public long NativePresentTicks { get; init; }
    public int HResult { get; init; }
    public int QueueDropped { get; init; }
    public long? CpuThreadTicks { get; init; }
    public long? NativeWaitTicks { get; init; }
    public long? WaitPrecheckTicks { get; init; }
    public long? WaitCallTicks { get; init; }
    public long? WaitPostcheckTicks { get; init; }
    public uint? SwapChainGeneration { get; init; }
    public uint? WaitReturnCode { get; init; }
}

internal sealed record TachRendererCounts(
    int ControlId, long HostWindowHandle, string Stage, string Result, long Count,
    double MeanMilliseconds, double MaximumMilliseconds, long DrawCommands, long MapCount,
    double TotalMapMilliseconds, double MaximumMapMilliseconds, double TotalSceneMilliseconds,
    double TotalNativeSetupMilliseconds, double TotalNativeDrawMilliseconds, double TotalNativePresentMilliseconds,
    long QueueDropped, long QueueAgeSamples, double MeanQueueAgeMilliseconds, double MaximumQueueAgeMilliseconds,
    long ReceiveAgeSamples, double MeanReceiveAgeMilliseconds, double MaximumReceiveAgeMilliseconds,
    long SampleAgeSamples, double MeanSampleAgeMilliseconds, double MaximumSampleAgeMilliseconds,
    long CpuThreadSamples, double TotalCpuThreadMilliseconds)
{
    public int HResult { get; init; }
    public uint? NativeThreadId { get; init; }
    public long NativeWaitSamples { get; init; }
    public double? TotalNativeWaitMilliseconds { get; init; }
    public long WaitPrecheckSamples { get; init; }
    public double? TotalWaitPrecheckMilliseconds { get; init; }
    public long WaitCallSamples { get; init; }
    public double? TotalWaitCallMilliseconds { get; init; }
    public double? MaximumWaitCallMilliseconds { get; init; }
    public long WaitPostcheckSamples { get; init; }
    public double? TotalWaitPostcheckMilliseconds { get; init; }
    public long SwapChainGenerationSamples { get; init; }
    public uint? MinimumSwapChainGeneration { get; init; }
    public uint? MaximumSwapChainGeneration { get; init; }
    public uint? WaitReturnCode { get; init; }
}

internal sealed record TachNativeCounts(string Stage, string Failure, string Cache, string Route,
    long Count, double MeanReadMilliseconds, double MaximumReadMilliseconds)
{
    public long Changed { get; init; }
}

internal sealed record TachNeedleCounts(int ControlId, string ControlKind, string HostKind,
    long HostWindowHandle, long Applied, long Changed, long Native, long Fallback, long Unavailable,
    long AtNewest, long SourceSwitches, double MaximumApplyGapMilliseconds,
    double MaximumChangedGapMilliseconds, double MaximumInputAgeMilliseconds,
    double MaximumNativeAgeMilliseconds, long Reseeds, long StarvationReseeds);

internal sealed record TachIntervalDiagnostic(
    DateTimeOffset TimestampUtc, string CaptureId, long Timestamp, double IntervalMilliseconds,
    long InputReceived, long InputDrained, long InputAccepted, long InputRejected, long UiSelected,
    long RpmChanges, double MaximumAcceptedGapMilliseconds, long FractionalWaits,
    long DeadlineAlreadyDue, long WaitWakeups, double MaximumWaitMilliseconds,
    long ContentionOmissions, long NativeDetailSampledOut,
    TachNativeCounts[] NativeReads, TachNeedleCounts[] Needles)
{
    public TachRendererCounts[] Renderer { get; init; } = [];
    public long RendererContentionOmissions { get; init; }
    public long RendererAggregateOmissions { get; init; }
    public long RendererInvalidOmissions { get; init; }
}

internal sealed record TachCaptureExport(
    string CaptureId, DateTimeOffset StartedAtUtc, long StartedTimestamp, long ExportedTimestamp,
    long StopwatchFrequency, long ContentionOmissions, long NativeDetailSampledOut,
    TachInputDiagnostic[] InputStartup, TachInputDiagnostic[] InputRecent, long InputOverwritten,
    TachNativeDiagnostic[] NativeStartup, TachNativeDiagnostic[] NativeRecent, long NativeOverwritten,
    TachNeedleDiagnostic[] NeedleStartup, TachNeedleDiagnostic[] NeedleRecent, long NeedleOverwritten,
    TachLifecycleDiagnostic[] Lifecycle, long LifecycleOverwritten)
{
    public TachNativeContext[] NativeContexts { get; init; } = [];
    public TachRendererDiagnostic[] RendererStartup { get; init; } = [];
    public TachRendererDiagnostic[] RendererRecent { get; init; } = [];
    public long RendererOverwritten { get; init; }
    public long RendererContentionOmissions { get; init; }
    public long RendererAggregateOmissions { get; init; }
    public long RendererInvalidOmissions { get; init; }
}

internal sealed record TachNativeContext(long Timestamp, string PackId, int Revision, string GameVersion,
    int ReaderVersion, int SchemaVersion, bool StoreBuild, bool HasNativeGaugeLayout);

/// <summary>
/// Optional process-local evidence. Producers never wait for a lock or perform file I/O.
/// The worker persists one-second counts; export snapshots bounded startup/recent histories.
/// </summary>
internal static class TachDiagnostics
{
    internal const int RendererStartupCapacity = 18_000;
    internal const int RendererRecentCapacity = 45_000;
    internal const int RendererAggregateCapacity = 256;
    private static readonly object LifecycleGate = new();
    private static Capture? _capture;
    private static int _enabled;
    private static int _controlId;
    private static readonly string[] ParseErrors = Enum.GetNames<PacketParseError>();

    internal static bool IsEnabled => Volatile.Read(ref _enabled) != 0;
    internal static int NextControlId() => Interlocked.Increment(ref _controlId);

    internal static void SetEnabled(bool enabled)
    {
        lock (LifecycleGate)
        {
            if (enabled && (_capture is null || !IsEnabled))
                Volatile.Write(ref _capture, new Capture());
            Volatile.Write(ref _enabled, enabled ? 1 : 0);
        }
    }

    internal static void Clear()
    {
        lock (LifecycleGate)
            Volatile.Write(ref _capture, IsEnabled ? new Capture() : null);
    }

    private static Capture? Enter()
    {
        if (!IsEnabled || Volatile.Read(ref _capture) is not { } capture)
            return null;
        if (Monitor.TryEnter(capture.Gate))
            return capture;
        Interlocked.Increment(ref capture.ContentionOmissions);
        return null;
    }

    internal static void RecordTelemetry(TelemetryPacketDiagnostic sample)
    {
        var capture = Enter();
        if (capture is null) return;
        try
        {
            var route = sample.Kind switch
            {
                TelemetryPacketDiagnosticKind.Received => "Received",
                TelemetryPacketDiagnosticKind.Drained => "Drained",
                TelemetryPacketDiagnosticKind.Accepted => "Accepted",
                _ => "Rejected"
            };
            switch (sample.Kind)
            {
                case TelemetryPacketDiagnosticKind.Received: capture.InputReceived++; break;
                case TelemetryPacketDiagnosticKind.Drained: capture.InputReceived++; capture.InputDrained++; break;
                case TelemetryPacketDiagnosticKind.Accepted:
                    capture.InputAccepted++;
                    if (capture.LastAccepted > 0)
                        capture.MaximumAcceptedGap = Math.Max(capture.MaximumAcceptedGap, sample.Timestamp - capture.LastAccepted);
                    capture.LastAccepted = sample.Timestamp;
                    if (sample.Rpm != capture.LastRpm) capture.RpmChanges++;
                    capture.LastRpm = sample.Rpm;
                    break;
                case TelemetryPacketDiagnosticKind.Rejected: capture.InputRejected++; break;
            }
            capture.Input.Add(new TachInputDiagnostic(route, sample.Timestamp, sample.ReceivedTimestamp,
                sample.Sequence, sample.GameTimestampMilliseconds, Finite(sample.Rpm), Finite(sample.MaximumRpm),
                sample.CarOrdinal, sample.RaceOn, ParseErrors[(int)sample.ParseError]), sample.Timestamp);
        }
        finally { Monitor.Exit(capture.Gate); }
    }

    internal static void RecordUiInput(VehicleState sample)
    {
        var capture = Enter();
        if (capture is null) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            capture.UiSelected++;
            capture.Input.Add(new TachInputDiagnostic("UiSelected", now, sample.ReceivedTimestamp ?? 0,
                0, sample.GameTimestampMilliseconds, Finite(sample.EngineRpm), Finite(sample.EngineMaximumRpm),
                sample.CarOrdinal, sample.IsRaceOn, "None"), now);
        }
        finally { Monitor.Exit(capture.Gate); }
    }

    internal static void RecordNative(in NativeGaugeReadDiagnostics read, ulong generation, int carOrdinal, string route)
    {
        var capture = Enter();
        if (capture is null) return;
        try
        {
            if (!read.Enabled) return;
            var now = Stopwatch.GetTimestamp();
            var key = (read.Stage, read.Failure, read.CacheOutcome, route);
            if (!capture.NativeCounts.TryGetValue(key, out var count))
                capture.NativeCounts[key] = count = new NativeCounter();
            count.Count++;
            if (read.HasValidatedSample && read.HasNeedlePair)
            {
                if (read.Angle != capture.LastNativeAngle) count.Changed++;
                capture.LastNativeAngle = read.Angle;
            }
            count.Ticks += read.ElapsedStopwatchTicks;
            count.MaximumTicks = Math.Max(count.MaximumTicks, read.ElapsedStopwatchTicks);
            // Count every attempt, but retain detailed worker reads at up to 60 Hz,
            // plus changes of rejection/source state. This cannot flood on a busy retry loop.
            if (key != capture.LastNativeKey || now - capture.LastNativeDetail >= Stopwatch.Frequency / 60)
            {
                capture.Native.Add(new TachNativeDiagnostic(now, generation, carOrdinal, route,
                    read with { Angle = 0, Blur = 0 },
                    read.HasValidatedSample ? NullableFinite(read.Angle) : null,
                    read.HasValidatedSample ? NullableFinite(read.Blur) : null), now);
                capture.LastNativeDetail = now;
                capture.LastNativeKey = key;
            }
            else capture.NativeDetailSampledOut++;
        }
        finally { Monitor.Exit(capture.Gate); }
    }

    internal static void RecordNativeContext(NativeHudCompatibilityPack pack)
    {
        var capture = Enter();
        if (capture is null) return;
        try
        {
            if (ReferenceEquals(capture.LastPack, pack)) return;
            capture.LastPack = pack;
            if (capture.NativeContexts.Count == 32) capture.NativeContexts.RemoveAt(0);
            capture.NativeContexts.Add(new TachNativeContext(Stopwatch.GetTimestamp(), pack.Id, pack.Revision,
                pack.GameVersion, pack.ReaderVersion, pack.SchemaVersion, pack.StoreIdentity is not null,
                pack.NativeGauge is not null));
        }
        finally { Monitor.Exit(capture.Gate); }
    }

    internal static void RecordWorkerWait(long remainingTicks, long elapsedTicks, bool signaled)
    {
        var capture = Enter();
        if (capture is null) return;
        try
        {
            if (remainingTicks > 0 && remainingTicks < Stopwatch.Frequency / 1000d) capture.FractionalWaits++;
            if (remainingTicks <= 0) capture.DeadlineAlreadyDue++;
            if (signaled) capture.WaitWakeups++;
            capture.MaximumWait = Math.Max(capture.MaximumWait, elapsedTicks);
        }
        finally { Monitor.Exit(capture.Gate); }
    }

    internal static void RecordNeedle(in TachNeedleDiagnostic sample)
    {
        var capture = Enter();
        if (capture is null) return;
        try
        {
            var clean = sample with
            {
                RawRpm = Finite(sample.RawRpm),
                AppliedRpm = NullableFinite(sample.AppliedRpm),
                AppliedFraction = NullableFinite(sample.AppliedFraction),
                Angle = NullableFinite(sample.Angle),
                Blur = NullableFinite(sample.Blur),
                PlaybackDelayMilliseconds = Finite(sample.PlaybackDelayMilliseconds),
                PlaybackTargetDelayMilliseconds = Finite(sample.PlaybackTargetDelayMilliseconds)
            };
            if (!capture.NeedleCounts.TryGetValue(sample.ControlId, out var counter))
            {
                if (capture.NeedleCounts.Count >= 128) { capture.ContentionOmissions++; return; }
                capture.NeedleCounts[sample.ControlId] = counter = new NeedleCounter();
            }
            counter.Observe(clean);
            if (sample.CarOrdinal > 0 && sample.IsLive)
                capture.Needle.Add(clean, sample.AppliedTimestamp);
        }
        finally { Monitor.Exit(capture.Gate); }
    }

    internal static void RecordNeedleLifecycle(int controlId, string controlKind, string hostKind,
        long hostWindowHandle, bool isLoaded, bool isVisible, bool isLive)
    {
        var capture = Enter();
        if (capture is null) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            capture.Lifecycle.Add(new TachLifecycleDiagnostic(now, controlId, controlKind, hostKind,
                hostWindowHandle, isLoaded, isVisible, isLive), now);
        }
        finally { Monitor.Exit(capture.Gate); }
    }

    internal static void RecordRenderer(in TachRendererDiagnostic sample)
    {
        if (!IsEnabled || Volatile.Read(ref _capture) is not { } capture) return;
        if (!Monitor.TryEnter(capture.Gate))
        {
            Interlocked.Increment(ref capture.ContentionOmissions);
            Interlocked.Increment(ref capture.RendererContentionOmissions);
            return;
        }
        try
        {
            if (sample.StartedTimestamp <= 0 || sample.CompletedTimestamp < sample.StartedTimestamp)
            {
                capture.RendererInvalidOmissions++;
                return;
            }
            var clean = sample with
            {
                Stage = RendererStage(sample.Stage),
                Result = RendererResult(sample.Result),
                DrawCommands = Math.Max(0, sample.DrawCommands),
                MapCount = Math.Max(0, sample.MapCount),
                MapTicks = Math.Max(0, sample.MapTicks),
                MaxMapTicks = Math.Max(0, sample.MaxMapTicks),
                SceneTicks = Math.Max(0, sample.SceneTicks),
                NativeSetupTicks = Math.Max(0, sample.NativeSetupTicks),
                NativeDrawTicks = Math.Max(0, sample.NativeDrawTicks),
                NativePresentTicks = Math.Max(0, sample.NativePresentTicks),
                QueueDropped = Math.Max(0, sample.QueueDropped),
                CpuThreadTicks = sample.CpuThreadTicks is >= 0 ? sample.CpuThreadTicks : null,
                NativeThreadId = sample.NativeThreadId is > 0 ? sample.NativeThreadId : null,
                NativeWaitTicks = sample.NativeWaitTicks is >= 0 ? sample.NativeWaitTicks : null,
                WaitPrecheckTicks = sample.WaitPrecheckTicks is >= 0 ? sample.WaitPrecheckTicks : null,
                WaitCallTicks = sample.WaitCallTicks is >= 0 ? sample.WaitCallTicks : null,
                WaitPostcheckTicks = sample.WaitPostcheckTicks is >= 0 ? sample.WaitPostcheckTicks : null,
                SwapChainGeneration = sample.SwapChainGeneration is > 0 ? sample.SwapChainGeneration : null
            };
            capture.Renderer.Add(clean, clean.CompletedTimestamp);
            var index = 0;
            for (; index < capture.RendererCount; index++)
                if (capture.RendererCounts[index].Matches(in clean)) break;
            if (index == capture.RendererCount)
            {
                if (index == capture.RendererCounts.Length)
                {
                    capture.RendererAggregateOmissions++;
                    return;
                }
                capture.RendererCount++;
            }
            capture.RendererCounts[index].Observe(in clean);
        }
        finally { Monitor.Exit(capture.Gate); }
    }

    internal static string RendererStage(string? value) => value is "frame_wait" or "draw" or "present" or "retry_wait" or "state" ? value : "unknown";
    internal static string RendererResult(string? value) => value is "ready" or "timeout" or "cancelled" or "submitted" or "busy" or "occluded" or "error" or "discarded" or "created" or "resized" or "resumed" ? value : "unknown";

    internal static TachIntervalDiagnostic? CollectInterval(DateTimeOffset utcNow)
    {
        if (!IsEnabled || Volatile.Read(ref _capture) is not { } capture) return null;
        lock (capture.Gate)
        {
            var now = Stopwatch.GetTimestamp();
            var result = new TachIntervalDiagnostic(utcNow, capture.Id, now, Milliseconds(now - capture.LastInterval),
                capture.InputReceived, capture.InputDrained, capture.InputAccepted, capture.InputRejected,
                capture.UiSelected, capture.RpmChanges, Milliseconds(capture.MaximumAcceptedGap),
                capture.FractionalWaits, capture.DeadlineAlreadyDue, capture.WaitWakeups, Milliseconds(capture.MaximumWait),
                Interlocked.Read(ref capture.ContentionOmissions), capture.NativeDetailSampledOut,
                capture.NativeCounts.Select(pair => new TachNativeCounts(pair.Key.Stage.ToString(), pair.Key.Failure.ToString(),
                    pair.Key.Cache.ToString(), pair.Key.Route, pair.Value.Count,
                    Milliseconds(pair.Value.Ticks) / Math.Max(1, pair.Value.Count), Milliseconds(pair.Value.MaximumTicks))
                { Changed = pair.Value.Changed }).ToArray(),
                capture.NeedleCounts.Select(pair => pair.Value.Snapshot(pair.Key)).ToArray())
            {
                Renderer = capture.RendererCounts.Take(capture.RendererCount).Select(count => count.Snapshot()).ToArray(),
                RendererContentionOmissions = Interlocked.Read(ref capture.RendererContentionOmissions),
                RendererAggregateOmissions = capture.RendererAggregateOmissions,
                RendererInvalidOmissions = capture.RendererInvalidOmissions
            };
            capture.LastInterval = now;
            capture.InputReceived = capture.InputDrained = capture.InputAccepted = capture.InputRejected = capture.UiSelected = 0;
            capture.RpmChanges = capture.MaximumAcceptedGap = capture.FractionalWaits = capture.DeadlineAlreadyDue = capture.WaitWakeups = capture.MaximumWait = 0;
            capture.NativeCounts.Clear();
            foreach (var count in capture.NeedleCounts.Values) count.ResetInterval();
            Array.Clear(capture.RendererCounts, 0, capture.RendererCount);
            capture.RendererCount = 0;
            return result;
        }
    }

    internal static TachCaptureExport? Snapshot()
    {
        if (Volatile.Read(ref _capture) is not { } capture) return null;
        lock (capture.Gate)
        {
            return new TachCaptureExport(capture.Id, capture.StartedUtc, capture.Started, Stopwatch.GetTimestamp(),
                Stopwatch.Frequency, Interlocked.Read(ref capture.ContentionOmissions), capture.NativeDetailSampledOut,
                capture.Input.Startup(), capture.Input.Recent(), capture.Input.Overwritten,
                capture.Native.Startup(), capture.Native.Recent(), capture.Native.Overwritten,
                capture.Needle.Startup(), capture.Needle.Recent(), capture.Needle.Overwritten,
                capture.Lifecycle.Recent(), capture.Lifecycle.Overwritten)
            {
                NativeContexts = capture.NativeContexts.ToArray(),
                RendererStartup = capture.Renderer.Startup(),
                RendererRecent = capture.Renderer.Recent(),
                RendererOverwritten = capture.Renderer.Overwritten,
                RendererContentionOmissions = Interlocked.Read(ref capture.RendererContentionOmissions),
                RendererAggregateOmissions = capture.RendererAggregateOmissions,
                RendererInvalidOmissions = capture.RendererInvalidOmissions
            };
        }
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
    private static double? NullableFinite(double? value) => value is { } v && double.IsFinite(v) ? v : null;
    private static double Milliseconds(long ticks) => Math.Max(0, ticks) * 1000d / Stopwatch.Frequency;

    private sealed class Capture
    {
        internal readonly object Gate = new();
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly DateTimeOffset StartedUtc = DateTimeOffset.UtcNow;
        internal readonly long Started = Stopwatch.GetTimestamp();
        internal readonly History<TachInputDiagnostic> Input = new(45_000, 18_000);
        internal readonly History<TachNativeDiagnostic> Native = new(18_000, 6_000);
        internal readonly History<TachNeedleDiagnostic> Needle = new(45_000, 18_000);
        internal readonly History<TachLifecycleDiagnostic> Lifecycle = new(1024, 0);
        internal readonly History<TachRendererDiagnostic> Renderer = new(RendererRecentCapacity, RendererStartupCapacity);
        internal readonly RendererCounter[] RendererCounts = new RendererCounter[RendererAggregateCapacity];
        internal int RendererCount;
        internal long RendererContentionOmissions, RendererAggregateOmissions, RendererInvalidOmissions;
        internal readonly Dictionary<(NativeGaugeReadStage Stage, NativeGaugeReadFailure Failure, NativeGaugeCacheOutcome Cache, string Route), NativeCounter> NativeCounts = new();
        internal readonly Dictionary<int, NeedleCounter> NeedleCounts = new();
        internal (NativeGaugeReadStage Stage, NativeGaugeReadFailure Failure, NativeGaugeCacheOutcome Cache, string Route) LastNativeKey;
        internal NativeHudCompatibilityPack? LastPack;
        internal readonly List<TachNativeContext> NativeContexts = new(32);
        internal double LastNativeAngle = double.NaN;
        internal long LastInterval = Stopwatch.GetTimestamp();
        internal long ContentionOmissions, NativeDetailSampledOut, LastNativeDetail, LastAccepted;
        internal long InputReceived, InputDrained, InputAccepted, InputRejected, UiSelected, RpmChanges, MaximumAcceptedGap;
        internal long FractionalWaits, DeadlineAlreadyDue, WaitWakeups, MaximumWait;
        internal double LastRpm = double.NaN;
    }

    private sealed class NativeCounter
    {
        internal long Count, Changed, Ticks, MaximumTicks;
    }

    private struct RendererCounter
    {
        private int _controlId, _hResult;
        private long _window;
        private string _stage, _result;
        private long _count, _maximum, _draws, _maps, _maxMap, _dropped;
        private double _ticks, _mapTicks, _sceneTicks, _setupTicks, _drawTicks, _presentTicks, _cpuTicks;
        private long _queueSamples, _receiveSamples, _sampleSamples, _cpuSamples;
        private double _queueAge, _receiveAge, _sampleAge;
        private long _maxQueueAge, _maxReceiveAge, _maxSampleAge;
        private long _nativeWaitSamples, _precheckSamples, _waitCallSamples, _postcheckSamples, _generationSamples;
        private double _nativeWaitTicks, _precheckTicks, _waitCallTicks, _postcheckTicks;
        private long _maxWaitCallTicks;
        private uint? _minimumGeneration, _maximumGeneration, _waitReturnCode, _nativeThreadId;

        internal readonly bool Matches(in TachRendererDiagnostic value) =>
            _controlId == value.ControlId && _window == value.HostWindowHandle && _stage == value.Stage &&
            _result == value.Result && _hResult == value.HResult && _waitReturnCode == value.WaitReturnCode &&
            _nativeThreadId == value.NativeThreadId;

        internal void Observe(in TachRendererDiagnostic value)
        {
            _controlId = value.ControlId; _window = value.HostWindowHandle; _stage = value.Stage; _result = value.Result;
            _hResult = value.HResult;
            _waitReturnCode = value.WaitReturnCode;
            _nativeThreadId = value.NativeThreadId;
            _count++;
            var elapsed = value.CompletedTimestamp - value.StartedTimestamp;
            _ticks += elapsed; _maximum = Math.Max(_maximum, elapsed);
            _draws += value.DrawCommands; _maps += value.MapCount; _mapTicks += value.MapTicks;
            _maxMap = Math.Max(_maxMap, value.MaxMapTicks); _sceneTicks += value.SceneTicks;
            _setupTicks += value.NativeSetupTicks; _drawTicks += value.NativeDrawTicks; _presentTicks += value.NativePresentTicks;
            _dropped += value.QueueDropped;
            Age(value.StartedTimestamp, value.QueuedTimestamp, ref _queueSamples, ref _queueAge, ref _maxQueueAge);
            Age(value.StartedTimestamp, value.ReceivedTimestamp, ref _receiveSamples, ref _receiveAge, ref _maxReceiveAge);
            Age(value.StartedTimestamp, value.SampleTimestamp, ref _sampleSamples, ref _sampleAge, ref _maxSampleAge);
            if (value.CpuThreadTicks is { } cpu) { _cpuSamples++; _cpuTicks += cpu; }
            if (value.NativeWaitTicks is { } nativeWait) { _nativeWaitSamples++; _nativeWaitTicks += nativeWait; }
            if (value.WaitPrecheckTicks is { } precheck) { _precheckSamples++; _precheckTicks += precheck; }
            if (value.WaitCallTicks is { } waitCall)
            {
                _waitCallSamples++; _waitCallTicks += waitCall;
                _maxWaitCallTicks = Math.Max(_maxWaitCallTicks, waitCall);
            }
            if (value.WaitPostcheckTicks is { } postcheck) { _postcheckSamples++; _postcheckTicks += postcheck; }
            if (value.SwapChainGeneration is { } generation)
            {
                _generationSamples++;
                _minimumGeneration = _minimumGeneration is { } minimum ? Math.Min(minimum, generation) : generation;
                _maximumGeneration = _maximumGeneration is { } maximum ? Math.Max(maximum, generation) : generation;
            }
        }

        private static void Age(long now, long? origin, ref long samples, ref double sum, ref long maximum)
        {
            if (origin is not > 0 || origin.Value > now) return;
            var age = now - origin.Value;
            samples++; sum += age; maximum = Math.Max(maximum, age);
        }

        internal readonly TachRendererCounts Snapshot() => new(_controlId, _window, _stage, _result, _count,
            Ms(_ticks) / Math.Max(1, _count), Milliseconds(_maximum), _draws, _maps, Ms(_mapTicks), Milliseconds(_maxMap),
            Ms(_sceneTicks), Ms(_setupTicks), Ms(_drawTicks), Ms(_presentTicks), _dropped,
            _queueSamples, Ms(_queueAge) / Math.Max(1, _queueSamples), Milliseconds(_maxQueueAge),
            _receiveSamples, Ms(_receiveAge) / Math.Max(1, _receiveSamples), Milliseconds(_maxReceiveAge),
            _sampleSamples, Ms(_sampleAge) / Math.Max(1, _sampleSamples), Milliseconds(_maxSampleAge), _cpuSamples, Ms(_cpuTicks))
        {
            HResult = _hResult,
            NativeThreadId = _nativeThreadId,
            NativeWaitSamples = _nativeWaitSamples,
            TotalNativeWaitMilliseconds = _nativeWaitSamples > 0 ? Ms(_nativeWaitTicks) : null,
            WaitPrecheckSamples = _precheckSamples,
            TotalWaitPrecheckMilliseconds = _precheckSamples > 0 ? Ms(_precheckTicks) : null,
            WaitCallSamples = _waitCallSamples,
            TotalWaitCallMilliseconds = _waitCallSamples > 0 ? Ms(_waitCallTicks) : null,
            MaximumWaitCallMilliseconds = _waitCallSamples > 0 ? Milliseconds(_maxWaitCallTicks) : null,
            WaitPostcheckSamples = _postcheckSamples,
            TotalWaitPostcheckMilliseconds = _postcheckSamples > 0 ? Ms(_postcheckTicks) : null,
            SwapChainGenerationSamples = _generationSamples,
            MinimumSwapChainGeneration = _minimumGeneration,
            MaximumSwapChainGeneration = _maximumGeneration,
            WaitReturnCode = _waitReturnCode
        };

        private static double Ms(double ticks) => ticks * 1000d / Stopwatch.Frequency;
    }

    private sealed class NeedleCounter
    {
        private TachNeedleDiagnostic _last;
        private long _lastChanged, _applied, _changed, _native, _fallback, _unavailable, _atNewest, _switches;
        private long _maximumGap, _maximumChangedGap, _maximumInputAge, _maximumNativeAge;
        internal void Observe(TachNeedleDiagnostic sample)
        {
            _applied++;
            if (sample.Source == "native") _native++;
            else if (sample.Source is "fallback" or "rpm") _fallback++;
            else _unavailable++;
            if (sample.PlaybackAtNewest) _atNewest++;
            if (_last.AppliedTimestamp > 0)
            {
                _maximumGap = Math.Max(_maximumGap, sample.AppliedTimestamp - _last.AppliedTimestamp);
                if (sample.Source != _last.Source) _switches++;
            }
            var changed = sample.Angle is { } angle
                ? _last.Angle != angle
                : sample.AppliedFraction is { } fraction ? _last.AppliedFraction != fraction : _last.AppliedRpm != sample.AppliedRpm;
            if (changed)
            {
                _changed++;
                if (_lastChanged > 0) _maximumChangedGap = Math.Max(_maximumChangedGap, sample.AppliedTimestamp - _lastChanged);
                _lastChanged = sample.AppliedTimestamp;
            }
            if (sample.ReceivedTimestamp is > 0)
                _maximumInputAge = Math.Max(_maximumInputAge, sample.AppliedTimestamp - sample.ReceivedTimestamp.Value);
            if (sample.NativeObservedTimestamp > 0)
                _maximumNativeAge = Math.Max(_maximumNativeAge, sample.AppliedTimestamp - sample.NativeObservedTimestamp);
            _last = sample;
        }
        internal TachNeedleCounts Snapshot(int id) => new(id, _last.ControlKind, _last.HostKind, _last.HostWindowHandle,
            _applied, _changed, _native, _fallback, _unavailable, _atNewest, _switches, Milliseconds(_maximumGap),
            Milliseconds(_maximumChangedGap), Milliseconds(_maximumInputAge), Milliseconds(_maximumNativeAge),
            _last.ReseedCount, _last.StarvationReseedCount);
        internal void ResetInterval()
        {
            _applied = _changed = _native = _fallback = _unavailable = _atNewest = _switches = 0;
            _maximumGap = _maximumChangedGap = _maximumInputAge = _maximumNativeAge = 0;
        }
    }

    private sealed class History<T>(int capacity, int startupCapacity) where T : struct
    {
        private readonly T[] _recent = new T[capacity];
        private readonly T[] _startup = new T[startupCapacity];
        private int _head, _count, _startupCount;
        private long _firstTimestamp;
        internal long Overwritten { get; private set; }
        internal void Add(T value, long timestamp)
        {
            if (_firstTimestamp == 0) _firstTimestamp = timestamp;
            if (_startupCount < _startup.Length && timestamp - _firstTimestamp <= Stopwatch.Frequency * 60L)
                _startup[_startupCount++] = value;
            if (_count == _recent.Length) Overwritten++;
            else _count++;
            _recent[_head] = value;
            _head = (_head + 1) % _recent.Length;
        }
        internal T[] Startup() => _startup.AsSpan(0, _startupCount).ToArray();
        internal T[] Recent()
        {
            var result = new T[_count];
            var start = (_head - _count + _recent.Length) % _recent.Length;
            var first = Math.Min(_count, _recent.Length - start);
            _recent.AsSpan(start, first).CopyTo(result);
            _recent.AsSpan(0, _count - first).CopyTo(result.AsSpan(first));
            return result;
        }
    }
}
