using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Wisp.App.Clips;

internal static class LosslessThumbnailDecoder
{
    private static DiagnosticState? _lastDiagnostic;
    internal static ThumbnailDiagnostic? LastDiagnostic => Volatile.Read(ref _lastDiagnostic)?.Snapshot();

    internal static async Task<byte[]?> DecodeAsync(ClipEntry clip, string path, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        // Cancellation bounds the caller, not a native destruction call. The worker
        // retains every buffer, delegate, source handle and lease until Stop joins.
        var token = deadline.Token;
        var diagnostic = new DiagnosticState();
        var operation = new object();
        Volatile.Write(ref _lastDiagnostic, diagnostic);
        var worker = Task.Run(() => DecodeOwnedAsync(clip, path, token, diagnostic, operation), CancellationToken.None);
        try { return await worker.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref diagnostic.CallerCancelled, 1);
            _ = ObserveAsync(worker, operation);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static async Task<byte[]?> DecodeOwnedAsync(ClipEntry clip, string path, CancellationToken token, DiagnosticState diagnostic, object operation)
    {
        var owner = new DecoderOwner();
        try
        {
            diagnostic.Stage("acquire-engine");
            owner.Lease = await LosslessVlcRuntime.AcquireAsync(operation, token).ConfigureAwait(false);
            diagnostic.Stage("hold-source");
            owner.Source = LosslessVlcRuntime.HoldSource(clip, path);
            token.ThrowIfCancellationRequested();
            owner.Allocation = Marshal.AllocHGlobal(ClipThumbnailWire.PixelBytes + 31);
            owner.Pixels = new IntPtr((owner.Allocation.ToInt64() + 31) & ~31L);
            var result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var copied = 0;
            diagnostic.Stage("create-player");
            owner.Player = new MediaPlayer(owner.Lease.Engine) { Mute = true, Volume = 0 };
            diagnostic.Stage("set-video-format");
            owner.Player.SetVideoFormat("RV32", ClipThumbnailWire.Width, ClipThumbnailWire.Height, ClipThumbnailWire.Stride);
            diagnostic.Stage("set-video-callbacks");
            owner.Player.SetVideoCallbacks((opaque, planes) =>
            {
                Interlocked.Increment(ref diagnostic.LocksEntered);
                owner.PixelGate.Wait();
                Interlocked.Increment(ref diagnostic.LocksAcquired);
                Marshal.WriteIntPtr(planes, owner.Pixels); return IntPtr.Zero;
            }, (opaque, picture, planes) =>
            {
                Interlocked.Increment(ref diagnostic.UnlocksEntered);
                try
                {
                    if (Interlocked.Exchange(ref copied, 1) != 0) return;
                    var bytes = new byte[ClipThumbnailWire.PixelBytes];
                    Marshal.Copy(owner.Pixels, bytes, 0, bytes.Length);
                    for (var index = 3; index < bytes.Length; index += 4) bytes[index] = 255;
                    Interlocked.Increment(ref diagnostic.FramesCopied);
                    result.TrySetResult(bytes);
                }
                finally { owner.PixelGate.Release(); }
            }, null);
            owner.Player.Playing += (_, _) => Interlocked.Increment(ref diagnostic.PlayingEvents);
            owner.Player.Paused += (_, _) => Interlocked.Increment(ref diagnostic.PausedEvents);
            owner.Player.EncounteredError += (_, _) =>
            {
                Interlocked.Increment(ref diagnostic.ErrorEvents);
                result.TrySetException(new InvalidDataException("The lossless thumbnail could not be decoded."));
            };
            diagnostic.Stage("create-media");
            owner.Media = new Media(owner.Lease.Engine, path, FromType.FromPath, ":start-paused", ":no-audio");
            diagnostic.Stage("attach-media");
            owner.Player.Media = owner.Media;
            diagnostic.Stage("play");
            if (!owner.Player.Play()) return null;
            diagnostic.Stage("wait-paused");
            while (owner.Player.State != VLCState.Paused)
            {
                if (result.Task.IsFaulted) return await result.Task.ConfigureAwait(false);
                await Task.Delay(20, token).ConfigureAwait(false);
            }
            diagnostic.Stage("paused-seek-zero");
            owner.Player.Time = 0;
            diagnostic.Stage("wait-pixels");
            return await result.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Volatile.Write(ref diagnostic.WorkerCancelled, 1); return null; }
        catch (Exception error) { diagnostic.Error(error); return null; } // Optional poster: playback reports actionable decoder failures.
        finally
        {
            try
            {
                diagnostic.Stage("stop-player");
                owner.Player?.Stop();
                diagnostic.Stage("dispose-player");
                owner.Player?.Dispose(); owner.Player = null;
                diagnostic.Stage("dispose-media");
                owner.Media?.Dispose(); owner.Media = null;
                diagnostic.Stage("release-owned-resources");
                if (owner.Allocation != IntPtr.Zero) { Marshal.FreeHGlobal(owner.Allocation); owner.Allocation = owner.Pixels = IntPtr.Zero; }
                owner.Source?.Dispose(); owner.Source = null;
                owner.Lease?.Dispose(); owner.Lease = null;
                owner.PixelGate.Dispose();
                diagnostic.Stage("complete");
            }
            catch (Exception error) { diagnostic.Error(error); diagnostic.Stage("cleanup-failed"); LosslessVlcRuntime.Quarantine(owner); }
        }
    }

    private static async Task ObserveAsync(Task<byte[]?> worker, object operation)
    {
        try { await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { LosslessVlcRuntime.CleanupDelayed(operation); }
        catch (Exception) { }
    }

    private sealed class DecoderOwner
    {
        internal LosslessVlcRuntime.Lease? Lease;
        internal FileStream? Source;
        internal Media? Media;
        internal MediaPlayer? Player;
        internal IntPtr Allocation, Pixels;
        internal readonly SemaphoreSlim PixelGate = new(1, 1);
    }

    // Sanitized, in-memory diagnostics only. Native callbacks update scalar counters;
    // readers never call the player or retain a source path, buffer or native owner.
    internal sealed record ThumbnailDiagnostic(string Stage, int LocksEntered, int LocksAcquired, int UnlocksEntered,
        int FramesCopied, int PlayingEvents, int PausedEvents, int ErrorEvents, bool CallerCancelled,
        bool WorkerCancelled, string? ExceptionType, int ExceptionHResult);

    private sealed class DiagnosticState
    {
        private string _stage = "queued";
        private string? _exceptionType;
        private int _exceptionHResult;
        internal int LocksEntered, LocksAcquired, UnlocksEntered, FramesCopied, PlayingEvents, PausedEvents,
            ErrorEvents, CallerCancelled, WorkerCancelled;
        internal void Stage(string value) => Volatile.Write(ref _stage, value);
        internal void Error(Exception error)
        { Volatile.Write(ref _exceptionHResult, error.HResult); Volatile.Write(ref _exceptionType, error.GetType().Name); }
        internal ThumbnailDiagnostic Snapshot() => new(Volatile.Read(ref _stage), Volatile.Read(ref LocksEntered),
            Volatile.Read(ref LocksAcquired), Volatile.Read(ref UnlocksEntered), Volatile.Read(ref FramesCopied),
            Volatile.Read(ref PlayingEvents), Volatile.Read(ref PausedEvents), Volatile.Read(ref ErrorEvents),
            Volatile.Read(ref CallerCancelled) != 0, Volatile.Read(ref WorkerCancelled) != 0,
            Volatile.Read(ref _exceptionType), Volatile.Read(ref _exceptionHResult));
    }
}
