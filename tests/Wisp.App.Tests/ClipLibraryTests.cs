using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipLibraryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WispClipLibraryTests", Guid.NewGuid().ToString("N"));
    private static readonly ClipRecordingSpec Recording = new(60, 1080, 60, 75);
    // Library contracts use synthetic bytes; these are not claimed to be playable media.
    private static readonly byte[] MediaBytes = [0, 0, 0, 24, 102, 116, 121, 112, 1, 2, 3, 4];
    private static FinalizedClipMedia Media => new(MediaBytes.Length, 1920, 1080, 60, 10_000_000, 610_000_000, true);

    [Fact]
    public void ExportSuggestionUsesTheCardLocalClockAndShortStableIdentity()
    {
        var saved = new DateTimeOffset(2026, 10, 1, 14, 5, 9, TimeSpan.Zero);
        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var clip = new ClipEntry(id, saved, Recording, Media);
        var localStamp = saved.ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"Wisp-{localStamp}-12345678.mp4", clip.SuggestedExportName);
        Assert.DoesNotContain(id.ToString("N"), clip.SuggestedExportName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingReservationSurvivesRestartButNeverAppearsAsPlayable()
    {
        var first = new ClipLibrary(_directory);
        var target = await first.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(target.MediaPath));
        var restarted = new ClipLibrary(_directory);
        var pending = Assert.Single(await restarted.ListPendingAsync(TestContext.Current.CancellationToken));
        Assert.Equal(target, pending);
        var page = await restarted.GetPageAsync(0, TestContext.Current.CancellationToken);
        Assert.Empty(page.Clips);
        Assert.Equal(1, page.PendingSaves);
        Assert.Equal(0, page.NewClips);
        await File.WriteAllBytesAsync(target.MediaPath, MediaBytes, TestContext.Current.CancellationToken);
        Assert.Empty((await restarted.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
        var saved = await restarted.CommitFinalizedAsync(target.Id, Media, TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, saved.Id);
        Assert.Equal(60, saved.DurationSeconds);
        Assert.Empty(await first.ListPendingAsync(TestContext.Current.CancellationToken));
        Assert.Single((await first.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
    }

    [Fact]
    public async Task FailedFinalizationKeepsTheReservationAndEveryExistingByte()
    {
        var library = new ClipLibrary(_directory);
        var target = await library.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<FileNotFoundException>(() => library.CommitFinalizedAsync(target.Id, Media, TestContext.Current.CancellationToken));
        await File.WriteAllBytesAsync(target.MediaPath, MediaBytes, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => library.CommitFinalizedAsync(target.Id, Media with { FileBytes = 2 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => library.CommitFinalizedAsync(target.Id, Media with { Width = 1280 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => library.CommitFinalizedAsync(target.Id, Media with { ActualEnd100ns = long.MaxValue }, TestContext.Current.CancellationToken));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(target.MediaPath, TestContext.Current.CancellationToken));
        Assert.Single(await library.ListPendingAsync(TestContext.Current.CancellationToken));
        Assert.Empty((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
    }

    [Fact]
    public async Task SaveAndViewAreIdempotentByIdentityWithoutOverwritingFootage()
    {
        var library = new ClipLibrary(_directory);
        var saved = await AddClipAsync(library);
        await Assert.ThrowsAsync<InvalidOperationException>(() => library.CommitFinalizedAsync(saved.Id, Media, TestContext.Current.CancellationToken));
        var viewed = await library.MarkViewedAsync(saved.Id, TestContext.Current.CancellationToken);
        Assert.Equal(viewed, await library.MarkViewedAsync(saved.Id, TestContext.Current.CancellationToken));
        var restarted = new ClipLibrary(_directory);
        var page = await restarted.GetPageAsync(0, TestContext.Current.CancellationToken);
        Assert.Equal(viewed, Assert.Single(page.Clips));
        Assert.Equal(0, page.UnviewedClips);
        Assert.Equal(1, page.UnexportedClips);
        Assert.Equal(0, page.NewClips);
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(await restarted.GetMediaPathAsync(saved.Id, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PaginationIsBoundedAndHasNoDuplicatesAcrossPages()
    {
        var library = new ClipLibrary(_directory);
        for (var i = 0; i < 26; i++) await AddClipAsync(library);
        var first = await library.GetPageAsync(0, TestContext.Current.CancellationToken);
        var second = await library.GetPageAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal(25, first.Clips.Count);
        Assert.Single(second.Clips);
        Assert.Equal(2, first.PageCount);
        Assert.Equal(26, first.TotalClips);
        Assert.Equal(26, first.UnviewedClips);
        Assert.Equal(26, first.UnexportedClips);
        Assert.Equal(26, first.NewClips);
        Assert.Equal(26, first.Clips.Concat(second.Clips).Select(clip => clip.Id).Distinct().Count());
        Assert.Equal(second.Clips, (await library.GetPageAsync(int.MaxValue, TestContext.Current.CancellationToken)).Clips);
    }

    [Fact]
    public async Task ExportKeepsOriginalEncodingAndNeverReplacesAnExistingFile()
    {
        var library = new ClipLibrary(_directory);
        var saved = await AddClipAsync(library);
        var destination = Path.Combine(_directory, "chosen-export.mp4");
        Assert.Equal(new ClipExportResult(true, true), await library.ExportAsync(saved.Id, destination, TestContext.Current.CancellationToken));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => library.ExportAsync(saved.Id, destination, TestContext.Current.CancellationToken));
        var page = await library.GetPageAsync(0, TestContext.Current.CancellationToken);
        Assert.Equal(0, page.UnexportedClips);
        Assert.Equal(1, page.UnviewedClips);
        Assert.Equal(0, page.NewClips);
        Assert.Equal(saved.Media, Assert.Single(page.Clips).Media);
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SuccessfulExportIsKeptWhenItsMetadataUpdateIsBlocked()
    {
        var library = new ClipLibrary(_directory);
        var saved = await AddClipAsync(library);
        var destination = Path.Combine(_directory, "kept-export.mp4");
        using (var heldIndex = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(new ClipExportResult(true, false), await library.ExportAsync(saved.Id, destination, TestContext.Current.CancellationToken));
        }
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal(1, (await library.GetPageAsync(0, TestContext.Current.CancellationToken)).UnexportedClips);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task ExportToDirectoryCreatesOneVerifiedFileAndRepeatedExportCreatesNothing()
    {
        var library = new ClipLibrary(_directory);
        var saved = await AddClipAsync(library);
        var folder = Path.Combine(_directory, "chosen-export-folder");
        Assert.False(Directory.Exists(folder));
        Assert.Equal(new ClipExportResult(true, true), await library.ExportToDirectoryAsync(saved.Id, folder, TestContext.Current.CancellationToken));
        var exported = Assert.Single(Directory.EnumerateFiles(folder));
        Assert.StartsWith("Wisp-", Path.GetFileName(exported), StringComparison.Ordinal);
        Assert.EndsWith($"-{saved.Id.ToString("N")[..8]}.mp4", exported, StringComparison.Ordinal);
        var timestamp = Assert.Single((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips).ExportedAtUtc;
        Assert.Equal(new ClipExportResult(false, true), await library.ExportToDirectoryAsync(saved.Id, folder, TestContext.Current.CancellationToken));
        Assert.Equal(exported, Assert.Single(Directory.EnumerateFiles(folder)));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(exported, TestContext.Current.CancellationToken));
        Assert.Equal(timestamp, Assert.Single((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips).ExportedAtUtc);
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(await library.GetMediaPathAsync(saved.Id, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RepeatedExportRejectsSameSizeDifferentContentWithoutReplacingIt()
    {
        var library = new ClipLibrary(_directory);
        var saved = await AddClipAsync(library);
        var folder = Path.Combine(_directory, "exports");
        await library.ExportToDirectoryAsync(saved.Id, folder, TestContext.Current.CancellationToken);
        var destination = Assert.Single(Directory.EnumerateFiles(folder));
        var unrelated = new byte[MediaBytes.Length];
        await File.WriteAllBytesAsync(destination, unrelated, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => library.ExportToDirectoryAsync(saved.Id, folder, TestContext.Current.CancellationToken));
        Assert.Equal(unrelated, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateFiles(folder));
    }

    [Fact]
    public async Task VerifiedRepeatCanRepairExportMetadataWithoutCreatingAnotherFile()
    {
        var library = new ClipLibrary(_directory);
        var saved = await AddClipAsync(library);
        var folder = Path.Combine(_directory, "exports");
        using (var heldIndex = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(new ClipExportResult(true, false), await library.ExportToDirectoryAsync(saved.Id, folder, TestContext.Current.CancellationToken));
        Assert.Equal(new ClipExportResult(false, true), await library.ExportToDirectoryAsync(saved.Id, folder, TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateFiles(folder));
        Assert.Equal(0, (await library.GetPageAsync(0, TestContext.Current.CancellationToken)).UnexportedClips);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentLegacyIndexNeverCreatesLegacyOrPrivateLibraryFiles(bool folderExists)
    {
        var legacyPath = Path.Combine(_directory, "legacy");
        var privatePath = Path.Combine(_directory, "private");
        if (folderExists) Directory.CreateDirectory(legacyPath);
        var result = await new ClipLibrary(privatePath).ImportLegacyAsync(legacyPath, TestContext.Current.CancellationToken);
        Assert.Equal(new ClipImportResult(0, 0, 0, 0), result);
        Assert.Equal(folderExists, Directory.Exists(legacyPath));
        if (folderExists) Assert.Empty(Directory.EnumerateFileSystemEntries(legacyPath));
        Assert.False(Directory.Exists(privatePath));
    }

    [Fact]
    public async Task ImportPreservesIndexedIdentityAndMetadataWithoutScanningOrChangingLegacyFiles()
    {
        var legacyPath = Path.Combine(_directory, "legacy");
        var privatePath = Path.Combine(_directory, "private");
        var legacy = new ClipLibrary(legacyPath);
        var saved = await AddClipAsync(legacy);
        await legacy.MarkViewedAsync(saved.Id, TestContext.Current.CancellationToken);
        await legacy.ExportToDirectoryAsync(saved.Id, Path.Combine(_directory, "exports"), TestContext.Current.CancellationToken);
        var expected = Assert.Single((await legacy.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
        var pending = await legacy.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(pending.MediaPath, MediaBytes, TestContext.Current.CancellationToken);
        var unknown = Path.Combine(legacyPath, "unindexed.mp4");
        await File.WriteAllBytesAsync(unknown, MediaBytes, TestContext.Current.CancellationToken);
        var originalIndex = await File.ReadAllBytesAsync(Path.Combine(legacyPath, ClipLibrary.IndexFileName), TestContext.Current.CancellationToken);
        var library = new ClipLibrary(privatePath);
        Assert.Equal(new ClipImportResult(1, 0, 0, 1), await library.ImportLegacyAsync(legacyPath, TestContext.Current.CancellationToken));
        Assert.Equal(expected, Assert.Single((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips));
        Assert.Empty(await library.ListPendingAsync(TestContext.Current.CancellationToken));
        Assert.Equal(originalIndex, await File.ReadAllBytesAsync(Path.Combine(legacyPath, ClipLibrary.IndexFileName), TestContext.Current.CancellationToken));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(await legacy.GetMediaPathAsync(saved.Id, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(pending.MediaPath));
        Assert.True(File.Exists(unknown));
        Assert.False(File.Exists(Path.Combine(privatePath, "unindexed.mp4")));
        Assert.False(File.Exists(Path.Combine(privatePath, $"{pending.Id:N}.mp4")));
        Assert.Equal(new ClipImportResult(0, 1, 0, 1), await library.ImportLegacyAsync(legacyPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportBatchesAreBoundedAndPreserveLaterPrivateReviewState()
    {
        var legacyPath = Path.Combine(_directory, "legacy");
        var legacy = new ClipLibrary(legacyPath);
        for (var index = 0; index < 26; index++) await AddClipAsync(legacy);
        var library = new ClipLibrary(Path.Combine(_directory, "private"));
        Assert.Equal(new ClipImportResult(25, 0, 1, 0), await library.ImportLegacyAsync(legacyPath, TestContext.Current.CancellationToken));
        var first = (await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips[0];
        var viewed = await library.MarkViewedAsync(first.Id, TestContext.Current.CancellationToken);
        Assert.Equal(new ClipImportResult(1, 25, 0, 0), await library.ImportLegacyAsync(legacyPath, TestContext.Current.CancellationToken));
        Assert.Equal(new ClipImportResult(0, 26, 0, 0), await library.ImportLegacyAsync(legacyPath, TestContext.Current.CancellationToken));
        var pages = (await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips
            .Concat((await library.GetPageAsync(1, TestContext.Current.CancellationToken)).Clips);
        Assert.Equal(viewed, Assert.Single(pages, clip => clip.Id == first.Id));
    }

    [Fact]
    public async Task ExportingAnImportedClipToItsLegacyFolderReusesTheVerifiedOriginal()
    {
        var legacyPath = Path.Combine(_directory, "legacy");
        var legacy = new ClipLibrary(legacyPath);
        var saved = await AddClipAsync(legacy);
        var originalIndex = await File.ReadAllBytesAsync(Path.Combine(legacyPath, ClipLibrary.IndexFileName), TestContext.Current.CancellationToken);
        var library = new ClipLibrary(Path.Combine(_directory, "private"));
        await library.ImportLegacyAsync(legacyPath, TestContext.Current.CancellationToken);
        Assert.Equal(new ClipExportResult(false, true), await library.ExportToDirectoryAsync(saved.Id, legacyPath, TestContext.Current.CancellationToken));
        Assert.Equal(new ClipExportResult(false, true), await library.ExportToDirectoryAsync(saved.Id, legacyPath, TestContext.Current.CancellationToken));
        var original = Assert.Single(Directory.EnumerateFiles(legacyPath, "*.mp4"));
        Assert.Equal($"{saved.Id:N}.mp4", Path.GetFileName(original));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(original, TestContext.Current.CancellationToken));
        Assert.Equal(originalIndex, await File.ReadAllBytesAsync(Path.Combine(legacyPath, ClipLibrary.IndexFileName), TestContext.Current.CancellationToken));
        Assert.Equal(0, (await library.GetPageAsync(0, TestContext.Current.CancellationToken)).UnexportedClips);
    }

    [Fact]
    public async Task DifferentLegacyNamedFileIsRefusedRatherThanExportedUnderAnotherName()
    {
        var library = new ClipLibrary(_directory);
        var saved = await AddClipAsync(library);
        var folder = Path.Combine(_directory, "exports");
        Directory.CreateDirectory(folder);
        var collision = Path.Combine(folder, $"{saved.Id:N}.mp4");
        var unrelated = new byte[MediaBytes.Length];
        await File.WriteAllBytesAsync(collision, unrelated, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => library.ExportToDirectoryAsync(saved.Id, folder, TestContext.Current.CancellationToken));
        Assert.Equal(collision, Assert.Single(Directory.EnumerateFiles(folder)));
        Assert.Equal(unrelated, await File.ReadAllBytesAsync(collision, TestContext.Current.CancellationToken));
        Assert.Equal(1, (await library.GetPageAsync(0, TestContext.Current.CancellationToken)).UnexportedClips);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportReusesOnlyIdenticalUnindexedDestinationBytes(bool differentBytes)
    {
        var legacyPath = Path.Combine(_directory, "legacy");
        var legacy = new ClipLibrary(legacyPath);
        var saved = await AddClipAsync(legacy);
        var privatePath = Path.Combine(_directory, "private");
        Directory.CreateDirectory(privatePath);
        var destination = Path.Combine(privatePath, $"{saved.Id:N}.mp4");
        var existing = differentBytes ? new byte[MediaBytes.Length] : MediaBytes;
        await File.WriteAllBytesAsync(destination, existing, TestContext.Current.CancellationToken);
        var library = new ClipLibrary(privatePath);
        if (differentBytes)
        {
            await Assert.ThrowsAsync<IOException>(() => library.ImportLegacyAsync(legacyPath, TestContext.Current.CancellationToken));
            Assert.Empty((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
        }
        else Assert.Equal(new ClipImportResult(1, 0, 0, 0), await library.ImportLegacyAsync(legacyPath, TestContext.Current.CancellationToken));
        Assert.Equal(existing, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(await legacy.GetMediaPathAsync(saved.Id, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledImportDoesNotCreateDestinationOrTouchLegacyIndex()
    {
        var legacyPath = Path.Combine(_directory, "legacy");
        var legacy = new ClipLibrary(legacyPath);
        await AddClipAsync(legacy);
        var original = await File.ReadAllBytesAsync(Path.Combine(legacyPath, ClipLibrary.IndexFileName), TestContext.Current.CancellationToken);
        var privatePath = Path.Combine(_directory, "private");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClipLibrary(privatePath).ImportLegacyAsync(legacyPath, cancelled.Token));
        Assert.False(Directory.Exists(privatePath));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(legacyPath, ClipLibrary.IndexFileName), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportIndexFailureKeepsBothCopiesAndRetryCommitsTheVerifiedPrivateFile()
    {
        var legacyPath = Path.Combine(_directory, "legacy");
        var legacy = new ClipLibrary(legacyPath);
        var saved = await AddClipAsync(legacy);
        var privatePath = Path.Combine(_directory, "private");
        var library = new ClipLibrary(privatePath);
        _ = await library.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken);
        using (var heldIndex = new FileStream(Path.Combine(privatePath, ClipLibrary.IndexFileName), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.ImportLegacyAsync(legacyPath, TestContext.Current.CancellationToken));
        var destination = Path.Combine(privatePath, $"{saved.Id:N}.mp4");
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Empty((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
        Assert.Equal(new ClipImportResult(1, 0, 0, 0), await library.ImportLegacyAsync(legacyPath, TestContext.Current.CancellationToken));
        Assert.Equal(saved, Assert.Single((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips));
        Assert.Single(await library.ListPendingAsync(TestContext.Current.CancellationToken));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(await legacy.GetMediaPathAsync(saved.Id, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnsupportedLegacyIndexIsRefusedBeforeCreatingAPrivateLibrary()
    {
        var legacyPath = Path.Combine(_directory, "legacy");
        var legacy = new ClipLibrary(legacyPath);
        await AddClipAsync(legacy);
        var path = Path.Combine(legacyPath, ClipLibrary.IndexFileName);
        var index = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        index["version"] = 999;
        var corrupt = index.ToJsonString();
        await File.WriteAllTextAsync(path, corrupt, TestContext.Current.CancellationToken);
        var privatePath = Path.Combine(_directory, "private");
        await Assert.ThrowsAsync<InvalidDataException>(() => new ClipLibrary(privatePath).ImportLegacyAsync(legacyPath, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(privatePath));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnsupportedOrCorruptIndexIsNeverReplaced()
    {
        var library = new ClipLibrary(_directory);
        _ = await library.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(IndexPath, TestContext.Current.CancellationToken))!;
        document["version"] = 999;
        var unsupported = document.ToJsonString();
        await File.WriteAllTextAsync(IndexPath, unsupported, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => library.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken));
        Assert.Equal(unsupported, await File.ReadAllTextAsync(IndexPath, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(IndexPath, "{invalid", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<JsonException>(() => library.GetPageAsync(0, TestContext.Current.CancellationToken));
        Assert.Equal("{invalid", await File.ReadAllTextAsync(IndexPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OversizedIndexFailsWithoutLoadingOrTruncatingIt()
    {
        Directory.CreateDirectory(_directory);
        using (var file = File.Create(IndexPath)) file.SetLength(ClipLibrary.MaximumIndexBytes + 1L);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ClipLibrary(_directory).GetPageAsync(0, TestContext.Current.CancellationToken));
        Assert.Equal(ClipLibrary.MaximumIndexBytes + 1L, new FileInfo(IndexPath).Length);
    }

    [Fact]
    public async Task FullIndexRefusesNewReservationsWithoutDroppingOldOnes()
    {
        var library = new ClipLibrary(_directory);
        _ = await library.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(IndexPath, TestContext.Current.CancellationToken))!;
        var pending = document["pending"]!.AsArray();
        var template = pending[0]!.DeepClone();
        for (var i = 1; i < ClipLibrary.MaximumEntries; i++)
        {
            var item = template.DeepClone();
            item["id"] = Guid.NewGuid().ToString();
            pending.Add(item);
        }
        var full = document.ToJsonString();
        await File.WriteAllTextAsync(IndexPath, full, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => library.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken));
        Assert.Equal(full, await File.ReadAllTextAsync(IndexPath, TestContext.Current.CancellationToken));
        Assert.Equal(ClipLibrary.MaximumEntries, (await library.GetPageAsync(0, TestContext.Current.CancellationToken)).PendingSaves);
    }

    [Fact]
    public async Task TwoInstancesReloadTheIndexAndRespectExclusiveOwnership()
    {
        var first = new ClipLibrary(_directory);
        var second = new ClipLibrary(_directory);
        var a = await first.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken);
        var b = await second.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await first.ListPendingAsync(TestContext.Current.CancellationToken)).Count);
        Assert.NotEqual(a.Id, b.Id);
        using (var heldLock = new FileStream(Path.Combine(_directory, ".wisp-clips.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => second.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken));
        }
        Assert.Equal(2, (await first.ListPendingAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task CancelledOperationAndStorageSwitchDoNotMoveOrDeleteOldClips()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClipLibrary(_directory).ReserveSaveAsync(Recording, cancelled.Token));
        Assert.False(Directory.Exists(_directory));
        var old = new ClipLibrary(_directory);
        var clip = await AddClipAsync(old);
        var newDirectory = Path.Combine(_directory, "other-location");
        Assert.Empty((await new ClipLibrary(newDirectory).GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
        Assert.Single((await old.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(await old.GetMediaPathAsync(clip.Id, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IndexCannotSupplyAnExternalMediaPath()
    {
        var library = new ClipLibrary(_directory);
        var clip = await AddClipAsync(library);
        var external = Path.Combine(_directory, "unowned.mp4");
        await File.WriteAllBytesAsync(external, MediaBytes, TestContext.Current.CancellationToken);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(IndexPath, TestContext.Current.CancellationToken))!;
        document["clips"]![0]!["mediaPath"] = external;
        await File.WriteAllTextAsync(IndexPath, document.ToJsonString(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<JsonException>(() => library.GetMediaPathAsync(clip.Id, TestContext.Current.CancellationToken));
        Assert.Equal(MediaBytes, await File.ReadAllBytesAsync(external, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("relative.mp4")]
    [InlineData(@"C:\NUL.mp4")]
    [InlineData(@"C:\clips\name.mp4:stream")]
    [InlineData(@"\\?\C:\clips\output.mp4")]
    public async Task InvalidExportPathIsRejectedBeforeIo(string destination)
    {
        var library = new ClipLibrary(_directory);
        await Assert.ThrowsAsync<ArgumentException>(() => library.ExportAsync(Guid.NewGuid(), destination, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(_directory));
    }

    private string IndexPath => Path.Combine(_directory, ClipLibrary.IndexFileName);

    private static async Task<ClipEntry> AddClipAsync(ClipLibrary library)
    {
        var target = await library.ReserveSaveAsync(Recording, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(target.MediaPath, MediaBytes, TestContext.Current.CancellationToken);
        return await library.CommitFinalizedAsync(target.Id, Media, TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
