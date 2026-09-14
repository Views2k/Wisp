using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ForzaDiscoveryTests
{
    [Fact]
    public void UnrelatedOwnersNeverNeedExecutablePathsAndNamesAreReadOnce()
    {
        var nameReads = 0;
        var pathReads = 0;
        var cache = new ForzaWindowOwnerCache(_ => { nameReads++; return "browser"; },
            _ => { pathReads++; return @"X:\Games\ForzaHorizon6\browser.exe"; });
        for (var index = 0; index < 60; index++)
            Assert.False(cache.Matches(42, index == 59 ? "Forza Horizon 6" : "Another window"));
        Assert.Null(cache.GetMatchedExecutablePath(42));
        Assert.Equal(1, nameReads);
        Assert.Equal(0, pathReads);
    }

    [Theory]
    [InlineData("ApplicationFrameHost")]
    [InlineData("GameHost")]
    [InlineData("GameLaunchHelper")]
    [InlineData("XGameHelper")]
    public void CachedOwnerStillChecksEachWindowCaption(string processName)
    {
        var nameReads = 0;
        var pathReads = 0;
        var cache = new ForzaWindowOwnerCache(_ => { nameReads++; return processName; },
            _ => { pathReads++; return @"X:\Games\ForzaHorizon6\GameHost.exe"; });
        Assert.False(cache.Matches(42, "Another game"));
        Assert.False(cache.Matches(42, ""));
        Assert.Null(cache.GetMatchedExecutablePath(42));
        Assert.Equal(0, pathReads);
        Assert.True(cache.Matches(42, "Forza Horizon 6"));
        Assert.NotNull(cache.GetMatchedExecutablePath(42));
        Assert.NotNull(cache.GetMatchedExecutablePath(42));
        Assert.Equal(1, nameReads);
        Assert.Equal(1, pathReads);
    }

    [Fact]
    public void MissingOwnerMetadataIsCachedAndStaysFailClosed()
    {
        var reads = 0;
        var cache = new ForzaWindowOwnerCache(_ => { reads++; return null; },
            _ => throw new InvalidOperationException("An unidentified owner must not be queried."));
        Assert.False(cache.Matches(42, "Forza Horizon 6"));
        Assert.False(cache.Matches(42, "Forza Horizon 6"));
        Assert.False(cache.Matches(0, "Forza Horizon 6"));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void ExactGameNameStillMatchesWithoutCaptionAndMissingPathIsSafe()
    {
        var paths = 0;
        var cache = new ForzaWindowOwnerCache(_ => "forzahorizon6", _ => { paths++; return null; });
        Assert.True(cache.Matches(42, ""));
        Assert.Null(cache.GetMatchedExecutablePath(42));
        Assert.Null(cache.GetMatchedExecutablePath(42));
        Assert.Equal(1, paths);
    }

    [Fact]
    public void SlowDiscoveryDoesNotBlockCallerOverlapOrPublishWithoutAPoll()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var calls = 0;
        var workerThread = 0;
        var callerThread = Environment.CurrentManagedThreadId;
        var expected = new ForzaProcessSnapshot([42], new(StringComparer.OrdinalIgnoreCase));
        var discovery = new ForzaProcessDiscovery(() =>
        {
            Interlocked.Increment(ref calls);
            workerThread = Environment.CurrentManagedThreadId;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            finished.Set();
            return expected;
        });
        var now = DateTimeOffset.UtcNow;
        try
        {
            Assert.False(discovery.Refresh(now));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.NotEqual(callerThread, workerThread);
            for (var index = 0; index < 100; index++)
                Assert.False(discovery.Refresh(now + TimeSpan.FromMinutes(index)));
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Empty(discovery.Current.ProcessIds);
            release.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Empty(discovery.Current.ProcessIds);
            Assert.True(SpinWait.SpinUntil(() => discovery.Refresh(now), TimeSpan.FromSeconds(3)));
            Assert.Same(expected, discovery.Current);
            Assert.False(discovery.Refresh(now + TimeSpan.FromSeconds(1.99)));
            Assert.False(discovery.IsSearching);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            release.Set();
            // Cleanup must still release and drain the worker after test cancellation.
            SpinWait.SpinUntil(() => finished.IsSet, TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public void FailedRefreshClearsPreviousIdentityAndRetriesOnlyAtNextInterval()
    {
        var calls = 0;
        var discovery = new ForzaProcessDiscovery(() =>
            Interlocked.Increment(ref calls) == 1
                ? new ForzaProcessSnapshot([42], new(StringComparer.OrdinalIgnoreCase))
                : throw new InvalidOperationException("Synthetic discovery failure"));
        var now = DateTimeOffset.UtcNow;
        discovery.Refresh(now);
        Assert.True(SpinWait.SpinUntil(() => discovery.Refresh(now), TimeSpan.FromSeconds(3)));
        Assert.Contains(42, discovery.Current.ProcessIds);
        var retryAt = now + TimeSpan.FromSeconds(2);
        discovery.Refresh(retryAt);
        Assert.True(SpinWait.SpinUntil(() => discovery.Refresh(retryAt), TimeSpan.FromSeconds(3)));
        Assert.Empty(discovery.Current.ProcessIds);
        Assert.Empty(discovery.Current.ExecutableDirectories);
        Assert.False(discovery.Refresh(retryAt + TimeSpan.FromSeconds(1)));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public void ResultCompletedDuringALongUiPauseCannotRestoreStaleGameIdentity()
    {
        using var finished = new ManualResetEventSlim();
        var calls = 0;
        var discovery = new ForzaProcessDiscovery(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                finished.Set();
                return new ForzaProcessSnapshot([42], new(StringComparer.OrdinalIgnoreCase));
            }
            return ForzaProcessSnapshot.Empty();
        });
        var now = DateTimeOffset.UtcNow;
        discovery.Refresh(now);
        Assert.True(finished.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.True(SpinWait.SpinUntil(() => discovery.Refresh(now + TimeSpan.FromMinutes(30)), TimeSpan.FromSeconds(3)));
        Assert.Empty(discovery.Current.ProcessIds);
        Assert.True(discovery.IsSearching);
    }

    [Fact]
    public void LimitedImageQueryReturnsOwnExecutableAndHandlesMissingProcesses()
    {
        var path = ForzaFocusService.TryGetExecutablePath(Environment.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.Equal(Environment.ProcessPath, path, ignoreCase: true);
        Assert.Null(ForzaFocusService.TryGetExecutablePath(0));
        Assert.Null(ForzaFocusService.TryGetExecutablePath(-1));
    }
}
