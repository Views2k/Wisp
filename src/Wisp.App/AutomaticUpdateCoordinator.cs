using Wisp.Update;

namespace Wisp.App;

// What an install handoff attempt tells the coordinator to do next.
internal enum AutomaticUpdateApplyOutcome
{
    // The helper accepted the handoff and Wisp is shutting down to install.
    Started,
    // The handoff did not start this time. Try again after ApplicationUpdateCheckPolicy.RetryInterval.
    Retry,
    // No attempt can succeed until the user runs the full installer: this installation has no
    // usable update helper, or the helper rejected the request or this installation before it was
    // ready. Automatic attempts stop for the rest of the session.
    Unavailable
}

internal sealed class AutomaticUpdateCoordinator(
    Func<bool> enabled,
    Func<bool> hasUpdate,
    Func<Task<bool>> gameOpen,
    Func<bool> workInProgress,
    Func<Task<VerifiedInstaller?>> prepare,
    Func<VerifiedInstaller, Task<AutomaticUpdateApplyOutcome>> apply,
    Action<string> status,
    Func<DateTimeOffset>? clock = null,
    Action<string>? waiting = null,
    Func<string?>? pendingVersion = null,
    Func<bool>? networkAvailable = null)
{
    // A download or handoff that keeps failing the same way will not succeed later in this session
    // either: after MaximumRetries for one release, automatic attempts stop for that release and the
    // dashboard points at the full installer. A newer release published later gets a fresh budget.
    internal const int MaximumRetries = 3;
    private bool _active;
    private bool _handoffUnavailable;
    private int _retries;
    private string? _retriedVersion;
    private string? _exhaustedVersion;
    private DateTimeOffset _retryAfter;
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<bool> _networkAvailable = networkAvailable ?? (() => ApplicationUpdateCheckPolicy.NetworkAvailable());

    // Set once a handoff reports that the update helper is missing or rejected this installation.
    // The coordinator then stays idle for the rest of the session; a manual install from the
    // dashboard can still try again.
    internal bool HandoffUnavailable => _handoffUnavailable;

    // True for the release whose handoff failed MaximumRetries times in this session.
    internal bool IsExhausted(string? version) => version is not null && version == _exhaustedVersion;

    // Counts one failed attempt for the release; the next attempt waits RetryInterval, and the
    // MaximumRetries-th failure exhausts the release for this session.
    private void RecordRetry(string? version)
    {
        if (_retriedVersion != version)
        {
            _retriedVersion = version;
            _retries = 0;
        }
        if (++_retries >= MaximumRetries) _exhaustedVersion = version;
        else _retryAfter = _clock() + ApplicationUpdateCheckPolicy.RetryInterval;
    }

    internal async Task RunAsync()
    {
        if (_active || _handoffUnavailable || !enabled() || !hasUpdate() || _clock() < _retryAfter ||
            IsExhausted(pendingVersion?.Invoke())) return;
        _active = true;
        try
        {
            if (await gameOpen())
            {
                waiting?.Invoke("waiting-for-game");
                status("Update ready. Wisp will update and restart after you close Forza.");
                return;
            }
            if (!enabled()) return;
            if (workInProgress())
            {
                waiting?.Invoke("waiting-for-work");
                status("Update ready. Wisp will restart after your current work is saved.");
                return;
            }
            var installer = await prepare();
            if (!enabled()) return;
            if (installer is null)
            {
                // A download that could not happen while the PC has no connection is a wait, as for the
                // check, not one of the release's attempts.
                if (_networkAvailable()) RecordRetry(pendingVersion?.Invoke());
                else _retryAfter = _clock() + ApplicationUpdateCheckPolicy.RetryInterval;
                return;
            }
            if (IsExhausted(installer.Version.ToString())) { _retryAfter = _clock() + ApplicationUpdateCheckPolicy.MinimumInterval; return; }
            var gameIsOpen = await gameOpen();
            if (gameIsOpen || workInProgress())
            {
                waiting?.Invoke(gameIsOpen ? "waiting-for-game" : "waiting-for-work");
                status("Update ready. Wisp will restart when Forza is closed and your work is saved.");
                return;
            }
            if (!enabled()) return;
            switch (await apply(installer))
            {
                case AutomaticUpdateApplyOutcome.Started:
                    break;
                case AutomaticUpdateApplyOutcome.Unavailable:
                    _handoffUnavailable = true;
                    break;
                default:
                    RecordRetry(installer.Version.ToString());
                    break;
            }
        }
        catch
        {
            _retryAfter = _clock() + ApplicationUpdateCheckPolicy.RetryInterval;
            throw;
        }
        finally { _active = false; }
    }
}
