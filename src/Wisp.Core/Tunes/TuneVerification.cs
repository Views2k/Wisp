namespace Wisp.Core.Tunes;

public enum TunePlatform { Steam, MicrosoftStore }
public enum TuneVerificationMethod { KnownProfile, AuthenticatedCompatibilityDescriptor }

// A layout fingerprint is distinct from the executable's file hash. Store reads
// use verified package provenance and code guards; they never invent a file hash.
public sealed record TuneVerification(TunePlatform Platform, string ProfileId, string LayoutSha256,
    string CompatibilityPackId, int CompatibilityRevision, string? PackageFullName)
{
    public TuneVerificationMethod Method { get; init; }
    public int SemanticsVersion { get; init; }
    public string? CompatibilityPackSha256 { get; init; }
}

public static class TuneVerificationProfiles
{
    public const string SteamProfile = "fh6-steam-tune-440-1";
    public const string StoreProfile = "fh6-store-tune-440-1";
    public const string DescriptorProfile = "fh6-tune-imperial-v1";
    public const int DescriptorSemanticsVersion = 1;
    public const string SteamLayoutSha256 = "72EB78DEBEF4A7E1059EDCE98598F2BAEA4A935B3A5D798917E450552283EFCD";
    public const string StoreLayoutSha256 = "4001943BC48648A9C5670FB52FDE6DEAF50F2E8FBDDAB02B5D1EC89B8FA555B2";

    public static bool IsSupported(string gameVersion, string? executableSha256, TuneVerification? verification)
    {
        if (verification is null)
            return gameVersion == TuneDecoder.SupportedGameVersion &&
                string.Equals(executableSha256, TuneDecoder.SupportedExecutableSha256, StringComparison.OrdinalIgnoreCase);
        if (!Version.TryParse(gameVersion, out var version) || version.Build < 0 || version.Revision < 0 ||
            verification.CompatibilityRevision < 1 || string.IsNullOrWhiteSpace(verification.CompatibilityPackId) ||
            verification.CompatibilityPackId.Length > 128 || verification.CompatibilityPackId.Any(char.IsControl))
            return false;
        // Persisted provenance describes an already decoded snapshot. This is not
        // process admission: only the authenticated native reader can verify a
        // descriptor, and decoding requires that separate verification result.
        var descriptor = verification.Method == TuneVerificationMethod.AuthenticatedCompatibilityDescriptor &&
            verification.ProfileId == DescriptorProfile && verification.SemanticsVersion == DescriptorSemanticsVersion &&
            IsHash(verification.LayoutSha256) && IsHash(verification.CompatibilityPackSha256);
        var known = verification.Method == TuneVerificationMethod.KnownProfile &&
            verification.SemanticsVersion == 0 && verification.CompatibilityPackSha256 is null;
        return verification.Platform switch
        {
            TunePlatform.Steam => (descriptor || known && verification.ProfileId == SteamProfile &&
                verification.LayoutSha256 == SteamLayoutSha256) && verification.PackageFullName is null && IsHash(executableSha256),
            TunePlatform.MicrosoftStore => (descriptor || known && verification.ProfileId == StoreProfile &&
                verification.LayoutSha256 == StoreLayoutSha256) && executableSha256 is null &&
                verification.PackageFullName == $"Microsoft.ForteBaseGame_{gameVersion}_x64__8wekyb3d8bbwe",
            _ => false
        };
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
