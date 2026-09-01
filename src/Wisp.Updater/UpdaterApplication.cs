namespace Wisp.Updater;

internal interface IUpdateReadySignal
{
    void Signal(string eventName);
}

internal sealed class WindowsUpdateReadySignal : IUpdateReadySignal
{
    public void Signal(string eventName)
    {
        try
        {
            using var readyEvent = EventWaitHandle.OpenExisting(eventName);
            readyEvent.Set();
        }
        catch (Exception exception) when (exception is WaitHandleCannotBeOpenedException or
                                           UnauthorizedAccessException or IOException)
        {
            throw new UpdateFailureException(
                UpdaterExitCode.ParentProcessFailure,
                "UPDATE_READY_SIGNAL",
                "Wisp did not accept the prepared update.",
                exception);
        }
    }
}

internal sealed class UpdaterApplication
{
    internal int Run(string[] args)
    {
        var localApplicationDataPath = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var resultFile = new UpdateResultFile(localApplicationDataPath);
        ValidatedUpdateRequest? request = null;
        UpdaterWorkflow? workflow = null;
        var parentExited = false;

        try
        {
            var requestPath = CommandLine.ParseApplyRequestPath(args);
            var stagingRoot = Path.Combine(localApplicationDataPath, "Wisp", "Updates");
            var updaterExecutablePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "Wisp.Updater.exe");
            var requestReader = new ApplyRequestReader(
                stagingRoot,
                updaterExecutablePath,
                Environment.ProcessId);
            request = requestReader.Read(requestPath);
            var executableInspector = new WindowsPortableExecutableInspector();
            workflow = new UpdaterWorkflow(
                new InstallerArtifactValidator(executableInspector),
                new InstalledApplicationValidator(executableInspector),
                new InstalledApplicationRegistrationValidator(
                    new WindowsInstallationRegistrationReader()),
                new WindowsUpdaterProcessHost());
            using var installer = workflow.Prepare(request);
            new WindowsUpdateReadySignal().Signal(request.ReadyEventName);
            workflow.Apply(request, installer, () => parentExited = true);
            resultFile.TryWriteSuccess(request.SourceVersion, request.TargetVersion);
            workflow.Restart(request);
            return (int)UpdaterExitCode.Success;
        }
        catch (UpdateFailureException exception)
        {
            var recoveryState = RecoverIfSafe(
                request,
                workflow,
                parentExited,
                exception.RecoveryIsSafe);
            resultFile.TryWriteFailure(
                request?.SourceVersion,
                request?.TargetVersion,
                exception.ErrorCode,
                exception.SafeMessage,
                recoveryState);
            return (int)exception.ExitCode;
        }
        catch
        {
            var recoveryState = RecoverIfSafe(
                request,
                workflow,
                parentExited,
                recoveryIsSafe: false);
            resultFile.TryWriteFailure(
                request?.SourceVersion,
                request?.TargetVersion,
                "UPDATE_UNEXPECTED_FAILURE",
                "The update helper stopped because of an unexpected error.",
                recoveryState);
            return (int)UpdaterExitCode.UnexpectedFailure;
        }
    }

    private static UpdateRecoveryState RecoverIfSafe(
        ValidatedUpdateRequest? request,
        UpdaterWorkflow? workflow,
        bool parentExited,
        bool recoveryIsSafe)
    {
        if (!parentExited || request is null || workflow is null)
        {
            return UpdateRecoveryState.ApplicationStillRunning;
        }

        if (!recoveryIsSafe)
        {
            return UpdateRecoveryState.Deferred;
        }

        try
        {
            if (!workflow.CanRestart(request))
            {
                return UpdateRecoveryState.NoVerifiedInstallation;
            }

            workflow.Restart(request);
            return UpdateRecoveryState.Restarted;
        }
        catch
        {
            return UpdateRecoveryState.RestartFailed;
        }
    }
}
