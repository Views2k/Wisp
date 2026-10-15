using Wisp.App.DebugLogging;
using Wisp.App.Supplementary;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryCurrentGameIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NativeDisabledSamplePreservesCompletedProbeAcrossCollectorIntervalThenExpires()
    {
        var snapshot = Snapshot(new HealthContextSample { TimestampUtc = Now, NativeStatus = NativeAssistProviderStatus.Unavailable });
        var probe = Steam(Now.AddSeconds(-90));

        Assert.Same(probe, SupplementaryCurrentGameIdentity.Select(snapshot, probe, Now));
        var expired = SupplementaryCurrentGameIdentity.Select(snapshot, probe, Now.AddSeconds(31));
        Assert.Null(expired.GameBuild);
        Assert.Equal("unknown", expired.Platform);
        Assert.Equal("unavailable", expired.Verification.Status);
    }

    [Fact]
    public void NewerNoGameEvidenceInvalidatesProbeEvenAfterNativeReaderBecomesUnavailable()
    {
        var probe = Steam(Now.AddSeconds(-30));
        var stopped = new HealthContextSample { TimestampUtc = Now.AddSeconds(-15), NativeStatus = NativeAssistProviderStatus.GameNotRunning };
        var unknown = new HealthContextSample { TimestampUtc = Now, NativeStatus = NativeAssistProviderStatus.Unavailable };

        var selected = SupplementaryCurrentGameIdentity.Select(Snapshot(stopped, unknown), probe, Now);
        Assert.Null(selected.GameBuild);
        Assert.Equal("unknown", selected.Platform);
        Assert.NotEqual("complete", selected.Verification.Inspection);

        var reopened = Steam(Now.AddSeconds(-5));
        Assert.Same(reopened, SupplementaryCurrentGameIdentity.Select(Snapshot(stopped, unknown), reopened, Now));
    }

    [Fact]
    public void NewerDifferentBuildCannotInheritPreviousStorefrontOrVerification()
    {
        var probe = Steam(Now.AddSeconds(-30));
        var newer = new HealthContextSample
        {
            TimestampUtc = Now,
            NativeGameVersion = "6.999.111.0",
            NativeStatus = NativeAssistProviderStatus.AccessDenied,
            GameVerification = NativeGameVerification.AccessUnavailable
        };

        var selected = SupplementaryCurrentGameIdentity.Select(Snapshot(newer), probe, Now);
        Assert.Equal(newer.NativeGameVersion, selected.GameBuild);
        Assert.Equal("unknown", selected.Platform);
        Assert.Equal("access-unavailable", selected.Verification.Reason);

        var laterUnknown = new HealthContextSample { TimestampUtc = Now.AddSeconds(5) };
        selected = SupplementaryCurrentGameIdentity.Select(Snapshot(newer, laterUnknown), probe, laterUnknown.TimestampUtc);
        Assert.Null(selected.GameBuild);
        Assert.NotEqual("complete", selected.Verification.Inspection);
    }

    [Fact]
    public void CurrentNativeCompatibilityRemainsAuthoritativeForTheSameBuild()
    {
        var probe = Steam(Now.AddSeconds(-1));
        var health = new HealthContextSample
        {
            TimestampUtc = Now.AddSeconds(-2), NativeStatus = NativeAssistProviderStatus.Ready,
            NativeGameVersion = probe.GameBuild, NativeGamePlatform = DiagnosticGamePlatform.Steam,
            GameVerification = NativeGameVerification.Detailed(NativeGameVerification.Steam).WithCompatibility("supported")
        };

        var selected = SupplementaryCurrentGameIdentity.Select(Snapshot(health), probe, Now);
        Assert.Equal("supported", selected.Verification.Compatibility);
        Assert.Equal(health.TimestampUtc, selected.ObservedAt);
        Assert.Equal("unknown", probe.Verification.Compatibility);
    }

    [Fact]
    public void NewerProbeGameExitAndBuildChangeReplaceEarlierHealthIdentity()
    {
        var health = new HealthContextSample
        {
            TimestampUtc = Now.AddSeconds(-2), NativeStatus = NativeAssistProviderStatus.Ready,
            NativeGameVersion = "6.461.691.0", NativeGamePlatform = DiagnosticGamePlatform.Steam,
            GameVerification = NativeGameVerification.Detailed(NativeGameVerification.Steam)
        };
        var stopped = new NativeGameIdentityObservation(Now, null, "unknown",
            NativeGameVerification.Detailed(NativeGameVerification.FromStatus(NativeAssistProviderStatus.GameNotRunning)));
        Assert.Same(stopped, SupplementaryCurrentGameIdentity.Select(Snapshot(health), stopped, Now));

        var newBuild = Steam(Now) with { GameBuild = "6.999.111.0" };
        Assert.Same(newBuild, SupplementaryCurrentGameIdentity.Select(Snapshot(health), newBuild, Now));
    }

    [Fact]
    public void CurrentProbeDoesNotCertifyHistoricalFailureOrAcceptFutureEvidence()
    {
        var health = new HealthContextSample
        {
            TimestampUtc = Now.AddSeconds(-2), NativeStatus = NativeAssistProviderStatus.AccessDenied,
            GameVerification = NativeGameVerification.AccessUnavailable
        };
        var snapshot = Snapshot(health);
        Assert.Equal("verified", SupplementaryCurrentGameIdentity.Select(snapshot, Steam(Now), Now).Verification.Status);
        Assert.Equal(NativeGameVerification.AccessUnavailable, SupplementaryGameVerification.At(snapshot, health.TimestampUtc));
        Assert.Equal("access-unavailable", SupplementaryCurrentGameIdentity.Select(snapshot, Steam(Now.AddSeconds(1)), Now).Verification.Reason);
    }

    private static NativeGameIdentityObservation Steam(DateTimeOffset at) => new(at, "6.461.691.0", "steam",
        NativeGameVerification.Detailed(NativeGameVerification.Steam).WithCompatibility("unknown"));

    private static HealthContextSnapshot Snapshot(params HealthContextSample[] samples) => new(Now, [.. samples], []);
}
