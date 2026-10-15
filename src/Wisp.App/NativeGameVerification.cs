using System.Diagnostics;
using System.IO;
using Wisp.App.DebugLogging;
using Wisp.Core;

namespace Wisp.App;

// Evidence from admission checks, never a claim about an account or license owner.
public sealed record NativeGameVerification(string Status, string Reason,
    string? Storefront = null, string? Inspection = null, string? Compatibility = null)
{
    internal static readonly NativeGameVerification Unavailable = new("unavailable", "not-observed");
    internal static readonly NativeGameVerification Store = new("verified", "store-package-and-image");
    internal static readonly NativeGameVerification Steam = new("verified", "steam-image-and-api");
    internal static readonly NativeGameVerification SteamApiUnverified = new("unverified", "steam-api-unverified");
    internal static readonly NativeGameVerification IdentityUnverified = new("unverified", "identity-unverified");
    internal static readonly NativeGameVerification AccessUnavailable = new("unavailable", "access-unavailable");

    internal static readonly NativeGameVerification IdentityPending = new("unavailable", "identity-not-established", "unknown", "pending");
    internal static readonly NativeGameVerification NonOfficialApi = new("unverified", "nonofficial-steam-api", "nonofficial", "complete");
    internal static readonly NativeGameVerification NoOfficialStorefront = new("unverified", "no-official-storefront", "nonofficial", "complete");

    internal NativeGameVerification WithCompatibility(string compatibility) => this with { Compatibility = compatibility };

    internal bool IsValid => Compatibility is null or "supported" or "unsupported" or "unknown" &&
        Storefront is null or "steam" or "store" or "nonofficial" or "unknown" &&
        Inspection is null or "complete" or "pending" or "unavailable" or "not-running" &&
        (Storefront is null || Storefront == (Reason switch
        {
            "store-package-and-image" => "store",
            "steam-image-and-api" => "steam",
            "nonofficial-steam-api" or "no-official-storefront" => "nonofficial",
            _ => "unknown"
        })) &&
        (Inspection is null || Inspection == (Status switch
        {
            "verified" or "unverified" => "complete",
            "not-running" => "not-running",
            _ => Reason is "not-observed" or "identity-not-established" ? "pending" : "unavailable"
        })) &&
        (Status, Reason) is
        ("verified", "store-package-and-image" or "steam-image-and-api") or
        ("unverified", "steam-api-unverified" or "identity-unverified" or "nonofficial-steam-api" or "no-official-storefront") or
        ("unavailable", "not-observed" or "access-unavailable" or "identity-not-established") or
        ("not-running", "game-not-running");

    internal static NativeGameVerification FromStatus(NativeAssistProviderStatus status) => status switch
    {
        NativeAssistProviderStatus.GameNotRunning => new("not-running", "game-not-running"),
        NativeAssistProviderStatus.UnsupportedBuild => IdentityUnverified,
        NativeAssistProviderStatus.AccessDenied => AccessUnavailable,
        _ => Unavailable
    };

    // Runs on the attachment worker once per admission. Reuses the same offline
    // signature policy as executable validation; it never changes game behavior.
    internal static NativeGameVerification InspectSteamApi(Process process, string executablePath) =>
        InspectSteam(process, executablePath, NativePackageInspection.NoPackage, true, false).WithCompatibility("supported");

    // Runs once per declined process identity. Applies the same Store-origin and Valve
    // signature checks used for supported builds, so an official build Wisp does not
    // support yet stays distinguishable from a modified installation. Reads no game memory.
    internal static NativeRejectedGame DescribeRejected(Process process, string executablePath,
        NativeStorePackageIdentity? package, string? executableVersion)
    {
        if (package is { } store)
        {
            var parts = store.PackageFullName.Split('_');
            return new(DiagnosticGamePlatform.XboxStore, parts.Length > 1 ? parts[1] : null,
                Detailed(Store).WithCompatibility("unsupported"));
        }
        using var handle = NativeHudProcessMemory.OpenReadOnly(process.Id);
        var packageResult = NativeStorePackageIdentity.Inspect(handle, executablePath, out var inspectedPackage);
        if (packageResult == NativePackageInspection.Verified)
            return DescribeRejected(process, executablePath, inspectedPackage, executableVersion);
        var settled = DateTime.UtcNow - process.StartTime.ToUniversalTime() >= TimeSpan.FromMinutes(1);
        var evidence = InspectSteam(process, executablePath, packageResult, false, settled).WithCompatibility("unsupported");
        return new(evidence.Storefront == "steam" ? DiagnosticGamePlatform.Steam : null, executableVersion, evidence);
    }

    internal static NativeGameVerification Detailed(NativeGameVerification evidence) => evidence with
    {
        Storefront = evidence.Reason switch
        {
            "store-package-and-image" => "store",
            "steam-image-and-api" => "steam",
            "nonofficial-steam-api" or "no-official-storefront" => "nonofficial",
            _ => "unknown"
        },
        Inspection = evidence.Status switch
        {
            "verified" or "unverified" => "complete",
            "not-running" => "not-running",
            _ => evidence.Reason is "not-observed" or "identity-not-established" ? "pending" : "unavailable"
        }
    };

    internal static NativeGameVerification ForAttachment(NativeGameVerification original, string gameBuild,
        DateTimeOffset admittedAt, NativeGameIdentityObservation? observation)
    {
        if (original.Status is "verified" or "unverified" || observation is null ||
            observation.ObservedAt < admittedAt || observation.GameBuild != gameBuild ||
            observation.Verification.Inspection != "complete") return original;
        return observation.Verification.WithCompatibility("supported");
    }

    internal static NativeGameVerification Classify(NativePackageInspection package, bool apiPresent,
        NativePublisherVerification apiTrust, NativePublisherVerification executableTrust, bool settled)
    {
        if (package == NativePackageInspection.Verified) return Detailed(Store);
        if (apiPresent && apiTrust == NativePublisherVerification.Verified && executableTrust == NativePublisherVerification.Verified)
            return Detailed(Steam);
        if (package == NativePackageInspection.NoPackage && apiPresent && apiTrust == NativePublisherVerification.Rejected)
            return NonOfficialApi;
        if (package == NativePackageInspection.NoPackage && !apiPresent && settled && executableTrust != NativePublisherVerification.Unavailable)
            return NoOfficialStorefront;
        return apiPresent || package == NativePackageInspection.Unavailable ? Detailed(AccessUnavailable) : IdentityPending;
    }

    private static NativeGameVerification InspectSteam(Process process, string executablePath,
        NativePackageInspection package, bool executableAlreadyVerified, bool settled)
    {
        try
        {
            var expected = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executablePath)!, "steam_api64.dll"));
            var executableTrust = executableAlreadyVerified ? NativePublisherVerification.Verified :
                NativeSteamPublisherTrust.InspectExecutable(executablePath);
            foreach (ProcessModule module in process.Modules)
            {
                if (!string.Equals(module.ModuleName, "steam_api64.dll", StringComparison.OrdinalIgnoreCase)) continue;
                var path = Path.GetFullPath(module.FileName);
                var trust = NativeSteamPublisherTrust.InspectSteamApi(path);
                // A trusted module at an unexpected location needs a new observation, not a piracy label.
                if (!path.Equals(expected, StringComparison.OrdinalIgnoreCase) && trust == NativePublisherVerification.Verified)
                    return Detailed(AccessUnavailable);
                return Classify(package, true, trust, executableTrust, settled);
            }
            return Classify(package, false, NativePublisherVerification.Unavailable, executableTrust, settled);
        }
        catch (Exception error) when (NativeHudFingerprintCache.IsExpectedFailure(error) || error is NotSupportedException)
        { return Detailed(AccessUnavailable); }
    }
}

// Identity of a running game build Wisp declined to read. It never enables native reads.
internal sealed record NativeRejectedGame(DiagnosticGamePlatform? Platform, string? GameVersion, NativeGameVerification Verification);
