namespace Wisp.App.Drift;

// Names one exact game build with the fields the scoring catalog matches on.
internal static class DriftZoneBuildKey
{
    private sealed record Cached(NativeHudCompatibilityPack Pack, string Key);

    private static Cached? _last;

    // The drift renderer asks on every frame, so the key of the attached build is kept.
    internal static string For(NativeHudCompatibilityPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (Volatile.Read(ref _last) is { } last && ReferenceEquals(last.Pack, pack))
        {
            return last.Key;
        }

        var key = pack.StoreIdentity is { } store
            ? FormattableString.Invariant($"store:{store.PackageFullName}:{pack.ImageSize}:{store.TimeDateStamp}")
            : FormattableString.Invariant($"steam:{pack.GameVersion}:{pack.ExecutableLength}:{pack.ImageSize}:{pack.ExecutableSha256.ToUpperInvariant()}");
        Volatile.Write(ref _last, new Cached(pack, key));
        return key;
    }
}
