using System.Diagnostics;
using Wisp.App.DebugLogging;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeGameVerificationTests
{
    private const string StoreExecutable = @"C:\XboxGames\Forza Horizon 6\Content\ForzaHorizon6.exe";

    [Fact]
    public void DeclinedStorePackageIsOfficialAndReportsThePackageVersion()
    {
        using var process = Process.GetCurrentProcess();
        var package = new NativeStorePackageIdentity("Microsoft.ForteBaseGame_3.462.120.0_x64__8wekyb3d8bbwe", StoreExecutable);

        var declined = NativeGameVerification.DescribeRejected(process, StoreExecutable, package, "1.0.0.0");

        Assert.Equal(new NativeRejectedGame(DiagnosticGamePlatform.XboxStore, "3.462.120.0", NativeGameVerification.Detailed(NativeGameVerification.Store).WithCompatibility("unsupported")), declined);
        Assert.True(declined.Verification.IsValid);
    }

    [Theory]
    [InlineData((int)NativePackageInspection.NoPackage, false, (int)NativePublisherVerification.Unavailable, (int)NativePublisherVerification.Verified, false, "identity-not-established")]
    [InlineData((int)NativePackageInspection.NoPackage, false, (int)NativePublisherVerification.Unavailable, (int)NativePublisherVerification.Verified, true, "no-official-storefront")]
    [InlineData((int)NativePackageInspection.NoPackage, false, (int)NativePublisherVerification.Unavailable, (int)NativePublisherVerification.Unavailable, true, "identity-not-established")]
    [InlineData((int)NativePackageInspection.Unavailable, false, (int)NativePublisherVerification.Unavailable, (int)NativePublisherVerification.Verified, true, "access-unavailable")]
    [InlineData((int)NativePackageInspection.NoPackage, true, (int)NativePublisherVerification.Unavailable, (int)NativePublisherVerification.Verified, true, "access-unavailable")]
    [InlineData((int)NativePackageInspection.NoPackage, true, (int)NativePublisherVerification.Rejected, (int)NativePublisherVerification.Verified, true, "nonofficial-steam-api")]
    [InlineData((int)NativePackageInspection.NoPackage, true, (int)NativePublisherVerification.Verified, (int)NativePublisherVerification.Verified, true, "steam-image-and-api")]
    [InlineData((int)NativePackageInspection.Verified, false, (int)NativePublisherVerification.Unavailable, (int)NativePublisherVerification.Unavailable, true, "store-package-and-image")]
    public void CompletedOriginEvidenceIsSeparateFromMissingOrFailedInspection(int package,
        bool apiPresent, int apiTrust, int executableTrust, bool settled, string reason)
    {
        var result = NativeGameVerification.Classify((NativePackageInspection)package, apiPresent, (NativePublisherVerification)apiTrust, (NativePublisherVerification)executableTrust, settled);
        Assert.Equal(reason, result.Reason);
        Assert.True(result.IsValid);
        Assert.True(result.WithCompatibility("supported").IsValid);
        Assert.True(result.WithCompatibility("unsupported").IsValid);
    }

    [Fact]
    public void RefreshedIdentityOnlyEnrichesTheSameActiveAttachmentAfterItsAdmission()
    {
        var now = DateTimeOffset.UtcNow;
        var original = NativeGameVerification.IdentityPending.WithCompatibility("supported");
        var observed = new NativeGameIdentityObservation(now, "6.1.2.3", "steam", NativeGameVerification.Detailed(NativeGameVerification.Steam));
        Assert.Equal("verified", NativeGameVerification.ForAttachment(original, "6.1.2.3", now.AddSeconds(-1), observed).Status);
        Assert.Equal(original, NativeGameVerification.ForAttachment(original, "6.1.2.4", now.AddSeconds(-1), observed));
        Assert.Equal(original, NativeGameVerification.ForAttachment(original, "6.1.2.3", now.AddSeconds(1), observed));
        Assert.Equal(original, NativeGameVerification.ForAttachment(original, "6.1.2.3", now, observed with { Verification = NativeGameVerification.IdentityPending }));
        Assert.Equal(NativeGameVerification.Steam,
            NativeGameVerification.ForAttachment(NativeGameVerification.Steam, "6.1.2.3", now, observed with { Verification = NativeGameVerification.NonOfficialApi }));
    }

    [Fact]
    public void VerificationCannotClaimContradictoryStorefrontOrInspection()
    {
        Assert.False((NativeGameVerification.NonOfficialApi with { Storefront = "steam" }).IsValid);
        Assert.False((NativeGameVerification.NonOfficialApi with { Inspection = "unavailable" }).IsValid);
        Assert.False((NativeGameVerification.Detailed(NativeGameVerification.Steam) with { Compatibility = "guessed" }).IsValid);
    }

    [Fact]
    public void SteamApiInspectionWithoutTheModuleRemainsPendingAndCanBeRetried()
    {
        using var process = Process.GetCurrentProcess();
        Assert.Equal(NativeGameVerification.IdentityPending.WithCompatibility("supported"),
            NativeGameVerification.InspectSteamApi(process, Environment.ProcessPath!));
    }
}
