using Wisp.Update;

namespace Wisp.App;

public static class ApplicationVersionInfo
{
    public static string DisplayVersion => Format(
        typeof(ApplicationVersionInfo).Assembly.GetName().Version ?? new Version(1, 0, 0));

    public static string FooterText => $"WHEEL-INDICATED SPEED PANEL {DisplayVersion}";

    public static string ReleaseHistoryIntroduction =>
        $"Feature updates, hotfixes, and important refinements from every documented public release. The current {DisplayVersion} entry covers this release.";

    public static string Format(Version version) => Format(
        new SemanticVersion(version.Major, version.Minor, Math.Max(0, version.Build)));

    public static string Format(SemanticVersion version) => version.Patch == 0
        ? $"{version.Major}.{version.Minor}"
        : version.ToString();
}
