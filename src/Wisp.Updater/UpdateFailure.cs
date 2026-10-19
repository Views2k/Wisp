namespace Wisp.Updater;

internal enum UpdaterExitCode
{
    Success = 0,
    UnexpectedFailure = 1,
    InvalidRequest = Wisp.Update.UpdateApplyContract.InvalidRequestExitCode,
    ParentProcessFailure = 3,
    InstallerValidationFailure = 4,
    InstallerExecutionFailure = 5,
    RestartFailure = 6
}

internal sealed class UpdateFailureException : Exception
{
    internal UpdateFailureException(
        UpdaterExitCode exitCode,
        string errorCode,
        string safeMessage,
        Exception? innerException = null,
        bool recoveryIsSafe = true, uint? installerExitCode = null)
        : base(safeMessage, innerException)
    {
        ExitCode = exitCode;
        ErrorCode = errorCode;
        SafeMessage = safeMessage;
        RecoveryIsSafe = recoveryIsSafe;
        InstallerExitCode = installerExitCode;
    }

    internal UpdaterExitCode ExitCode { get; }

    internal string ErrorCode { get; }

    internal string SafeMessage { get; }

    internal bool RecoveryIsSafe { get; }

    internal uint? InstallerExitCode { get; }
}
