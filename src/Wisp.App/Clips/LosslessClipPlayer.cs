using System.IO;
using System.Globalization;
using System.Windows.Threading;

namespace Wisp.App.Clips;

internal sealed record LosslessPlaybackSnapshot(bool Ready = false, double Duration = 0, double Position = 0,
    bool Buffering = false, bool Ended = false, string? Failure = null);

// mpv owns the playback/audio clock. All native calls, including destruction,
// are serialized away from WPF. Native events are drained on that same worker.
internal sealed class LosslessClipPlayer
{
    private readonly object _sync = new();
    private readonly object _runtimeOperation = new();
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _tail = Task.CompletedTask;
    private Task<bool>? _close;
    private LosslessMpvRuntime.Lease? _lease;
    private FileStream? _source;
    private LosslessMpvNative? _native;
    private LosslessPlaybackSnapshot _snapshot = new();
    private bool _closed, _paused = true;
    private int _pollPending, _notifyPending;
    private string _stage = "created", _cleanupStatus = "not-requested";
    private double _volume;
    internal LosslessVideoHost Host { get; }
    internal LosslessPlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal string DiagnosticStage => Volatile.Read(ref _stage);
    internal string CleanupStatus => Volatile.Read(ref _cleanupStatus);
    internal event EventHandler? Changed;

    internal LosslessClipPlayer(LosslessVideoHost host, double volume)
    {
        Host = host; _dispatcher = host.Dispatcher; _volume = Math.Clamp(volume, 0, 1);
        host.Closed += HostClosed; host.Failed += HostFailed;
    }

    internal void Open(ClipEntry clip, string path) => Queue(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        var handle = await Host.Ready.WaitAsync(token).ConfigureAwait(false);
        _lease = await LosslessMpvRuntime.AcquireAsync(_runtimeOperation, token).ConfigureAwait(false);
        _source = LosslessVlcRuntime.HoldSource(clip, path);
        token.ThrowIfCancellationRequested();
        SetStage("initialize-native-library");
        _native = new LosslessMpvNative();
        _native.Initialize(handle);
        SetStage("prepare-paused-video");
        _native.Run("loadfile", path, "replace");
        await PreparePausedAsync(token).ConfigureAwait(false);
        var duration = _native.Number("duration") ?? 0;
        if (_native.Integer("video-params/w") != clip.Media.Width || _native.Integer("video-params/h") != clip.Media.Height ||
            duration <= 0 || !double.IsFinite(duration) || Math.Abs(duration - clip.DurationSeconds) > 1 ||
            _native.Flag("seekable") != true || _native.Number("time-pos") is not (>= 0 and <= 0.034) ||
            _native.Text("video-dec-params/pixelformat") != "gbrp")
            throw new InvalidDataException("The decoder metadata did not match the saved clip.");
        token.ThrowIfCancellationRequested();
        SetStage("player-ready");
        Publish(new(true, duration));
    });

    private async Task PreparePausedAsync(CancellationToken token)
    {
        var player = _native!;
        do { await TickAsync(token).ConfigureAwait(false); }
        while (player.Restarts == 0 || player.Flag("pause") != true || player.Flag("seeking") == true ||
            player.Flag("paused-for-cache") != false || player.Flag("vo-configured") != true ||
            player.Number("time-pos") is not (>= 0 and <= 0.034));
        _paused = true;
    }

    private async Task TickAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _native!.DrainEvents();
        await Task.Delay(20, token).ConfigureAwait(false);
    }

    internal void SetPaused(bool paused) => Queue(async () =>
    {
        if (_native is null || !Snapshot.Ready) return;
        if (!paused && _native.Flag("eof-reached") == true)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await SeekAsync(0, deadline.Token).ConfigureAwait(false);
        }
        _paused = paused;
        _native.Set("volume", (paused ? 0 : _volume * 100).ToString(CultureInfo.InvariantCulture));
        _native.Set("mute", paused ? "yes" : "no");
        _native.Set("pause", paused ? "yes" : "no");
        SetStage(paused ? "paused" : "playing");
        Poll();
    });

    internal void SetVolume(double value) => Queue(() =>
    {
        _volume = Math.Clamp(value, 0, 1);
        if (_native is not null && !_paused) _native.Set("volume", (_volume * 100).ToString(CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    });

    internal void Seek(double seconds) => Queue(async () =>
    {
        if (_native is null || !Snapshot.Ready || !double.IsFinite(seconds)) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await SeekAsync(Math.Clamp(seconds, 0, Snapshot.Duration), deadline.Token).ConfigureAwait(false);
        Poll();
    });

    private async Task SeekAsync(double seconds, CancellationToken token)
    {
        var player = _native!;
        SetStage("seeking");
        player.DrainEvents();
        var previous = player.Restarts;
        player.Run("seek", seconds.ToString(CultureInfo.InvariantCulture), "absolute+exact");
        do { await TickAsync(token).ConfigureAwait(false); }
        while (player.Flag("seeking") == true || player.Flag("paused-for-cache") != false ||
            player.Restarts <= previous && !(seconds >= Snapshot.Duration && player.Flag("eof-reached") == true));
        SetStage(_paused ? "paused" : "playing");
    }

    internal Task<LosslessAudioStatistics> ReadAudioStatisticsAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<LosslessAudioStatistics>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue(() =>
        {
            try
            {
                var player = _native ?? throw new InvalidOperationException("The player is closed.");
                completion.TrySetResult(new(player.Text("current-tracks/audio/codec"), player.Number("audio-pts"),
                    player.Integer("audio-params/channel-count"), player.Number("volume"), player.Flag("mute")));
            }
            catch (Exception error) { completion.TrySetException(error); }
            return Task.CompletedTask;
        });
        return completion.Task.WaitAsync(token);
    }

    internal Task<LosslessDecodedFrame> ReadDiagnosticFrameAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<LosslessDecodedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue(() =>
        {
            try { completion.TrySetResult((_native ?? throw new InvalidOperationException("The player is closed.")).ReadDiagnosticFrame()); }
            catch (Exception error) { completion.TrySetException(error); }
            return Task.CompletedTask;
        });
        return completion.Task.WaitAsync(token);
    }

    internal void RequestPoll()
    {
        if (Interlocked.Exchange(ref _pollPending, 1) != 0) return;
        Queue(() => { try { Poll(); } finally { Interlocked.Exchange(ref _pollPending, 0); } return Task.CompletedTask; });
    }

    private void Poll()
    {
        if (_native is null || !Snapshot.Ready) return;
        _native.DrainEvents();
        var ended = _native.Flag("eof-reached") == true;
        if (ended) { _native.DrainEvents(); _paused = true; }
        Publish(Snapshot with
        {
            Position = ended ? Snapshot.Duration : Math.Clamp(_native.Number("time-pos") ?? Snapshot.Position, 0, Snapshot.Duration),
            Buffering = !_paused && _native.Flag("paused-for-cache") == true,
            Ended = ended
        });
    }

    private void Queue(Func<Task> operation)
    {
        lock (_sync)
        {
            if (_closed) return;
            _tail = _tail.ContinueWith(async previous =>
            {
                if (_lifetime.IsCancellationRequested || Snapshot.Failure is not null) return;
                try { await operation().ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception error)
                {
                    var code = error is OperationCanceledException ? "prepare-timeout" : error is LosslessMpvException native ? native.Code : error.GetType().Name;
                    var action = LosslessMpvRuntime.CleanupPending
                        ? "Decoder cleanup is still pending. Restart Wisp before opening another lossless clip."
                        : "Reopen the clip; if it repeats, restart Wisp and report this code.";
                    Publish(Snapshot with { Failure = $"Lossless playback failed ({code}, 0x{error.HResult:X8}). {action}" });
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private void Publish(LosslessPlaybackSnapshot value) { Volatile.Write(ref _snapshot, value); Signal(); }
    private void SetStage(string stage) { Volatile.Write(ref _stage, stage); LosslessMpvRuntime.SetStage(_runtimeOperation, stage); }
    private void Signal()
    {
        if (Interlocked.Exchange(ref _notifyPending, 1) != 0 || _dispatcher.HasShutdownStarted) return;
        _ = _dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _notifyPending, 0);
            if (!_closed) Changed?.Invoke(this, EventArgs.Empty);
        }, DispatcherPriority.Background);
    }

    private void HostClosed(object? sender, EventArgs e) => _ = CloseAsync();
    private void HostFailed(object? sender, EventArgs e)
    {
        Publish(Snapshot with { Failure = "The lossless video surface could not be clipped safely. Reopen the clip to retry." });
        _lifetime.Cancel();
    }

    internal Task<bool> CloseAsync()
    {
        lock (_sync)
        {
            if (_close is not null) return _close;
            _closed = true; _lifetime.Cancel();
            Volatile.Write(ref _cleanupStatus, "stopping");
            Host.Closed -= HostClosed; Host.Failed -= HostFailed;
            var cleanup = _tail.ContinueWith(previous =>
            {
                SetStage("stopping");
                try
                {
                    _native?.Close(); _native = null;
                    _detached.TrySetResult();
                    _source?.Dispose(); _source = null;
                    _lease?.Dispose(); _lease = null;
                    _lifetime.Dispose();
                    Volatile.Write(ref _cleanupStatus, "complete");
                    return true;
                }
                catch (Exception)
                {
                    // Preserve the exact native owner and lease rather than racing a
                    // second Dispose or reusing an engine with unconfirmed workers.
                    Volatile.Write(ref _cleanupStatus, "cleanup-failed-restart-required");
                    LosslessMpvRuntime.Quarantine(this); return false;
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            Host.RetireAfter(_detached.Task);
            return _close = ObserveCleanupAsync(cleanup);
        }
    }

    private async Task<bool> ObserveCleanupAsync(Task<bool> cleanup)
    {
        try { return await cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Interlocked.CompareExchange(ref _cleanupStatus, "cleanup-pending-restart-may-be-required", "stopping");
            LosslessMpvRuntime.CleanupDelayed(_runtimeOperation); return false;
        }
    }
}

internal sealed record LosslessAudioStatistics(string? Codec, double? Position, long? Channels, double? Volume, bool? Mute);
