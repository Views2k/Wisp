using System.IO;
using System.Text.Json;

namespace Wisp.App;

// Shared by HUD and Tune attachment workers. A new build is scanned once per
// process generation, not once per telemetry sample or every attachment retry.
internal static class NativeAdaptiveCompatibility
{
    private static readonly object Gate = new();
    private static readonly Dictionary<CacheKey, Entry> Entries = [];
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    internal static bool TryResolve(NativeHudProcessIdentity identity, long catalogGeneration,
        NativeHudExecutableFingerprint fingerprint, NativeStorePackageIdentity? package, NativeAdaptiveRead read,
        out NativeHudCompatibilityPack? pack, out string reason, CancellationToken cancellationToken = default)
    {
        pack = null;
        reason = "The updated game's reader layout could not be verified";
        var key = new CacheKey(identity, catalogGeneration, fingerprint, package?.PackageFullName);
        var budget = new NativeAdaptiveBudget(TimeSpan.FromSeconds(30), cancellationToken);
        IDisposable lease;
        try { lease = Enter(budget); }
        catch (TimeoutException)
        {
            reason = "Another reader compatibility check is still running";
            return false;
        }
        using (lease)
        {
            try
            {
                budget.Check();
                if (Entries.TryGetValue(key, out var entry))
                {
                    if (entry.TryReuse(read, identity.ModuleBase, budget, DateTimeOffset.UtcNow, out var cachedPack, out var cachedReason))
                    {
                        pack = cachedPack;
                        reason = cachedReason;
                        return pack is not null;
                    }
                    Entries.Remove(key);
                }
                if (package is null && !NativeSteamPublisherTrust.TryVerify(identity.ExecutablePath))
                {
                    // Unavailable cached trust or a temporary file-sharing error
                    // must deny this attempt without permanently disabling it.
                    reason = "The updated Steam executable's publisher could not be verified";
                    throw new IOException(reason);
                }
                var platform = package is null ? "steam" : "store";
                using var profile = JsonDocument.Parse(ReadResource("Wisp.NativeCompatibility.Adaptive." + platform + ".json", 8 * 1024 * 1024));
                var reference = ReadResource(package is null ? "Wisp.NativeCompatibility.BuiltIn.json" : "Wisp.NativeCompatibility.Store.json", 64 * 1024);
                using var gaugeFields = package is null ? JsonDocument.Parse(ReadResource("Wisp.NativeCompatibility.Adaptive.store.json", 8 * 1024 * 1024)) : null;
                if (profile.RootElement.GetProperty("platform").GetString() != platform)
                    throw new InvalidDataException("The adaptive platform profile is invalid");
                var image = new NativeAdaptiveImage(read, identity.ModuleBase, identity.ImageSize, package is null, budget);
                var resolver = new NativeAdaptiveResolver(image, profile.RootElement, ["core"],
                    gaugeFields?.RootElement, gaugeFields is null ? null : NativeHudCompatibilityPack.Parse(reference).NativeGauge);
                resolver.Resolve();
                resolver.TryEnableCapability("tune", out _);
                resolver.TryEnableCapability("gauge", out _);
                var version = package is { } store ? store.PackageFullName.Split('_')[1] : fingerprint.Metadata.Version;
                var json = resolver.CreatePack(reference, version, fingerprint.Metadata.Length,
                    fingerprint.Sha256, package?.PackageFullName);
                var result = NativeHudCompatibilityPack.FromRuntimeValidation(json);
                var proof = image.FinishProof();
                if (Entries.Count >= 4) Entries.Clear();
                // Keep the verified core, but retry temporarily unreadable optional
                // readers on a later attachment instead of caching their absence.
                Entries[key] = new Entry(result, proof, resolver.HasTransientCapabilityFailure
                    ? DateTimeOffset.UtcNow + RetryDelay : DateTimeOffset.MaxValue, string.Empty);
                pack = result;
                return true;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or FormatException or InvalidOperationException or
                ArgumentException or OverflowException or KeyNotFoundException or JsonException or TimeoutException)
            {
                // These messages contain only release-owned role names, not paths,
                // memory addresses, player details or raw game data.
                reason = exception is InvalidDataException or TimeoutException ? exception.Message : reason;
                if (Entries.Count >= 4) Entries.Clear();
                Entries[key] = new Entry(null, null, exception is InvalidDataException or FormatException
                    ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + RetryDelay, reason);
                return false;
            }
        }
    }

    private static IDisposable Enter(NativeAdaptiveBudget budget)
    {
        budget.Check();
        while (!Monitor.TryEnter(Gate, 50)) budget.Check();
        return new GateLease();
    }

    private sealed class GateLease : IDisposable
    {
        public void Dispose() => Monitor.Exit(Gate);
    }

    private static byte[] ReadResource(string name, int maximum)
    {
        using var source = typeof(NativeAdaptiveCompatibility).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("The adaptive compatibility profile is unavailable");
        if (source.Length is <= 0 || source.Length > maximum) throw new InvalidDataException("Adaptive profile size");
        using var output = new MemoryStream();
        source.CopyTo(output);
        return output.ToArray();
    }

    private sealed record CacheKey(NativeHudProcessIdentity Process, long CatalogGeneration,
        NativeHudExecutableFingerprint File, string? PackageName);
    internal sealed record Entry(NativeHudCompatibilityPack? Pack, NativeAdaptiveProof? Proof, DateTimeOffset RetryAt, string Reason)
    {
        internal bool TryReuse(NativeAdaptiveRead read, ulong moduleBase, NativeAdaptiveBudget budget,
            DateTimeOffset now, out NativeHudCompatibilityPack? pack, out string reason)
        {
            pack = null;
            reason = Reason;
            if (now >= RetryAt || Pack is not null && !Proof!.Matches(read, moduleBase, budget)) return false;
            pack = Pack;
            return true;
        }
    }
}
