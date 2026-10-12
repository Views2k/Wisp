using System.Diagnostics;
using System.IO;
using Wisp.Core;

namespace Wisp.App;

// Evidence from admission checks, never a claim about an account or license owner.
public sealed record NativeGameVerification(string Status, string Reason)
{
    internal static readonly NativeGameVerification Unavailable = new("unavailable", "not-observed");
    internal static readonly NativeGameVerification Store = new("verified", "store-package-and-image");
    internal static readonly NativeGameVerification Steam = new("verified", "steam-image-and-api");
    internal static readonly NativeGameVerification SteamApiUnverified = new("unverified", "steam-api-unverified");

    internal bool IsValid => (Status, Reason) is
        ("verified", "store-package-and-image" or "steam-image-and-api") or
        ("unverified", "steam-api-unverified" or "identity-unverified") or
        ("unavailable", "not-observed" or "access-unavailable") or
        ("not-running", "game-not-running");

    internal static NativeGameVerification FromStatus(NativeAssistProviderStatus status) => status switch
    {
        NativeAssistProviderStatus.GameNotRunning => new("not-running", "game-not-running"),
        NativeAssistProviderStatus.UnsupportedBuild => new("unverified", "identity-unverified"),
        NativeAssistProviderStatus.AccessDenied => new("unavailable", "access-unavailable"),
        _ => Unavailable
    };

    // Runs on the attachment worker once per admission. Reuses the same offline
    // signature policy as executable validation; it never changes game behavior.
    internal static NativeGameVerification InspectSteamApi(Process process, string executablePath)
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
        { return new("unavailable", "access-unavailable"); }
        return SteamApiUnverified;
    }
}
