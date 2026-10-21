namespace Wisp.Update;

internal static class ReleaseUriPolicy
{
    internal static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/Views2k/Wisp/releases/latest");

    internal static readonly Uri LatestReleasePageUri = new("https://github.com/Views2k/Wisp/releases/latest");

    private const string ReleaseTagPagePrefix = "https://github.com/Views2k/Wisp/releases/tag/";
    private const string GitHubHost = "github.com";
    private const string ReleaseAssetsHost = "release-assets.githubusercontent.com";
    private const string ObjectsHost = "objects.githubusercontent.com";
    private const string ReleaseAssetDirectory = "/github-production-release-asset/";
    private const string LegacyReleaseAssetDirectoryPrefix = "/github-production-release-asset-";

    internal static string InstallerFileName(SemanticVersion version) => $"Wisp-Setup-{version}.exe";

    // The release page answers with a redirect to the latest tag's page. Only that exact canonical URL with a
    // vX.Y.Z tag is accepted, compared as received so no host, scheme, port, path or encoding variation passes.
    // It only says which version is newest; everything installed still comes from the verified API release.
    internal static bool TryParseLatestTagRedirect(Uri? location, out SemanticVersion version)
    {
        version = default;
        var received = location?.OriginalString;
        return received is not null &&
            received.StartsWith(ReleaseTagPagePrefix, StringComparison.Ordinal) &&
            SemanticVersion.TryParseTag(received[ReleaseTagPagePrefix.Length..], out version);
    }

    internal static Uri InitialDownloadUri(SemanticVersion version) =>
        InitialDownloadUri(version.ToTagString(), InstallerFileName(version));

    internal static Uri InitialDownloadUri(string tagName, string fileName)
    {
        if (!ReleaseIdentity.IsSafeTag(tagName) || !ReleaseIdentity.TryParseInstallerVersion(fileName, out _))
        {
            throw new UpdateSecurityException("The release URL identity is invalid.");
        }

        return new Uri($"https://{GitHubHost}/Views2k/Wisp/releases/download/{tagName}/{fileName}");
    }

    internal static void RequireInitialDownloadUri(Uri actual, string tagName, string fileName)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var expected = InitialDownloadUri(tagName, fileName);
        if (!IsCleanHttpsUri(actual) ||
            !string.Equals(actual.AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new UpdateSecurityException("The release asset URL is not the canonical Wisp release URL.");
        }
    }

    internal static void RequireRedirectTarget(Uri target, string tagName, string fileName)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!IsCleanHttpsUri(target))
        {
            throw new UpdateSecurityException("The installer redirect target is not a clean HTTPS URL.");
        }

        var canonical = InitialDownloadUri(tagName, fileName);
        if (string.Equals(target.AbsoluteUri, canonical.AbsoluteUri, StringComparison.Ordinal))
        {
            return;
        }

        if ((string.Equals(target.IdnHost, ReleaseAssetsHost, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(target.IdnHost, ObjectsHost, StringComparison.OrdinalIgnoreCase)) &&
            (target.AbsolutePath.StartsWith(ReleaseAssetDirectory, StringComparison.Ordinal) ||
             target.AbsolutePath.StartsWith(LegacyReleaseAssetDirectoryPrefix, StringComparison.Ordinal)))
        {
            return;
        }

        throw new UpdateSecurityException("The installer redirect target is outside the GitHub release-asset allowlist.");
    }

    private static bool IsCleanHttpsUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 &&
        uri.IsDefaultPort;
}
