namespace Wisp.Updater;

internal sealed class UpdaterWorkflow
{
    private readonly IInstallerArtifactValidator _installerValidator;
    private readonly IInstalledApplicationValidator _applicationValidator;
    private readonly InstalledApplicationRegistrationValidator _registrationValidator;
    private readonly IUpdaterProcessHost _processHost;

    internal UpdaterWorkflow(
        IInstallerArtifactValidator installerValidator,
        IInstalledApplicationValidator applicationValidator,
        InstalledApplicationRegistrationValidator registrationValidator,
        IUpdaterProcessHost processHost)
    {
        _installerValidator = installerValidator;
        _applicationValidator = applicationValidator;
        _registrationValidator = registrationValidator;
        _processHost = processHost;
    }

    internal ValidatedInstaller Prepare(ValidatedUpdateRequest request)
    {
        _registrationValidator.Validate(request.AppExecutablePath);
        _applicationValidator.Validate(request.AppExecutablePath, request.SourceVersion);
        _processHost.ValidateParentIdentity(request.ParentProcessId, request.AppExecutablePath);
        return _installerValidator.Validate(request);
    }

    internal void Apply(
        ValidatedUpdateRequest request,
        ValidatedInstaller installer,
        Action parentExited)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(parentExited);
        _processHost.WaitForParentExit(
            request.ParentProcessId,
            request.AppExecutablePath,
            UpdaterConstants.ParentExitTimeout);
        parentExited();
        _processHost.RunInstaller(
            installer.Path,
            UpdaterConstants.InstallerArguments,
            UpdaterConstants.InstallerExitTimeout);
        _applicationValidator.Validate(request.AppExecutablePath, request.TargetVersion);
    }

    internal bool CanRestart(ValidatedUpdateRequest request) =>
        _applicationValidator.IsValid(request.AppExecutablePath, request.SourceVersion)
        || _applicationValidator.IsValid(request.AppExecutablePath, request.TargetVersion);

    internal void Restart(ValidatedUpdateRequest request)
    {
        _processHost.StartApplication(request.AppExecutablePath);
    }
}
