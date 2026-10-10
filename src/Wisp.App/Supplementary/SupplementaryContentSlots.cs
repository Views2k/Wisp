namespace Wisp.App.Supplementary;

// A missing slot means the existing local presentation. Signed content never controls updater actions or popup cadence.
internal sealed record SupplementaryContentSlots(SupplementaryContentItem? Note, SupplementaryContentItem? Banner,
    DateTimeOffset? NextChange)
{
    internal static SupplementaryContentSlots Select(SupplementaryContentSnapshot content, SupplementaryAudienceContext audience,
        DateTimeOffset now)
    {
        if (!content.IsCurrent(now)) return new(null, null, null);
        var items = content.Items.Where(i => i.Kind is "support-note" or "dashboard-banner" && i.Audience.Matches(audience)).ToArray();
        var current = items.Where(i => i.StartsAt <= now && i.ExpiresAt > now).ToArray();
        var next = items.SelectMany(i => new[] { i.StartsAt, i.ExpiresAt }).Append(content.ExpiresAt)
            .Where(t => t > now).Min();
        return new(current.FirstOrDefault(i => i.Kind == "support-note"),
            current.FirstOrDefault(i => i.Kind == "dashboard-banner"), items.Length == 0 ? null : next);
    }
}
