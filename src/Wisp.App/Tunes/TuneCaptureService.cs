using System.IO;
using System.Runtime.CompilerServices;
using Wisp.Core;
using Wisp.Core.Tunes;
using Wisp.App.Supplementary;
using Wisp.App.DebugLogging;

namespace Wisp.App.Tunes;

public enum TuneCaptureStatus { Ready, GameNotRunning, UnsupportedBuild, Unavailable, Changed, Cancelled }
public sealed record TuneCaptureResult(TuneSnapshot? Snapshot, TuneCaptureStatus Status, string Message, string Details = "")
{
    public bool Success => Snapshot is not null && Status == TuneCaptureStatus.Ready;
    internal SupplementaryIncident? Incident { get; init; }
    internal NativeGameVerification? GameVerification { get; init; }
}

public sealed class TuneCaptureService : IAsyncDisposable
{
    internal const string CompatibilityUnavailableMessage = "Tune reading isn't ready for this Forza update. Wisp checks for compatibility updates automatically.";
    internal const string LayoutUnavailableMessage = "Wisp could not verify this game's tune data. Copy details to report the problem.";
    private readonly object _gate = new();
    private readonly INativeHudProcessMemoryFactory _factory;
    private readonly Func<INativeHudProcessMemory, CancellationToken, TuneDecodeInput> _capture;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly ConditionalWeakTable<TuneSnapshot, SnapshotStamp> _stamps = new();
    private readonly HashSet<Flight> _activeFlights = [];
    private TuneAssetMetadata? _metadata;
    private string _metadataSession = string.Empty;
    private string _metadataLayout = string.Empty;
    private string _metadataPack = string.Empty;
    private long _metadataCompatibility = -1;
    private Flight? _flight;
    private long _generation;
    private long _compatibilityGeneration = -1;
    private long _observedCompatibilityGeneration;
    private string _session = string.Empty;
    private int _carOrdinal;
    private bool _disposed;
    private bool _featureAvailable = true;
    private Task? _disposeTask;

    public TuneCaptureService() : this(new NativeHudProcessMemoryFactory(), null) { }
    internal TuneCaptureService(INativeHudProcessMemoryFactory factory,
        Func<INativeHudProcessMemory, CancellationToken, TuneDecodeInput>? capture)
    {
        _factory = factory;
        _observedCompatibilityGeneration = factory.CompatibilityGeneration;
        _capture = capture ?? Read;
    }

    public event EventHandler? Invalidated;

    public bool NeedsGameObservation
    {
        get { lock (_gate) return !_disposed && (_session.Length != 0 || _activeFlights.Any(flight => !flight.Task.IsCompleted)); }
    }

    public void ObserveCompatibilityGeneration()
    {
        var changed = false;
        lock (_gate)
        {
            var compatibility = _factory.CompatibilityGeneration;
            if (!_disposed && _observedCompatibilityGeneration != compatibility)
            {
                _observedCompatibilityGeneration = compatibility;
                _generation++;
                _session = string.Empty;
                _carOrdinal = 0;
                if (_flight is { Task.IsCompleted: false }) _flight.Cancellation.Cancel();
                changed = true;
            }
        }
        if (changed) Invalidated?.Invoke(this, EventArgs.Empty);
    }

    // Calls share one operation. A cancelled caller does not cancel another caller's request.
    public async Task<TuneCaptureResult> RequestSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flight flight;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_featureAvailable) return new(null, TuneCaptureStatus.Cancelled, "Tune reading is temporarily paused.");
            if (_flight is null || _flight.Task.IsCompleted || _flight.Generation != _generation)
            {
                flight = new Flight(_generation, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                _flight = flight;
                _activeFlights.Add(flight);
                flight.Task = Task.Run(() => CaptureAsync(flight.Generation, flight.Cancellation.Token));
                _ = flight.Task.ContinueWith(completed =>
                {
                    _ = completed.Exception; // Observe faults even when every caller cancelled its wait.
                    lock (_gate)
                    {
                        _activeFlights.Remove(flight);
                        if (flight.Waiters == 0) flight.Cancellation.Dispose();
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else flight = _flight;
            flight.Waiters++;
        }
        try { return await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                flight.Waiters--;
                if (flight.Waiters == 0 && !flight.Task.IsCompleted) flight.Cancellation.Cancel();
                if (flight.Waiters == 0 && flight.Task.IsCompleted) flight.Cancellation.Dispose();
            }
        }
    }

    // This validates known lifecycle state. Save/start still require a fresh capture and
    // exact setup comparison; an on-demand reader cannot observe every in-game edit.
    public bool IsCurrent(TuneSnapshot snapshot)
    {
        lock (_gate)
            return !_disposed && _featureAvailable && _stamps.TryGetValue(snapshot, out var stamp) &&
                stamp.Generation == _generation && stamp.CompatibilityGeneration == _factory.CompatibilityGeneration &&
                stamp.Session == _session && snapshot.Identity.CarOrdinal == _carOrdinal;
    }

    internal void SetFeatureAvailable(bool available)
    {
        lock (_gate)
        {
            if (_disposed || _featureAvailable == available) return;
            _featureAvailable = available;
            _generation++;
            _session = string.Empty;
            _carOrdinal = 0;
            if (!available)
                foreach (var flight in _activeFlights.Where(f => !f.Task.IsCompleted)) flight.Cancellation.Cancel();
        }
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _session = string.Empty;
            _carOrdinal = 0;
            if (_flight is { Task.IsCompleted: false }) _flight.Cancellation.Cancel();
        }
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    private TuneDecodeInput Read(INativeHudProcessMemory memory, CancellationToken cancellationToken)
    {
        var layout = NativeTuneLayout.Resolve(memory, cancellationToken);
        var compatibility = _factory.CompatibilityGeneration;
        if (_metadata is null || _metadataSession != memory.SessionIdentity ||
            _metadataLayout != layout.Digest || _metadataPack != memory.CompatibilityPack.Fingerprint ||
            _metadataCompatibility != compatibility)
        {
            var decoded = TuneAssetCapture.ReadDecoded(memory, cancellationToken, layout);
            try
            {
                _metadata = TuneAssetSqlite.Extract(decoded, cancellationToken, layout.Descriptor?.Asset);
                _metadataSession = memory.SessionIdentity;
                _metadataLayout = layout.Digest;
                _metadataPack = memory.CompatibilityPack.Fingerprint;
                _metadataCompatibility = compatibility;
            }
            finally { Array.Clear(decoded); }
        }
        var input = NativeTuneCapture.Read(memory, _metadata, cancellationToken, layout);
        if (memory.GameDirectory is { } directory && _metadata.CarNameKey(input.CarOrdinal) is { } car)
            input = input with
            {
                CarName = TuneCarNameResolver.TryResolve(
                Path.Combine(directory, "Media", "Stripped", "StringTables", "EN.zip"),
                car.Year, car.ModelToken, car.MakeToken, cancellationToken)
            };
        layout.Verify(memory, cancellationToken);
        return input;
    }

    private async Task<TuneCaptureResult> CaptureAsync(long generation, CancellationToken cancellationToken)
    {
        var verification = NativeGameVerification.Unavailable;
        var result = await Capture().ConfigureAwait(false);
        return result with { GameVerification = verification };

        async Task<TuneCaptureResult> Capture()
        {
            var entered = false;
            var stage = TuneCaptureStage.OpenGame;
            NativeHudCompatibilityPack? build = null;
            try
            {
                await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                var compatibility = _factory.CompatibilityGeneration;
                var attached = _factory.TryOpen(cancellationToken, out var opened, out var status);
                verification = opened?.GameVerification ?? (status == NativeAssistProviderStatus.UnsupportedBuild
                    ? _factory.RejectedGame?.Verification : null) ?? NativeGameVerification.FromStatus(status);
                if (!attached || opened is null)
                    return Fail(status == NativeAssistProviderStatus.GameNotRunning ? TuneCaptureStatus.GameNotRunning :
                        status == NativeAssistProviderStatus.UnsupportedBuild ? TuneCaptureStatus.UnsupportedBuild : TuneCaptureStatus.Unavailable,
                        status == NativeAssistProviderStatus.GameNotRunning ? "Open Forza to read the current car." :
                        status == NativeAssistProviderStatus.UnsupportedBuild ? CompatibilityUnavailableMessage :
                        "The current car could not be read. Refresh and try again.", generation,
                        TuneCaptureDetails.Create(TuneCaptureStage.OpenGame, providerStatus: status),
                        status == NativeAssistProviderStatus.GameNotRunning ? null : TuneIncident(stage,
                            status == NativeAssistProviderStatus.UnsupportedBuild ? "compatibility-unavailable" :
                            status == NativeAssistProviderStatus.AccessDenied ? "access-denied" : "session-unverified"));
                using var memory = opened;
                build = memory.CompatibilityPack;
                stage = TuneCaptureStage.VerifySession;
                if (string.IsNullOrEmpty(memory.SessionIdentity))
                    return Fail(TuneCaptureStatus.Unavailable, "The current game session could not be verified.", generation,
                        TuneCaptureDetails.Create(stage, build), TuneIncident(stage, "session-unverified"));
                stage = TuneCaptureStage.ReadTune;
                var input = _capture(memory, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                stage = TuneCaptureStage.DecodeTune;
                if (!TuneDecoder.TryDecode(input, out var snapshot, out var decodeFailure) || snapshot is null)
                    return Fail(TuneCaptureStatus.Unavailable, "The current tune could not be verified. Refresh and try again.", generation,
                        TuneCaptureDetails.Create(stage, build, decodeFailure: decodeFailure), TuneIncident(stage, "tune-decode-failed"));
                stage = TuneCaptureStage.VerifyCurrent;
                var changed = false;
                lock (_gate)
                {
                    if (_disposed || generation != _generation || compatibility != _factory.CompatibilityGeneration)
                        return new(null, TuneCaptureStatus.Changed, "The car or game changed. Refresh the current tune.",
                            TuneCaptureDetails.Create(stage, build))
                        { Incident = TuneIncident(stage, "session-changed") };
                    if (_session.Length != 0 && (_session != memory.SessionIdentity || _carOrdinal != snapshot.Identity.CarOrdinal ||
                        _compatibilityGeneration != compatibility))
                    {
                        _generation++;
                        changed = true;
                    }
                    _session = memory.SessionIdentity;
                    _carOrdinal = snapshot.Identity.CarOrdinal;
                    _compatibilityGeneration = compatibility;
                    _stamps.Add(snapshot, new SnapshotStamp(_generation, compatibility, _session));
                }
                if (changed) Invalidated?.Invoke(this, EventArgs.Empty);
                return new(snapshot, TuneCaptureStatus.Ready, snapshot.IsComplete ? string.Empty : "Some settings are unavailable for this tune.");
            }
            catch (OperationCanceledException) { return new(null, TuneCaptureStatus.Cancelled, "Tune reading was cancelled."); }
            catch (TuneLayoutException exception)
            {
                return Fail(TuneCaptureStatus.UnsupportedBuild, LayoutUnavailableMessage, generation,
                    TuneCaptureDetails.Create(stage, build, exception), TuneIncident(stage, "layout-unverified", exception));
            }
            catch (TuneChangedException exception)
            {
                return Fail(TuneCaptureStatus.Changed, "The car or tune changed while reading. Refresh and try again.", generation,
                    TuneCaptureDetails.Create(stage, build, exception), TuneIncident(stage, "session-changed", exception));
            }
            catch (TuneCarSelectionException exception)
            {
                return Fail(TuneCaptureStatus.Unavailable, exception.Failure == TuneCarSelectionFailure.NoCar
                    ? "No current car is available. Drive in the open world, then refresh."
                    : "Wisp could not identify one current car. Drive in the open world, then refresh.", generation,
                    TuneCaptureDetails.Create(stage, build, exception), TuneIncident(stage,
                        exception.Failure == TuneCarSelectionFailure.NoCar ? "not_ready" : "validation", exception));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or
                ArgumentException or OverflowException)
            {
                return Fail(TuneCaptureStatus.Unavailable, "The current tune could not be read. Refresh and try again.", generation,
                    TuneCaptureDetails.Create(stage, build, exception), TuneIncident(stage,
                        exception is UnauthorizedAccessException || exception is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
                            ? "access-denied" : exception is InvalidDataException ? "validation" : "tune-read-failed", exception));
            }
            finally { if (entered) _captureGate.Release(); }
        }
    }

    internal static SupplementaryIncident TuneIncident(TuneCaptureStage stage, string reason, Exception? error = null) =>
        SupplementaryIncidentSchema.Create("tune", reason, stage switch
        {
            TuneCaptureStage.OpenGame => "tune-open",
            TuneCaptureStage.VerifySession => "tune-session",
            TuneCaptureStage.ReadTune => "tune-read",
            TuneCaptureStage.DecodeTune => "tune-decode",
            _ => "tune-current"
        }, error is System.ComponentModel.Win32Exception native ? unchecked((uint)native.NativeErrorCode) :
            error is not null ? unchecked((uint)error.HResult) : null,
        error is null ? null : [new(CrashDiagnostics.CrashReportSymbols.ExceptionType(error.GetType()), unchecked((uint)error.HResult), [])],
        SupplementaryIncidentCapture.Context(HealthContextRecorder.Current.Snapshot(), DateTimeOffset.UtcNow));

    private TuneCaptureResult Fail(TuneCaptureStatus status, string message, long generation, string details, SupplementaryIncident? incident = null)
    {
        var changed = false;
        lock (_gate)
        {
            if (generation == _generation && _session.Length != 0)
            {
                _generation++;
                _session = string.Empty;
                _carOrdinal = 0;
                changed = true;
            }
        }
        if (changed) Invalidated?.Invoke(this, EventArgs.Empty);
        return new(null, status, message, details) { Incident = incident };
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            _generation++;
            _lifetime.Cancel();
            var pending = _activeFlights.Select(flight => (Task)flight.Task).ToArray();
            _disposeTask = CompleteDisposalAsync(pending);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task CompleteDisposalAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        finally
        {
            _metadata = null;
            _captureGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed record SnapshotStamp(long Generation, long CompatibilityGeneration, string Session);
    private sealed class Flight(long generation, CancellationTokenSource cancellation)
    {
        internal long Generation { get; } = generation;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Task<TuneCaptureResult> Task { get; set; } = null!;
        internal int Waiters { get; set; }
    }
}
