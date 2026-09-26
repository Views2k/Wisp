using Wisp.Core;

namespace Wisp.App.Drift;

internal static class DriftZoneProfileCatalog
{
    private const string VerifiedSteamHash = "FEC4A63CDEAD26F6528564F0E33C3D7FD02CD887337ED6E1AEACA79EB2843FCD";
    private static readonly DriftZoneScoringProfile CurrentProfile = new(10, 110, (double)59.4f, 30, 40);

    // Scoring guidance is separate from native attachment. Store attachment keeps
    // its package and loaded-image guards; unknown builds receive no angle guide.
    internal static DriftZoneScoringProfile? ForBuild(NativeHudCompatibilityPack? pack)
    {
        if (pack is { StoreIdentity: null, GameVersion: "6.440.853.0", ExecutableLength: 184055768 } &&
            string.Equals(pack.ExecutableSha256, VerifiedSteamHash, StringComparison.OrdinalIgnoreCase))
            return CurrentProfile;

        return pack is { GameVersion: "3.440.853.0", ImageSize: 188420096, StoreIdentity: { } identity } &&
            identity.PackageFullName == "Microsoft.ForteBaseGame_3.440.853.0_x64__8wekyb3d8bbwe" &&
            identity.TimeDateStamp == 1788432457
                ? CurrentProfile : null;
    }
}
