using Microsoft.Win32;

namespace Wisp.Updater;

internal interface IInstalledApplicationValidator
{
    void Validate(string applicationPath, StableVersion targetVersion);

    bool IsValid(string applicationPath, StableVersion version);
}

internal interface IInstallationRegistrationReader
{
    string? ReadInstallLocation();
}

internal sealed class WindowsInstallationRegistrationReader : IInstallationRegistrationReader
{
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{A8FC0D58-11E3-4B25-B78D-3B98E9855473}_is1";

    public string? ReadInstallLocation()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: false);
        return key?.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string;
    }
}

internal sealed class InstalledApplicationRegistrationValidator
{
    private readonly IInstallationRegistrationReader _registrationReader;

    internal InstalledApplicationRegistrationValidator(IInstallationRegistrationReader registrationReader)
    {
        _registrationReader = registrationReader;
    }

    internal void Validate(string applicationPath)
    {
        try
        {
            var installLocation = _registrationReader.ReadInstallLocation();
            if (string.IsNullOrWhiteSpace(installLocation)
                || !Path.IsPathFullyQualified(installLocation))
            {
                throw RegistrationFailure();
            }

            var registeredApplicationPath = Path.Combine(
                Path.GetFullPath(installLocation),
                UpdaterConstants.ApplicationFileName);
            if (!UpdatePathSafety.PathsEqual(registeredApplicationPath, applicationPath))
            {
                throw RegistrationFailure();
            }
        }
        catch (UpdateFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           System.Security.SecurityException or ArgumentException or
                                           NotSupportedException)
        {
            throw RegistrationFailure(exception);
        }
    }

    private static UpdateFailureException RegistrationFailure(Exception? innerException = null) =>
        new(
            UpdaterExitCode.InvalidRequest,
            "UPDATE_INSTALL_REGISTRATION",
            "The installed Wisp location could not be verified.",
            innerException);
}

internal sealed class InstalledApplicationValidator : IInstalledApplicationValidator
{
    private readonly IPortableExecutableInspector _executableInspector;

    internal InstalledApplicationValidator(IPortableExecutableInspector executableInspector)
    {
        _executableInspector = executableInspector;
    }

    public void Validate(string applicationPath, StableVersion targetVersion)
    {
        if (!IsValid(applicationPath, targetVersion))
        {
            throw RestartFailure();
        }
    }

    public bool IsValid(string applicationPath, StableVersion version)
    {
        try
        {
            using var stream = new FileStream(
                applicationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            var identity = _executableInspector.Inspect(stream, applicationPath);
            return identity.IsExecutable
                && string.Equals(identity.ProductName, UpdaterConstants.ExpectedProductName, StringComparison.Ordinal)
                && string.Equals(
                    identity.FileDescription,
                    UpdaterConstants.ExpectedApplicationDescription,
                    StringComparison.Ordinal)
                && version.MatchesVersionResource(identity.ProductVersion)
                && version.MatchesVersionResource(identity.FileVersion);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static UpdateFailureException RestartFailure(Exception? innerException = null) =>
        new(
            UpdaterExitCode.RestartFailure,
            "UPDATE_INSTALLED_APP_INVALID",
            "The installer finished, but the installed Wisp executable could not be verified.",
            innerException);
}
