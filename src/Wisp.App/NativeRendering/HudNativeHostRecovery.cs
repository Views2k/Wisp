namespace Wisp.App.NativeRendering;

internal enum HudNativeHostRecoveryAction { Retry, Rebuild, Retire }

// Bounded recovery of a HUD window's native host from failed host-window Win32 calls. The layout
// window, its owner or its monitor can change under such a call while a display is added or
// removed, the game attaches or the player returns from another window. The renderer is intact,
// so the host window is synchronized again after HostRetryDelay, up to HostRetries times in one
// episode. An episode whose retries run out releases the host window and its renderer instead of
// retiring the HUD until Wisp restarts: the same window presents normally at the next transition
// on the same PC, so the host is built again on the next game attach or foreground return, once
// per such event. Only MaximumFailedEpisodes consecutive exhausted episodes, with no frame
// presented between them, retire the HUD.
internal sealed class HudNativeHostRecovery
{
    internal const int HostRetries = 3;
    internal static readonly TimeSpan HostRetryDelay = TimeSpan.FromMilliseconds(500);
    internal const int MaximumFailedEpisodes = 3;
    private int _retries, _failedEpisodes;
    private bool _rebuildPending;

    // Failed calls in the current episode; zero before an episode's first failure.
    internal int Retries => _retries;
    internal int FailedEpisodes => _failedEpisodes;
    // True while the released host waits for the next attach or foreground return.
    internal bool RebuildPending => _rebuildPending;

    // One failed host-window call: synchronize again within the episode, release the host and
    // rebuild it on the next event once the retries ran out, or retire the HUD after
    // MaximumFailedEpisodes such episodes.
    internal HudNativeHostRecoveryAction Fail()
    {
        if (++_retries <= HostRetries) return HudNativeHostRecoveryAction.Retry;
        _retries = 0;
        if (++_failedEpisodes >= MaximumFailedEpisodes) return HudNativeHostRecoveryAction.Retire;
        _rebuildPending = true;
        return HudNativeHostRecoveryAction.Rebuild;
    }

    // Called on each attach or foreground return. True once per pending rebuild, so one event
    // starts at most one rebuild attempt; that attempt's own failures form the next episode.
    internal bool TryBeginRebuild()
    {
        if (!_rebuildPending) return false;
        _rebuildPending = false;
        return true;
    }

    // A presented frame proves the host window works; the next failure starts a first episode. A
    // host waiting for its rebuild has no renderer, so a late frame from the released one does not count.
    internal void Presented()
    {
        if (_rebuildPending) return;
        _retries = 0;
        _failedEpisodes = 0;
    }
}
