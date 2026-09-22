using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCaptureStorageCheckTests
{
    [Fact]
    public async Task BoundedRoundtripUsesFreshDirectoriesAndRetainsReviewableEvidence()
    {
        var parent = Path.Combine(Path.GetTempPath(), "Wisp-storage-check-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = await ShiftCaptureStorageCheck.RunAsync(parent);
            var second = await ShiftCaptureStorageCheck.RunAsync(parent);
            Assert.True(first.Passed, first.ErrorType);
            Assert.True(second.Passed, second.ErrorType);
            Assert.NotEqual(first.EvidenceDirectory, second.EvidenceDirectory);
            Assert.NotNull(first.EvidenceDirectory);
            Assert.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, first.EvidenceDirectory, StringComparison.OrdinalIgnoreCase);
            using var archive = ZipFile.OpenRead(Path.Combine(first.EvidenceDirectory, "capture.zip"));
            Assert.Equal(3, archive.Entries.Count);
            Assert.True(archive.GetEntry("events.jsonl")!.Length < 64 * 1024);
            var serialized = JsonSerializer.Serialize(first);
            Assert.DoesNotContain(parent, serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EvidenceDirectory", serialized, StringComparison.Ordinal);
            Assert.Contains("No game data", serialized, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task FilesystemFailureIsSanitizedAndNeverDeletesExistingContent()
    {
        var parent = Path.Combine(Path.GetTempPath(), "Wisp-storage-check-blocked-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(parent, "preserve this existing file", TestContext.Current.CancellationToken);
            var result = await ShiftCaptureStorageCheck.RunAsync(parent);
            Assert.False(result.Passed);
            Assert.Equal("storage-check-failed", result.Status);
            Assert.NotNull(result.ErrorType);
            Assert.Equal("preserve this existing file", await File.ReadAllTextAsync(parent, TestContext.Current.CancellationToken));
            Assert.DoesNotContain(parent, JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        }
        finally { if (File.Exists(parent)) File.Delete(parent); }
    }
}
