using System.IO;
using LibVLCSharp.Shared;

namespace Wisp.App.Clips;

internal static class LosslessVlcRuntime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object Sync = new();
    private static LibVLC? _engine;
    private static Task? _shutdown;
    private static bool _shuttingDown;
    private static object? _quarantined;
    private static readonly CleanupOwnership Ownership = new();
    private static string? _cleanupOverride;
    internal static string CleanupStatus { get { lock (Sync) return _cleanupOverride ?? Ownership.Status; } }
    internal static string RuntimeStage { get { lock (Sync) return Ownership.Stage; } }
    internal static bool CleanupPending { get { lock (Sync) return Ownership.Pending; } }
    internal static void CleanupStarted(object operation) { lock (Sync) Ownership.SetStage(operation, "stopping"); }
    internal static void CleanupDelayed(object operation) { lock (Sync) Ownership.MarkPending(operation); }
    internal static void Quarantine(object owner)
    {
        lock (Sync) { _quarantined = owner; _cleanupOverride = "cleanup-failed-restart-required"; }
    }

    internal static async Task<Lease> AcquireAsync(object operation, CancellationToken cancellationToken)
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_shuttingDown, typeof(LosslessVlcRuntime));
            if (_quarantined is not null) throw new InvalidOperationException("The lossless decoder requires a restart.");
            ThrowIfCleanupPending();
        }
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (Sync)
            {
                ObjectDisposedException.ThrowIf(_shuttingDown, typeof(LosslessVlcRuntime));
                if (_quarantined is not null) throw new InvalidOperationException("The lossless decoder requires a restart.");
                ThrowIfCleanupPending();
                Ownership.Begin(operation);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_engine is null)
            {
                var native = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
                if (!File.Exists(Path.Combine(native, "libvlc.dll")) || !File.Exists(Path.Combine(native, "libvlccore.dll")))
                    throw new InvalidOperationException("The lossless playback components are missing. Reinstall Wisp to restore them.");
                // App-local replaceable libraries: no system search, runtime download or hash lock.
                SetStage(operation, "initialize-native-library");
                LibVLCSharp.Shared.Core.Initialize(native);
                SetStage(operation, "create-playback-engine");
                _engine = new LibVLC("--ignore-config", "--quiet", "--no-osd", "--no-video-title-show", "--no-snapshot-preview");
            }
            SetStage(operation, "engine-ready");
            return new Lease(_engine, operation);
        }
        catch { lock (Sync) { Ownership.Complete(operation); Gate.Release(); } throw; }
    }

    private static void SetStage(object operation, string stage) { lock (Sync) Ownership.SetStage(operation, stage); }
    private static void ThrowIfCleanupPending()
    {
        if (Ownership.Pending)
            throw new InvalidOperationException("Lossless decoder cleanup is still pending. Restart Wisp before opening another lossless clip.");
    }

    internal static async Task<bool> ShutdownAsync()
    {
        Task shutdown;
        lock (Sync)
        {
            _shuttingDown = true;
            shutdown = _shutdown ??= Task.Run(async () =>
            {
                await Gate.WaitAsync().ConfigureAwait(false);
                try { _engine?.Dispose(); _engine = null; }
                finally { Gate.Release(); }
            });
        }
        try { await shutdown.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); return true; }
        catch (TimeoutException)
        {
            lock (Sync) if (!shutdown.IsCompleted) _cleanupOverride = "cleanup-pending-restart-may-be-required";
            return false; // Retain ownership; never race native cleanup.
        }
        catch (Exception) { lock (Sync) _cleanupOverride = "engine-cleanup-failed"; return false; }
    }

    internal static FileStream HoldSource(ClipEntry clip, string path)
    {
        if (!clip.Media.LosslessVideo || !clip.Recording.LosslessVideo) throw new InvalidDataException("This clip is not lossless video.");
        _ = ClipThumbnailWire.Request(path, clip, 1);
        ClipLibrary.CheckPath(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != clip.Media.FileBytes) throw new InvalidDataException("The saved clip has changed.");
            ClipLibrary.CheckPath(path);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    internal sealed class Lease(LibVLC engine, object operation) : IDisposable
    {
        private int _released;
        internal LibVLC Engine { get; } = engine;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (Sync) { Ownership.Complete(operation); Gate.Release(); }
        }
    }

    // All production access is under Sync. Kept independent of native libraries so
    // late-timeout/release ordering can be verified without starting a decoder.
    internal sealed class CleanupOwnership
    {
        private object? _active;
        internal bool Pending { get; private set; }
        internal string Stage { get; private set; } = "idle";
        internal string Status
        {
            get
            {
                if (Pending) return "cleanup-pending-restart-may-be-required";
                if (Stage == "stopping") return "stopping";
                if (_active is not null) return "active";
                return _released ? "complete" : "idle";
            }
        }
        private bool _released;
        internal void Begin(object operation)
        {
            if (_active is not null) throw new InvalidOperationException("A decoder operation still owns the runtime.");
            _active = operation; Pending = false; Stage = "acquired";
        }
        internal void SetStage(object operation, string stage) { if (ReferenceEquals(_active, operation)) Stage = stage; }
        internal bool MarkPending(object operation)
        {
            if (!ReferenceEquals(_active, operation)) return false;
            Pending = true; return true;
        }
        internal void Complete(object operation)
        {
            if (!ReferenceEquals(_active, operation)) return;
            _active = null; Pending = false; Stage = "idle"; _released = true;
        }
    }
}
