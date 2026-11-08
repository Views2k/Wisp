namespace Wisp.App;

internal static class ApplicationUpdateCheckPolicy
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);

    // Whether any network interface other than loopback is up. Replaceable for tests.
    internal static Func<bool> NetworkAvailable { get; set; } = () =>
    {
        try { return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); }
        catch (Exception) { return true; }
    };
}

// While the network or the release service is unavailable, every check fails the same way until it clears
// (one DNS outage sent 17 reports in a session). Each such reason is reported once per outage; a successful
// check ends the outage. Other failures, such as a release that fails verification, are reported every time.
//
// Until the first check in a process succeeds, an automatic check that got no response from the service is
// deferred instead of reported: Wisp often starts with Windows before the PC is online, and an adapter that
// is up (NetworkAvailable) does not mean the release service is reachable yet. In one week 19 of 50 network
// failure reports came 0-30 s after launch from PCs that were still offline.
internal sealed class ApplicationUpdateCheckOutage
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private bool _succeeded;

    internal static bool IsOutageReason(string reason) =>
        reason is "rate-limited" or "http-status" or "timeout" || reason.StartsWith("network-", StringComparison.Ordinal);

    // Reasons that mean no response came back at all. A rate limit or an HTTP status means the service was reached.
    internal static bool IsConnectionReason(string reason) =>
        reason == "timeout" || reason.StartsWith("network-", StringComparison.Ordinal);

    internal bool ShouldDefer(string reason) => !_succeeded && IsConnectionReason(reason);

    internal bool ShouldReport(string reason) => !IsOutageReason(reason) || _reported.Add(reason);

    internal void Recovered()
    {
        _succeeded = true;
        _reported.Clear();
    }
}
