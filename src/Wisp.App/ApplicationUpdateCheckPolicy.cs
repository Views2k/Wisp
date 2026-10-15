namespace Wisp.App;

internal static class ApplicationUpdateCheckPolicy
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);
}
