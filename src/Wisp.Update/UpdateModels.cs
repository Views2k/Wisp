using System.Text.Json.Serialization;

namespace Wisp.Update;

public sealed class UpdateRelease
{
    internal UpdateRelease(
        SemanticVersion version,
        string fileName,
        long size,
        string sha256,
        Uri downloadUri)
    {
        Version = version;
        FileName = fileName;
        Size = size;
        Sha256 = sha256;
        DownloadUri = downloadUri;
    }

    public SemanticVersion Version { get; }
    public string FileName { get; }
    public long Size { get; }
    public string Sha256 { get; }
    public Uri DownloadUri { get; }
}

public sealed class VerifiedInstaller
{
    internal VerifiedInstaller(
        string stagedPath,
        SemanticVersion version,
        long size,
        string sha256)
    {
        StagedPath = stagedPath;
        Version = version;
        Size = size;
        Sha256 = sha256;
    }

    public string StagedPath { get; }
    public SemanticVersion Version { get; }
    public long Size { get; }
    public string Sha256 { get; }
}

public readonly record struct UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percentage => TotalBytes == 0 ? 0 : (double)BytesReceived / TotalBytes * 100;
}

public sealed record UpdateApplyRequest(
    [property: JsonPropertyName("stagedInstallerPath")] string StagedInstallerPath,
    [property: JsonPropertyName("targetVersion")] string TargetVersion,
    [property: JsonPropertyName("sourceVersion")] string SourceVersion,
    [property: JsonPropertyName("parentProcessId")] int ParentProcessId,
    [property: JsonPropertyName("appExecutablePath")] string AppExecutablePath,
    [property: JsonPropertyName("expectedSha256")] string ExpectedSha256,
    [property: JsonPropertyName("expectedSizeBytes")] long ExpectedSizeBytes,
    [property: JsonPropertyName("readyEventName")] string ReadyEventName);

public static class UpdateApplyContract
{
    public const string ReadyEventPrefix = @"Local\Wisp.Update.Ready.";
    public const int ReadyTokenHexLength = 64;

    public static string CreateReadyEventName(string token)
    {
        if (!IsValidReadyToken(token))
        {
            throw new ArgumentException("The update-ready token is invalid.", nameof(token));
        }

        return ReadyEventPrefix + token;
    }

    public static bool IsValidReadyEventName(string? value) =>
        value is not null
        && value.StartsWith(ReadyEventPrefix, StringComparison.Ordinal)
        && IsValidReadyToken(value[ReadyEventPrefix.Length..]);

    private static bool IsValidReadyToken(string value) =>
        value.Length == ReadyTokenHexLength
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class UpdateSecurityException : Exception
{
    public UpdateSecurityException(string message)
        : base(message)
    {
    }

    public UpdateSecurityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
