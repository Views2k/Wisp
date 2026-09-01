using System.ComponentModel;
using System.Diagnostics;

namespace Wisp.Updater;

internal interface IUpdaterProcessHost
{
    void ValidateParentIdentity(int parentProcessId, string expectedApplicationPath);

    void WaitForParentExit(int parentProcessId, string expectedApplicationPath, TimeSpan timeout);

    void RunInstaller(string installerPath, IReadOnlyList<string> arguments, TimeSpan timeout);

    void StartApplication(string applicationPath);
}

internal sealed class WindowsUpdaterProcessHost : IUpdaterProcessHost
{
    public void ValidateParentIdentity(int parentProcessId, string expectedApplicationPath)
    {
        using var parentProcess = GetVerifiedParentProcess(parentProcessId, expectedApplicationPath);
        if (parentProcess is null)
        {
            throw ParentFailure(
                "UPDATE_PARENT_EXITED",
                "Wisp closed before the update helper was ready.");
        }
    }

    public void WaitForParentExit(int parentProcessId, string expectedApplicationPath, TimeSpan timeout)
    {
        using var parentProcess = GetVerifiedParentProcess(parentProcessId, expectedApplicationPath);
        if (parentProcess is null)
        {
            return;
        }

        try
        {
            if (!parentProcess.WaitForExit(TimeoutMilliseconds(timeout)))
            {
                throw ParentFailure(
                    "UPDATE_PARENT_TIMEOUT",
                    "Wisp did not exit before the update timeout.");
            }
        }
        catch (InvalidOperationException)
        {
            // The verified process exited between inspection and waiting.
        }
    }

    public void RunInstaller(string installerPath, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        Process? installer = null;
        try
        {
            installer = Process.Start(CreateInstallerStartInfo(installerPath, arguments))
                ?? throw InstallerFailure(
                    "UPDATE_INSTALLER_START_FAILED",
                    "The Wisp installer could not be started.");
            if (!installer.WaitForExit(TimeoutMilliseconds(timeout)))
            {
                var recoveryIsSafe = TryStopInstaller(installer);
                throw InstallerFailure(
                    recoveryIsSafe ? "UPDATE_INSTALLER_TIMEOUT" : "UPDATE_INSTALLER_STOP_FAILED",
                    recoveryIsSafe
                        ? "The Wisp installer did not finish before the update timeout."
                        : "The Wisp installer did not finish and could not be stopped safely.",
                    recoveryIsSafe: recoveryIsSafe);
            }

            if (installer.ExitCode != 0)
            {
                throw InstallerFailure(
                    "UPDATE_INSTALLER_EXIT_FAILED",
                    "The Wisp installer reported that the update failed.");
            }
        }
        catch (UpdateFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            var recoveryIsSafe = installer is null || TryStopInstaller(installer);
            throw InstallerFailure(
                "UPDATE_INSTALLER_START_FAILED",
                "The Wisp installer could not be started.",
                exception,
                recoveryIsSafe);
        }
        finally
        {
            installer?.Dispose();
        }
    }

    public void StartApplication(string applicationPath)
    {
        try
        {
            _ = Process.Start(CreateApplicationStartInfo(applicationPath))
                ?? throw RestartFailure();
        }
        catch (UpdateFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw RestartFailure(exception);
        }
    }

    internal static ProcessStartInfo CreateInstallerStartInfo(
        string installerPath,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    internal static ProcessStartInfo CreateApplicationStartInfo(string applicationPath) =>
        new()
        {
            FileName = applicationPath,
            WorkingDirectory = Path.GetDirectoryName(applicationPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false
        };

    private static Process? GetVerifiedParentProcess(
        int parentProcessId,
        string expectedApplicationPath)
    {
        Process parentProcess;
        try
        {
            parentProcess = Process.GetProcessById(parentProcessId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            var parentPath = parentProcess.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(parentPath)
                || !UpdatePathSafety.PathsEqual(parentPath, expectedApplicationPath))
            {
                throw ParentFailure(
                    "UPDATE_PARENT_IDENTITY",
                    "The update request did not identify the running Wisp process.");
            }

            return parentProcess;
        }
        catch (InvalidOperationException)
        {
            parentProcess.Dispose();
            return null;
        }
        catch (UpdateFailureException)
        {
            parentProcess.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or NotSupportedException)
        {
            parentProcess.Dispose();
            throw ParentFailure(
                "UPDATE_PARENT_INSPECTION",
                "The running Wisp process could not be verified.",
                exception);
        }
    }

    private static int TimeoutMilliseconds(TimeSpan timeout) =>
        checked((int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue));

    private static bool TryStopInstaller(Process installer)
    {
        try
        {
            if (installer.HasExited)
            {
                return true;
            }

            installer.Kill(entireProcessTree: true);
            return installer.WaitForExit(5_000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or
                                           NotSupportedException)
        {
            try
            {
                return installer.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    private static UpdateFailureException ParentFailure(
        string errorCode,
        string safeMessage,
        Exception? innerException = null) =>
        new(UpdaterExitCode.ParentProcessFailure, errorCode, safeMessage, innerException);

    private static UpdateFailureException InstallerFailure(
        string errorCode,
        string safeMessage,
        Exception? innerException = null,
        bool recoveryIsSafe = true) =>
        new(
            UpdaterExitCode.InstallerExecutionFailure,
            errorCode,
            safeMessage,
            innerException,
            recoveryIsSafe);

    private static UpdateFailureException RestartFailure(Exception? innerException = null) =>
        new(
            UpdaterExitCode.RestartFailure,
            "UPDATE_RESTART_FAILED",
            "The update installed, but Wisp could not be restarted.",
            innerException);
}
