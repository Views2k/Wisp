using System.Text.Json;

namespace Wisp.Updater;

internal enum UpdateRecoveryState
{
    ApplicationStillRunning,
    Restarted,
    Deferred,
    NoVerifiedInstallation,
    RestartFailed
}

internal sealed class UpdateResultFile
{
    private const int SchemaVersion = 1;
    private readonly string _resultPath;

    internal UpdateResultFile(string localApplicationDataPath)
    {
        _resultPath = Path.Combine(localApplicationDataPath, "Wisp", "update-result.json");
    }

    internal string ResultPath => _resultPath;

    internal void TryWriteSuccess(StableVersion sourceVersion, StableVersion targetVersion) =>
        TryWrite("installed", sourceVersion.ToString(), targetVersion.ToString(), string.Empty,
            "The update was installed successfully.");

    internal void TryWriteFailure(
        StableVersion? sourceVersion,
        StableVersion? targetVersion,
        string errorCode,
        string safeMessage,
        UpdateRecoveryState recoveryState) =>
        TryWrite(
            "failed",
            sourceVersion?.ToString() ?? string.Empty,
            targetVersion?.ToString() ?? string.Empty,
            errorCode,
            safeMessage,
            RecoveryStateValue(recoveryState));

    private void TryWrite(
        string state,
        string sourceVersion,
        string targetVersion,
        string errorCode,
        string safeMessage,
        string recoveryState = "not-needed")
    {
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_resultPath);
            if (directory is null)
            {
                return;
            }

            Directory.CreateDirectory(directory);
            if (File.Exists(_resultPath)
                && (File.GetAttributes(_resultPath) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            temporaryPath = Path.Combine(directory, $".update-result.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4 * 1024,
                Options = FileOptions.WriteThrough
            }))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", SchemaVersion);
                writer.WriteString("state", state);
                writer.WriteString("sourceVersion", sourceVersion);
                writer.WriteString("targetVersion", targetVersion);
                writer.WriteString("errorCode", errorCode);
                writer.WriteString("message", safeMessage);
                writer.WriteString("recoveryState", recoveryState);
                writer.WriteString("recordedAtUtc", DateTimeOffset.UtcNow);
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _resultPath, overwrite: true);
            temporaryPath = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Updating the diagnostic must never mask the original update result.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A later staging cleanup can remove an abandoned temporary file.
                }
            }
        }
    }

    private static string RecoveryStateValue(UpdateRecoveryState state) => state switch
    {
        UpdateRecoveryState.ApplicationStillRunning => "application-still-running",
        UpdateRecoveryState.Restarted => "restarted",
        UpdateRecoveryState.Deferred => "deferred",
        UpdateRecoveryState.NoVerifiedInstallation => "no-verified-installation",
        UpdateRecoveryState.RestartFailed => "restart-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
