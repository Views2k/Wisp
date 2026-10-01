using System.IO;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace Wisp.App.Clips;

internal sealed record LosslessPlaybackSnapshot(bool Ready = false, double Duration = 0, double Position = 0,
    bool Buffering = false, bool Ended = false, string? Failure = null);

// LibVLC owns the playback/audio clock. All native calls, including destruction,
// are serialized away from WPF; native callbacks only publish managed signals.
internal sealed class LosslessClipPlayer
{
    private readonly object _sync = new();
    private readonly object _runtimeOperation = new();
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _tail = Task.CompletedTask;
    private Task<bool>? _close;
    private LosslessVlcRuntime.Lease? _lease;
    private FileStream? _source;
    private Media? _media;
    private MediaPlayer? _native;
    private LosslessPlaybackSnapshot _snapshot = new();
    private bool _closed, _paused = true;
    private int _failed, _ended, _pollPending, _notifyPending;
    private double _volume;
    internal LosslessVideoHost Host { get; }
    internal LosslessPlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);
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
        _lease = await LosslessVlcRuntime.AcquireAsync(_runtimeOperation, token).ConfigureAwait(false);
        _source = LosslessVlcRuntime.HoldSource(clip, path);
        token.ThrowIfCancellationRequested();
        _native = new MediaPlayer(_lease.Engine)
        { Mute = true, Volume = 0, EnableKeyInput = false, EnableMouseInput = false, Hwnd = handle };
        _native.EncounteredError += (_, _) => { Interlocked.Exchange(ref _failed, 1); RequestPoll(); };
        _native.EndReached += (_, _) => { Interlocked.Exchange(ref _ended, 1); RequestPoll(); };
        _native.Buffering += (_, _) => RequestPoll();
        _media = new Media(_lease.Engine, path, FromType.FromPath, ":start-paused");
        _native.Media = _media;
        await PreparePausedAsync(token).ConfigureAwait(false);
        uint width = 0, height = 0;
        var duration = _native.Length / 1000d;
        if (!_native.Size(0, ref width, ref height) || width != clip.Media.Width || height != clip.Media.Height ||
            duration <= 0 || !double.IsFinite(duration) || Math.Abs(duration - clip.DurationSeconds) > 1 ||
            !_native.CanPause || !_native.IsSeekable || _native.Time is < 0 or > 34)
            throw new InvalidDataException("The decoder metadata did not match the saved clip.");
        Publish(new(true, duration));
    });

    private async Task PreparePausedAsync(CancellationToken token)
    {
        var player = _native!;
        player.Mute = true; player.Volume = 0;
        Interlocked.Exchange(ref _ended, 0);
        if (!player.Play()) throw new InvalidOperationException("The decoder could not open this clip.");
        while (player.State != VLCState.Paused) await TickAsync(token).ConfigureAwait(false);
        // A paused seek permits the decoder's first preview frame without advancing
        // its clock. Cache percentage can remain below 100 while intentionally paused.
        player.Time = 0;
        while (player.State != VLCState.Paused || player.VoutCount == 0) await TickAsync(token).ConfigureAwait(false);
        _paused = true;
    }

    private async Task TickAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _failed) != 0) throw new InvalidDataException("The lossless decoder reported an error.");
        await Task.Delay(20, token).ConfigureAwait(false);
    }

    internal void SetPaused(bool paused) => Queue(async () =>
    {
        if (_native is null || !Snapshot.Ready) return;
        if (!paused && Volatile.Read(ref _ended) != 0)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            _native.Stop();
            await PreparePausedAsync(deadline.Token).ConfigureAwait(false);
        }
        _paused = paused;
        _native.Volume = paused ? 0 : (int)Math.Round(_volume * 100);
        _native.Mute = paused;
        _native.SetPause(paused);
        Poll();
    });

    internal void SetVolume(double value) => Queue(() =>
    {
        _volume = Math.Clamp(value, 0, 1);
        if (_native is not null && !_paused) _native.Volume = (int)Math.Round(_volume * 100);
        return Task.CompletedTask;
    });

    internal void Seek(double seconds) => Queue(async () =>
    {
        if (_native is null || !Snapshot.Ready || !double.IsFinite(seconds)) return;
        if (Volatile.Read(ref _ended) != 0)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            _native.Stop();
            await PreparePausedAsync(deadline.Token).ConfigureAwait(false);
        }
        _native.Time = (long)(Math.Clamp(seconds, 0, Snapshot.Duration) * 1000);
        Poll();
    });

    internal void RequestPoll()
    {
        if (Interlocked.Exchange(ref _pollPending, 1) != 0) return;
        Queue(() => { try { Poll(); } finally { Interlocked.Exchange(ref _pollPending, 0); } return Task.CompletedTask; });
    }

    private void Poll()
    {
        if (_native is null || !Snapshot.Ready) return;
        if (Volatile.Read(ref _failed) != 0) throw new InvalidDataException("The lossless decoder reported an error.");
        var ended = Volatile.Read(ref _ended) != 0;
        if (ended) _paused = true;
        Publish(Snapshot with
        {
            Position = ended ? Snapshot.Duration : Math.Clamp(_native.Time / 1000d, 0, Snapshot.Duration),
            Buffering = !_paused && _native.State == VLCState.Buffering,
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
                    var code = error is OperationCanceledException ? "prepare-timeout" : error.GetType().Name;
                    var action = LosslessVlcRuntime.CleanupPending
                        ? "Decoder cleanup is still pending. Restart Wisp before opening another lossless clip."
                        : "Reopen the clip; if it repeats, restart Wisp and report this code.";
                    Publish(Snapshot with { Failure = $"Lossless playback failed ({code}, 0x{error.HResult:X8}). {action}" });
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private void Publish(LosslessPlaybackSnapshot value) { Volatile.Write(ref _snapshot, value); Signal(); }
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
    private void HostFailed(object? sender, EventArgs e) => Publish(Snapshot with { Failure = "The lossless video surface could not be clipped safely. Reopen the clip to retry." });

    internal Task<bool> CloseAsync()
    {
        lock (_sync)
        {
            if (_close is not null) return _close;
            _closed = true; _lifetime.Cancel();
            Host.Closed -= HostClosed; Host.Failed -= HostFailed;
            var cleanup = _tail.ContinueWith(previous =>
            {
                LosslessVlcRuntime.CleanupStarted(_runtimeOperation);
                try
                {
                    _native?.Stop();
                    if (_native is not null) _native.Hwnd = IntPtr.Zero;
                    _native?.Dispose(); _native = null;
                    _detached.TrySetResult();
                    _media?.Dispose(); _media = null;
                    _source?.Dispose(); _source = null;
                    _lease?.Dispose(); _lease = null;
                    _lifetime.Dispose();
                    return true;
                }
                catch (Exception)
                {
                    // Preserve the exact native owner and lease rather than racing a
                    // second Dispose or reusing an engine with unconfirmed workers.
                    LosslessVlcRuntime.Quarantine(this); return false;
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            Host.RetireAfter(_detached.Task);
            return _close = ObserveCleanupAsync(cleanup);
        }
    }

    private async Task<bool> ObserveCleanupAsync(Task<bool> cleanup)
    {
        try { return await cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { LosslessVlcRuntime.CleanupDelayed(_runtimeOperation); return false; }
    }
}
