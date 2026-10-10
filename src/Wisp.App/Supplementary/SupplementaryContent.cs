using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wisp.App.Supplementary;

internal sealed record SupplementaryAudience(string? MinVersion, string? MaxVersion, ImmutableArray<string> Channels,
    ImmutableArray<string> Platforms, ImmutableArray<string> GameBuilds)
{
    internal bool Matches(SupplementaryAudienceContext context) => SupplementarySchema.NumericVersion(context.AppVersion, true) &&
        Channels.Contains(context.Channel) && Platforms.Contains(context.Platform) &&
        (MinVersion is null || Version.Parse(context.AppVersion) >= Version.Parse(MinVersion)) &&
        (MaxVersion is null || Version.Parse(context.AppVersion) <= Version.Parse(MaxVersion)) &&
        (GameBuilds.Length == 0 || context.GameBuild is not null && GameBuilds.Contains(context.GameBuild));
}
internal sealed record SupplementaryAudienceContext(string AppVersion, string Channel, string Platform, string? GameBuild = null);
internal sealed record SupplementaryContentItem(string Id, string Kind, string Title, string Message, string? Url,
    string Severity, DateTimeOffset StartsAt, DateTimeOffset ExpiresAt, SupplementaryAudience Audience, bool Dismissible);
internal sealed record SupplementaryContentSnapshot(long Revision, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt,
    bool ReportingDisabled, ImmutableArray<SupplementaryContentItem> Items)
{
    internal static SupplementaryContentSnapshot Empty { get; } = new(0, DateTimeOffset.MinValue, DateTimeOffset.MinValue, false, []);
    internal bool IsCurrent(DateTimeOffset now) => Revision > 0 && IssuedAt <= now + TimeSpan.FromMinutes(5) && ExpiresAt > now;
    internal ImmutableArray<SupplementaryContentItem> ForAudience(SupplementaryAudienceContext context, DateTimeOffset now) =>
        IsCurrent(now) ? Items.Where(i => i.StartsAt <= now && i.ExpiresAt > now && i.Audience.Matches(context)).ToImmutableArray() : [];
}
internal sealed record SupplementaryVerifiedContent(SupplementaryContentSnapshot Snapshot, string PayloadHash, byte[] Envelope);
internal sealed class SupplementaryValidationException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

// This verifier has an independent trust root, payload purpose and cache. It cannot authorize native maps or settings.
internal sealed class SupplementaryContentVerifier
{
    internal const int MaximumEnvelopeBytes = 128 * 1024;
    internal const int MaximumPayloadBytes = 64 * 1024;
    private readonly IReadOnlyDictionary<string, byte[]> _keys;
    internal bool IsConfigured => _keys.Count > 0;
    internal SupplementaryContentVerifier(IReadOnlyDictionary<string, byte[]> publicKeys)
    {
        if (publicKeys.Count > 16) throw new ArgumentException("Too many supplementary signing keys.", nameof(publicKeys));
        var copy = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, bytes) in publicKeys)
        {
            if (!SupplementarySchema.Token(id, 80) || bytes is not { Length: > 0 and <= 1024 })
                throw new ArgumentException("Invalid supplementary signing key.", nameof(publicKeys));
            using var key = ReadKey(bytes);
            copy.Add(id, bytes.ToArray());
        }
        _keys = copy;
    }

    internal SupplementaryVerifiedContent Verify(ReadOnlyMemory<byte> envelope, DateTimeOffset now, bool cached = false)
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
            var p = SupplementaryJson.Object(document.RootElement, "schemaVersion", "purpose", "revision", "issuedAt", "expiresAt", "items", "reportingDisabled");
            SupplementaryJson.Require(SupplementaryJson.Int(p["schemaVersion"]) == 1 &&
                SupplementaryJson.String(p["purpose"], 64) == "wisp-supplementary-content", "purpose");
            var revision = SupplementaryJson.Int(p["revision"]);
            var issued = SupplementaryJson.Utc(p["issuedAt"]);
            var expires = SupplementaryJson.Utc(p["expiresAt"]);
            SupplementaryJson.Require(revision is > 0 and <= 9_007_199_254_740_991 && issued < expires &&
                expires - issued <= TimeSpan.FromDays(7), "validity");
            // Cached signatures still establish the high-water mark after expiry. Expired content is never exposed.
            SupplementaryJson.Require(cached || issued <= now + TimeSpan.FromMinutes(5) && expires > now, "expired");
            SupplementaryJson.Require(p["items"].ValueKind == JsonValueKind.Array && p["items"].GetArrayLength() <= 32, "items");
            var items = p["items"].EnumerateArray().Select(i => ReadItem(i, issued, expires)).ToImmutableArray();
            SupplementaryJson.Require(items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() == items.Length, "duplicate-item");
            SupplementaryJson.Require(items.Count(i => i.Kind == "support-note") <= 1 &&
                items.Count(i => i.Kind == "dashboard-banner") <= 1, "duplicate-slot");
            return new(new(revision, issued, expires, SupplementaryJson.Bool(p["reportingDisabled"]), items),
                Convert.ToHexString(SHA256.HashData(payload)), envelope.ToArray());
        }
        catch (SupplementaryValidationException) { throw; }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or
            CryptographicException or ArgumentException or OverflowException)
        { throw new SupplementaryValidationException("invalid-content"); }
    }

    private static ECDsa ReadKey(byte[] bytes)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(bytes, out var consumed);
            if (consumed != bytes.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new CryptographicException("Supplementary signing requires P-256.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }

    private static SupplementaryContentItem ReadItem(JsonElement element, DateTimeOffset issued, DateTimeOffset expires)
    {
        var p = SupplementaryJson.Object(element, "id", "kind", "title", "message", "url", "severity", "startsAt", "expiresAt", "audience", "dismissible");
        var id = SupplementaryJson.String(p["id"], 64);
        var kind = SupplementaryJson.String(p["kind"], 32);
        var title = SupplementaryJson.String(p["title"], 100);
        var message = SupplementaryJson.String(p["message"], 2000);
        var url = SupplementaryJson.NullableString(p["url"], 2048);
        var severity = SupplementaryJson.String(p["severity"], 16);
        var starts = SupplementaryJson.Utc(p["startsAt"]);
        var ends = SupplementaryJson.Utc(p["expiresAt"]);
        SupplementaryJson.Require(SupplementarySchema.Token(id, 64) &&
            kind is "support-note" or "dashboard-banner" or "announcement" or "troubleshooting" or "changelog" or "incident" or "recommendation" &&
            SupplementarySchema.PlainText(title, 100, false) && SupplementarySchema.PlainText(message, 2000, true) &&
            severity is "info" or "warning" or "critical" && starts < ends && ends - starts <= TimeSpan.FromDays(7) && ends <= expires &&
            (url is null || kind != "dashboard-banner" && ValidUrl(url)), "item");
        var a = SupplementaryJson.Object(p["audience"], "minVersion", "maxVersion", "channels", "platforms", "gameBuilds");
        var min = SupplementaryJson.NullableString(a["minVersion"], 32);
        var max = SupplementaryJson.NullableString(a["maxVersion"], 32);
        var channels = SupplementaryJson.StringArray(a["channels"], 2, s => SupplementarySchema.Channels.Contains(s), nonempty: true);
        var platforms = SupplementaryJson.StringArray(a["platforms"], 3, s => SupplementarySchema.Platforms.Contains(s), nonempty: true);
        var games = SupplementaryJson.StringArray(a["gameBuilds"], 16, s => SupplementarySchema.NumericVersion(s, false));
        SupplementaryJson.Require((min is null || SupplementarySchema.NumericVersion(min, true)) &&
            (max is null || SupplementarySchema.NumericVersion(max, true)) &&
            (min is null || max is null || Version.Parse(min) <= Version.Parse(max)), "audience");
        return new(id, kind, title, message, url, severity, starts, ends, new(min, max, channels, platforms, games), SupplementaryJson.Bool(p["dismissible"]));
    }

    internal static bool ValidUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Port != 443 ||
            url.UserInfo.Length != 0 || value.Any(char.IsControl) || value.Contains('\\')) return false;
        if (url.IdnHost == "wispoverlay.com") return true;
        return url.IdnHost == "github.com" && (url.AbsolutePath.Equals("/Views2k/Wisp", StringComparison.OrdinalIgnoreCase) ||
            url.AbsolutePath.StartsWith("/Views2k/Wisp/", StringComparison.OrdinalIgnoreCase));
    }
}

internal static class SupplementaryJson
{
    internal static JsonDocument Parse(ReadOnlyMemory<byte> value, int maximum)
    {
        Require(value.Length is > 0 && value.Length <= maximum, "size");
        return JsonDocument.Parse(value, new() { MaxDepth = 12, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
    }
    internal static Dictionary<string, JsonElement> Object(JsonElement element, params string[] required)
    {
        Require(element.ValueKind == JsonValueKind.Object, "object");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject()) Require(required.Contains(p.Name, StringComparer.Ordinal) && result.TryAdd(p.Name, p.Value), "field");
        Require(result.Count == required.Length, "missing-field");
        return result;
    }
    internal static void Require(bool valid, string code) { if (!valid) throw new SupplementaryValidationException(code); }
    internal static string String(JsonElement e, int max)
    {
        Require(e.ValueKind == JsonValueKind.String, "string");
        var s = e.GetString()!;
        Require(s.Length <= max, "string-size"); return s;
    }
    internal static string? NullableString(JsonElement e, int max) => e.ValueKind == JsonValueKind.Null ? null : String(e, max);
    internal static long Int(JsonElement e) { Require(e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out _), "integer"); return e.GetInt64(); }
    internal static bool Bool(JsonElement e) { Require(e.ValueKind is JsonValueKind.True or JsonValueKind.False, "boolean"); return e.GetBoolean(); }
    internal static DateTimeOffset Utc(JsonElement e)
    {
        var s = String(e, 32);
        Require(s.EndsWith('Z') && DateTimeOffset.TryParseExact(s,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _), "utc");
        return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
    internal static byte[] Base64(JsonElement e, int maximum)
    {
        var s = String(e, checked((maximum + 2) / 3 * 4));
        var bytes = Convert.FromBase64String(s);
        Require(bytes.Length > 0 && bytes.Length <= maximum && Convert.ToBase64String(bytes) == s, "base64");
        return bytes;
    }
    internal static ImmutableArray<string> StringArray(JsonElement e, int max, Func<string, bool> validate, bool nonempty = false)
    {
        Require(e.ValueKind == JsonValueKind.Array && e.GetArrayLength() <= max && (!nonempty || e.GetArrayLength() > 0), "array");
        var values = e.EnumerateArray().Select(v => String(v, 80)).ToImmutableArray();
        Require(values.All(validate) && values.Distinct(StringComparer.Ordinal).Count() == values.Length, "array-values");
        return values;
    }
}
