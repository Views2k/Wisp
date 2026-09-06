using System.Text.RegularExpressions;

namespace Wisp.Update;

internal static class ReleaseIdentity
{
    private const string InstallerPrefix = "Wisp-Setup-";
    private const string InstallerSuffix = ".exe";
    private const string PrereleaseWords = "alpha|beta|rc|preview|nightly|pre|prerelease|dev|canary|experimental|candidate|unstable";
    private static readonly Regex SafeTagPattern = new(
        "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex PrereleaseTagPattern = new(
        $"(?:\\A|[^a-z])(?:{PrereleaseWords})(?:[0-9]|\\z|[^a-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex PrereleaseTitlePattern = new(
        $"\\A(?:Wisp[ _-]+)?v?[0-9]+(?:\\.[0-9]+){{0,2}}[ ._-]+(?:{PrereleaseWords})(?:[ ._-]*[0-9]+)*\\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool IsInstallerAsset(string? name) =>
        name is not null &&
        name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase);

    internal static bool TryParseInstallerVersion(string? name, out SemanticVersion version)
    {
        version = default;
        return name is not null &&
            name.StartsWith(InstallerPrefix, StringComparison.Ordinal) &&
            name.EndsWith(InstallerSuffix, StringComparison.Ordinal) &&
            SemanticVersion.TryParseReleaseVersion(
                name[InstallerPrefix.Length..^InstallerSuffix.Length], out version);
    }

    internal static bool IsSafeTag(string? tag) =>
        tag is not null && SafeTagPattern.IsMatch(tag) &&
        !tag.Contains("..", StringComparison.Ordinal) && !tag.EndsWith('.');

    internal static void RequireMatchingTag(string? tag, SemanticVersion version)
    {
        if (!IsSafeTag(tag) || PrereleaseTagPattern.IsMatch(tag!))
        {
            throw new UpdateSecurityException("The release tag is unsafe or identifies a prerelease.");
        }

        var numericTag = tag!.EndsWith("-stable", StringComparison.OrdinalIgnoreCase) ? tag[..^7] : tag;
        if (SemanticVersion.TryParseReleaseVersion(numericTag, out var tagVersion))
        {
            if (tagVersion != version)
            {
                throw new UpdateSecurityException("The release tag and installer versions disagree.");
            }
        }
        else if (char.IsAsciiDigit(tag[0]) ||
                 (tag.Length > 1 && (tag[0] is 'v' or 'V') && char.IsAsciiDigit(tag[1])))
        {
            throw new UpdateSecurityException("The release tag contains an invalid numeric version.");
        }
    }

    internal static void RequireStableTitle(string? title)
    {
        if (title is not null && PrereleaseTitlePattern.IsMatch(title.Trim()))
        {
            throw new UpdateSecurityException("The release title identifies a prerelease.");
        }
    }
}
