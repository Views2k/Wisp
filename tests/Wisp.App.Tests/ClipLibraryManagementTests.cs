using System.IO;
using System.Text.Json.Nodes;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipLibraryManagementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WispClipLibraryManagementTests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] MediaBytes = [0, 0, 0, 24, 102, 116, 121, 112, 1, 2, 3, 4];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RenameIsKeptOutsideTheIndexAndSurvivesRestart()
    {
        var library = new ClipLibrary(_directory);
        var clip = await SaveAsync(library);
        var renamed = await library.RenameAsync(clip.Id, "  Goliath sprint  ", Token);
        Assert.Equal("Goliath sprint", renamed.Name);

        var reopened = new ClipLibrary(_directory);
        Assert.Equal("Goliath sprint", Assert.Single((await reopened.GetPageAsync(0, Token)).Clips).Name);
        // Earlier Wisp versions reject unknown index fields, so the index must not carry names.
        var index = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, ".wisp-clips.json"), Token))!;
        Assert.Null(index["clips"]![0]!["name"]);
        Assert.True(File.Exists(Path.Combine(_directory, ClipLibrary.NamesFileName)));

        var cleared = await reopened.RenameAsync(clip.Id, "   ", Token);
        Assert.Null(cleared.Name);
        Assert.Null(Assert.Single((await library.GetPageAsync(0, Token)).Clips).Name);
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("tab\there")]
    public async Task RenameRejectsMultilineAndControlCharacters(string name)
    {
        var library = new ClipLibrary(_directory);
        var clip = await SaveAsync(library);
        await Assert.ThrowsAsync<ArgumentException>(() => library.RenameAsync(clip.Id, name, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => library.RenameAsync(clip.Id, new string('x', ClipLibrary.MaximumNameLength + 1), Token));
        Assert.Null(Assert.Single((await library.GetPageAsync(0, Token)).Clips).Name);
    }

    [Fact]
    public async Task UnreadableNamesFileFallsBackToDefaultTitlesAndIsNotOverwritten()
    {
        var library = new ClipLibrary(_directory);
        var clip = await SaveAsync(library);
        var names = Path.Combine(_directory, ClipLibrary.NamesFileName);
        await File.WriteAllTextAsync(names, "{ not json", Token);
        Assert.Null(Assert.Single((await library.GetPageAsync(0, Token)).Clips).Name);
        await Assert.ThrowsAsync<InvalidDataException>(() => library.RenameAsync(clip.Id, "Keep", Token));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(names, Token));
    }

    [Fact]
    public async Task DeleteRemovesTheVideoRecordAndName()
    {
        var library = new ClipLibrary(_directory);
        var kept = await SaveAsync(library);
        var deleted = await SaveAsync(library);
        await library.RenameAsync(deleted.Id, "Remove me", Token);
        await library.DeleteAsync(deleted.Id, Token);

        Assert.False(File.Exists(Path.Combine(_directory, $"{deleted.Id:N}.mp4")));
        Assert.True(File.Exists(Path.Combine(_directory, $"{kept.Id:N}.mp4")));
        var page = await new ClipLibrary(_directory).GetPageAsync(0, Token);
        Assert.Equal(kept.Id, Assert.Single(page.Clips).Id);
        Assert.DoesNotContain(deleted.Id.ToString(), await File.ReadAllTextAsync(Path.Combine(_directory, ClipLibrary.NamesFileName), Token),
            StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => library.DeleteAsync(deleted.Id, Token));
    }

    [Fact]
    public async Task DeleteKeepsAClipThatAnotherProgramHoldsOpen()
    {
        var library = new ClipLibrary(_directory);
        var clip = await SaveAsync(library);
        var path = Path.Combine(_directory, $"{clip.Id:N}.mp4");
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<IOException>(() => library.DeleteAsync(clip.Id, Token));
            Assert.True(ClipLibraryFiles.IsInUse(error));
        }
        Assert.True(File.Exists(path));
        Assert.Single((await library.GetPageAsync(0, Token)).Clips);
        await library.DeleteAsync(clip.Id, Token);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeleteKeepsAFileWhoseSizeNoLongerMatchesItsRecord()
    {
        var library = new ClipLibrary(_directory);
        var clip = await SaveAsync(library);
        var path = Path.Combine(_directory, $"{clip.Id:N}.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3], Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => library.DeleteAsync(clip.Id, Token));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DeleteRemovesARecordWhoseFileIsAlreadyGone()
    {
        var library = new ClipLibrary(_directory);
        var clip = await SaveAsync(library);
        File.Delete(Path.Combine(_directory, $"{clip.Id:N}.mp4"));
        await library.DeleteAsync(clip.Id, Token);
        Assert.Empty((await library.GetPageAsync(0, Token)).Clips);
        Assert.DoesNotContain(clip.Id.ToString("D"), await File.ReadAllTextAsync(Path.Combine(_directory, ".wisp-clips.json"), Token),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchMatchesNamesAndCardDetailsAcrossPages()
    {
        var library = new ClipLibrary(_directory);
        var named = await SaveAsync(library);
        await library.RenameAsync(named.Id, "Goliath sprint", Token);
        var hdr = await SaveAsync(library, media => media with { HdrVideo = true }, recording => recording with { PreserveHdrRecording = true });
        for (var index = 0; index < ClipLibrary.PageSize; index++) await SaveAsync(library);

        var byName = await library.GetPageAsync(0, "goliath", Token);
        Assert.Equal(named.Id, Assert.Single(byName.Clips).Id);
        Assert.Equal(1, byName.MatchingClips);
        Assert.Equal(ClipLibrary.PageSize + 2, byName.TotalClips);
        Assert.Equal(1, byName.PageCount);

        Assert.Equal(hdr.Id, Assert.Single((await library.GetPageAsync(0, "HDR", Token)).Clips).Id);
        Assert.Empty((await library.GetPageAsync(0, "goliath hdr", Token)).Clips);

        var everything = await library.GetPageAsync(0, "1080p", Token);
        Assert.Equal(ClipLibrary.PageSize + 2, everything.MatchingClips);
        Assert.Equal(2, everything.PageCount);
        Assert.Equal(ClipLibrary.PageSize, everything.Clips.Count);
        var date = named.SavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(ClipLibrary.PageSize + 2, (await library.GetPageAsync(0, date, Token)).MatchingClips);
        Assert.Empty((await library.GetPageAsync(3, "no such clip", Token)).Clips);
    }

    [Fact]
    public async Task NamesAreUniqueIgnoringCaseAndFileNameCharacters()
    {
        var library = new ClipLibrary(_directory);
        var first = await SaveAsync(library);
        var second = await SaveAsync(library);
        await library.RenameAsync(first.Id, "Drift: run", Token);

        var taken = await Assert.ThrowsAsync<ClipNameInUseException>(() => library.RenameAsync(second.Id, "drift: RUN", Token));
        Assert.Equal("Another clip is already named “Drift: run”. Choose a different name.", taken.Message);
        // Both names export as "Drift- run.mp4".
        await Assert.ThrowsAsync<ClipNameInUseException>(() => library.RenameAsync(second.Id, "Drift? run", Token));
        Assert.Null((await library.GetPageAsync(0, Token)).Clips.Single(clip => clip.Id == second.Id).Name);

        Assert.Equal("DRIFT: RUN", (await library.RenameAsync(first.Id, "DRIFT: RUN", Token)).Name);
        Assert.Equal("Drift run", (await library.RenameAsync(second.Id, "Drift run", Token)).Name);
        await library.DeleteAsync(first.Id, Token);
        Assert.Equal("Drift: run", (await library.RenameAsync(second.Id, "Drift: run", Token)).Name);
    }

    [Fact]
    public async Task FolderExportOfANamedClipIsStableAndNotDuplicated()
    {
        var library = new ClipLibrary(_directory);
        var clip = await SaveAsync(library);
        await library.RenameAsync(clip.Id, "Drift", Token);
        var folder = Path.Combine(_directory, "exports");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "Drift.mp4"), [9], Token);

        Assert.True((await library.ExportToDirectoryAsync(clip.Id, folder, Token)).FileCreated);
        Assert.False((await library.ExportToDirectoryAsync(clip.Id, folder, Token)).FileCreated);
        Assert.Equal(["Drift.mp4", $"Drift-{clip.Id.ToString("N")[..8]}.mp4"],
            Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(name => name!.Length).ToArray());
    }

    private async Task<ClipEntry> SaveAsync(ClipLibrary library, Func<FinalizedClipMedia, FinalizedClipMedia>? media = null,
        Func<ClipRecordingSpec, ClipRecordingSpec>? recording = null)
    {
        var spec = recording?.Invoke(new(60, 1080, 60, 75)) ?? new(60, 1080, 60, 75);
        var target = await library.ReserveSaveAsync(spec, Token);
        await File.WriteAllBytesAsync(target.MediaPath, MediaBytes, Token);
        var finalized = new FinalizedClipMedia(MediaBytes.Length, 1920, 1080, 60, 0, 600_000_000, true);
        return await library.CommitFinalizedAsync(target.Id, media?.Invoke(finalized) ?? finalized, Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
