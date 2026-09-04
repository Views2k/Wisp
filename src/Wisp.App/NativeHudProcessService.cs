using System.Diagnostics;
using Wisp.Core;

namespace Wisp.App;

public sealed class NativeHudProcessService : IAsyncDisposable
{
    private static readonly TimeSpan AttachRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly long FullResolveAuditTicks =
        (long)Math.Ceiling(Stopwatch.Frequency / 60d);
    private static readonly long StructuralSourceAuditTicks =
        Stopwatch.Frequency * 5L;
    private static readonly long NativeGaugeCarryForwardTicks =
        (long)Math.Round(
            Stopwatch.Frequency *
            NativeNeedlePlayback.NativeSampleFreshnessMilliseconds /
            1_000d);

    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly INativeHudProcessMemoryFactory _memoryFactory;
    private readonly Func<NativeHudCompatibilityPack, INativeGameplayVisibilityResolver> _visibilityResolverFactory;
    private readonly Task _worker;
    private NativeAssistTelemetrySample? _telemetry;
    private NativeHudSnapshot _snapshot = NativeHudSnapshot.Unavailable();
    private long _snapshotCompatibilityGeneration = -1;
    private long _sessionEpoch;
    private long _generation;
    private long _diagnosticReadAttempts;
    private long _diagnosticReadFailures;
    private bool _fullResolvePending;
    private bool _disposed;
    private Task? _disposeTask;

    // Only the worker touches process memory, resolver caches, or attachment timing.
    private NativeHudMemoryResolver _resolver = new();
    private INativeGameplayVisibilityResolver? _visibilityResolver;
    private INativeHudProcessMemory? _memory;
    private DateTimeOffset _nextAttachAtUtc = DateTimeOffset.MinValue;
    private long _nextFullResolveAuditTimestamp;
    private long _nextStructuralSourceAuditTimestamp;

    public NativeHudProcessService()
        : this(new NativeHudProcessMemoryFactory())
    {
    }

    public NativeHudProcessService(INativeHudProcessMemoryFactory memoryFactory)
        : this(memoryFactory, static pack => new NativeGameplayVisibilityResolver(pack))
    {
    }

    internal NativeHudProcessService(
        INativeHudProcessMemoryFactory memoryFactory,
        Func<NativeHudCompatibilityPack, INativeGameplayVisibilityResolver> visibilityResolverFactory)
    {
        _memoryFactory = memoryFactory ?? throw new ArgumentNullException(nameof(memoryFactory));
        _visibilityResolverFactory = visibilityResolverFactory ?? throw new ArgumentNullException(nameof(visibilityResolverFactory));
        _worker = Task.Run(() => RunAsync(_cancellation.Token));
    }

    public string CompatibilityStatus => _memoryFactory.CompatibilityStatus;

    internal long DiagnosticReadAttempts => Interlocked.Read(ref _diagnosticReadAttempts);
    internal long DiagnosticReadFailures => Interlocked.Read(ref _diagnosticReadFailures);

    public NativeHudSnapshot SnapshotFor(int carOrdinal)
    {
        lock (_stateGate)
        {
            var compatibilityGeneration = _memoryFactory.CompatibilityGeneration;
            if (_snapshotCompatibilityGeneration != compatibilityGeneration)
            {
                _snapshot = NativeHudSnapshot.Unavailable(
                    NativeAssistProviderStatus.Unavailable,
                    _snapshot.Generation,
                    carOrdinal);
                _snapshotCompatibilityGeneration = compatibilityGeneration;
            }

            return _snapshot.CarOrdinal == carOrdinal
                ? _snapshot
                : NativeHudSnapshot.Unavailable(_snapshot.Status, _snapshot.Generation, carOrdinal);
        }
    }

    public void UpdateTelemetry(VehicleState? state, bool nativeLayoutActive)
    {
        NativeAssistTelemetrySample? next = nativeLayoutActive && state is { IsRaceOn: true, CarOrdinal: > 0 }
            ? new NativeAssistTelemetrySample(
                state.CarOrdinal,
                state.EngineRpm,
                state.EngineMaximumRpm,
                state.GameTimestampMilliseconds,
                state.IsElectric)
            : null;

        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            var sessionChanged = SessionChanged(_telemetry, next);
            if (sessionChanged)
            {
                _sessionEpoch++;
                _snapshot = NativeHudSnapshot.Unavailable(
                    NativeAssistProviderStatus.Unavailable,
                    (ulong)Math.Max(0, Interlocked.Read(ref _generation)),
                    next?.CarOrdinal ?? 0);
            }

            _telemetry = next;
            _fullResolvePending |= sessionChanged && next is not null;
            SignalWorker();
        }
    }

    /// <summary>
    /// Requests one native gauge capture against the latest accepted telemetry
    /// identity. Calls are coalesced so compositor cadence cannot build a queue.
    /// </summary>
    public void RequestNativeGaugeSample()
    {
        lock (_stateGate)
        {
            if (_disposed || _telemetry is null)
            {
                return;
            }

            SignalWorker();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            _sessionEpoch++;
            _telemetry = null;
            _snapshot = NativeHudSnapshot.Unavailable();
            _cancellation.Cancel();
            _disposeTask = CompleteDisposalAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task CompleteDisposalAsync()
    {
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _memory?.Dispose();
            _memory = null;
            _wake.Dispose();
            _cancellation.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        long workerEpoch = -1;
        long compatibilityGeneration = -1;
        while (!cancellationToken.IsCancellationRequested)
        {
            await WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
            NativeAssistTelemetrySample? telemetry;
            NativeHudSnapshot baseline;
            bool fullResolveRequested;
            long epoch;
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return;
                }

                telemetry = _telemetry;
                baseline = _snapshot;
                fullResolveRequested = _fullResolvePending;
                _fullResolvePending = false;
                epoch = _sessionEpoch;
            }

            var currentCompatibilityGeneration = _memoryFactory.CompatibilityGeneration;
            if (currentCompatibilityGeneration != compatibilityGeneration)
            {
                CloseMemory();
                _nextAttachAtUtc = DateTimeOffset.MinValue;
                compatibilityGeneration = currentCompatibilityGeneration;
                fullResolveRequested = true;
            }

            if (epoch != workerEpoch)
            {
                _resolver.Reset();
                ResetAuditDeadlines();
                _nextAttachAtUtc = DateTimeOffset.MinValue;
                workerEpoch = epoch;
                fullResolveRequested = true;
            }

            if (telemetry is null)
            {
                CloseMemory();
                continue;
            }

            if (_memory is null)
            {
                var now = DateTimeOffset.UtcNow;
                if (now < _nextAttachAtUtc)
                {
                    continue;
                }

                if (!_memoryFactory.TryOpen(out _memory, out var status))
                {
                    TryPublish(epoch, compatibilityGeneration, NativeHudSnapshot.Unavailable(
                        status, (ulong)Math.Max(0, Interlocked.Read(ref _generation)), telemetry.CarOrdinal));
                    _nextAttachAtUtc = now + AttachRetryInterval;
                    continue;
                }

                if (_memory is null)
                {
                    TryPublish(epoch, compatibilityGeneration, NativeHudSnapshot.Unavailable(
                        NativeAssistProviderStatus.ReadFailure,
                        (ulong)Math.Max(0, Interlocked.Read(ref _generation)), telemetry.CarOrdinal));
                    _nextAttachAtUtc = now + AttachRetryInterval;
                    continue;
                }

                _resolver = new NativeHudMemoryResolver(_memory.CompatibilityPack);
                _visibilityResolver = _visibilityResolverFactory(_memory.CompatibilityPack);
                ResetAuditDeadlines();
                fullResolveRequested = true;
            }

            if (!IsCurrentSession(epoch))
            {
                continue;
            }

            var memory = _memory;
            Interlocked.Increment(ref _diagnosticReadAttempts);
            var generation = (ulong)Interlocked.Increment(ref _generation);
            var nowTimestamp = Stopwatch.GetTimestamp();
            var performFullResolve = fullResolveRequested ||
                                     baseline.CarOrdinal != telemetry.CarOrdinal ||
                                     AuditDue(nowTimestamp, _nextFullResolveAuditTimestamp);
            var forceAudit = performFullResolve &&
                             AuditDue(nowTimestamp, _nextStructuralSourceAuditTimestamp);

            NativeHudSnapshot result;
            if (performFullResolve)
            {
                result = _resolver.Resolve(
                    memory,
                    memory.ModuleBase,
                    telemetry.CarOrdinal,
                    telemetry.EngineRpm,
                    telemetry.EngineMaximumRpm,
                    generation,
                    forceSourceAudit: forceAudit,
                    isElectric: telemetry.IsElectric);

                // Resolve the independent UI capability after the vehicle reader. It must
                // neither depend on an optional stock gauge nor validate a cached provider.
                var visibility = _visibilityResolver!.Resolve(memory, memory.ModuleBase);
                var observedTimestamp = visibility is NativeGameplayVisibility.Visible or NativeGameplayVisibility.Hidden
                    ? Stopwatch.GetTimestamp()
                    : 0L;
                result = result with
                {
                    GameplayVisibility = observedTimestamp > 0 ? visibility : NativeGameplayVisibility.Unknown,
                    VisibilityObservedTimestamp = Math.Max(0L, observedTimestamp)
                };
                if (telemetry.IsElectric)
                {
                    result = RetainFreshElectricGearState(
                        result,
                        baseline,
                        nowTimestamp);
                }

                var completedTimestamp = Stopwatch.GetTimestamp();
                _nextFullResolveAuditTimestamp = completedTimestamp + FullResolveAuditTicks;
                if (forceAudit)
                {
                    _nextStructuralSourceAuditTimestamp = completedTimestamp + StructuralSourceAuditTicks;
                }
            }
            else
            {
                result = _resolver.RefreshNativeGauge(
                    memory,
                    memory.ModuleBase,
                    baseline,
                    generation,
                    telemetry.IsElectric);
                if (ReferenceEquals(result, baseline))
                {
                    continue;
                }
            }

            // Invalidation and publication share the same lock. A -> menu -> A and
            // A -> B -> A cannot republish an old read merely because the car ID matches again.
            if (!TryPublish(epoch, compatibilityGeneration, result))
            {
                continue;
            }

            if (result.Status is NativeAssistProviderStatus.ReadFailure or
                NativeAssistProviderStatus.InvalidSourceVector or NativeAssistProviderStatus.InvalidProvider)
            {
                Interlocked.Increment(ref _diagnosticReadFailures);
            }

            if (!result.HasAvailableCapabilities &&
                result.Status is NativeAssistProviderStatus.ReadFailure or NativeAssistProviderStatus.InvalidSourceVector)
            {
                CloseMemory();
                _nextAttachAtUtc = DateTimeOffset.UtcNow + AttachRetryInterval;
            }
        }
    }

    private bool IsCurrentSession(long epoch)
    {
        lock (_stateGate)
        {
            return !_disposed && _sessionEpoch == epoch && _telemetry is not null;
        }
    }

    private bool TryPublish(long epoch, long compatibilityGeneration, NativeHudSnapshot snapshot)
    {
        lock (_stateGate)
        {
            if (_disposed || _sessionEpoch != epoch || _telemetry is null ||
                compatibilityGeneration != _memoryFactory.CompatibilityGeneration ||
                _telemetry.CarOrdinal != snapshot.CarOrdinal)
            {
                return false;
            }

            _snapshot = snapshot;
            _snapshotCompatibilityGeneration = compatibilityGeneration;
            return true;
        }
    }

    private void CloseMemory()
    {
        _memory?.Dispose();
        _memory = null;
        _visibilityResolver = null;
        _resolver.Reset();
        ResetAuditDeadlines();
    }

    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        var deadline = _nextFullResolveAuditTimestamp;
        if (deadline <= 0)
        {
            await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var remainingTicks = deadline - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return;
        }

        var timeout = TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
        await _wake.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private void ResetAuditDeadlines()
    {
        _nextFullResolveAuditTimestamp = 0;
        _nextStructuralSourceAuditTimestamp = 0;
    }

    private static bool AuditDue(long nowTimestamp, long deadlineTimestamp) =>
        deadlineTimestamp <= 0 || nowTimestamp >= deadlineTimestamp;

    private void SignalWorker()
    {
        // All producers (including disposal) are serialized; the worker only consumes.
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    private static bool SessionChanged(NativeAssistTelemetrySample? previous, NativeAssistTelemetrySample? next)
    {
        if (previous is null || next is null)
        {
            return previous is not null || next is not null;
        }

        return previous.CarOrdinal != next.CarOrdinal ||
            previous.IsElectric != next.IsElectric ||
            BitConverter.SingleToInt32Bits(previous.EngineMaximumRpm) != BitConverter.SingleToInt32Bits(next.EngineMaximumRpm) ||
            unchecked((int)(next.GameTimestampMilliseconds - previous.GameTimestampMilliseconds)) < 0;
    }

    internal static NativeHudSnapshot RetainFreshElectricGearState(
        NativeHudSnapshot current,
        NativeHudSnapshot previous,
        long nowTimestamp)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);
        var observedTimestamp = previous.NativeGaugeObservedTimestamp;
        if (current.ElectricGearState.Available ||
            current.NativeGaugeObservedTimestamp > 0 ||
            !previous.ElectricGearState.Available ||
            current.CarOrdinal <= 0 ||
            current.CarOrdinal != previous.CarOrdinal ||
            (!current.Available && !current.Assists.Available) ||
            observedTimestamp <= 0 ||
            nowTimestamp < observedTimestamp ||
            nowTimestamp - observedTimestamp > NativeGaugeCarryForwardTicks)
        {
            return current;
        }

        return current with
        {
            NativeGaugeObservedTimestamp = observedTimestamp,
            ElectricGearState = previous.ElectricGearState
        };
    }

    private sealed record NativeAssistTelemetrySample(
        int CarOrdinal,
        float EngineRpm,
        float EngineMaximumRpm,
        uint GameTimestampMilliseconds,
        bool IsElectric);
}

public interface INativeGameplayVisibilityResolver
{
    NativeGameplayVisibility Resolve(IReadOnlyProcessMemory memory, ulong moduleBase);
}

public interface INativeHudProcessMemory : IReadOnlyProcessMemory, IDisposable
{
    ulong ModuleBase { get; }
    NativeHudCompatibilityPack CompatibilityPack => NativeHudBuildContract.BuiltIn;
}

public interface INativeHudProcessMemoryFactory
{
    long CompatibilityGeneration => 0;
    string CompatibilityStatus => "Built-in compatibility; awaiting FH6";

    bool TryOpen(
        out INativeHudProcessMemory? memory,
        out NativeAssistProviderStatus status);
}
