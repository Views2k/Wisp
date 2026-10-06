namespace Wisp.App;

internal static class SupportReminderPolicy
{
    internal static bool IsDue(DateTimeOffset? lastShownUtc, DateTimeOffset now) =>
        lastShownUtc is null || now - lastShownUtc.Value >= TimeSpan.FromHours(24);
}
