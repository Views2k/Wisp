using Wisp.Core;

namespace Wisp.App;

// Owned by the controller's dispatcher. This schedules checks, never build acceptance.
internal sealed class NativeCompatibilityUpdateSchedule
{
    internal static readonly TimeSpan MinimumAutomaticInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] RetryIntervals =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)
    ];

    private DateTimeOffset _routineAt = DateTimeOffset.MinValue;
    private DateTimeOffset _unsupportedAt = DateTimeOffset.MaxValue;
    private DateTimeOffset _automaticAllowedAt = DateTimeOffset.MinValue;
    private string? _unsupportedIdentity;
    private bool _unsupported;
    private bool _checking;
    private int _retryIndex;

    internal void ObserveBuild(NativeAssistProviderStatus status, string identity, DateTimeOffset now)
    {
        _unsupported = status == NativeAssistProviderStatus.UnsupportedBuild;
        if (_unsupported && (!string.Equals(_unsupportedIdentity, identity, StringComparison.Ordinal) ||
            _unsupportedAt == DateTimeOffset.MaxValue))
        {
            _unsupportedIdentity = identity;
            _unsupportedAt = now;
        }
        else if (status == NativeAssistProviderStatus.Ready && _unsupportedAt != DateTimeOffset.MaxValue)
        {
            _unsupportedAt = DateTimeOffset.MaxValue;
            _retryIndex = 0;
        }
        // Menus, absent telemetry and game restarts must not restart the retry cadence.
    }

    internal bool IsDue(DateTimeOffset now) => !_checking && now >= _automaticAllowedAt &&
        (now >= _routineAt || _unsupported && now >= _unsupportedAt);

    // Manual checks bypass the automatic delay and join the transport's existing operation.
    // Only its first caller owns the completion that advances this schedule.
    internal bool TryBeginCheck(DateTimeOffset now)
    {
        if (_checking) return false;
        _checking = true;
        _automaticAllowedAt = now + MinimumAutomaticInterval;
        return true;
    }

    internal void CompleteCheck(DateTimeOffset now, bool success)
    {
        _checking = false;
        var retry = RetryIntervals[_retryIndex];
        var waitingForBuild = _unsupportedAt != DateTimeOffset.MaxValue;
        _routineAt = now + (success ? TimeSpan.FromDays(1) : retry);
        if (waitingForBuild) _unsupportedAt = now + retry;
        if (!success || waitingForBuild)
            _retryIndex = Math.Min(_retryIndex + 1, RetryIntervals.Length - 1);
        else
            _retryIndex = 0;
    }
}
