using System.IO;
using System.Text.Json.Nodes;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipLibraryReconciliationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WispClipReconciliationTests", Guid.NewGuid().ToString("N"));
    private static readonly ClipRecordingSpec Recording = new(60, 1080, 60, 75);
    // Synthetic library bytes, not playable media.
    private static readonly byte[] MediaBytes = [0, 0, 0, 24, 102, 116, 121, 112, 1, 2, 3, 4];
    private static FinalizedClipMedia Media => new(MediaBytes.Length, 1920, 1080, 60, 0, 600_000_000, false);
    private string IndexPath => Path.Combine(_directory, ClipLibrary.IndexFileName);
    private string MediaPath(Guid id) => Path.Combine(_directory, $"{id:N}.mp4");

    [Fact]
    public async Task ListingPrunesDeletedMediaAcrossPagesAndPersistsCountsWithoutTouchingPendingOrOtherFiles()
    {
        var library = new ClipLibrary(_directory);
        for (var i = 0; i < 26; i++) await AddClipAsync(library);
        var deleted = Assert.Single((await library.GetPageAsync(1, TestContext.Current.CancellationToken)).Clips);
        var emptyPending = await library.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken);
        var writtenPending = await library.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(writtenPending.MediaPath, MediaBytes, TestContext.Current.CancellationToken);
        await library.DismissPendingNoticesAsync(TestContext.Current.CancellationToken);
        var pendingBefore = (await ReadIndexAsync())["pending"]!.ToJsonString();
        var unknown = Path.Combine(_directory, "unindexed.mp4");
        await File.WriteAllBytesAsync(unknown, MediaBytes, TestContext.Current.CancellationToken);
        File.Delete(MediaPath(deleted.Id));

        var page = await library.GetPageAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(0, page.PageIndex);
        Assert.Equal(1, page.PageCount);
        Assert.Equal(25, page.TotalClips);
        Assert.Equal(25, page.Clips.Count);
        Assert.Equal(25, page.UnviewedClips);
        Assert.Equal(25, page.UnexportedClips);
        Assert.Equal(25, page.NewClips);
        Assert.Equal(2, page.PendingSaves);
        Assert.Equal(0, page.PendingNotices);
        Assert.DoesNotContain(page.Clips, clip => clip.Id == deleted.Id);
        Assert.Equal(pendingBefore, (await ReadIndexAsync())["pending"]!.ToJsonString());
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(writtenPending.MediaPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(emptyPending.MediaPath));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(unknown, TestContext.Current.CancellationToken));
        foreach (var clip in page.Clips)
            Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(MediaPath(clip.Id), TestContext.Current.CancellationToken));
        Assert.DoesNotContain((await ReadIndexAsync())["clips"]!.AsArray(), item => item!["id"]!.GetValue<Guid>() == deleted.Id);
        Assert.Equal(page.Clips, (await new ClipLibrary(_directory).GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
    }

    [Fact]
    public async Task DeletingTheLastClipClearsOnlyItsCompletedRecordAndReminderCounts()
    {
        var library = new ClipLibrary(_directory);
        var deleted = await AddClipAsync(library);
        File.Delete(MediaPath(deleted.Id));

        var page = await library.GetPageAsync(0, TestContext.Current.CancellationToken);

        Assert.Empty(page.Clips);
        Assert.Equal(0, page.TotalClips);
        Assert.Equal(0, page.PageCount);
        Assert.Equal(0, page.NewClips);
        Assert.Equal(0, page.UnviewedClips);
        Assert.Equal(0, page.UnexportedClips);
        Assert.Empty((await ReadIndexAsync())["clips"]!.AsArray());
    }

    [Fact]
    public async Task SharingFailureKeepsItsRecordWhileAnActuallyMissingNeighborIsRemoved()
    {
        var library = new ClipLibrary(_directory);
        var retained = await AddClipAsync(library);
        var deleted = await AddClipAsync(library);
        File.Delete(MediaPath(deleted.Id));
        using (var held = new FileStream(MediaPath(retained.Id), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var page = await library.GetPageAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(retained, Assert.Single(page.Clips));
            Assert.Equal(1, page.NewClips);
        }
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(MediaPath(retained.Id), TestContext.Current.CancellationToken));
        Assert.Equal(retained, Assert.Single((await new ClipLibrary(_directory).GetPageAsync(0, TestContext.Current.CancellationToken)).Clips));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingButInvalidMediaIsNotTreatedAsAnExternalDeletion(bool replaceWithDirectory)
    {
        var library = new ClipLibrary(_directory);
        var retained = await AddClipAsync(library);
        var before = await File.ReadAllBytesAsync(IndexPath, TestContext.Current.CancellationToken);
        if (replaceWithDirectory)
        {
            File.Delete(MediaPath(retained.Id));
            Directory.CreateDirectory(MediaPath(retained.Id));
        }
        else await File.WriteAllBytesAsync(MediaPath(retained.Id), [1], TestContext.Current.CancellationToken);

        Assert.Equal(retained, Assert.Single((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips));
        Assert.Equal(before, await File.ReadAllBytesAsync(IndexPath, TestContext.Current.CancellationToken));
        if (replaceWithDirectory) Assert.True(Directory.Exists(MediaPath(retained.Id)));
        else Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(MediaPath(retained.Id), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedIndexPublicationKeepsTheOriginalIndexAndCanBeRetried()
    {
        var library = new ClipLibrary(_directory);
        var deleted = await AddClipAsync(library);
        var before = await File.ReadAllBytesAsync(IndexPath, TestContext.Current.CancellationToken);
        File.Delete(MediaPath(deleted.Id));
        using (var held = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => library.GetPageAsync(0, TestContext.Current.CancellationToken));
            Assert.NotNull(error);
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Contains(error.HResult & 0xffff, new[] { 5, 32 });
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(IndexPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
        Assert.Empty((await new ClipLibrary(_directory).GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)] // Missing directory is not a missing child file.
    [InlineData(5, false)] // Access denied.
    [InlineData(21, false)] // Device not ready.
    [InlineData(32, false)] // Sharing violation.
    [InlineData(53, false)] // Network path unavailable.
    [InlineData(1117, false)] // Device I/O failure.
    [InlineData(1167, false)] // Device disconnected.
    public void OnlyAConfirmedMissingChildFileAllowsPruning(int win32Error, bool expected)
    {
        Assert.Equal(expected, ClipLibrary.IsConfirmedMissingClipFile(new IOException("Test failure", unchecked((int)0x80070000) | win32Error)));
    }

    private static async Task<ClipEntry> AddClipAsync(ClipLibrary library)
    {
        var target = await library.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(target.MediaPath, MediaBytes, TestContext.Current.CancellationToken);
        return await library.CommitFinalizedAsync(target.Id, Media, TestContext.Current.CancellationToken);
    }

    private async Task<JsonNode> ReadIndexAsync() => JsonNode.Parse(await File.ReadAllTextAsync(IndexPath, TestContext.Current.CancellationToken))!;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
