using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Wisp.Update;

namespace Wisp.App;

internal enum ApplicationUpdateHandoffState
{
    Ready,
    HelperExited,
    TimedOut
}

internal readonly record struct ApplicationUpdateResultStatus(string Status, string Action);

internal sealed class ApplicationUpdateHandoff : IDisposable
{
    private readonly Process _helper;
    private readonly EventWaitHandle _readyEvent;
    private readonly TaskCompletionSource _helperExited = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    internal ApplicationUpdateHandoff(Process helper, EventWaitHandle readyEvent)
    {
        _helper = helper;
        _readyEvent = readyEvent;
        _helper.EnableRaisingEvents = true;
        _helper.Exited += OnHelperExited;
        if (_helper.HasExited)
        {
            _helperExited.TrySetResult();
        }
    }

    internal async Task<ApplicationUpdateHandoffState> WaitUntilReadyAsync(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var readyTask = Task.Run(() => _readyEvent.WaitOne(timeout));
        var timeoutTask = Task.Delay(timeout);
        var completed = await Task.WhenAny(readyTask, _helperExited.Task, timeoutTask);
        if (readyTask.IsCompletedSuccessfully && readyTask.Result)
        {
            return ApplicationUpdateHandoffState.Ready;
        }

        // Release the bounded worker before this handoff can be disposed. At this
        // point the helper either failed or exceeded the app's readiness window.
        _readyEvent.Set();
        await readyTask;
        return completed == _helperExited.Task
            ? ApplicationUpdateHandoffState.HelperExited
            : ApplicationUpdateHandoffState.TimedOut;
    }

    internal void StopIfRunning()
    {
        try
        {
            if (!_helper.HasExited)
            {
                _helper.Kill(entireProcessTree: true);
                _helper.WaitForExit(5_000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                           System.ComponentModel.Win32Exception or
                                           NotSupportedException)
        {
            // The parent app stays open, so a helper that did not acknowledge
            // readiness cannot cross the parent-exit gate and install anything.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _helper.Exited -= OnHelperExited;
        _helper.Dispose();
        _readyEvent.Dispose();
    }

    private void OnHelperExited(object? sender, EventArgs e) => _helperExited.TrySetResult();
}

internal static class ApplicationUpdateLauncher
{
    private const int MaximumResultBytes = 16 * 1024;
    private const string ResultFileName = "update-result.json";

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    internal static ApplicationUpdateHandoff Launch(VerifiedInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);
        var installerPath = Path.GetFullPath(installer.StagedPath);
        var attemptDirectory = ApplicationUpdateStaging.RequireAttemptDirectory(
            Path.GetDirectoryName(installerPath)
            ?? throw new InvalidOperationException("The verified installer path is invalid."));
        if (!string.Equals(Path.GetDirectoryName(installerPath), attemptDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The verified installer is not directly inside its update attempt.");
        }

        var applicationPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Wisp could not resolve its installed executable path.");
        if (!string.Equals(Path.GetFileName(applicationPath), "Wisp.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Application updates are available only from an installed Wisp executable.");
        }

        var assemblyVersion = typeof(ApplicationUpdateLauncher).Assembly.GetName().Version
            ?? throw new InvalidOperationException("Wisp could not resolve its installed version.");
        var sourceVersion = SemanticVersion.FromSystemVersion(assemblyVersion);
        if (sourceVersion >= installer.Version)
        {
            throw new InvalidOperationException("The downloaded installer is not newer than this Wisp installation.");
        }

        var readyToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var readyEventName = UpdateApplyContract.CreateReadyEventName(readyToken);
        var readyEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            readyEventName,
            out var createdNew);
        if (!createdNew)
        {
            readyEvent.Dispose();
            throw new InvalidOperationException("Wisp could not create a private update handoff.");
        }

        var helperPath = ApplicationUpdateStaging.StageUpdater(attemptDirectory);
        var requestPath = Path.Combine(attemptDirectory, "apply-request.json");
        if (File.Exists(requestPath))
        {
            var attributes = File.GetAttributes(requestPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                readyEvent.Dispose();
                throw new IOException("The staged update request is invalid.");
            }
            File.Delete(requestPath);
        }
        var temporaryRequestPath = Path.Combine(
            attemptDirectory,
            $".apply-request.{Guid.NewGuid():N}.tmp");
        var request = new UpdateApplyRequest(
            installerPath,
            installer.Version.ToString(),
            sourceVersion.ToString(),
            Environment.ProcessId,
            Path.GetFullPath(applicationPath),
            installer.Sha256,
            installer.Size,
            readyEventName);

        try
        {
            using (var stream = new FileStream(temporaryRequestPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 16 * 1024,
                Options = FileOptions.WriteThrough
            }))
            {
                JsonSerializer.Serialize(stream, request, RequestJson);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryRequestPath, requestPath, overwrite: false);
            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                WorkingDirectory = attemptDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--apply");
            startInfo.ArgumentList.Add(requestPath);
            var helper = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The Wisp update helper could not be started.");
            return new ApplicationUpdateHandoff(helper, readyEvent);
        }
        catch
        {
            readyEvent.Dispose();
            TryDelete(temporaryRequestPath);
            TryDelete(requestPath);
            TryDelete(helperPath);
            throw;
        }
    }

    internal static bool TryConsumeResult(out ApplicationUpdateResultStatus result) =>
        TryConsumeResult(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            out result);

    internal static bool TryConsumeResult(
        string localApplicationDataPath,
        out ApplicationUpdateResultStatus result)
    {
        result = default;
        var resultPath = Path.Combine(localApplicationDataPath, "Wisp", ResultFileName);
        if (!File.Exists(resultPath))
        {
            return false;
        }

        var consumedPath = Path.Combine(
            Path.GetDirectoryName(resultPath)!,
            $".update-result.{Guid.NewGuid():N}.consumed");
        try
        {
            var attributes = File.GetAttributes(resultPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            File.Move(resultPath, consumedPath, overwrite: false);
            var info = new FileInfo(consumedPath);
            if (info.Length <= 0 || info.Length > MaximumResultBytes)
            {
                return false;
            }

            using var stream = new FileStream(
                consumedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4 * 1024,
                FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schema)
                || !schema.TryGetInt32(out var schemaVersion)
                || schemaVersion != 1
                || !root.TryGetProperty("state", out var stateElement)
                || stateElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var state = stateElement.GetString();
            if (string.Equals(state, "installed", StringComparison.Ordinal)
                && root.TryGetProperty("targetVersion", out var targetElement)
                && targetElement.ValueKind == JsonValueKind.String
                && SemanticVersion.TryParse(targetElement.GetString(), out var targetVersion))
            {
                result = new ApplicationUpdateResultStatus(
                    $"Wisp {targetVersion} was installed successfully.",
                    "Check again");
                return true;
            }

            if (string.Equals(state, "failed", StringComparison.Ordinal))
            {
                // Do not render text from the helper record. A fixed local message
                // keeps the UI safe even if a same-user process edits the file.
                var recoveryState = root.TryGetProperty("recoveryState", out var recoveryElement)
                    && recoveryElement.ValueKind == JsonValueKind.String
                    ? recoveryElement.GetString()
                    : null;
                result = new ApplicationUpdateResultStatus(
                    FailureStatus(recoveryState),
                    "Try again");
                return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or ArgumentException)
        {
            return false;
        }
        finally
        {
            TryDelete(consumedPath);
        }
    }

    private static string FailureStatus(string? recoveryState) => recoveryState switch
    {
        "application-still-running" =>
            "The previous update did not start. Wisp remained open.",
        "restarted" =>
            "The previous update did not complete. Wisp restarted the verified installation.",
        "deferred" =>
            "The installer may still be closing. Wait for it to finish, then start Wisp again.",
        "no-verified-installation" =>
            "The update did not complete and the installed copy could not be verified. Reinstall Wisp from the official release.",
        "restart-failed" =>
            "The update did not complete. A verified Wisp installation remains, but it could not be restarted.",
        _ =>
            "The previous update did not complete. Check the installed Wisp copy before trying again."
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A later update staging cleanup can retry abandoned temporary files.
        }
    }
}
