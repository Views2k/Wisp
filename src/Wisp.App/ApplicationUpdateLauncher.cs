using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Wisp.Update;

namespace Wisp.App;

internal enum ApplicationUpdateHandoffState
{
    Ready,
    HelperExited,
    TimedOut,
    Cancelled
}

// Outcome of App.TryBeginApplicationUpdateAsync. HelperUnavailable marks a failure that no later
// attempt in this session can fix: Wisp.Updater.exe is missing from the application directory.
// HelperRejected marks a helper that started and rejected the request or this installation before
// it was ready; the same checks fail the same way for the rest of the session, so automatic
// attempts stop as well. A manual install from the dashboard still tries once per click.
internal readonly record struct ApplicationUpdateStartResult(bool Started, string Error, bool HelperUnavailable = false,
    bool HelperRejected = false)
{
    internal AutomaticUpdateApplyOutcome Outcome => Started ? AutomaticUpdateApplyOutcome.Started
        : HelperUnavailable || HelperRejected ? AutomaticUpdateApplyOutcome.Unavailable : AutomaticUpdateApplyOutcome.Retry;

    public static implicit operator ApplicationUpdateStartResult((bool Started, string Error) result) =>
        new(result.Started, result.Error);
}

internal readonly record struct ApplicationUpdateResultStatus(string Status, string Action, string Outcome = "unknown", Supplementary.SupplementaryIncident? Incident = null,
    string? SourceVersion = null, string? TargetVersion = null);

// What the update helper reported when it exited before signalling readiness. Status is fixed local
// copy chosen from the helper's error code; text from the helper's record is never rendered.
// Permanent means the same handoff fails the same way for the rest of this session.
internal readonly record struct ApplicationUpdateHelperExit(Supplementary.SupplementaryIncident Incident, string Status, bool Permanent);

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

    // The helper's exit code once it has exited; null while it runs or when the process is gone.
    internal int? ExitCode
    {
        get
        {
            try
            {
                return _helper.HasExited ? _helper.ExitCode : null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                                               System.ComponentModel.Win32Exception or
                                               NotSupportedException)
            {
                return null;
            }
        }
    }

    internal async Task<ApplicationUpdateHandoffState> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellation.IsCancellationRequested) return ApplicationUpdateHandoffState.Cancelled;
        var readyTask = Task.Run(() => _readyEvent.WaitOne(timeout));
        var timeoutTask = Task.Delay(timeout);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellation.Register(() => cancelled.TrySetResult());
        var completed = await Task.WhenAny(readyTask, _helperExited.Task, timeoutTask, cancelled.Task);
        if (!cancellation.IsCancellationRequested && readyTask.IsCompletedSuccessfully && readyTask.Result)
        {
            return ApplicationUpdateHandoffState.Ready;
        }

        // Release the bounded worker before this handoff can be disposed. At this
        // point the helper either failed or exceeded the app's readiness window.
        _readyEvent.Set();
        await readyTask;
        if (cancellation.IsCancellationRequested) return ApplicationUpdateHandoffState.Cancelled;
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

    // A record written this long before the helper was launched, or after the report, belongs to
    // another attempt and is left for the next launch to consume.
    private static readonly TimeSpan HelperResultSkew = TimeSpan.FromMinutes(1);

    // Helper checks about this installation's own identity. Their result cannot change while this
    // Wisp process runs, whichever exit code class the helper reported them under.
    private static readonly HashSet<string> SessionPermanentHelperReasons = new(StringComparer.Ordinal)
    { "update-install-registration", "update-application-path", "update-installed-app-invalid" };

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

        var helperPath = ApplicationUpdateStaging.StageUpdater(attemptDirectory);
        var readyToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var readyEventName = UpdateApplyContract.CreateReadyEventName(readyToken);
        var request = new UpdateApplyRequest(
            installerPath,
            installer.Version.ToString(),
            sourceVersion.ToString(),
            Environment.ProcessId,
            Path.GetFullPath(applicationPath),
            installer.Sha256,
            installer.Size,
            readyEventName);

        return StartHelper(helperPath, request);
    }

    internal static bool IsHelperUnavailable(Exception exception) =>
        exception is ApplicationUpdateHelperUnavailableException;

    internal static string DescribeStartFailure(Exception exception) =>
        IsHelperUnavailable(exception)
            ? "This copy of Wisp is missing a usable update helper. Download and run the full installer from wispoverlay.com to repair it. Wisp stayed open and no files were installed."
            : "Wisp could not start the verified update. Try again, or download and run the full installer from wispoverlay.com. Wisp stayed open and no files were installed.";

    internal static ApplicationUpdateHandoff StartHelper(string helperPath, UpdateApplyRequest request)
    {
        var attemptDirectory = Path.GetDirectoryName(request.StagedInstallerPath)!;
        var requestPath = Path.Combine(attemptDirectory, "apply-request.json");
        var temporaryRequestPath = Path.Combine(
            attemptDirectory,
            $".apply-request.{Guid.NewGuid():N}.tmp");
        var readyEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            request.ReadyEventName,
            out var createdNew);
        if (!createdNew)
        {
            readyEvent.Dispose();
            throw new InvalidOperationException("Wisp could not create a private update handoff.");
        }

        var requestCreated = false;
        try
        {
            if (File.Exists(requestPath) || Directory.Exists(requestPath))
            {
                var attributes = File.GetAttributes(requestPath);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new IOException("The staged update request is invalid.");
                }
                File.Delete(requestPath);
            }

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
            requestCreated = true;
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
            if (requestCreated)
            {
                TryDelete(requestPath);
            }
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
        var resultPath = ResultPath(localApplicationDataPath);
        if (!File.Exists(resultPath))
        {
            return false;
        }

        var consumedPath = Path.Combine(
            Path.GetDirectoryName(resultPath)!,
            $".update-result.{Guid.NewGuid():N}.consumed");
        try
        {
            if (!IsRegularFile(resultPath))
            {
                return false;
            }

            File.Move(resultPath, consumedPath, overwrite: false);
            if (!TryReadRecord(consumedPath, out var record))
            {
                return false;
            }

            if (record.State == "installed" && record.TargetVersion is { } targetVersion)
            {
                var startedTarget = targetVersion == ApplicationVersionInfo.MachineVersion;
                result = new ApplicationUpdateResultStatus(
                    startedTarget ? $"Wisp {targetVersion} was installed successfully." : "The update needs another attempt. Check for updates to try again.",
                    "Check again", startedTarget ? "success" : "unknown",
                    SourceVersion: record.SourceVersion, TargetVersion: targetVersion);
                return true;
            }

            if (record.State == "failed")
            {
                // Do not render text from the helper record. A fixed local message
                // keeps the UI safe even if a same-user process edits the file.
                result = new ApplicationUpdateResultStatus(
                    FailureStatus(record.RecoveryState),
                    "Try again", "failure", UpdateResultIncident(record, "update-installed"), record.SourceVersion, record.TargetVersion);
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

    internal static ApplicationUpdateHelperExit DescribeHelperExit(int? exitCode, DateTimeOffset launchedAt) =>
        DescribeHelperExit(exitCode, launchedAt,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DateTimeOffset.UtcNow);

    // The helper writes update-result.json before it exits, so a record from this handoff names the
    // check that failed. It is consumed here so the next launch does not report the same failure a
    // second time under the "installed" stage. An older or unreadable record is left for that launch
    // and only the exit code is reported. Automatic attempts stop for the session when the helper
    // rejected the request itself (UpdateApplyContract.InvalidRequestExitCode: the same request
    // fails the same way again) or a check about this installation's identity failed.
    internal static ApplicationUpdateHelperExit DescribeHelperExit(int? exitCode, DateTimeOffset launchedAt,
        string localApplicationDataPath, DateTimeOffset now)
    {
        var resultPath = ResultPath(localApplicationDataPath);
        UpdateResultRecord? record = null;
        try
        {
            if (File.Exists(resultPath) && IsRegularFile(resultPath)
                && TryReadRecord(resultPath, out var fresh)
                && fresh.State == "failed"
                && (fresh.RecordedAt is not { } recordedAt
                    || recordedAt >= launchedAt - HelperResultSkew && recordedAt <= now + HelperResultSkew))
            {
                TryDelete(resultPath);
                record = fresh;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or ArgumentException)
        {
            // Without a readable record the exit code is the only evidence.
        }

        var reason = record is null ? "update-unexpected-failure" : HelperFailureReason(record.ErrorCode);
        var permanent = exitCode == UpdateApplyContract.InvalidRequestExitCode || SessionPermanentHelperReasons.Contains(reason);
        var incident = record is null
            ? Supplementary.SupplementaryIncidentSchema.Create("update", reason, "update-install-handoff",
                exitCode is { } code ? unchecked((uint)code) : null)
            : UpdateResultIncident(record, "update-install-handoff");
        return new(incident, HelperExitStatus(reason, permanent), permanent);
    }

    private static string ResultPath(string localApplicationDataPath) =>
        Path.Combine(localApplicationDataPath, "Wisp", ResultFileName);

    private static bool IsRegularFile(string path) =>
        (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;

    // The bounded fields Wisp reads from the helper's record. Its free text stays in the file.
    private sealed record UpdateResultRecord(string State, string? ErrorCode, string? RecoveryState, uint? NativeErrorCode,
        uint? InstallerExitCode, string? SourceVersion, string? TargetVersion, DateTimeOffset? RecordedAt);

    private static bool TryReadRecord(string path, [NotNullWhen(true)] out UpdateResultRecord? record)
    {
        record = null;
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumResultBytes)
        {
            return false;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4 * 1024,
            FileOptions.SequentialScan);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out var schemaVersion)
            || schemaVersion != 1
            || !root.TryGetProperty("state", out var stateElement)
            || stateElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? Text(string name) => root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        string? VersionField(string name) => Text(name) is { } text && SemanticVersion.TryParse(text, out var parsed) ? parsed.ToString() : null;
        uint? Number(string name) => root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetUInt32(out var number) ? number : null;
        DateTimeOffset? recordedAt = root.TryGetProperty("recordedAtUtc", out var recorded) && recorded.ValueKind == JsonValueKind.String &&
            recorded.TryGetDateTimeOffset(out var at) ? at : null;
        record = new(stateElement.GetString()!, Text("errorCode"), Text("recoveryState"), Number("nativeErrorCode"), Number("installerExitCode"),
            VersionField("sourceVersion"), VersionField("targetVersion"), recordedAt);
        return true;
    }

    private static Supplementary.SupplementaryIncident UpdateResultIncident(UpdateResultRecord record, string failureStage)
    {
        var recovery = record.RecoveryState is { } state && Supplementary.SupplementaryIncidentSchema.UpdateRecoveries.Contains(state) ? state : "unknown";
        return Supplementary.SupplementaryIncidentSchema.Create("update", HelperFailureReason(record.ErrorCode), failureStage, record.NativeErrorCode,
            update: new(recovery, record.InstallerExitCode));
    }

    // The helper's UPDATE_* error code in the catalog's kebab-case form; codes outside the catalog stay unexpected.
    private static string HelperFailureReason(string? errorCode)
    {
        var reason = errorCode?.ToLowerInvariant().Replace('_', '-');
        return reason is not null && Supplementary.SupplementaryIncidentSchema.UpdateFailureReasons.Contains(reason)
            ? reason : "update-unexpected-failure";
    }

    private static string HelperExitStatus(string reason, bool permanent)
    {
        const string suffix = " Wisp stayed open and no files were installed.";
        return reason switch
        {
            "update-install-registration" =>
                "This copy of Wisp does not match the Wisp installation registered in Windows. Download and run the full installer from wispoverlay.com to reinstall it." + suffix,
            "update-application-path" or "update-installed-app-invalid" =>
                "The update helper could not verify this Wisp installation. Download and run the full installer from wispoverlay.com to reinstall it." + suffix,
            _ when permanent =>
                "The update helper rejected the prepared update. Download and run the full installer from wispoverlay.com to update." + suffix,
            _ =>
                "The update helper stopped before the update could start. Try again, or download and run the full installer from wispoverlay.com." + suffix
        };
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
