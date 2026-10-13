using System.Diagnostics;
using System.IO;
using Wisp.App.DebugLogging;
using Wisp.Core;

namespace Wisp.App;

// Evidence from admission checks, never a claim about an account or license owner.
public sealed record NativeGameVerification(string Status, string Reason)
{
    internal static readonly NativeGameVerification Unavailable = new("unavailable", "not-observed");
    internal static readonly NativeGameVerification Store = new("verified", "store-package-and-image");
    internal static readonly NativeGameVerification Steam = new("verified", "steam-image-and-api");
    internal static readonly NativeGameVerification SteamApiUnverified = new("unverified", "steam-api-unverified");
    internal static readonly NativeGameVerification IdentityUnverified = new("unverified", "identity-unverified");
    internal static readonly NativeGameVerification AccessUnavailable = new("unavailable", "access-unavailable");

    internal bool IsValid => (Status, Reason) is
        ("verified", "store-package-and-image" or "steam-image-and-api") or
        ("unverified", "steam-api-unverified" or "identity-unverified") or
        ("unavailable", "not-observed" or "access-unavailable") or
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
        SteamApiEvidence(process, executablePath) ?? SteamApiUnverified;

    // Runs once per declined process identity. Applies the same Store-origin and Valve
    // signature checks used for supported builds, so an official build Wisp does not
    // support yet stays distinguishable from a modified installation. Reads no game memory.
    internal static NativeRejectedGame DescribeRejected(Process process, string executablePath,
        NativeStorePackageIdentity? package, string? executableVersion)
    {
        if (package is { } store)
        {
            var parts = store.PackageFullName.Split('_');
            return new(DiagnosticGamePlatform.XboxStore, parts.Length > 1 ? parts[1] : null, Store);
        }

        return SteamApiEvidence(process, executablePath) switch
        {
            null => new(null, executableVersion, IdentityUnverified),
            { Status: "unavailable" } unavailable => new(null, executableVersion, unavailable),
            var steam => new(DiagnosticGamePlatform.Steam, executableVersion, steam)
        };
    }

    // Null when the process has no steam_api64.dll module loaded.
    private static NativeGameVerification? SteamApiEvidence(Process process, string executablePath)
    {
        try
        {
            var expected = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executablePath)!, "steam_api64.dll"));
            foreach (ProcessModule module in process.Modules)
            {
                if (!string.Equals(module.ModuleName, "steam_api64.dll", StringComparison.OrdinalIgnoreCase)) continue;
                return Path.GetFullPath(module.FileName).Equals(expected, StringComparison.OrdinalIgnoreCase) &&
                    NativeSteamPublisherTrust.TryVerifySteamApi(expected) ? Steam : SteamApiUnverified;
            }
        }
        catch (Exception error) when (NativeHudFingerprintCache.IsExpectedFailure(error) || error is NotSupportedException)
        { return AccessUnavailable; }
        return null;
    }
}

// Identity of a running game build Wisp declined to read. It describes the installation
// for diagnostics only and never enables native reads.
internal sealed record NativeRejectedGame(DiagnosticGamePlatform? Platform, string? GameVersion, NativeGameVerification Verification);
