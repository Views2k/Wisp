using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wisp.Update.Tests;

internal static class ReleaseTestData
{
    internal static readonly SemanticVersion FixtureVersion = new(9, 8, 7);

    internal static string CreateJson(
        SemanticVersion? version = null,
        long size = 123,
        string? sha256 = null,
        Action<JsonObject>? mutateRelease = null,
        Action<JsonObject>? mutateAsset = null,
        string? tagName = null,
        string? fileName = null,
        string? title = null)
    {
        var selectedVersion = version ?? FixtureVersion;
        var selectedFileName = fileName ?? ReleaseUriPolicy.InstallerFileName(selectedVersion);
        var selectedTagName = tagName ?? selectedVersion.ToTagString();
        var asset = new JsonObject
        {
            ["name"] = selectedFileName,
            ["state"] = "uploaded",
            ["size"] = size,
            ["digest"] = $"sha256:{sha256 ?? new string('a', 64)}",
            ["browser_download_url"] = $"https://github.com/Views2k/Wisp/releases/download/{selectedTagName}/{selectedFileName}"
        };
        mutateAsset?.Invoke(asset);

        var release = new JsonObject
        {
            ["tag_name"] = selectedTagName,
            ["name"] = title,
            ["draft"] = false,
            ["prerelease"] = false,
            ["immutable"] = true,
            ["body"] = "A focused Wisp update.",
            ["assets"] = new JsonArray(asset),
            ["ignored_by_the_updater"] = "GitHub responses contain additional fields"
        };
        mutateRelease?.Invoke(release);
        return release.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    internal static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string FixtureExecutablePath()
    {
        var path = Path.ChangeExtension(typeof(ReleaseTestData).Assembly.Location, ".exe");
        return File.Exists(path) ? path : throw new FileNotFoundException("The test apphost fixture is missing.", path);
    }
}

internal sealed class CaptureProgress<T> : IProgress<T>
{
    internal List<T> Values { get; } = [];

    public void Report(T value) => Values.Add(value);
}

internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = Directory.CreateTempSubdirectory("WispUpdateTests-").FullName;
    }

    internal string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
