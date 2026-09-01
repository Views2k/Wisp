using System.IO;

namespace Wisp.App;

public static class NativeHudBuildContract
{
    // Embedded packs are trusted with the application. External packs must pass
    // the signed catalog before the process reader can select them.
    public static NativeHudCompatibilityPack BuiltIn { get; } = LoadBuiltIn();

    public static string SupportedVersion => BuiltIn.GameVersion;
    public static long SupportedExecutableLength => BuiltIn.ExecutableLength;
    public static string SupportedSha256 => BuiltIn.ExecutableSha256;
    public static ulong SourceVectorRva => BuiltIn.SourceVectorRva;
    public static ulong ThresholdRva => BuiltIn.ThresholdRva;
    public static ulong LeadVtableRva => BuiltIn.LeadVtableRva;
    public static IReadOnlyDictionary<ulong, ulong> RequiredVtableSlots => BuiltIn.RequiredVtableSlots;

    public static bool Matches(string? version, long length, string? sha256) =>
        BuiltIn.Matches(version, length, sha256);

    private static NativeHudCompatibilityPack LoadBuiltIn()
    {
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream(
            "Wisp.NativeCompatibility.BuiltIn.json")
            ?? throw new InvalidDataException("The bundled Native compatibility pack is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return NativeHudCompatibilityPack.Parse(bytes.ToArray());
    }
}
