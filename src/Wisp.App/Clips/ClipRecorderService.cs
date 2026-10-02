using System.Diagnostics;
using System.IO;
using System.Text;

namespace Wisp.App.Clips;

// Epoch changes only after the controller observes a real availability/window
// transition. It is not serialized as part of the immutable native identity.
internal sealed record RecorderTargetObservation(RecorderTarget Target, long Epoch);

internal interface IRecorderSession : IAsyncDisposable
{
    bool BorderlessAllowed { set; }
    RecorderFailureDiagnostic? FailureDiagnostic => null;
    event EventHandler<RecorderStateUpdate>? StateChanged;
    Task OpenAsync(ClipRecordingSpec recording, string storage, CancellationToken cancellationToken);
    Task StartAsync(RecorderTarget target, CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(RecorderTarget target, CancellationToken cancellationToken);
    Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed class ProcessRecorderSession(string installedHelperPath) : IRecorderSession
{
    private readonly RecorderProcessClient _client = new(installedHelperPath);
    public bool BorderlessAllowed { set => _client.BorderlessAllowed = value; }
    public RecorderFailureDiagnostic? FailureDiagnostic => _client.FailureDiagnostic;
    public event EventHandler<RecorderStateUpdate>? StateChanged { add => _client.StateChanged += value; remove => _client.StateChanged -= value; }
    public Task OpenAsync(ClipRecordingSpec recording, string storage, CancellationToken cancellationToken) => _client.OpenAsync(recording, storage, cancellationToken);
    public Task StartAsync(RecorderTarget target, CancellationToken cancellationToken) => _client.StartAsync(target, cancellationToken);
    public Task PauseAsync(CancellationToken cancellationToken) => _client.PauseAsync(cancellationToken);
    public Task ResumeAsync(RecorderTarget target, CancellationToken cancellationToken) => _client.ResumeAsync(target, cancellationToken);
    public Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken cancellationToken) => _client.SaveAsync(target, cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => _client.StopAsync(cancellationToken);
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

internal sealed class ClipRecorderService : IClipRecorder, IAsyncDisposable
{
    private readonly Func<string> _storageDirectory;
    private readonly Func<IRecorderSession> _createSession;
    private readonly Func<CancellationToken, Task<string>> _validateStorage;
    private readonly Func<TimeSpan, CancellationToken, Task> _recoveryDelay;
    private readonly Func<CancellationToken, Task<ClipBorderlessAccessResult>> _requestBorderless;
    private readonly Func<CancellationToken, Task<ClipBorderlessAccessResult>> _checkBorderless;
    private readonly SemaphoreSlim _permissionGate = new(1, 1);
    private ClipBorderlessAccessResult? _borderlessAccess;
    private readonly bool _helperAvailable;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _wake = new(0, 1), _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private ClipRecorderSnapshot _snapshot;
    private RecorderTargetObservation? _observed, _activeObservation, _blockedObservation;
    private IRecorderSession? _session, _unconfirmedSession;
    private CancellationTokenSource? _sessionLifetime;
    private EventHandler<RecorderStateUpdate>? _sessionHandler;
    private RecorderStateUpdate? _nativeState;
    private bool _capturePaused;
    private long _nativePauseSequence;
    private ClipRecordingSpec? _recording;
    private string? _storage, _fault;
    private FailureReportState? _failureReport;
    private sealed record FailureReportState(string Reason, ClipRecordingSpec? Recording,
        IRecorderSession? Owner, RecorderFailureDiagnostic? Diagnostic, ClipStorageDiagnostic? Storage);
    private const int MaximumResetHistory = 8;
    private readonly List<ResetReportState> _resetHistory = [];
    private sealed record ResetReportState(long Sequence, string Trigger, long? SessionAgeMilliseconds, FailureReportState Detail);
    private long _resetSequence, _sessionStarted;
    private bool _enabled, _saving, _cleanupFailed, _showCaptureBorder;
    private CancellationTokenSource? _recoveryCancellation;
    private Task _recoveryTask = Task.CompletedTask;
    private bool _recoveryWaiting;
    private int _recoveryAttempt;
    private long _bufferingSince;
    private long _revision;
    private long _targetObservationGeneration;
    private int _disposed, _resourcesDisposed;

    internal ClipRecorderService(string installedHelperPath, Func<string> storageDirectory)
        : this(storageDirectory, () => new ProcessRecorderSession(installedHelperPath), File.Exists(installedHelperPath),
            requestBorderless: new RecorderBorderlessAccess(installedHelperPath).RequestAsync,
            checkBorderless: new RecorderBorderlessAccess(installedHelperPath).CheckAsync)
    { }

    internal ClipRecorderService(Func<string> storageDirectory, Func<IRecorderSession> createSession, bool helperAvailable,
        Func<CancellationToken, Task<string>>? validateStorage = null,
        Func<TimeSpan, CancellationToken, Task>? recoveryDelay = null,
        Func<CancellationToken, Task<ClipBorderlessAccessResult>>? requestBorderless = null,
        Func<CancellationToken, Task<ClipBorderlessAccessResult>>? checkBorderless = null)
    {
        _storageDirectory = storageDirectory ?? throw new ArgumentNullException(nameof(storageDirectory));
        _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        _validateStorage = validateStorage ?? ValidateStorageAsync;
        _recoveryDelay = recoveryDelay ?? Task.Delay;
        _requestBorderless = requestBorderless ?? (_ => Task.FromResult(ClipBorderlessAccessResult.Unavailable));
        _checkBorderless = checkBorderless ?? (_ => Task.FromResult(ClipBorderlessAccessResult.Unavailable));
        _helperAvailable = helperAvailable;
        _snapshot = OffSnapshot();
        _worker = Task.Run(WorkAsync);
    }

    public ClipRecorderSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public string FailureReport
    {
        get
        {
            lock (_sync)
            {
                var report = new StringBuilder();
                if (_failureReport is { } failure) report.Append(FormatFailure(failure));
                if (_resetHistory.Count > 0)
                {
                    if (report.Length > 0) report.AppendLine();
                    report.AppendLine("Recent recording resets (oldest first; this Wisp session only):");
                    foreach (var reset in _resetHistory)
                    {
                        report.AppendLine(FormattableString.Invariant($"Reset {reset.Sequence}: {reset.Trigger}"));
                        if (reset.SessionAgeMilliseconds is { } age)
                            report.AppendLine(FormattableString.Invariant($"Session age (ms): {age}"));
                        report.Append(FormatFailure(reset.Detail));
                    }
                }
                return report.ToString();
            }
        }
    }
    public event EventHandler? StateChanged;

    public Task<ClipBorderlessAccessResult> RequestBorderlessAccessAsync(CancellationToken cancellationToken) => ResolveBorderlessAccessAsync(true, cancellationToken);
    public Task<ClipBorderlessAccessResult> CheckBorderlessAccessAsync(CancellationToken cancellationToken) => ResolveBorderlessAccessAsync(false, cancellationToken);
    private async Task<ClipBorderlessAccessResult> ResolveBorderlessAccessAsync(bool request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _permissionGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_helperAvailable) return ClipBorderlessAccessResult.Unavailable;
            var result = await (request ? _requestBorderless : _checkBorderless)(linked.Token).ConfigureAwait(false);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                linked.Token.ThrowIfCancellationRequested();
                _borderlessAccess = result;
            }
            return result;
        }
        finally { _permissionGate.Release(); }
    }
    // Zero means there is no active capture demand. An observation is valid
    // only for the enable/resume demand that requested its discovery.
    internal long TargetObservationGeneration
    {
        get { lock (_sync) return _enabled && Volatile.Read(ref _disposed) == 0 ? _targetObservationGeneration : 0; }
    }

    // An unavailable observation pauses acquisition without discarding recent
    // footage. A different process/window still requires a new media session.
    internal void ObserveTarget(RecorderTargetObservation? observation, long generation)
    {
        if (observation is not null && (observation.Epoch < 0 || observation.Target.ProcessId == 0 ||
            observation.Target.Window == 0 || observation.Target.CreationFileTime == 0))
            throw new ArgumentException("The game observation is invalid.", nameof(observation));
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_enabled || generation == 0 ||
                generation != _targetObservationGeneration || Equals(_observed, observation)) return;
            var replacement = observation is not null && _activeObservation is not null &&
                observation.Target != _activeObservation.Target;
            if (replacement && _session is not null && _sessionLifetime?.IsCancellationRequested == false)
                RememberReset("target_observation_changed", _session);
            _observed = observation; _revision++;
            CancelRecoveryLocked();
            _recoveryAttempt = 0;
            if (replacement) _sessionLifetime?.Cancel();
        }
        Signal();
    }

    public async Task SetEnabledAsync(bool enabled, ClipRecordingSpec recording, CancellationToken cancellationToken, bool showCaptureBorder = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled)
        {
            if (!_helperAvailable || _cleanupFailed) throw new RecorderClientException(_cleanupFailed ? "helper_shutdown_failed" : "unsupported_os");
            RecorderProtocol.ValidateRecording(recording);
            long requestedRevision;
            lock (_sync) requestedRevision = _revision;
            var storage = await _validateStorage(cancellationToken).ConfigureAwait(false);
            long revision;
            IRecorderSession? previousSession;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (_revision != requestedRevision)
                    throw new OperationCanceledException("The recording request was superseded.", cancellationToken);
                if (!ClipsSettings.CanRecordToDirectory(storage))
                    throw new IOException(ClipsSettings.RecordingPathTooLongMessage);
                if (_enabled)
                {
                    if (_recording != recording || _showCaptureBorder != showCaptureBorder || !string.Equals(_storage, storage, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Disable clipping before changing its recording settings.");
                    return;
                }
                _targetObservationGeneration = checked(_targetObservationGeneration + 1);
                CancelRecoveryLocked();
                _recoveryAttempt = 0;
                _observed = null;
                _showCaptureBorder = showCaptureBorder;
                _recording = recording; _storage = storage; _enabled = true; _fault = null; _blockedObservation = null; _revision++;
                revision = _revision; previousSession = _session;
            }
            Publish(new(ClipRecorderState.WaitingForGame, true, true, false, "Waiting for Forza to be focused and fullscreen."), revision, previousSession);
            Signal();
            return;
        }
        long stoppingRevision;
        IRecorderSession? stoppingSession;
        lock (_sync)
        {
            _enabled = false; _observed = null;
            CancelRecoveryLocked();
            if (!_cleanupFailed) _fault = null; _revision++; _sessionLifetime?.Cancel();
            stoppingRevision = _revision; stoppingSession = _session;
        }
        Publish(new(ClipRecorderState.Stopping, true, true, false, "Stopping clip recording…"), stoppingRevision, stoppingSession);
        Signal();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseSessionAsync().ConfigureAwait(false);
            PublishOff();
        }
        finally { _lifecycle.Release(); }
    }

    private Task<string> ValidateStorageAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!ClipsSettings.TryNormalizeStorageDirectory(_storageDirectory(), out var path) || !Directory.Exists(path))
            throw new IOException("Choose an existing clip library folder.");
        ClipLibrary.CheckPath(path);
        return path;
    }, cancellationToken);

    public async Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken cancellationToken)
    {
        IRecorderSession session;
        CancellationToken sessionToken;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_enabled || _saving || _session is null || _sessionLifetime!.IsCancellationRequested ||
                !NativeCanSave(_nativeState)) throw new RecorderClientException("not_ready") { SaveCompletedWithoutMedia = true };
            session = _session; sessionToken = _sessionLifetime!.Token; _saving = true;
        }
        PublishNativeState();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionToken);
        try { return await session.SaveAsync(target, operation.Token).ConfigureAwait(false); }
        catch (RecorderClientException error)
        {
            var current = false;
            lock (_sync)
            {
                if (ReferenceEquals(session, _session)) { RememberFailure(error.Reason, session, error); current = true; }
            }
            if (current) StateChanged?.Invoke(this, EventArgs.Empty);
            throw;
        }
        finally
        {
            lock (_sync) { if (ReferenceEquals(session, _session)) _saving = false; }
            PublishNativeState();
        }
    }

    private async Task WorkAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                await _lifecycle.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try { await ReconcileAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException && !_lifetime.IsCancellationRequested)
                { SetFault("capture_failed"); await CloseSessionAsync().ConfigureAwait(false); }
                finally { _lifecycle.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try { await CloseSessionAsync().ConfigureAwait(false); }
            finally { _lifecycle.Release(); }
        }
    }

    private async Task ReconcileAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            RecorderTargetObservation? observed;
            ClipRecordingSpec? recording;
            string? storage;
            bool enabled, blocked, recoveryWaiting;
            IRecorderSession? retainedSession;
            CancellationToken retainedToken;
            bool paused;
            long revision, pauseSequence;
            lock (_sync)
            {
                observed = _observed; recording = _recording; storage = _storage; enabled = _enabled;
                recoveryWaiting = _recoveryWaiting;
                blocked = _blockedObservation is not null && Equals(observed, _blockedObservation); revision = _revision;
                retainedSession = enabled && !blocked && _session is not null && !_sessionLifetime!.IsCancellationRequested &&
                    (observed is null || observed.Target == _activeObservation?.Target) ? _session : null;
                retainedToken = retainedSession is null ? default : _sessionLifetime!.Token;
                paused = _capturePaused; pauseSequence = _nativePauseSequence;
            }
            if (retainedSession is not null)
            {
                var controlRevision = revision;
                try
                {
                    if (observed is null && !paused) await retainedSession.PauseAsync(retainedToken).ConfigureAwait(false);
                    else if (observed is not null && paused)
                    {
                        await retainedSession.ResumeAsync(observed.Target, retainedToken).ConfigureAwait(false);
                        var confirmPause = false;
                        lock (_sync)
                        {
                            if (ReferenceEquals(_session, retainedSession) && !retainedToken.IsCancellationRequested)
                            {
                                // Saving can suppress buffering until mux completes. The
                                // acknowledgment still means capture may now be running.
                                if (pauseSequence == _nativePauseSequence) _capturePaused = false;
                                else if (_capturePaused)
                                {
                                    // A paused event raced the acknowledgment. Require
                                    // fresh eligibility and keep acquisition stopped.
                                    InvalidateObservedTargetLocked();
                                    controlRevision = _revision;
                                    confirmPause = true;
                                }
                            }
                        }
                        if (confirmPause) await retainedSession.PauseAsync(retainedToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (retainedToken.IsCancellationRequested) { }
                catch (RecorderClientException error) when (IsPausedTarget(error.Reason))
                {
                    lock (_sync)
                    {
                        if (observed is not null && paused && revision == _revision &&
                            ReferenceEquals(_session, retainedSession) && !retainedToken.IsCancellationRequested)
                        {
                            _capturePaused = true;
                            InvalidateObservedTargetLocked();
                        }
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    var reason = error is RecorderClientException recorder ? recorder.Reason : "capture_failed";
                    if (IsRecoverable(reason)) RequestRecovery(reason, retainedSession, controlRevision);
                    else SetFault(reason, retainedSession, controlRevision);
                }
                lock (_sync) { if (revision != _revision) continue; }
                PublishNativeState();
                return;
            }
            await CloseSessionAsync().ConfigureAwait(false);
            lock (_sync) { if (revision != _revision) continue; }
            if (!enabled || _cleanupFailed) { PublishOff(); return; }
            if (recoveryWaiting)
            {
                Publish(new(ClipRecorderState.Reconnecting, true, true, false, RecoveryStatus(_fault!)), revision, null);
                return;
            }
            if (observed is null || blocked)
            {
                Publish(new(IsPausedTarget(_fault) ? ClipRecorderState.Paused : ClipRecorderState.WaitingForGame, true, true, false,
                    (IsPausedTarget(_fault) || blocked) && _fault is not null ? ReasonText(_fault) : "Waiting for Forza to be focused and fullscreen."), revision, null);
                return;
            }
            IRecorderSession? session = null;
            CancellationTokenSource? lifetime = null;
            try
            {
                session = _createSession();
                lock (_sync) session.BorderlessAllowed = !_showCaptureBorder && _borderlessAccess == ClipBorderlessAccessResult.Allowed;
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var owned = session;
                EventHandler<RecorderStateUpdate> handler = (_, state) => OnNativeState(owned, state);
                lock (_sync)
                {
                    _session = session; _sessionLifetime = lifetime; _activeObservation = observed;
                    _sessionHandler = handler; _nativeState = null; _capturePaused = false; _nativePauseSequence = 0;
                    _sessionStarted = Stopwatch.GetTimestamp();
                    _bufferingSince = 0;
                    if (revision != _revision) lifetime.Cancel();
                }
                session.StateChanged += handler;
                Publish(new(ClipRecorderState.Preparing, true, true, false, "Preparing game capture…"), revision, session);
                await session.OpenAsync(recording!, storage!, lifetime.Token).ConfigureAwait(false);
                await session.StartAsync(observed.Target, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime?.IsCancellationRequested == true || _lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (lifetime?.IsCancellationRequested != true)
                {
                    var reason = error is RecorderClientException recorder ? recorder.Reason : "capture_failed";
                    if (IsRecoverable(reason)) RequestRecovery(reason, session, revision);
                    else SetFault(reason, session, revision);
                }
            }
            lock (_sync) { if (revision == _revision) return; }
        }
    }

    private void OnNativeState(IRecorderSession session, RecorderStateUpdate state)
    {
        var transition = false;
        var pausedTransition = false;
        long revision;
        lock (_sync)
        {
            if (!ReferenceEquals(_session, session) || _sessionLifetime!.IsCancellationRequested || !_enabled || Volatile.Read(ref _disposed) != 0) return;
            pausedTransition = state.State == "paused" && !_capturePaused;
            if (state.State == "paused") { _capturePaused = true; _nativePauseSequence++; }
            else if (state.State == "buffering") _capturePaused = false;
            _nativeState = state;
            if (pausedTransition)
            {
                _bufferingSince = 0;
                // Native may detect focus loss before the UI observation.
                InvalidateObservedTargetLocked();
            }
            if ((state.State is "waiting" or "reconnecting" or "stopped" or "error") && IsTargetTransition(state.Reason))
            {
                RememberReset(state.Reason, session);
                _blockedObservation = _activeObservation; _fault = state.Reason; _revision++;
                if (IsPausedTarget(state.Reason) || state.Reason == "window_closed")
                {
                    // Native can see a brief focus/geometry change between UI
                    // observations. Force the controller to stabilize anew.
                    _targetObservationGeneration = checked(_targetObservationGeneration + 1);
                    _observed = null;
                }
                CancelRecoveryLocked();
                _sessionLifetime.Cancel(); transition = true;
            }
            revision = _revision;
        }
        if (transition)
        {
            Publish(new(IsPausedTarget(state.Reason) ? ClipRecorderState.Paused : ClipRecorderState.WaitingForGame,
            true, true, false, ReasonText(state.Reason)), revision, session); Signal();
        }
        else if ((state.State is "error" or "stopped" or "reconnecting") && IsRecoverable(state.Reason)) RequestRecovery(state.Reason, session, revision);
        else if (state.State is "error" or "stopped") SetFault(state.Reason, session, revision);
        else
        {
            PublishNativeState();
            if (pausedTransition) Signal();
        }
    }

    private void InvalidateObservedTargetLocked()
    {
        if (_observed is null) return;
        _targetObservationGeneration = checked(_targetObservationGeneration + 1);
        _observed = null;
        _revision++;
    }

    private void PublishNativeState()
    {
        ClipRecorderSnapshot? snapshot = null;
        IRecorderSession? session;
        long revision;
        lock (_sync)
        {
            if (!_enabled || _session is null || _sessionLifetime!.IsCancellationRequested) return;
            if (_saving || _nativeState?.State == "saving") snapshot = new(ClipRecorderState.Saving, true, true, false, "Saving clip…");
            else if (_nativeState?.State == "buffering")
            {
                if (_bufferingSince == 0) _bufferingSince = Stopwatch.GetTimestamp();
                snapshot = new(ClipRecorderState.Buffering, true, true, true, BufferingStatus(_nativeState));
            }
            else if (_nativeState?.State == "waiting") snapshot = new(ClipRecorderState.Preparing, true, true, false, "Preparing game capture…");
            else if (_nativeState?.State == "paused") snapshot = new(ClipRecorderState.Paused, true, true,
                _nativeState.BufferReady, ReasonText(_nativeState.Reason) +
                (_nativeState.BufferReady ? " Your recent recording is kept and can be saved." : " No clip is ready yet."));
            session = _session; revision = _revision;
        }
        if (snapshot is not null) Publish(snapshot, revision, session);
    }

    private async Task CloseSessionAsync()
    {
        IRecorderSession? session;
        CancellationTokenSource? lifetime;
        EventHandler<RecorderStateUpdate>? handler;
        lock (_sync)
        {
            session = _session; lifetime = _sessionLifetime; handler = _sessionHandler;
            _session = null; _sessionLifetime = null; _sessionHandler = null; _activeObservation = null; _nativeState = null; _saving = false;
            _capturePaused = false; _nativePauseSequence = 0;
            _sessionStarted = 0;
        }
        if (session is null) return;
        if (handler is not null) session.StateChanged -= handler;
        lifetime!.Cancel();
        var cleanupFailed = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await session.StopAsync(stop.Token).ConfigureAwait(false); }
        catch (RecorderClientException error) when (error.Reason == "helper_shutdown_failed") { cleanupFailed = true; }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        try { await session.DisposeAsync().ConfigureAwait(false); cleanupFailed = false; }
        catch (Exception error) when (error is not OutOfMemoryException) { cleanupFailed = true; }
        var reportUpdated = false;
        lock (_sync)
        {
            if (_failureReport is { Diagnostic: null } failure && !IsManagedStorageFailure(failure.Reason) && ReferenceEquals(failure.Owner, session) &&
                session.FailureDiagnostic is { } diagnostic)
            {
                _failureReport = failure with { Diagnostic = diagnostic };
                if (_fault == "storage_failed" && diagnostic.HResult is 0x80070070 or 0x80070027 or 0xD000007F)
                    _fault = "buffer_storage_full";
                reportUpdated = true;
            }
            for (var index = 0; index < _resetHistory.Count; index++)
            {
                var reset = _resetHistory[index];
                if (!ReferenceEquals(reset.Detail.Owner, session)) continue;
                var detail = reset.Detail with { Owner = null, Diagnostic = reset.Detail.Diagnostic ?? session.FailureDiagnostic };
                _resetHistory[index] = reset with { Detail = detail };
                reportUpdated = true;
            }
        }
        if (reportUpdated && Volatile.Read(ref _disposed) == 0) StateChanged?.Invoke(this, EventArgs.Empty);
        lifetime.Dispose();
        if (cleanupFailed)
        {
            lock (_sync) { _cleanupFailed = true; _unconfirmedSession = session; }
            SetFault("helper_shutdown_failed");
        }
    }

    private void SetFault(string reason, IRecorderSession? expectedSession = null, long? expectedRevision = null)
    {
        lock (_sync)
        {
            if (expectedRevision is not null && (_revision != expectedRevision || !ReferenceEquals(_session, expectedSession))) return;
            RememberFailure(reason, _session);
            if (reason is "cleanup_failed" or "helper_shutdown_failed") _cleanupFailed = true;
            _fault = reason; _enabled = false; _revision++;
            CancelRecoveryLocked();
            _sessionLifetime?.Cancel();
        }
        PublishOff(); Signal();
    }

    private void RequestRecovery(string reason, IRecorderSession? expectedSession, long expectedRevision)
    {
        long revision;
        CancellationTokenSource cancellation;
        TimeSpan delay;
        lock (_sync)
        {
            if (!_enabled || _revision != expectedRevision || !ReferenceEquals(_session, expectedSession) ||
                Volatile.Read(ref _disposed) != 0) return;
            RememberFailure(reason, expectedSession);
            RememberReset(reason, expectedSession);
            if (_bufferingSince != 0 && Stopwatch.GetElapsedTime(_bufferingSince) >= TimeSpan.FromSeconds(30))
                _recoveryAttempt = 0;
            CancelRecoveryLocked();
            _recoveryAttempt = Math.Min(_recoveryAttempt + 1, 6);
            delay = TimeSpan.FromSeconds(Math.Min(30, 1 << (_recoveryAttempt - 1)));
            _fault = reason; _blockedObservation = null; _recoveryWaiting = true; revision = ++_revision;
            _sessionLifetime?.Cancel();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _recoveryCancellation = cancellation;
        }
        Publish(new(ClipRecorderState.Reconnecting, true, true, false, RecoveryStatus(reason)), revision, expectedSession);
        Signal();
        lock (_sync) _recoveryTask = RetryAfterDelayAsync(revision, delay, cancellation);
    }

    private async Task RetryAfterDelayAsync(long revision, TimeSpan delay, CancellationTokenSource cancellation)
    {
        try
        {
            await _recoveryDelay(delay, cancellation.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (cancellation.IsCancellationRequested || !_enabled || _revision != revision || Volatile.Read(ref _disposed) != 0) return;
                _recoveryWaiting = false;
                _recoveryCancellation = null;
            }
            Signal();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { cancellation.Dispose(); }
    }

    private void CancelRecoveryLocked()
    {
        _recoveryCancellation?.Cancel();
        _recoveryCancellation = null;
        _recoveryWaiting = false;
    }

    private static bool IsRecoverable(string reason) => reason is "capture_stale" or "capture_reconnecting" or
        "encoder_reconnecting" or "audio_reconnecting" or "scheduler_late" or "capture_failed" or
        "audio_failed" or "audio_capture_failed" or "helper_exited" or "helper_timeout" or "not_ready" or "window_resized";

    private static string RecoveryStatus(string reason) => (reason switch
    {
        "audio_reconnecting" or "audio_failed" or "audio_capture_failed" => "Audio changed. Reconnecting capture…",
        "encoder_reconnecting" => "The video device changed. Reconnecting capture…",
        "capture_stale" => "Waiting for fresh game frames. Reconnecting capture…",
        "scheduler_late" => "Recording fell behind. Reconnecting capture…",
        "window_resized" => "The game resolution changed. Reconnecting capture…",
        _ => "Game capture was interrupted. Reconnecting automatically…"
    }) + " The rolling buffer restarts after reconnecting.";

    private void RememberFailure(string reason, IRecorderSession? owner, RecorderClientException? error = null) =>
        _failureReport ??= new(reason, _recording, owner, IsManagedStorageFailure(reason) ? null : owner?.FailureDiagnostic, ClipStorageDiagnostic.From(error));

    // Call only while holding _sync and before cancelling the current session.
    private void RememberReset(string trigger, IRecorderSession? owner)
    {
        var safeTrigger = IsRecoverable(trigger) || IsTargetTransition(trigger) ||
            trigger is "target_observation_unavailable" or "target_observation_changed" ? trigger : "capture_reconnecting";
        long? age = owner is not null && _sessionStarted != 0 ? (long)Stopwatch.GetElapsedTime(_sessionStarted).TotalMilliseconds : null;
        var reason = safeTrigger switch
        {
            "target_observation_unavailable" => "waiting_for_game",
            "target_observation_changed" => "target_changed",
            _ => safeTrigger
        };
        if (_resetHistory.Count == MaximumResetHistory) _resetHistory.RemoveAt(0);
        _resetHistory.Add(new(++_resetSequence, safeTrigger, age,
            new(reason, _recording, owner, owner?.FailureDiagnostic, null)));
    }

    private static string FormatFailure(FailureReportState failure) => ClipFailureReport.Build(failure.Reason, failure.Recording,
        IsManagedStorageFailure(failure.Reason) ? null : failure.Diagnostic ?? failure.Owner?.FailureDiagnostic, failure.Storage);

    private static bool IsManagedStorageFailure(string reason) => reason is
        "buffer_storage_unavailable" or "buffer_storage_full" or "clip_storage_full" or "clip_publish_failed";

    private ClipRecorderSnapshot OffSnapshot()
    {
        lock (_sync)
        {
            if (!_helperAvailable) return new(ClipRecorderState.Unavailable, false, false, false, "Clip recording is unavailable in this build.");
            if (_fault is not null) return new(ClipRecorderState.Error, false, !_cleanupFailed, false, ReasonText(_fault));
            return new(ClipRecorderState.Disabled, false, !_cleanupFailed, false, "Clipping is off.");
        }
    }
    private void PublishOff()
    {
        ClipRecorderSnapshot snapshot;
        long revision;
        IRecorderSession? session;
        lock (_sync) { snapshot = OffSnapshot(); revision = _revision; session = _session; }
        Publish(snapshot, revision, session);
    }
    private void Publish(ClipRecorderSnapshot snapshot, long expectedRevision, IRecorderSession? expectedSession)
    {
        ClipRecorderSnapshot previous;
        var reportCleared = false;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (_revision != expectedRevision || !ReferenceEquals(_session, expectedSession)) return;
            if (snapshot.State == ClipRecorderState.Stopping ? _enabled : snapshot.Enabled != _enabled) return;
            if ((snapshot.State is ClipRecorderState.Buffering or ClipRecorderState.Saving) &&
                (_session is null || _sessionLifetime!.IsCancellationRequested)) return;
            if (snapshot.State == ClipRecorderState.Buffering && _failureReport is { } failure &&
                !ReferenceEquals(failure.Owner, expectedSession))
            {
                _failureReport = null;
                reportCleared = true;
            }
            previous = Interlocked.Exchange(ref _snapshot, snapshot);
        }
        if (previous != snapshot || reportCleared) StateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Signal() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
    private static bool NativeCanSave(RecorderStateUpdate? state) => state?.State == "buffering" ||
        state is { State: "paused", BufferReady: true };
    private static bool IsPausedTarget(string? reason) => reason is "window_minimized" or "focus_lost" or "fullscreen_required";
    private static bool IsTargetTransition(string reason) => reason is "target_exited" or "target_changed" or "window_closed" || IsPausedTarget(reason);
    internal static string BufferingStatus(RecorderStateUpdate state)
    {
        var status = state.Reason == "capture_stale" ? "No new game frames. Recording the last frame while waiting for the game." :
            state.Reason is "audio_unavailable" or "audio_capture_failed" ? "Recording video. The selected audio is unavailable." : "Recording game clips.";
        if (state.LosslessBuffer is not { } buffer) return status;
        return status + $" Lossless history: {buffer.Duration100ns / 10_000_000d:0.0} s · {buffer.PayloadBytes / 1048576d:0} of {buffer.BudgetBytes / 1048576d:0} MiB." +
            (buffer.SizeLimited ? " Size limit reached; saves use the available history." : "");
    }
    internal static string ReasonText(string reason) => reason switch
    {
        "target_exited" or "window_closed" or "waiting_for_game" => "Waiting for Forza to be focused and fullscreen.",
        "window_minimized" => "Recording paused while the game is minimized.",
        "focus_lost" => "Recording paused. Return to Forza in fullscreen to resume.",
        "fullscreen_required" => "Recording paused. Use fullscreen or borderless fullscreen in Forza to resume.",
        "window_resized" or "target_changed" => "The game window changed. Waiting for Forza to be focused and fullscreen.",
        "unsupported_os" => "This Windows version cannot record game clips.",
        "unsupported_gpu" => "A compatible hardware video encoder is unavailable.",
        "lossless_encoder_unsupported" => "Lossless video needs a supported NVIDIA encoder. Turn off Lossless video to use standard recording.",
        "unsupported_format" => "The screen color format, orientation or recording settings are unsupported. Copy error details to report this.",
        "capture_stale" or "capture_reconnecting" or "encoder_reconnecting" or "audio_reconnecting" or "scheduler_late" => RecoveryStatus(reason),
        "capture_failed" => "Game capture failed. Enable clipping to try again.",
        "encoder_failed" => "Video encoding failed. Enable clipping to try again.",
        "audio_failed" or "audio_capture_failed" => "Audio recording failed. Enable clipping to try again.",
        "protocol_error" => "The recorder connection failed. Enable clipping to try again.",
        "helper_start_failed" => "The recorder could not start. Enable clipping to try again.",
        "helper_timeout" => "The recorder did not respond in time. Enable clipping to try again.",
        "helper_exited" => "The recorder closed unexpectedly. Enable clipping to try again.",
        "storage_failed" => "The clip folder could not be written. Check free space and folder access.",
        "lossless_storage_low" => "Lossless recording needs more free space for its buffer and finished clips. Free space on Wisp's local app data drive, or turn off Lossless video.",
        "buffer_storage_unavailable" => "The local recording buffer could not be opened. Check free space and access to Wisp's local app data.",
        "buffer_storage_full" => "The recording buffer drive is full. Free space before enabling clipping again.",
        "clip_storage_full" => "The selected clips drive is full. Free space or choose another folder. The finished local clip has been kept.",
        "clip_publish_failed" => "The finished clip could not be saved to your folder. Check folder access, cloud sync and free space. Its local copy has been kept.",
        "mux_failed" => "The clip could not be finalized. Any unfinished save and its file have been kept.",
        "no_keyframe" => "No playable clip is ready yet. Let recording continue, then save again.",
        "not_ready" => "No clip is ready yet. Wait for recording to start, then save again.",
        "save_in_progress" => "A clip is already being saved. Wait for it to finish.",
        "cancelled" => "The clip operation was cancelled. Any unfinished save and its file have been kept.",
        "buffer_full" => "The recording buffer is full. Enable clipping again to start a new buffer.",
        "helper_shutdown_failed" or "cleanup_failed" => "The recorder did not confirm shutdown. Restart Wisp before enabling clips again.",
        _ => "Recording stopped. Enable clipping to try again."
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_sync) { _enabled = false; CancelRecoveryLocked(); _sessionLifetime?.Cancel(); }
            _lifetime.Cancel();
        }
        await _worker.ConfigureAwait(false);
        await _recoveryTask.ConfigureAwait(false);
        if (_unconfirmedSession is { } unconfirmed)
        {
            await unconfirmed.DisposeAsync().ConfigureAwait(false);
            _unconfirmedSession = null;
        }
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0) _lifetime.Dispose();
    }
}
