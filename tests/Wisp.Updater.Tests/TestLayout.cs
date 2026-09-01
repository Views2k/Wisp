using System.Security.Cryptography;
using System.Text.Json;
using Wisp.Update;

namespace Wisp.Updater.Tests;

internal sealed class TestLayout : IDisposable
{
    private readonly string _root;

    internal TestLayout()
    {
        _root = Path.Combine(Path.GetTempPath(), "Wisp.Updater.Tests", Guid.NewGuid().ToString("N"));
        StagingRoot = Path.Combine(_root, "Local", "Wisp", "Updates");
        RequestDirectory = Path.Combine(StagingRoot, "1.2.3");
        InstallerPath = Path.Combine(RequestDirectory, "Wisp-Setup-1.2.3.exe");
        RequestPath = Path.Combine(RequestDirectory, "apply.json");
        InstallDirectory = Path.Combine(_root, "Programs", "Wisp");
        UpdaterPath = Path.Combine(RequestDirectory, "Wisp.Updater.exe");
        ApplicationPath = Path.Combine(InstallDirectory, "Wisp.exe");

        Directory.CreateDirectory(RequestDirectory);
        Directory.CreateDirectory(InstallDirectory);
        File.WriteAllBytes(InstallerPath, InstallerBytes);
        File.WriteAllBytes(UpdaterPath, [0x4D, 0x5A]);
        File.WriteAllBytes(ApplicationPath, [0x4D, 0x5A]);
        WriteRequest(CreateRequest());
    }

    internal static byte[] InstallerBytes { get; } = "verified Wisp installer fixture"u8.ToArray();

    internal string StagingRoot { get; }

    internal string RequestDirectory { get; }

    internal string InstallerPath { get; }

    internal string RequestPath { get; }

    internal string InstallDirectory { get; }

    internal string UpdaterPath { get; }

    internal string ApplicationPath { get; }

    internal int CurrentProcessId { get; } = 41_001;

    internal int ParentProcessId { get; } = 41_002;

    internal UpdateApplyRequest CreateRequest(
        string? installerPath = null,
        string targetVersion = "1.2.3",
        string sourceVersion = "1.0.0",
        int? parentProcessId = null,
        string? applicationPath = null,
        string? expectedSha256 = null,
        long? expectedSizeBytes = null,
        string? readyEventName = null) =>
        new(
            installerPath ?? InstallerPath,
            targetVersion,
            sourceVersion,
            parentProcessId ?? ParentProcessId,
            applicationPath ?? ApplicationPath,
            expectedSha256 ?? Convert.ToHexString(SHA256.HashData(InstallerBytes)).ToLowerInvariant(),
            expectedSizeBytes ?? InstallerBytes.LongLength,
            readyEventName ?? UpdateApplyContract.CreateReadyEventName(new string('a', 64)));

    internal void WriteRequest(UpdateApplyRequest request) =>
        File.WriteAllText(RequestPath, JsonSerializer.Serialize(request));

    internal ApplyRequestReader CreateReader() =>
        new(StagingRoot, UpdaterPath, CurrentProcessId);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
