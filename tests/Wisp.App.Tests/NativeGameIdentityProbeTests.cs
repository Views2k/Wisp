using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeGameIdentityProbeTests
{
    [Fact]
    public async Task ProbeIsSingleFlightAndDisposalPreventsLatePublication()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var probe = new NativeGameIdentityProbe(() =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            return new(DateTimeOffset.UtcNow, "6.1.2.3", "steam", NativeGameVerification.Detailed(NativeGameVerification.Steam));
        });
        var first = probe.RequestRefresh();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(first, probe.RequestRefresh());
        Assert.Null(probe.Recent);
        probe.Dispose();
        release.Set();
        await first;
        Assert.Null(probe.Recent);
        await probe.RequestRefresh();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ProbeCachesCompletedObservationWithoutRepeatedInspection()
    {
        var calls = 0;
        using var probe = new NativeGameIdentityProbe(() =>
        {
            calls++;
            return new(DateTimeOffset.UtcNow, "6.1.2.3", "unknown", NativeGameVerification.NonOfficialApi);
        });
        NativeGameIdentityObservation? published = null;
        probe.Updated += observation => published = observation;
        await probe.RequestRefresh();
        await probe.RequestRefresh();
        Assert.Equal(1, calls);
        Assert.Same(probe.Recent, published);
        Assert.Equal("nonofficial", probe.Recent?.Verification.Storefront);
        Assert.Equal("6.1.2.3", probe.Recent?.GameBuild);
    }
}
