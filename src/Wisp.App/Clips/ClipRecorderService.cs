using System.IO;

namespace Wisp.App.Clips;

// Epoch changes only after the controller observes a real availability/window
// transition. It is not serialized as part of the immutable native identity.
internal sealed record RecorderTargetObservation(RecorderTarget Target, long Epoch);

internal interface IRecorderSession : IAsyncDisposable
{
    RecorderFailureDiagnostic? FailureDiagnostic => null;
    event EventHandler<RecorderStateUpdate>? StateChanged;
    Task OpenAsync(ClipRecordingSpec recording, string storage, CancellationToken cancellationToken);
    Task StartAsync(RecorderTarget target, CancellationToken cancellationToken);
    Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed class ProcessRecorderSession(string installedHelperPath) : IRecorderSession
{
    private readonly RecorderProcessClient _client = new(installedHelperPath);
    public RecorderFailureDiagnostic? FailureDiagnostic => _client.FailureDiagnostic;
    public event EventHandler<RecorderStateUpdate>? StateChanged { add => _client.StateChanged += value; remove => _client.StateChanged -= value; }
    public Task OpenAsync(ClipRecordingSpec recording, string storage, CancellationToken cancellationToken) => _client.OpenAsync(recording, storage, cancellationToken);
    public Task StartAsync(RecorderTarget target, CancellationToken cancellationToken) => _client.StartAsync(target, cancellationToken);
    public Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken cancellationToken) => _client.SaveAsync(target, cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => _client.StopAsync(cancellationToken);
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

internal sealed class ClipRecorderService : IClipRecorder, IAsyncDisposable
{
    private readonly Func<string> _storageDirectory;
    private readonly Func<IRecorderSession> _createSession;
    private readonly Func<CancellationToken, Task<string>> _validateStorage;
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
    private ClipRecordingSpec? _recording;
    private string? _storage, _fault;
    private FailureReportState? _failureReport;
    private sealed record FailureReportState(string Reason, ClipRecordingSpec? Recording,
        IRecorderSession? Owner, RecorderFailureDiagnostic? Diagnostic);
    private bool _enabled, _saving, _cleanupFailed;
    private long _revision;
    private long _targetObservationGeneration;
    private int _disposed, _resourcesDisposed;

    internal ClipRecorderService(string installedHelperPath, Func<string> storageDirectory)
        : this(storageDirectory, () => new ProcessRecorderSession(installedHelperPath), File.Exists(installedHelperPath)) { }

    internal ClipRecorderService(Func<string> storageDirectory, Func<IRecorderSession> createSession, bool helperAvailable,
        Func<CancellationToken, Task<string>>? validateStorage = null)
    {
        _storageDirectory = storageDirectory ?? throw new ArgumentNullException(nameof(storageDirectory));
        _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        _validateStorage = validateStorage ?? ValidateStorageAsync;
        _helperAvailable = helperAvailable;
        _snapshot = OffSnapshot();
        _worker = Task.Run(WorkAsync);
    }

    public ClipRecorderSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public string FailureReport
    {
        get
        {
            lock (_sync) return _failureReport is { } failure
                ? ClipFailureReport.Build(failure.Reason, failure.Recording, failure.Diagnostic ?? failure.Owner?.FailureDiagnostic) : "";
        }
    }
    public event EventHandler? StateChanged;
    // Zero means there is no active capture demand. An observation is valid
    // only for the enable operation that requested its discovery.
    internal long TargetObservationGeneration
    {
        get { lock (_sync) return _enabled && Volatile.Read(ref _disposed) == 0 ? _targetObservationGeneration : 0; }
    }

    // Opening Wisp/Clips is not loss of the game target. Call null only for an
    // unavailable/exited/minimized target, not merely a foreground change.
    internal void ObserveTarget(RecorderTargetObservation? observation, long generation)
    {
        if (observation is not null && (observation.Epoch < 0 || observation.Target.ProcessId == 0 ||
            observation.Target.Window == 0 || observation.Target.CreationFileTime == 0))
            throw new ArgumentException("The game observation is invalid.", nameof(observation));
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_enabled || generation == 0 ||
                generation != _targetObservationGeneration || Equals(_observed, observation)) return;
            _observed = observation; _revision++;
            _sessionLifetime?.Cancel();
        }
        Signal();
    }

    public async Task SetEnabledAsync(bool enabled, ClipRecordingSpec recording, CancellationToken cancellationToken)
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
                    if (_recording != recording || !string.Equals(_storage, storage, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Disable clipping before changing its recording settings.");
                    return;
                }
                _targetObservationGeneration = checked(_targetObservationGeneration + 1);
                _observed = null;
                _recording = recording; _storage = storage; _enabled = true; _fault = null; _blockedObservation = null; _revision++;
                revision = _revision; previousSession = _session;
            }
            Publish(new(ClipRecorderState.WaitingForGame, true, true, false, "Waiting for a game window."), revision, previousSession);
            Signal();
            return;
        }
        long stoppingRevision;
        IRecorderSession? stoppingSession;
        lock (_sync)
        {
            _enabled = false; _observed = null;
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
            if (!_enabled || _saving || _session is null || _nativeState?.State != "buffering") throw new RecorderClientException("not_ready");
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
                if (ReferenceEquals(session, _session)) { RememberFailure(error.Reason, session); current = true; }
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
            bool enabled, blocked;
            long revision;
            lock (_sync)
            {
                observed = _observed; recording = _recording; storage = _storage; enabled = _enabled;
                blocked = Equals(observed, _blockedObservation); revision = _revision;
                if (enabled && observed is not null && !blocked && _session is not null && Equals(_activeObservation, observed)) return;
            }
            await CloseSessionAsync().ConfigureAwait(false);
            lock (_sync) { if (revision != _revision) continue; }
            if (!enabled || _cleanupFailed) { PublishOff(); return; }
            if (observed is null || blocked)
            {
                Publish(new(ClipRecorderState.WaitingForGame, true, true, false,
                    blocked && _fault is not null ? ReasonText(_fault) : "Waiting for a game window."), revision, null);
                return;
            }
            IRecorderSession? session = null;
            CancellationTokenSource? lifetime = null;
            try
            {
                session = _createSession();
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var owned = session;
                EventHandler<RecorderStateUpdate> handler = (_, state) => OnNativeState(owned, state);
                lock (_sync)
                {
                    _session = session; _sessionLifetime = lifetime; _activeObservation = observed;
                    _sessionHandler = handler; _nativeState = null;
                    if (revision != _revision) lifetime.Cancel();
                }
                session.StateChanged += handler;
                Publish(new(ClipRecorderState.WaitingForGame, true, true, false, "Preparing game capture…"), revision, session);
                await session.OpenAsync(recording!, storage!, lifetime.Token).ConfigureAwait(false);
                await session.StartAsync(observed.Target, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime?.IsCancellationRequested == true || _lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (lifetime?.IsCancellationRequested != true)
                    SetFault(error is RecorderClientException recorder ? recorder.Reason : "capture_failed");
            }
            lock (_sync) { if (revision == _revision) return; }
        }
    }

    private void OnNativeState(IRecorderSession session, RecorderStateUpdate state)
    {
        var transition = false;
        long revision;
        lock (_sync)
        {
            if (!ReferenceEquals(_session, session) || _sessionLifetime!.IsCancellationRequested || !_enabled || Volatile.Read(ref _disposed) != 0) return;
            _nativeState = state;
            if ((state.State is "waiting" or "stopped" or "error") && IsTargetTransition(state.Reason))
            {
                _blockedObservation = _activeObservation; _fault = state.Reason; _revision++;
                _sessionLifetime.Cancel(); transition = true;
            }
            revision = _revision;
        }
        if (transition) { Publish(new(ClipRecorderState.WaitingForGame, true, true, false, ReasonText(state.Reason)), revision, session); Signal(); }
        else if (state.State is "error" or "stopped") SetFault(state.Reason, session, revision);
        else PublishNativeState();
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
            else if (_nativeState?.State == "buffering") snapshot = new(ClipRecorderState.Buffering, true, true, true,
                _nativeState.Reason is "audio_unavailable" or "audio_capture_failed" ? "Recording video. Game audio is unavailable." : "Recording game clips.");
            else if (_nativeState?.State == "waiting") snapshot = new(ClipRecorderState.WaitingForGame, true, true, false, "Waiting for a game window.");
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
            if (_failureReport is { Diagnostic: null } failure && ReferenceEquals(failure.Owner, session) &&
                session.FailureDiagnostic is { } diagnostic)
            {
                _failureReport = failure with { Diagnostic = diagnostic };
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
            _fault = reason; _enabled = false; _revision++;
            _sessionLifetime?.Cancel();
        }
        PublishOff(); Signal();
    }

    private void RememberFailure(string reason, IRecorderSession? owner) =>
        _failureReport ??= new(reason, _recording, owner, owner?.FailureDiagnostic);

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
    private static bool IsTargetTransition(string reason) => reason is "target_exited" or "target_changed" or "window_closed" or "window_minimized" or "window_resized";
    internal static string ReasonText(string reason) => reason switch
    {
        "target_exited" or "window_closed" or "waiting_for_game" => "Waiting for a game window.",
        "window_minimized" => "Recording paused while the game is minimized.",
        "window_resized" or "target_changed" => "The game window changed. Waiting for a refreshed game window.",
        "unsupported_os" => "This Windows version cannot record game clips.",
        "unsupported_gpu" => "A compatible hardware video encoder is unavailable.",
        "unsupported_format" => "The selected recording format is unavailable. Try a lower resolution or frame rate.",
        "capture_failed" => "Game capture failed. Enable clipping to try again.",
        "encoder_failed" => "Video encoding failed. Enable clipping to try again.",
        "audio_failed" or "audio_capture_failed" => "Game audio recording failed. Enable clipping to try again.",
        "protocol_error" => "The recorder connection failed. Enable clipping to try again.",
        "helper_start_failed" => "The recorder could not start. Enable clipping to try again.",
        "helper_timeout" => "The recorder did not respond in time. Enable clipping to try again.",
        "helper_exited" => "The recorder closed unexpectedly. Enable clipping to try again.",
        "storage_failed" => "The clip folder could not be written. Check free space and folder access.",
        "mux_failed" => "The clip could not be finalized. Any unfinished save and its file have been kept.",
        "no_keyframe" => "No playable clip is ready yet. Let recording continue, then save again.",
        "not_ready" => "No clip is ready yet. Wait for recording to start, then save again.",
        "save_in_progress" => "A clip is already being saved. Wait for it to finish.",
        "cancelled" => "The clip operation was cancelled. Any unfinished save and its file have been kept.",
        "buffer_full" => "The recording buffer is full. Enable clipping again to start a new buffer.",
        "helper_shutdown_failed" => "The recorder did not confirm shutdown. Restart Wisp before enabling clips again.",
        _ => "Recording stopped. Enable clipping to try again."
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_sync) { _enabled = false; _sessionLifetime?.Cancel(); }
            _lifetime.Cancel();
        }
        await _worker.ConfigureAwait(false);
        if (_unconfirmedSession is { } unconfirmed)
        {
            await unconfirmed.DisposeAsync().ConfigureAwait(false);
            _unconfirmedSession = null;
        }
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0) _lifetime.Dispose();
    }
}
