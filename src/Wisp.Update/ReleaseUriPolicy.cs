namespace Wisp.Update;

internal static class ReleaseUriPolicy
{
    internal static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/Views2k/Wisp/releases/latest");

    private const string GitHubHost = "github.com";
    private const string ReleaseAssetsHost = "release-assets.githubusercontent.com";
    private const string ObjectsHost = "objects.githubusercontent.com";
    private const string ReleaseAssetDirectory = "/github-production-release-asset/";
    private const string LegacyReleaseAssetDirectoryPrefix = "/github-production-release-asset-";

    internal static string InstallerFileName(SemanticVersion version) => $"Wisp-Setup-{version}.exe";

    internal static Uri InitialDownloadUri(SemanticVersion version)
    {
        var fileName = InstallerFileName(version);
        return new Uri($"https://{GitHubHost}/Views2k/Wisp/releases/download/{version.ToTagString()}/{fileName}");
    }

    internal static void RequireInitialDownloadUri(Uri actual, SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var expected = InitialDownloadUri(version);
        if (!IsCleanHttpsUri(actual) ||
            !string.Equals(actual.AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new UpdateSecurityException("The release asset URL is not the canonical Wisp release URL.");
        }
    }

    internal static void RequireRedirectTarget(Uri target, SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!IsCleanHttpsUri(target))
        {
            throw new UpdateSecurityException("The installer redirect target is not a clean HTTPS URL.");
        }

        var canonical = InitialDownloadUri(version);
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
