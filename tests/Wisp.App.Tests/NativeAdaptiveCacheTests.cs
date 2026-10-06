using System.Security.Cryptography;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeAdaptiveCacheTests
{
    private static int _nextProcess = 500_000;

    [Theory]
    [InlineData(59, true)]
    [InlineData(60, false)]
    [InlineData(61, false)]
    public void TemporarilyPartialSupportExpiresAndRequiresRediscovery(int elapsedSeconds, bool reusable)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var reads = 0;
        bool Read(ulong address, Span<byte> destination) { reads++; bytes.CopyTo(destination); return true; }
        var proof = new NativeAdaptiveProof([new(0x1000, bytes.Length, SHA256.HashData(bytes))]);
        var entry = new NativeAdaptiveCompatibility.Entry(NativeHudBuildContract.BuiltIn, proof, start.AddMinutes(1), string.Empty);

        Assert.Equal(reusable, entry.TryReuse(Read, 0x140000000, new NativeAdaptiveBudget(TimeSpan.FromSeconds(5)),
            start.AddSeconds(elapsedSeconds), out var pack, out _));
        Assert.Equal(reusable ? NativeHudBuildContract.BuiltIn : null, pack);
        Assert.Equal(reusable ? 1 : 0, reads);
    }

    [Theory]
    [InlineData(false, 59, true)]
    [InlineData(false, 60, false)]
    [InlineData(true, 60, true)]
    [InlineData(true, 86400, true)]
    public void NegativeCacheDistinguishesTransientAndStructuralFailures(bool structural, int seconds, bool reusable)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        static bool UnexpectedRead(ulong address, Span<byte> destination) => throw new InvalidOperationException();
        var entry = new NativeAdaptiveCompatibility.Entry(null, null,
            structural ? DateTimeOffset.MaxValue : start.AddMinutes(1), "Unavailable reader");
        Assert.Equal(reusable, entry.TryReuse(UnexpectedRead, 0x140000000, new NativeAdaptiveBudget(TimeSpan.FromSeconds(5)),
            start.AddSeconds(seconds), out var pack, out var reason));
        Assert.Null(pack);
        Assert.Equal("Unavailable reader", reason);
    }

    [Fact]
    public void FullCachedSupportStillRequiresItsCodeEvidenceToMatch()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        bool Read(ulong address, Span<byte> destination) { bytes.CopyTo(destination); return true; }
        var proof = new NativeAdaptiveProof([new(0x1000, bytes.Length, SHA256.HashData(bytes))]);
        var entry = new NativeAdaptiveCompatibility.Entry(NativeHudBuildContract.BuiltIn, proof, DateTimeOffset.MaxValue, string.Empty);
        Assert.True(entry.TryReuse(Read, 0x140000000, new NativeAdaptiveBudget(TimeSpan.FromSeconds(5)), DateTimeOffset.UtcNow, out var pack, out _));
        Assert.Same(NativeHudBuildContract.BuiltIn, pack);
        bytes[0] ^= 1;
        Assert.False(entry.TryReuse(Read, 0x140000000, new NativeAdaptiveBudget(TimeSpan.FromSeconds(5)), DateTimeOffset.UtcNow, out pack, out _));
        Assert.Null(pack);
    }

    [Fact]
    public void ChangedLayoutReturnsUnavailableAndDoesNotRescanTheSameProcess()
    {
        var identity = Identity();
        var reads = 0;
        bool Read(ulong address, Span<byte> destination)
        {
            reads++;
            destination.Clear();
            return true;
        }

        Assert.False(NativeAdaptiveCompatibility.TryResolve(identity, 1, default, Package, Read, out var pack, out var reason, TestContext.Current.CancellationToken));
        Assert.Null(pack);
        Assert.Equal("Adaptive image header", reason);
        var initialReads = reads;
        Assert.True(initialReads > 0);
        Assert.False(NativeAdaptiveCompatibility.TryResolve(identity, 1, default, Package, Read, out _, out _, TestContext.Current.CancellationToken));
        Assert.Equal(initialReads, reads);

        Assert.False(NativeAdaptiveCompatibility.TryResolve(identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 },
            1, default, Package, Read, out _, out _, TestContext.Current.CancellationToken));
        Assert.True(reads > initialReads);
    }

    [Fact]
    public async Task WaitingForAnotherDiscoveryCanBeCancelledWithoutWaitingForItsRead()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var firstIdentity = Identity();
        var secondIdentity = Identity();
        bool BlockedRead(ulong address, Span<byte> destination)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) throw new TimeoutException();
            destination.Clear();
            return true;
        }
        static bool UnexpectedRead(ulong address, Span<byte> destination) => throw new InvalidOperationException("The cancelled caller must not read");
        var first = Task.Run(() => NativeAdaptiveCompatibility.TryResolve(firstIdentity, 1, default, Package,
            BlockedRead, out _, out _, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            var waiting = Task.Run(() => NativeAdaptiveCompatibility.TryResolve(secondIdentity, 1, default, Package,
                UnexpectedRead, out _, out _, cancellation.Token), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.False(first.IsCompleted);
        }
        finally { release.Set(); }
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
    }

    private static NativeHudProcessIdentity Identity() => new(Interlocked.Increment(ref _nextProcess), 1,
        @"C:\Game\ForzaHorizon6.exe", 0x140000000, 0x8000);
    // This tests discovery after the factory's OS package-provenance boundary.
    // No process is opened and no package API is mocked as verified by this test.
    private static NativeStorePackageIdentity Package => new("Microsoft.ForteBaseGame_3.999.4.0_x64__8wekyb3d8bbwe",
        @"C:\Game\ForzaHorizon6.exe");
}
