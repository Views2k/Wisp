using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisp.Update;

internal static class GitHubReleaseParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32
    };

    internal static UpdateRelease Parse(ReadOnlySpan<byte> json)
    {
        GitHubRelease release;
        try
        {
            release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions) ??
                throw new UpdateSecurityException("The latest-release response is empty.");
        }
        catch (JsonException exception)
        {
            throw new UpdateSecurityException("The latest-release response is not valid JSON.", exception);
        }

        if (release.Draft is not false)
        {
            throw new UpdateSecurityException("The latest GitHub release is a draft or omitted its draft state.");
        }

        if (release.Prerelease is not false)
        {
            throw new UpdateSecurityException("The latest GitHub release is a prerelease or omitted its prerelease state.");
        }

        if (release.Immutable is not true)
        {
            throw new UpdateSecurityException("The latest GitHub release is not immutable.");
        }

        var matchingAssets = release.Assets?
            .Where(asset => asset is not null && ReleaseIdentity.IsInstallerAsset(asset.Name))
            .Take(2)
            .ToArray() ?? [];
        if (matchingAssets.Length != 1)
        {
            throw new UpdateSecurityException("The release must contain exactly one canonical Wisp installer asset.");
        }

        var asset = matchingAssets[0]!;
        if (!ReleaseIdentity.TryParseInstallerVersion(asset.Name, out var version))
        {
            throw new UpdateSecurityException("The Wisp installer asset must contain an unambiguous stable version.");
        }

        ReleaseIdentity.RequireMatchingTag(release.TagName, version);
        ReleaseIdentity.RequireStableTitle(release.Name);
        if (!string.Equals(asset.State, "uploaded", StringComparison.Ordinal))
        {
            throw new UpdateSecurityException("The Wisp installer asset is not in the uploaded state.");
        }

        if (asset.Size is null or <= 0 or > WispUpdateClient.MaximumInstallerSizeBytes)
        {
            throw new UpdateSecurityException("The Wisp installer asset size is missing or outside the allowed range.");
        }

        if (!TryNormalizeSha256(asset.Digest, out var sha256))
        {
            throw new UpdateSecurityException("The Wisp installer asset is missing a strict SHA-256 digest.");
        }

        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var downloadUri))
        {
            throw new UpdateSecurityException("The Wisp installer asset URL is invalid.");
        }

        ReleaseUriPolicy.RequireInitialDownloadUri(downloadUri, release.TagName!, asset.Name!);
        var releaseSummary = ReleaseSummaryFormatter.Format(release.Body);
        return new UpdateRelease(version, release.TagName!, asset.Name!, asset.Size.Value, sha256, downloadUri, releaseSummary);
    }

    internal static bool TryNormalizeSha256(string? digest, out string sha256)
    {
        sha256 = string.Empty;
        const string prefix = "sha256:";
        if (digest is null || digest.Length != prefix.Length + 64 ||
            !digest.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var hex = digest.AsSpan(prefix.Length);
        foreach (var character in hex)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        sha256 = hex.ToString().ToLowerInvariant();
        return true;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("draft")]
        public bool? Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool? Prerelease { get; init; }

        [JsonPropertyName("immutable")]
        public bool? Immutable { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset?>? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("size")]
        public long? Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; init; }
    }
}
