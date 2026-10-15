using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wisp.App.Supplementary;

internal static class ControlledFeatures
{
    internal static readonly ImmutableHashSet<string> All = ImmutableHashSet.Create(StringComparer.Ordinal,
        "hud", "main-hud", "boost-gauge", "g-force-gauge", "tire-temperature-gauge", "drift-hud", "power-gauge", "torque-gauge",
        "lap-delta", "lap-map", "lap-recording",
        "run-recording", "run-library", "lap-review", "tune-read", "tune-library",
        "clip-recording", "clip-playback", "clip-export", "clip-library", "shift-calibration",
        "tire-calibration", "shift-guidance", "traction-cue", "background-animation",
        "hud-profiles", "appearance", "diagnostics", "news", "support", "updates");
}

internal sealed record FeatureControlRule(string Id, ImmutableArray<string> Features, string Reason,
    string? MinVersion, string? MaxVersion, ImmutableArray<string> Channels,
    DateTimeOffset StartsAt, DateTimeOffset ExpiresAt)
{
    internal ImmutableArray<string> GameBuilds { get; init; } = [];
    internal ImmutableArray<string> GpuVendors { get; init; } = [];

    internal bool Matches(string version, string channel, DateTimeOffset now, FeatureControlContext? context = null) =>
        now >= StartsAt && now < ExpiresAt && Channels.Contains(channel) &&
        SupplementarySchema.NumericVersion(version, true) &&
        (MinVersion is null || Version.Parse(version) >= Version.Parse(MinVersion)) &&
        (MaxVersion is null || Version.Parse(version) <= Version.Parse(MaxVersion)) &&
        (GameBuilds.IsDefaultOrEmpty || context?.GameBuild is { } build && GameBuilds.Contains(build)) &&
        (GpuVendors.IsDefaultOrEmpty || context is not null && context.GpuVendors.Overlaps(GpuVendors));
}

internal sealed record FeatureControlContext(string? GameBuild, ImmutableHashSet<string> GpuVendors)
{
    internal static FeatureControlContext Empty { get; } = new(null, ImmutableHashSet<string>.Empty);
    internal static readonly ImmutableHashSet<string> SupportedGpuVendors = ImmutableHashSet.Create(StringComparer.Ordinal,
        "amd", "nvidia", "intel");

    internal static ImmutableHashSet<string> FromVendorIds(IEnumerable<uint> vendors) => vendors
        .Select(id => id switch { 0x1002 => "amd", 0x10de => "nvidia", 0x8086 => "intel", _ => null })
        .Where(vendor => vendor is not null).Select(vendor => vendor!).ToImmutableHashSet(StringComparer.Ordinal);
}

internal sealed record FeatureControlPolicy(long Revision, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt,
    ImmutableArray<FeatureControlRule> Rules)
{
    internal static FeatureControlPolicy Empty { get; } = new(0, DateTimeOffset.MinValue, DateTimeOffset.MinValue, []);
    internal bool IsCurrent(DateTimeOffset now) => Revision > 0 && IssuedAt <= now + TimeSpan.FromMinutes(5) && ExpiresAt > now;

    internal ImmutableDictionary<string, string> Disabled(string version, string channel, DateTimeOffset now, FeatureControlContext? context = null)
    {
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (IsCurrent(now))
            foreach (var rule in Rules)
                if (rule.Matches(version, channel, now, context))
                    foreach (var feature in rule.Features) result.TryAdd(feature, rule.Reason);
        return result.ToImmutable();
    }
}

internal sealed record VerifiedFeatureControlPolicy(FeatureControlPolicy Policy, string PayloadHash, byte[] Envelope);

// Only the purpose-separated disable policy is accepted here. The catalog contains no command or setting values.
internal sealed class FeatureControlVerifier
{
    internal const int MaximumEnvelopeBytes = 64 * 1024;
    internal const int MaximumPayloadBytes = 32 * 1024;
    private readonly IReadOnlyDictionary<string, byte[]> _keys;
    internal bool IsConfigured => _keys.Count > 0;

    internal FeatureControlVerifier(IReadOnlyDictionary<string, byte[]> keys)
    {
        if (keys.Count > 16) throw new ArgumentException("Too many feature control keys.", nameof(keys));
        var copy = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, bytes) in keys)
        {
            if (!SupplementarySchema.Token(id, 80) || bytes is not { Length: > 0 and <= 1024 })
                throw new ArgumentException("Invalid feature control key.", nameof(keys));
            using var key = ReadKey(bytes);
            copy.Add(id, bytes.ToArray());
        }
        _keys = copy;
    }

    internal VerifiedFeatureControlPolicy Verify(ReadOnlyMemory<byte> envelope, DateTimeOffset now, bool cached = false)
    {
        try
        {
            using var outer = SupplementaryJson.Parse(envelope, MaximumEnvelopeBytes);
            var root = SupplementaryJson.Object(outer.RootElement, "schemaVersion", "keyId", "payload", "signature");
            SupplementaryJson.Require(SupplementaryJson.Int(root["schemaVersion"]) == 1, "schema");
            var keyId = SupplementaryJson.String(root["keyId"], 80);
            SupplementaryJson.Require(_keys.TryGetValue(keyId, out var keyBytes), "publisher");
            var payload = SupplementaryJson.Base64(root["payload"], MaximumPayloadBytes);
            var signature = SupplementaryJson.Base64(root["signature"], 64);
            SupplementaryJson.Require(signature.Length == 64, "signature");
            using (var key = ReadKey(keyBytes!))
                SupplementaryJson.Require(key.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation), "signature");
            using var document = SupplementaryJson.Parse(payload, MaximumPayloadBytes);
            var p = SupplementaryJson.Object(document.RootElement, "schemaVersion", "purpose", "revision", "issuedAt", "expiresAt", "rules");
            var schemaVersion = SupplementaryJson.Int(p["schemaVersion"]);
            SupplementaryJson.Require(schemaVersion is 1 or 2 &&
                SupplementaryJson.String(p["purpose"], 64) == "wisp-feature-controls", "purpose");
            var revision = SupplementaryJson.Int(p["revision"]);
            var issued = SupplementaryJson.Utc(p["issuedAt"]);
            var expires = SupplementaryJson.Utc(p["expiresAt"]);
            SupplementaryJson.Require(revision is > 0 and <= 9_007_199_254_740_991 && issued < expires &&
                expires - issued <= TimeSpan.FromDays(7), "validity");
            SupplementaryJson.Require(cached || issued <= now + TimeSpan.FromMinutes(5) && expires > now, "expired");
            SupplementaryJson.Require(p["rules"].ValueKind == JsonValueKind.Array && p["rules"].GetArrayLength() <= 32, "rules");
            var rules = p["rules"].EnumerateArray().Select(r => ReadRule(r, issued, expires, schemaVersion)).ToImmutableArray();
            SupplementaryJson.Require(rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() == rules.Length, "duplicate-rule");
            return new(new(revision, issued, expires, rules), Convert.ToHexString(SHA256.HashData(payload)), envelope.ToArray());
        }
        catch (SupplementaryValidationException) { throw; }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or
            CryptographicException or ArgumentException or OverflowException)
        { throw new SupplementaryValidationException("invalid-controls"); }
    }

    private static FeatureControlRule ReadRule(JsonElement element, DateTimeOffset issued, DateTimeOffset expires, long schemaVersion)
    {
        var p = schemaVersion == 1
            ? SupplementaryJson.Object(element, "id", "features", "reason", "minVersion", "maxVersion", "channels", "startsAt", "expiresAt")
            : SupplementaryJson.Object(element, "id", "features", "reason", "minVersion", "maxVersion", "channels", "startsAt", "expiresAt", "gameBuilds", "gpuVendors");
        var id = SupplementaryJson.String(p["id"], 64);
        var features = SupplementaryJson.StringArray(p["features"], ControlledFeatures.All.Count, ControlledFeatures.All.Contains, nonempty: true);
        var reason = SupplementaryJson.String(p["reason"], 300);
        var min = SupplementaryJson.NullableString(p["minVersion"], 32);
        var max = SupplementaryJson.NullableString(p["maxVersion"], 32);
        var channels = SupplementaryJson.StringArray(p["channels"], 2, SupplementarySchema.Channels.Contains, nonempty: true);
        var starts = SupplementaryJson.Utc(p["startsAt"]);
        var ends = SupplementaryJson.Utc(p["expiresAt"]);
        SupplementaryJson.Require(SupplementarySchema.Token(id, 64) && SupplementarySchema.PlainText(reason, 300, false) &&
            starts >= issued && starts < ends && ends <= expires &&
            (min is null || SupplementarySchema.NumericVersion(min, true)) &&
            (max is null || SupplementarySchema.NumericVersion(max, true)) &&
            (min is null || max is null || Version.Parse(min) <= Version.Parse(max)), "rule");
        var games = schemaVersion == 2
            ? SupplementaryJson.StringArray(p["gameBuilds"], 16, value => SupplementarySchema.NumericVersion(value, false)) : [];
        var vendors = schemaVersion == 2
            ? SupplementaryJson.StringArray(p["gpuVendors"], 3, FeatureControlContext.SupportedGpuVendors.Contains) : [];
        return new(id, features, reason, min, max, channels, starts, ends) { GameBuilds = games, GpuVendors = vendors };
    }

    private static ECDsa ReadKey(byte[] bytes)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(bytes, out var consumed);
            if (consumed != bytes.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new CryptographicException("Feature control signing requires P-256.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }
}
