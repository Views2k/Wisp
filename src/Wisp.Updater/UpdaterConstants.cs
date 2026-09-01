namespace Wisp.Updater;

internal static class UpdaterConstants
{
    internal const string ApplicationFileName = "Wisp.exe";
    internal const string ExpectedProductName = "Wisp";
    internal const string ExpectedApplicationDescription = "Wisp";
    internal const string ExpectedInstallerDescription = "Wisp installer";
    internal const int MaximumRequestBytes = 64 * 1024;

    internal static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan InstallerExitTimeout = TimeSpan.FromMinutes(15);

    internal static IReadOnlyList<string> InstallerArguments { get; } = Array.AsReadOnly(
    [
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/SP-",
        "/CURRENTUSER",
        "/CLOSEAPPLICATIONS",
        "/WISPUPDATE"
    ]);
}
