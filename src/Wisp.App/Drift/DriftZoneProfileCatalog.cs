using Wisp.Core;

namespace Wisp.App.Drift;

internal static class DriftZoneProfileCatalog
{
    private const string VerifiedSteamHash = "FEC4A63CDEAD26F6528564F0E33C3D7FD02CD887337ED6E1AEACA79EB2843FCD";
    private static readonly DriftZoneScoringProfile VerifiedSteamProfile = new(10, 110, (double)59.4f, 30, 40);

    // Recovered from the zone component and corroborated against active scoring.
    // A future/native-compatible build is not automatically a verified scoring build.
    internal static DriftZoneScoringProfile? ForBuild(NativeHudCompatibilityPack? pack) =>
        pack is { StoreIdentity: null, GameVersion: "6.440.853.0", ExecutableLength: 184055768 } &&
        string.Equals(pack.ExecutableSha256, VerifiedSteamHash, StringComparison.OrdinalIgnoreCase)
            ? VerifiedSteamProfile : null;
}
