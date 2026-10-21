namespace Wisp.App;

internal static class ApplicationUpdateCheckPolicy
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);
}

// While the network or the release service is unavailable, every check fails the same way until it clears
// (one DNS outage sent 17 reports in a session). Each such reason is reported once per outage; a successful
// check ends the outage. Other failures, such as a release that fails verification, are reported every time.
internal sealed class ApplicationUpdateCheckOutage
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    internal static bool IsOutageReason(string reason) =>
        reason is "rate-limited" or "http-status" or "timeout" || reason.StartsWith("network-", StringComparison.Ordinal);

    internal bool ShouldReport(string reason) => !IsOutageReason(reason) || _reported.Add(reason);

    internal void Recovered() => _reported.Clear();
}
