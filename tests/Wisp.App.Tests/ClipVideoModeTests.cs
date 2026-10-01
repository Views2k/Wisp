using System.IO;
using System.Text.Json.Nodes;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipVideoModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WispClipVideoModeTests", Guid.NewGuid().ToString("N"));
    private string LibraryRoot => Path.Combine(_root, "library");
    private string IndexPath => Path.Combine(LibraryRoot, ClipLibrary.IndexFileName);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly byte[] Bytes = [1, 2, 3, 4, 5, 6];
    private static readonly ClipRecordingSpec Recording = new(60, 1080, 60, 100);
    private static FinalizedClipMedia Media => new(Bytes.Length, 1920, 1080, 60, 0, 10_000_000, true);

    [Fact]
    public async Task LegacyLibraryReadsWithoutModificationAndMigratesOnNextWrite()
    {
        var library = new ClipLibrary(LibraryRoot);
        var saved = await SaveAsync(library, false);
        var index = JsonNode.Parse(await File.ReadAllTextAsync(IndexPath, Token))!;
        index["version"] = 1;
        var clip = index["clips"]![0]!;
        clip["recording"]!.AsObject().Remove("losslessVideo");
        clip["media"]!.AsObject().Remove("losslessVideo");
        await File.WriteAllTextAsync(IndexPath, index.ToJsonString(), Token);
        var before = await File.ReadAllBytesAsync(IndexPath, Token);

        var reopened = new ClipLibrary(LibraryRoot);
        var loaded = Assert.Single((await reopened.GetPageAsync(0, Token)).Clips);
        Assert.False(loaded.Recording.LosslessVideo);
        Assert.False(loaded.Media.LosslessVideo);
        Assert.Equal(100, loaded.Recording.Quality);
        Assert.Equal(before, await File.ReadAllBytesAsync(IndexPath, Token));

        await reopened.MarkViewedAsync(saved.Id, Token);
        var migrated = JsonNode.Parse(await File.ReadAllTextAsync(IndexPath, Token))!;
        Assert.Equal(2, migrated["version"]!.GetValue<int>());
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(await reopened.GetMediaPathAsync(saved.Id, Token), Token));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task EncodingMismatchNeverPublishesOrLosesPendingClip(bool requested, bool actual)
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording with { LosslessVideo = requested }, Token);
        await File.WriteAllBytesAsync(target.MediaPath, Bytes, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => library.CommitFinalizedAsync(target.Id,
            Media with { LosslessVideo = actual }, Token));
        Assert.Single(await library.ListPendingAsync(Token));
        Assert.Empty((await library.GetPageAsync(0, Token)).Clips);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(target.MediaPath, Token));
    }

    [Fact]
    public async Task LosslessMetadataSurvivesRestartAndExportCopiesOriginalBytes()
    {
        var library = new ClipLibrary(LibraryRoot);
        var saved = await SaveAsync(library, true);
        var reopened = new ClipLibrary(LibraryRoot);
        var loaded = Assert.Single((await reopened.GetPageAsync(0, Token)).Clips);
        Assert.True(loaded.Recording.LosslessVideo);
        Assert.True(loaded.Media.LosslessVideo);
        Assert.Contains("Lossless video", new ClipCardItem(loaded).Detail, StringComparison.Ordinal);
        var export = Path.Combine(_root, "export.mp4");
        Assert.Equal(new ClipExportResult(true, true), await reopened.ExportAsync(saved.Id, export, Token));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(export, Token));
        await Assert.ThrowsAsync<IOException>(() => reopened.ExportAsync(saved.Id, export, Token));
    }

    [Fact]
    public async Task LegacyVersionCannotMislabelLosslessMetadata()
    {
        var library = new ClipLibrary(LibraryRoot);
        await SaveAsync(library, true);
        var index = JsonNode.Parse(await File.ReadAllTextAsync(IndexPath, Token))!;
        index["version"] = 1;
        await File.WriteAllTextAsync(IndexPath, index.ToJsonString(), Token);
        var before = await File.ReadAllBytesAsync(IndexPath, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ClipLibrary(LibraryRoot).GetPageAsync(0, Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(IndexPath, Token));
    }

    [Fact]
    public async Task PrivatePublicationRejectsEncodingMismatchBeforeCreatingDestination()
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording with { LosslessVideo = true }, Token);
        await using var buffer = await ClipBufferStore.OpenAsync(Guid.NewGuid(), Path.Combine(_root, "buffer"), Token);
        await File.WriteAllBytesAsync(buffer.PrivateMediaPath(target.Id), Bytes, Token);
        await Assert.ThrowsAsync<RecorderClientException>(() => buffer.PublishAsync(target, Media, Token));
        Assert.False(File.Exists(target.MediaPath));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(buffer.PrivateMediaPath(target.Id), Token));
    }

    private static async Task<ClipEntry> SaveAsync(ClipLibrary library, bool lossless)
    {
        // These bytes test metadata and ownership, not codec validity.
        var target = await library.ReserveSaveAsync(Recording with { LosslessVideo = lossless }, Token);
        await File.WriteAllBytesAsync(target.MediaPath, Bytes, Token);
        return await library.CommitFinalizedAsync(target.Id, Media with { LosslessVideo = lossless }, Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
