using System.IO;

namespace Wisp.App.Clips;

internal static class HdrThumbnailDecoder
{
    // A small SDR poster, not the HDR player's output. Tone mapping operates
    // on linear floating-point RGB before the final sRGB transfer/quantization.
    internal const string Filter = "lavfi=[zscale=w=320:h=180:transfer=linear:npl=100,format=gbrpf32le,zscale=primaries=bt709,tonemap=mobius:param=0.3:desat=2,zscale=transfer=iec61966-2-1,format=bgra]";
    private static DiagnosticState? _lastDiagnostic;
    internal static ThumbnailDiagnostic? LastDiagnostic => Volatile.Read(ref _lastDiagnostic)?.Snapshot();

    internal static async Task<byte[]?> DecodeAsync(ClipEntry clip, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!clip.Media.HdrVideo) throw new InvalidDataException("An HDR preview requires an HDR clip.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
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

    private static async Task<byte[]?> DecodeOwnedAsync(ClipEntry clip, string path, CancellationToken token,
        DiagnosticState diagnostic, object operation)
    {
        var owner = new DecoderOwner();
        byte[]? pixels = null;
        try
        {
            diagnostic.Stage("acquire-engine");
            owner.Lease = await LosslessMpvRuntime.AcquireAsync(operation, token).ConfigureAwait(false);
            diagnostic.Stage("hold-source");
            owner.Source = LosslessVlcRuntime.HoldSource(clip, path);
            token.ThrowIfCancellationRequested();
            diagnostic.Stage("initialize-native-library");
            owner.Native = new LosslessMpvNative();
            owner.Native.InitializeHeadless([("vid", "auto"), ("aid", "no"), ("pause", "yes"), ("screenshot-sw", "yes"),
                ("demuxer-max-bytes", "268435456"), ("vd-lavc-threads", "2"), ("vd-queue-enable", "no"), ("vf", Filter)]);
            diagnostic.Stage("prepare-sdr-poster");
            owner.Native.Run("loadfile", path, "replace");
            var native = owner.Native;
            while (native.Restarts == 0 || native.Integer("video-out-params/w") != ClipThumbnailWire.Width ||
                native.Integer("video-out-params/h") != ClipThumbnailWire.Height)
            {
                token.ThrowIfCancellationRequested(); native.DrainEvents();
                await Task.Delay(20, token).ConfigureAwait(false);
            }
            if (native.Integer("video-dec-params/w") != clip.Media.Width || native.Integer("video-dec-params/h") != clip.Media.Height ||
                !LosslessClipPlayer.MatchesDecodedFormat(clip.Media, native.Text("video-format"), native.Text("video-dec-params/pixelformat"),
                    native.Text("video-dec-params/gamma"), native.Text("video-dec-params/colormatrix"),
                    native.Text("video-dec-params/colorlevels"), native.Text("video-dec-params/primaries")) ||
                !IsSdrOutput(native.Integer("video-out-params/w"), native.Integer("video-out-params/h"),
                    native.Text("video-out-params/gamma"), native.Text("video-out-params/primaries"),
                    native.Text("video-out-params/colormatrix"), native.Text("video-out-params/colorlevels")))
                throw new InvalidDataException("The HDR preview color format is invalid.");
            token.ThrowIfCancellationRequested();
            diagnostic.Stage("copy-sdr-poster");
            var image = native.ReadDiagnosticFrame();
            if (image.Width != ClipThumbnailWire.Width || image.Height != ClipThumbnailWire.Height || image.Bgra.Length != ClipThumbnailWire.PixelBytes)
                throw new InvalidDataException("The HDR preview dimensions are invalid.");
            for (var offset = 3; offset < image.Bgra.Length; offset += 4) image.Bgra[offset] = 255;
            pixels = image.Bgra;
        }
        catch (OperationCanceledException) { Volatile.Write(ref diagnostic.WorkerCancelled, 1); }
        catch (Exception error) when (error is not OutOfMemoryException) { diagnostic.Error(error); }
        finally
        {
            // Never release source/lease until native destruction has joined.
            // A delayed worker retains all ownership after its caller cancels.
            try
            {
                diagnostic.Stage("stop-player"); LosslessMpvRuntime.SetStage(operation, "stopping");
                owner.Native?.Close(); owner.Native = null;
                owner.Source?.Dispose(); owner.Source = null;
                owner.Lease?.Dispose(); owner.Lease = null;
                diagnostic.Stage("complete");
            }
            catch (Exception error)
            {
                pixels = null; diagnostic.Error(error); diagnostic.Stage("cleanup-failed");
                LosslessMpvRuntime.Quarantine(owner);
            }
        }
        return pixels;
    }

    internal static bool IsSdrOutput(long? width, long? height, string? gamma, string? primaries, string? matrix, string? range) =>
        width == ClipThumbnailWire.Width && height == ClipThumbnailWire.Height && gamma == "srgb" && primaries == "bt.709" &&
        matrix == "rgb" && range == "full";

    private static async Task ObserveAsync(Task<byte[]?> worker, object operation)
    {
        try { await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { LosslessMpvRuntime.CleanupDelayed(operation); }
        catch (Exception) { }
    }

    private sealed class DecoderOwner
    {
        internal LosslessMpvRuntime.Lease? Lease;
        internal FileStream? Source;
        internal LosslessMpvNative? Native;
    }

    internal sealed record ThumbnailDiagnostic(string Stage, bool CallerCancelled, bool WorkerCancelled, string? Failure, int HResult);
    private sealed class DiagnosticState
    {
        private string _stage = "queued";
        private string? _failure;
        private int _hresult;
        internal int CallerCancelled, WorkerCancelled;
        internal void Stage(string value) => Volatile.Write(ref _stage, value);
        internal void Error(Exception error)
        {
            Volatile.Write(ref _failure, error is LosslessMpvException native ? native.Code : "preview-failed");
            Volatile.Write(ref _hresult, error.HResult);
        }
        internal ThumbnailDiagnostic Snapshot() => new(Volatile.Read(ref _stage), Volatile.Read(ref CallerCancelled) != 0,
            Volatile.Read(ref WorkerCancelled) != 0, Volatile.Read(ref _failure), Volatile.Read(ref _hresult));
    }
}
