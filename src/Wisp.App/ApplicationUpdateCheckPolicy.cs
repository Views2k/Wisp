namespace Wisp.App;

internal static class ApplicationUpdateCheckPolicy
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(24);

    internal static bool IsDue(
        bool automaticChecksEnabled,
        DateTimeOffset? lastCheckUtc,
        DateTimeOffset nowUtc) =>
        automaticChecksEnabled &&
        (lastCheckUtc is null || nowUtc >= lastCheckUtc.Value + MinimumInterval);
}
