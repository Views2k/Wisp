using System.Text.Json;
using Wisp.Update;

namespace Wisp.Updater;

internal sealed record ValidatedUpdateRequest(
    string RequestPath,
    string StagedInstallerPath,
    StableVersion TargetVersion,
    StableVersion SourceVersion,
    int ParentProcessId,
    string AppExecutablePath,
    string ExpectedSha256,
    long ExpectedSizeBytes,
    string ReadyEventName);

internal sealed class ApplyRequestReader
{
    private const int MaximumJsonDepth = 8;

    private readonly string _stagingRoot;
    private readonly string _updaterExecutablePath;
    private readonly int _currentProcessId;

    internal ApplyRequestReader(string stagingRoot, string updaterExecutablePath, int currentProcessId)
    {
        _stagingRoot = UpdatePathSafety.RequireAbsolutePath(stagingRoot, "UPDATE_REQUEST_ROOT");
        _updaterExecutablePath = UpdatePathSafety.RequireAbsolutePath(
            updaterExecutablePath,
            "UPDATE_HELPER_PATH");
        _currentProcessId = currentProcessId;
    }

    internal ValidatedUpdateRequest Read(string requestPath)
    {
        var fullRequestPath = UpdatePathSafety.RequireAbsolutePath(requestPath, "UPDATE_REQUEST_PATH");
        if (!UpdatePathSafety.IsContainedBy(fullRequestPath, _stagingRoot)
            || !Path.GetExtension(fullRequestPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullRequestPath))
        {
            throw InvalidRequest("UPDATE_REQUEST_PATH", "The update request file is missing or outside the staging area.");
        }

        UpdatePathSafety.RequireNoReparsePoints(_stagingRoot, fullRequestPath, "UPDATE_REQUEST_PATH");
        var requestDirectory = Path.GetDirectoryName(fullRequestPath)
            ?? throw InvalidRequest("UPDATE_REQUEST_PATH", "The update request path is invalid.");
        if (!UpdatePathSafety.IsContainedBy(_updaterExecutablePath, _stagingRoot)
            || !UpdatePathSafety.IsContainedBy(_updaterExecutablePath, requestDirectory)
            || !Path.GetFileName(_updaterExecutablePath).Equals("Wisp.Updater.exe", StringComparison.Ordinal)
            || !File.Exists(_updaterExecutablePath))
        {
            throw InvalidRequest(
                "UPDATE_HELPER_PATH",
                "The update helper is not running from the request staging directory.");
        }

        UpdatePathSafety.RequireNoReparsePoints(_stagingRoot, _updaterExecutablePath, "UPDATE_HELPER_PATH");

        var fileInfo = new FileInfo(fullRequestPath);
        if (fileInfo.Length <= 0 || fileInfo.Length > UpdaterConstants.MaximumRequestBytes)
        {
            throw InvalidRequest("UPDATE_REQUEST_SIZE", "The update request file has an invalid size.");
        }

        UpdateApplyRequest request;
        try
        {
            using var stream = new FileStream(
                fullRequestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
            request = ParseRequestDocument(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw InvalidRequest("UPDATE_REQUEST_JSON", "The update request file is invalid.", exception);
        }

        return Validate(fullRequestPath, request);
    }

    private ValidatedUpdateRequest Validate(string requestPath, UpdateApplyRequest request)
    {
        if (!StableVersion.TryParse(request.TargetVersion, out var targetVersion))
        {
            throw InvalidRequest("UPDATE_TARGET_VERSION", "The update request has an invalid target version.");
        }

        if (!StableVersion.TryParse(request.SourceVersion, out var sourceVersion)
            || sourceVersion.CompareTo(targetVersion) >= 0)
        {
            throw InvalidRequest("UPDATE_SOURCE_VERSION", "The update request has an invalid source version.");
        }

        if (!UpdateApplyContract.IsValidReadyEventName(request.ReadyEventName))
        {
            throw InvalidRequest("UPDATE_READY_EVENT", "The update request has an invalid ready signal.");
        }

        if (request.ParentProcessId <= 0 || request.ParentProcessId == _currentProcessId)
        {
            throw InvalidRequest("UPDATE_PARENT_PROCESS", "The update request has an invalid parent process.");
        }

        if (request.ExpectedSizeBytes <= 0)
        {
            throw InvalidRequest("UPDATE_INSTALLER_SIZE", "The update request has invalid installer metadata.");
        }

        var normalizedHash = NormalizeSha256(request.ExpectedSha256);
        var installerPath = UpdatePathSafety.RequireAbsolutePath(
            request.StagedInstallerPath,
            "UPDATE_INSTALLER_PATH");
        var requestDirectory = Path.GetDirectoryName(requestPath)
            ?? throw InvalidRequest("UPDATE_REQUEST_PATH", "The update request path is invalid.");
        if (!UpdatePathSafety.IsContainedBy(installerPath, _stagingRoot)
            || !UpdatePathSafety.IsContainedBy(installerPath, requestDirectory)
            || !Path.GetExtension(installerPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(installerPath))
        {
            throw InvalidRequest(
                "UPDATE_INSTALLER_PATH",
                "The staged installer is missing or outside the request directory.");
        }

        UpdatePathSafety.RequireNoReparsePoints(_stagingRoot, installerPath, "UPDATE_INSTALLER_PATH");

        var applicationPath = UpdatePathSafety.RequireAbsolutePath(
            request.AppExecutablePath,
            "UPDATE_APPLICATION_PATH");
        if (!Path.GetFileName(applicationPath).Equals(
                UpdaterConstants.ApplicationFileName,
                StringComparison.Ordinal)
            || UpdatePathSafety.IsContainedBy(applicationPath, _stagingRoot, allowRoot: true)
            || !File.Exists(applicationPath)
            || (File.GetAttributes(applicationPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidRequest(
                "UPDATE_APPLICATION_PATH",
                "The requested application is not a safe installed Wisp executable.");
        }

        return new ValidatedUpdateRequest(
            requestPath,
            installerPath,
            targetVersion,
            sourceVersion,
            request.ParentProcessId,
            applicationPath,
            normalizedHash,
            request.ExpectedSizeBytes,
            request.ReadyEventName);
    }

    private static string NormalizeSha256(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw InvalidRequest("UPDATE_INSTALLER_HASH", "The update request has invalid installer metadata.");
        }

        return value.ToLowerInvariant();
    }

    private static UpdateApplyRequest ParseRequestDocument(JsonElement rootElement)
    {
        if (rootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The request must be a JSON object.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        string? stagedInstallerPath = null;
        string? targetVersion = null;
        string? sourceVersion = null;
        var parentProcessId = 0;
        var hasParentProcessId = false;
        string? appExecutablePath = null;
        string? expectedSha256 = null;
        long expectedSizeBytes = 0;
        var hasExpectedSizeBytes = false;
        string? readyEventName = null;

        foreach (var property in rootElement.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new JsonException("The request contains duplicate properties.");
            }

            switch (property.Name)
            {
                case "stagedInstallerPath":
                    stagedInstallerPath = RequireString(property);
                    break;
                case "targetVersion":
                    targetVersion = RequireString(property);
                    break;
                case "sourceVersion":
                    sourceVersion = RequireString(property);
                    break;
                case "parentProcessId":
                    parentProcessId = RequireInt32(property);
                    hasParentProcessId = true;
                    break;
                case "appExecutablePath":
                    appExecutablePath = RequireString(property);
                    break;
                case "expectedSha256":
                    expectedSha256 = RequireString(property);
                    break;
                case "expectedSizeBytes":
                    expectedSizeBytes = RequireInt64(property);
                    hasExpectedSizeBytes = true;
                    break;
                case "readyEventName":
                    readyEventName = RequireString(property);
                    break;
                default:
                    throw new JsonException("The request contains an unknown property.");
            }
        }

        if (names.Count != 8
            || stagedInstallerPath is null
            || targetVersion is null
            || sourceVersion is null
            || !hasParentProcessId
            || appExecutablePath is null
            || expectedSha256 is null
            || !hasExpectedSizeBytes
            || readyEventName is null)
        {
            throw new JsonException("The request is missing a required property.");
        }

        return new UpdateApplyRequest(
            stagedInstallerPath,
            targetVersion,
            sourceVersion,
            parentProcessId,
            appExecutablePath,
            expectedSha256,
            expectedSizeBytes,
            readyEventName);
    }

    private static string RequireString(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.String
            || property.Value.GetString() is not { } value)
        {
            throw new JsonException("A request property has an invalid type.");
        }

        return value;
    }

    private static int RequireInt32(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Number
            || !property.Value.TryGetInt32(out var value))
        {
            throw new JsonException("A request property has an invalid type.");
        }

        return value;
    }

    private static long RequireInt64(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Number
            || !property.Value.TryGetInt64(out var value))
        {
            throw new JsonException("A request property has an invalid type.");
        }

        return value;
    }

    private static UpdateFailureException InvalidRequest(
        string errorCode,
        string safeMessage,
        Exception? innerException = null) =>
        new(UpdaterExitCode.InvalidRequest, errorCode, safeMessage, innerException);
}
