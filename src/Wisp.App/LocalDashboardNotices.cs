using System.Collections.Immutable;
using Wisp.App.Supplementary;

namespace Wisp.App;

// Notices Wisp raises about this PC. They use the dashboard banner slot after any published banner.
// Each one is raised at most once per run, says what the player can do, and closing it removes it for
// the rest of the run without touching the saved dismissal of a published banner.
internal sealed class LocalDashboardNotices
{
    internal const string IdPrefix = "local:";
    private static readonly DateTimeOffset Always = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Never = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly SupplementaryAudience Everyone =
        new(null, null, ["stable", "private"], ["unknown", "steam", "store"], []);
    private readonly object _gate = new();
    private ImmutableList<SupplementaryContentItem> _items = [];

    internal event EventHandler? Changed;

    internal static readonly SupplementaryContentItem SoftwareRendering = Create("software-rendering",
        "Dashboard drawing switched to software",
        "A display rendering failure ended Wisp's last run, so the dashboard now draws in software mode. " +
        "The HUD and clips keep using the graphics card, and the dashboard returns to it after two weeks.");

    internal static readonly SupplementaryContentItem SettingsRestored = Create("settings-restored",
        "Settings restored from the last good copy",
        "Wisp's settings file could not be read, so the last good copy was restored. Check your HUD layout and profiles.");

    internal static SupplementaryContentItem Create(string id, string title, string message) =>
        new(IdPrefix + id, "dashboard-banner", title, message, null, "info", Always, Never, Everyone, Dismissible: true);

    internal static bool IsLocal(SupplementaryContentItem item) =>
        item.Id.StartsWith(IdPrefix, StringComparison.Ordinal);

    internal bool Raise(SupplementaryContentItem item)
    {
        lock (_gate)
        {
            if (_items.Any(existing => existing.Id == item.Id)) return false;
            _items = _items.Add(item);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal SupplementaryContentItem? Current()
    {
        lock (_gate) return _items.FirstOrDefault();
    }

    internal void Dismiss(SupplementaryContentItem item)
    {
        lock (_gate)
        {
            if (!_items.Any(existing => existing.Id == item.Id)) return;
            _items = _items.RemoveAll(existing => existing.Id == item.Id);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
