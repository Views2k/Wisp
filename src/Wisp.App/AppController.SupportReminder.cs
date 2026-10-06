namespace Wisp.App;

public sealed partial class AppController
{
    internal bool TryRecordSupportReminderShown(DateTimeOffset now)
    {
        if (_disposed || Settings.RequiresSetup ||
            !SupportReminderPolicy.IsDue(Settings.LastSupportReminderShownUtc, now)) return false;
        var previous = Settings.LastSupportReminderShownUtc;
        Settings.LastSupportReminderShownUtc = now.ToUniversalTime();
        if (TrySavePendingSettings()) return true;
        // Skip the reminder when its cooldown cannot be remembered.
        Settings.LastSupportReminderShownUtc = previous;
        return false;
    }
}
