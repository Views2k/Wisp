namespace Wisp.App.Clips;

internal static class LosslessMpvRuntime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object Sync = new();
    private static readonly LosslessVlcRuntime.CleanupOwnership Ownership = new();
    private static object? _quarantined;
    private static bool _shuttingDown;
    private static Task? _shutdown;
    internal static string CleanupStatus { get { lock (Sync) return _quarantined is not null ? "cleanup-failed-restart-required" : Ownership.Status; } }
    internal static string RuntimeStage { get { lock (Sync) return Ownership.Stage; } }
    internal static bool CleanupPending { get { lock (Sync) return _quarantined is not null || Ownership.Pending; } }
    internal static void SetStage(object operation, string stage) { lock (Sync) Ownership.SetStage(operation, stage); }
    internal static void CleanupDelayed(object operation) { lock (Sync) Ownership.MarkPending(operation); }
    internal static void Quarantine(object owner) { lock (Sync) _quarantined = owner; }

    internal static async Task<Lease> AcquireAsync(object operation, CancellationToken token)
    {
        lock (Sync) CheckAvailable();
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (Sync) { CheckAvailable(); Ownership.Begin(operation); }
            token.ThrowIfCancellationRequested();
            return new Lease(operation);
        }
        catch { lock (Sync) { Ownership.Complete(operation); Gate.Release(); } throw; }
    }
    private static void CheckAvailable()
    {
        ObjectDisposedException.ThrowIf(_shuttingDown, typeof(LosslessMpvRuntime));
        if (_quarantined is not null || Ownership.Pending)
            throw new InvalidOperationException("Decoder cleanup is still pending. Restart Wisp before opening another lossless clip.");
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
                Gate.Release();
            });
        }
        try { await shutdown.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }
    internal sealed class Lease(object operation) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (Sync) { Ownership.Complete(operation); Gate.Release(); }
        }
    }
}
