using System.Text.Json;
using Wisp.App.DebugLogging;
using Wisp.App.Supplementary;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryGameVerificationTests
{
    [Fact]
    public void EvidenceStaysAtObservationTimeAndExpiresRatherThanUsingLaterAttachment()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new HealthContextSnapshot(now, [
            new() { TimestampUtc = now.AddSeconds(-4), GameVerification = NativeGameVerification.SteamApiUnverified },
            new() { TimestampUtc = now, GameVerification = NativeGameVerification.Steam }], []);
        Assert.Equal(NativeGameVerification.SteamApiUnverified, SupplementaryGameVerification.At(snapshot, now.AddSeconds(-2)));
        Assert.Equal(NativeGameVerification.Steam, SupplementaryGameVerification.At(snapshot, now));
        Assert.Equal(NativeGameVerification.Unavailable, SupplementaryGameVerification.At(snapshot, now.AddSeconds(-5)));
        Assert.Equal(NativeGameVerification.Unavailable, SupplementaryGameVerification.At(snapshot, now.AddSeconds(11)));
    }

    [Fact]
    public void ContractRejectsContradictoryOrUnboundedEvidenceAndRetainsLegacyAbsence()
    {
        using var f = new SupplementaryFixture();
        foreach (var result in new[] { NativeGameVerification.Steam, NativeGameVerification.Store, NativeGameVerification.SteamApiUnverified,
            NativeGameVerification.Unavailable, NativeGameVerification.FromStatus(NativeAssistProviderStatus.GameNotRunning),
            NativeGameVerification.FromStatus(NativeAssistProviderStatus.UnsupportedBuild), NativeGameVerification.FromStatus(NativeAssistProviderStatus.AccessDenied) })
        {
            var value = f.Event() with { GameVerification = result };
            Assert.True(SupplementarySchema.Valid(value, f.Now));
            var restored = JsonSerializer.Deserialize<SupplementaryEvent>(JsonSerializer.Serialize(value, SupplementarySchema.Json), SupplementarySchema.Json);
            Assert.Equal(result, restored!.GameVerification);
        }
        Assert.True(SupplementarySchema.Valid(f.Event(), f.Now));
        Assert.False(SupplementarySchema.Valid(f.Event() with { GameVerification = new("verified", "not-observed") }, f.Now));
        Assert.False(SupplementarySchema.Valid(f.Event() with { GameVerification = new("cracked", "PRIVATE") }, f.Now));
    }

    [Fact]
    public void ReadyReaderAloneDoesNotClaimRuntimeVerification()
    {
        Assert.Equal("unavailable", NativeGameVerification.FromStatus(NativeAssistProviderStatus.Ready).Status);
        Assert.Equal("unavailable", NativeGameVerification.FromStatus(NativeAssistProviderStatus.AccessDenied).Status);
        Assert.Equal("not-running", NativeGameVerification.FromStatus(NativeAssistProviderStatus.GameNotRunning).Status);
    }
}
