using Wisp.App.DebugLogging;
using Wisp.Core;

namespace Wisp.App.Supplementary;

// Current audience and heartbeat identity only. Historical events keep their event-time evidence.
internal static class SupplementaryCurrentGameIdentity
{
    internal static NativeGameIdentityObservation Select(HealthContextSnapshot snapshot,
        NativeGameIdentityObservation? probe, DateTimeOffset now)
    {
        // The collector refreshes every 60–90 seconds. Its completion callback replaces
        // this bounded snapshot without adding game reads to any caller.
        if (probe is not null && (probe.ObservedAt > now || now - probe.ObservedAt > TimeSpan.FromMinutes(2) || !probe.Verification.IsValid))
            probe = null;
        if (probe is { } candidate && snapshot.Samples.Any(sample => sample.TimestampUtc > candidate.ObservedAt && sample.TimestampUtc <= now &&
            (sample.NativeStatus == NativeAssistProviderStatus.GameNotRunning || sample.GameVerification?.Status == "not-running" ||
                SupplementarySchema.NumericVersion(sample.NativeGameVersion, false) && sample.NativeGameVersion != candidate.GameBuild)))
            probe = null;

        var latest = snapshot.Samples.LastOrDefault(sample => sample.TimestampUtc <= now && now - sample.TimestampUtc <= TimeSpan.FromSeconds(10));
        var health = latest is null ? null : FromHealth(latest);
        if (health is null) return probe ?? Unknown(now);
        if (probe is null) return health;

        if (health.Verification.Status == "not-running")
            return health.ObservedAt >= probe.ObservedAt ? health : probe;
        if (probe.Verification.Status == "not-running" ||
            health.GameBuild is not null && probe.GameBuild is not null && health.GameBuild != probe.GameBuild)
            return probe.ObservedAt >= health.ObservedAt ? probe : health;

        if (health.Verification.Inspection == "complete")
        {
            if (probe.Verification.Inspection == "complete" && probe.ObservedAt > health.ObservedAt &&
                probe.Verification.Storefront != health.Verification.Storefront) return probe;
            return health;
        }
        if (probe.Verification.Inspection == "complete")
            return health.GameBuild == probe.GameBuild && health.Verification.Compatibility is "supported" or "unsupported"
                ? probe with { Verification = probe.Verification.WithCompatibility(health.Verification.Compatibility) } : probe;
        return health.GameBuild is not null ? health : probe;
    }

    private static NativeGameIdentityObservation FromHealth(HealthContextSample sample)
    {
        var verification = NativeGameVerification.Detailed(sample.NativeStatus == NativeAssistProviderStatus.GameNotRunning
            ? NativeGameVerification.FromStatus(sample.NativeStatus)
            : sample.GameVerification is { IsValid: true } evidence ? evidence : NativeGameVerification.FromStatus(sample.NativeStatus));
        var stopped = verification.Status == "not-running";
        return new(sample.TimestampUtc,
            !stopped && SupplementarySchema.NumericVersion(sample.NativeGameVersion, false) ? sample.NativeGameVersion : null,
            stopped ? "unknown" : sample.NativeGamePlatform switch
            { DiagnosticGamePlatform.Steam => "steam", DiagnosticGamePlatform.XboxStore => "store", _ => "unknown" }, verification);
    }

    private static NativeGameIdentityObservation Unknown(DateTimeOffset now) =>
        new(now, null, "unknown", NativeGameVerification.Detailed(NativeGameVerification.Unavailable));
}
