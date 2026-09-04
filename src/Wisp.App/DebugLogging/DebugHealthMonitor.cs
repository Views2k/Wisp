using System.Diagnostics;
using System.Runtime.InteropServices;
using Wisp.Core;
using Wisp.Telemetry;

namespace Wisp.App.DebugLogging;

internal sealed record DebugHealthUiContext(
    long HeartbeatTimestamp,
    bool OverlayExpectedVisible,
    bool NativeExpected,
    bool RaceOn,
    int CarOrdinal,
    uint GameTimestampMilliseconds);

internal sealed class DebugHealthMonitor : IAsyncDisposable
{
    private static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(1);
    private static readonly long UiFreshnessTicks = Stopwatch.Frequency * 2;

    private readonly TelemetryUdpReceiver _receiver;
    private readonly NativeHudProcessService _nativeHud;
    private readonly DebugLogService _log;
    private readonly Func<long> _processedPackets;
    private readonly Action<Action> _postDispatcher;
    private readonly Action _loggingExpired;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<long> _timestamp;
    private readonly TimeSpan _sampleInterval;
    private readonly Func<DebugFocus> _focus;
    private readonly object _lifecycleGate = new();
    private DebugHealthUiContext? _uiContext;
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private int _generation;
    private int _probePending;
    private long _probePostedTimestamp;
    private long _dispatcherDelayBits;
    private int _hasDispatcherDelay;
    private long _compositionTimestamp;
    private long _previousCompositionTimestamp;
    private long _compositionFrames;
    private long _compositionMaximumGapBits;
    private long _collectorFailures;
    private long _expiresAtUtcTicks;
    private int _expirationNotificationPending;
    private bool _restartRequested;
    private int _disposed;
    private readonly ForzaFocusService _focusService = new();

    internal DebugHealthMonitor(
        TelemetryUdpReceiver receiver,
        NativeHudProcessService nativeHud,
        DebugLogService log,
        Func<long> processedPackets,
        Action<Action> postDispatcher,
        Action loggingExpired,
        Func<DateTimeOffset>? utcNow = null,
        Func<long>? timestamp = null,
        TimeSpan? sampleInterval = null,
        Func<DebugFocus>? focus = null)
    {
        _receiver = receiver;
        _nativeHud = nativeHud;
        _log = log;
        _processedPackets = processedPackets;
        _postDispatcher = postDispatcher;
        _loggingExpired = loggingExpired;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _sampleInterval = sampleInterval ?? DefaultSampleInterval;
        _focus = focus ?? GetFocus;
        if (_sampleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        }
    }

    internal void PublishUiContext(DebugHealthUiContext context) =>
        Volatile.Write(ref _uiContext, context);

    internal void RecordCompositionFrame(long timestamp, double _)
    {
        Interlocked.Exchange(ref _compositionTimestamp, timestamp);
        Interlocked.Increment(ref _compositionFrames);
        var previous = Interlocked.Exchange(ref _previousCompositionTimestamp, timestamp);
        if (previous > 0 && timestamp >= previous)
        {
            UpdateMaximum(ref _compositionMaximumGapBits, ToMilliseconds(timestamp - previous));
        }
    }

    internal void Start(DateTimeOffset expiresAtUtc)
    {
        Interlocked.Exchange(ref _expiresAtUtcTicks, expiresAtUtc.UtcDateTime.Ticks);
        CancellationTokenSource? completedCancellation = null;
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            if (_runTask is { IsCompleted: false })
            {
                _restartRequested |= _cancellation?.IsCancellationRequested == true;
                return;
            }

            completedCancellation = _cancellation;
            var cancellation = new CancellationTokenSource();
            Interlocked.Exchange(ref _hasDispatcherDelay, 0);
            Interlocked.Exchange(ref _previousCompositionTimestamp, 0);
            Interlocked.Exchange(ref _compositionMaximumGapBits, 0);
            _cancellation = cancellation;
            var generation = ++_generation;
            var runTask = Task.Run(() => RunAsync(generation, cancellation.Token));
            _runTask = runTask;
            _ = runTask.ContinueWith(
                _ => CompleteRun(runTask, cancellation),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
        completedCancellation?.Dispose();
    }

    internal async Task StopAsync()
    {
        Task? runTask;
        CancellationTokenSource? cancellation;
        lock (_lifecycleGate)
        {
            ++_generation;
            cancellation = _cancellation;
            runTask = _runTask;
            cancellation?.Cancel();
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
                return;
            }
            catch
            {
                Interlocked.Increment(ref _collectorFailures);
            }
        }
        CompleteRun(runTask, cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    private async Task RunAsync(int generation, CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var startedAt = _timestamp();
        var previousCollection = startedAt;
        var previousRateAt = startedAt;
        var previousReceived = _receiver.ReceivedDatagrams;
        var previousProcessed = _processedPackets();
        var previousCompositionFrames = Interlocked.Read(ref _compositionFrames);
        var previousGameTimestamp = _receiver.Latest?.GameTimestampMilliseconds;
        long? compositionExpectedSince = null;
        TimeSpan? previousCpu = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var nowUtc = _utcNow();
            if (nowUtc.UtcDateTime.Ticks >= Interlocked.Read(ref _expiresAtUtcTicks) || !_log.IsEnabled)
            {
                if (_log.ExpireIfNeeded(nowUtc))
                {
                    TryPostExpiration();
                }
                return;
            }

            var now = _timestamp();
            try
            {
                var context = Volatile.Read(ref _uiContext);
                var received = _receiver.ReceivedDatagrams;
                var processed = _processedPackets();
                var rateSeconds = Math.Max(ToMilliseconds(now - previousRateAt) / 1000d, 0.001);
                var statistics = _receiver.GetStatistics(nowUtc);
                var latest = _receiver.Latest;
                var native = _nativeHud.SnapshotFor(latest?.CarOrdinal ?? context?.CarOrdinal ?? 0);
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                double? cpuPercent = previousCpu is { } oldCpu
                    ? Math.Clamp((cpu - oldCpu).TotalMilliseconds / (rateSeconds * 1000d * Environment.ProcessorCount) * 100d, 0d, 100d)
                    : null;
                var heartbeatAge = AgeMilliseconds(now, context?.HeartbeatTimestamp ?? 0);
                var uiContextFresh = heartbeatAge is { } age &&
                                     age <= UiFreshnessTicks * 1000d / Stopwatch.Frequency;
                compositionExpectedSince = context?.OverlayExpectedVisible == true && uiContextFresh
                    ? compositionExpectedSince ?? now
                    : null;
                var gameTimestampAdvancing = latest is { IsRaceOn: true } &&
                                             previousGameTimestamp is { } oldGameTimestamp &&
                                             latest.GameTimestampMilliseconds != oldGameTimestamp;
                var compositionTimestamp = Interlocked.Read(ref _compositionTimestamp);
                var compositionFrames = Interlocked.Read(ref _compositionFrames);
                var compositionAge = compositionExpectedSince is { } expectedSince &&
                                     compositionTimestamp < expectedSince
                    ? AgeMilliseconds(now, expectedSince)
                    : AgeMilliseconds(now, compositionTimestamp);
                var probePending = Volatile.Read(ref _probePending) != 0;
                var dispatcherDelay = probePending
                    ? AgeMilliseconds(now, Interlocked.Read(ref _probePostedTimestamp))
                    : Volatile.Read(ref _hasDispatcherDelay) != 0
                        ? BitConverter.Int64BitsToDouble(Interlocked.Read(ref _dispatcherDelayBits))
                        : null;

                _log.TryLogHealthSample(new DebugHealthSample
                {
                    TimestampUtc = nowUtc,
                    SessionId = sessionId,
                    ElapsedMilliseconds = ToMilliseconds(now - startedAt),
                    CollectionGapMilliseconds = ToMilliseconds(now - previousCollection),
                    ReceivedDatagrams = received,
                    DrainedDatagrams = _receiver.DrainedDatagrams,
                    AcceptedPackets = statistics.AcceptedPackets,
                    RejectedPackets = statistics.RejectedPackets,
                    ProcessedPackets = processed,
                    IncomingHz = Math.Max(0, received - previousReceived) / rateSeconds,
                    ProcessedHz = Math.Max(0, processed - previousProcessed) / rateSeconds,
                    PacketAgeMilliseconds = AgeMilliseconds(now, latest?.ReceivedTimestamp ?? 0),
                    ListenerRunning = _receiver.IsRunning,
                    ListenerError = statistics.ListenerError is not null,
                    RaceOn = latest?.IsRaceOn ?? false,
                    GameTimestampAdvancing = gameTimestampAdvancing,
                    Focus = _focus(),
                    UiContextFresh = uiContextFresh,
                    OverlayExpectedVisible = context?.OverlayExpectedVisible ?? false,
                    NativeExpected = context?.NativeExpected ?? false,
                    UiHeartbeatAgeMilliseconds = heartbeatAge,
                    DispatcherDelayMilliseconds = dispatcherDelay,
                    DispatcherProbePending = probePending,
                    CompositionHz = Math.Max(0, compositionFrames - previousCompositionFrames) / rateSeconds,
                    CompositionAgeMilliseconds = compositionAge,
                    CompositionMaximumGapMilliseconds = BitConverter.Int64BitsToDouble(
                        Interlocked.Exchange(ref _compositionMaximumGapBits, 0)),
                    NativeAvailable = native.NativeGaugeObservedTimestamp > 0,
                    NativeReadAttempts = _nativeHud.DiagnosticReadAttempts,
                    NativeReadFailures = _nativeHud.DiagnosticReadFailures,
                    NativeStatus = native.Status.ToString(),
                    NativeAgeMilliseconds = AgeMilliseconds(now, native.NativeGaugeObservedTimestamp),
                    NativeVisibilityAgeMilliseconds = AgeMilliseconds(now, native.VisibilityObservedTimestamp),
                    GameplayVisibility = native.GameplayVisibility.ToString(),
                    WispCpuPercent = cpuPercent,
                    WorkingSetBytes = process.WorkingSet64,
                    ManagedHeapBytes = GC.GetTotalMemory(false),
                    Gen2Collections = GC.CollectionCount(2),
                    DroppedRecords = _log.DroppedRecords,
                    CollectorFailures = Interlocked.Read(ref _collectorFailures)
                });

                previousCollection = now;
                previousRateAt = now;
                previousReceived = received;
                previousProcessed = processed;
                previousCompositionFrames = compositionFrames;
                previousGameTimestamp = latest?.GameTimestampMilliseconds;
                previousCpu = cpu;
            }
            catch
            {
                Interlocked.Increment(ref _collectorFailures);
            }

            QueueDispatcherProbe(_timestamp(), generation);

            await Task.Delay(_sampleInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private void QueueDispatcherProbe(long postedAt, int generation)
    {
        if (Interlocked.CompareExchange(ref _probePending, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _probePostedTimestamp, postedAt);
            _postDispatcher(() =>
            {
                if (Volatile.Read(ref _generation) == generation)
                {
                    var delay = ToMilliseconds(_timestamp() - postedAt);
                    Interlocked.Exchange(ref _dispatcherDelayBits, BitConverter.DoubleToInt64Bits(delay));
                    Volatile.Write(ref _hasDispatcherDelay, 1);
                }

                Interlocked.Exchange(ref _probePostedTimestamp, 0);
                Interlocked.Exchange(ref _probePending, 0);
            });
        }
        catch
        {
            Interlocked.Exchange(ref _probePending, 0);
            Interlocked.Increment(ref _collectorFailures);
        }
    }

    private void TryPostExpiration()
    {
        if (Interlocked.CompareExchange(ref _expirationNotificationPending, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _postDispatcher(() =>
            {
                try
                {
                    _loggingExpired();
                }
                finally
                {
                    Interlocked.Exchange(ref _expirationNotificationPending, 0);
                }
            });
        }
        catch
        {
            Interlocked.Exchange(ref _expirationNotificationPending, 0);
            Interlocked.Increment(ref _collectorFailures);
        }
    }

    private void CompleteRun(Task? runTask, CancellationTokenSource? cancellation)
    {
        var disposeCancellation = false;
        var restart = false;
        lock (_lifecycleGate)
        {
            if (ReferenceEquals(_runTask, runTask))
            {
                _runTask = null;
                _cancellation = null;
                disposeCancellation = true;
                restart = _restartRequested &&
                          Volatile.Read(ref _disposed) == 0 &&
                          _log.IsEnabled;
                _restartRequested = false;
            }
        }
        _ = runTask?.Exception;
        if (disposeCancellation)
        {
            cancellation?.Dispose();
        }
        if (restart)
        {
            Start(new DateTimeOffset(Interlocked.Read(ref _expiresAtUtcTicks), TimeSpan.Zero));
        }
    }

    private DebugFocus GetFocus()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return DebugFocus.None;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId)
        {
            return DebugFocus.Wisp;
        }

        return _focusService.GetState(_utcNow()).IsForzaForeground
            ? DebugFocus.Game
            : DebugFocus.Other;
    }

    private static double? AgeMilliseconds(long now, long observed) =>
        observed > 0 && now >= observed ? ToMilliseconds(now - observed) : null;

    private static double ToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static void UpdateMaximum(ref long targetBits, double value)
    {
        while (true)
        {
            var currentBits = Interlocked.Read(ref targetBits);
            if (BitConverter.Int64BitsToDouble(currentBits) >= value)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref targetBits, BitConverter.DoubleToInt64Bits(value), currentBits) == currentBits)
            {
                return;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out int processId);
}
