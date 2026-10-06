namespace Wisp.App;

internal static class SupportReminderPolicy
{
    private static readonly TimeSpan DailyInterval = TimeSpan.FromHours(24);

    internal static bool IsDue(DateTimeOffset? lastShownUtc, DateTimeOffset now) =>
        lastShownUtc is null || now - lastShownUtc.Value >= DailyInterval;

    internal static TimeSpan DelayUntilDue(DateTimeOffset? lastShownUtc, DateTimeOffset now,
        DateTimeOffset? lastDismissedUtc = null)
    {
        var baseline = lastDismissedUtc is { } dismissed &&
            (lastShownUtc is null || dismissed > lastShownUtc.Value) ? lastDismissedUtc : lastShownUtc;
        if (baseline is null) return TimeSpan.Zero;
        var elapsed = now - baseline.Value;
        // Recheck at most a day later after clock rollback, keeping the timer bounded.
        if (elapsed <= TimeSpan.Zero) return DailyInterval;
        return elapsed >= DailyInterval ? TimeSpan.Zero : DailyInterval - elapsed;
    }
}
