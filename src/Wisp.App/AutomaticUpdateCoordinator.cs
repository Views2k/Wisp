using Wisp.Update;

namespace Wisp.App;

internal sealed class AutomaticUpdateCoordinator(
    Func<bool> enabled,
    Func<bool> hasUpdate,
    Func<Task<bool>> gameOpen,
    Func<bool> workInProgress,
    Func<Task<VerifiedInstaller?>> prepare,
    Func<VerifiedInstaller, Task<bool>> apply,
    Action<string> status,
    Func<DateTimeOffset>? clock = null,
    Action<string>? waiting = null)
{
    private bool _active;
    private DateTimeOffset _retryAfter;
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    internal async Task RunAsync()
    {
        if (_active || !enabled() || !hasUpdate() || _clock() < _retryAfter) return;
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
            if (installer is null) { _retryAfter = _clock() + ApplicationUpdateCheckPolicy.RetryInterval; return; }
            var gameIsOpen = await gameOpen();
            if (gameIsOpen || workInProgress())
            {
                waiting?.Invoke(gameIsOpen ? "waiting-for-game" : "waiting-for-work");
                status("Update ready. Wisp will restart when Forza is closed and your work is saved.");
                return;
            }
            if (!enabled()) return;
            if (!await apply(installer)) _retryAfter = _clock() + ApplicationUpdateCheckPolicy.RetryInterval;
        }
        catch
        {
            _retryAfter = _clock() + ApplicationUpdateCheckPolicy.RetryInterval;
            throw;
        }
        finally { _active = false; }
    }
}
