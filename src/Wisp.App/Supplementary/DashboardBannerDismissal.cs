using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wisp.App.Supplementary;

internal static class DashboardBannerDismissal
{
    internal static string Key(SupplementaryContentItem item) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            item.Id,
            item.Title,
            item.Message,
            item.Severity,
            item.StartsAt,
            item.ExpiresAt
        }))));

    internal static bool IsDismissed(SupplementaryContentItem item, string? savedKey) =>
        item.Dismissible && string.Equals(Key(item), savedKey, StringComparison.Ordinal);
}
