using Wisp.Core;

namespace Wisp.App.Drift;

internal static class DriftZoneProfileCatalog
{
    private const string PreviousSteamHash = "FEC4A63CDEAD26F6528564F0E33C3D7FD02CD887337ED6E1AEACA79EB2843FCD";
    private const string CurrentSteamHash = "B8C18EC88AB3143DA03C3BB9BD9D0761F23E00F809B1BD9D50CF07496E5D1CE5";
    private static readonly DriftZoneScoringProfile CurrentProfile = new(10, 110, (double)59.4f, 30, 40);

    // Scoring guidance is separate from HUD/Tune attachment. Adaptive validation
    // of those readers does not establish a game's loaded scoring parameters.
    // Unverified scoring retains the measured angle without claiming a bonus.
    internal static DriftZoneScoringProfile? ForBuild(NativeHudCompatibilityPack? pack)
    {
        return pack switch
        {
            { StoreIdentity: null, GameVersion: "6.440.853.0", ExecutableLength: 184055768 }
                when string.Equals(pack.ExecutableSha256, PreviousSteamHash, StringComparison.OrdinalIgnoreCase) => CurrentProfile,
            { StoreIdentity: null, GameVersion: "6.461.691.0", ExecutableLength: 184038360 }
                when string.Equals(pack.ExecutableSha256, CurrentSteamHash, StringComparison.OrdinalIgnoreCase) => CurrentProfile,
            {
                GameVersion: "3.440.853.0", ImageSize: 188420096, StoreIdentity:
                { PackageFullName: "Microsoft.ForteBaseGame_3.440.853.0_x64__8wekyb3d8bbwe", TimeDateStamp: 1788432457 }
            } => CurrentProfile,
            {
                GameVersion: "3.461.691.0", ImageSize: 188403712, StoreIdentity:
                { PackageFullName: "Microsoft.ForteBaseGame_3.461.691.0_x64__8wekyb3d8bbwe", TimeDateStamp: 1790608750 }
            } => CurrentProfile,
            _ => null
        };
    }
}
