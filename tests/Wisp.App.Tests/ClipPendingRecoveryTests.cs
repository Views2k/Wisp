using System.IO;
using System.Text.Json.Nodes;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipPendingRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WispClipPendingTests", Guid.NewGuid().ToString("N"));
    private string BufferRoot => Path.Combine(_root, "buffer");
    private string LibraryRoot => Path.Combine(_root, "clips");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ClipRecordingSpec Recording = new(60, 1080, 60, 75);
    // Storage contracts use inert bytes, not playable video.
    private static readonly byte[] Bytes = [1, 2, 3, 4, 5, 6];
    private static FinalizedClipMedia Media => new(Bytes.Length, 1920, 1080, 60, 0, 10_000_000, true);

    [Fact]
    public async Task TerminalEmptyReservationCanBeRemovedButExistingMediaIsKept()
    {
        var library = new ClipLibrary(LibraryRoot);
        var empty = await library.ReserveSaveAsync(Recording, Token);
        Assert.True(await library.DropEmptyReservationAsync(empty.Id, Token));
        Assert.False(await library.DropEmptyReservationAsync(empty.Id, Token));
        var retained = await library.ReserveSaveAsync(Recording, Token);
        await File.WriteAllBytesAsync(retained.MediaPath, Bytes, Token);
        Assert.False(await library.DropEmptyReservationAsync(retained.Id, Token));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(retained.MediaPath, Token));
        Assert.Equal(retained.Id, Assert.Single(await library.ListPendingAsync(Token)).Id);
    }

    [Fact]
    public async Task PrivateEmptinessRequiresNoMediaOrRecoveryRecord()
    {
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        var id = Guid.NewGuid();
        Assert.True(await store.IsEmptyAsync(id, Token));
        await File.WriteAllBytesAsync(store.PrivateMediaPath(id), [], Token);
        Assert.False(await store.IsEmptyAsync(id, Token));
        var recordOnly = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(store.Workspace, $"clip-{recordOnly:N}.json"), "{}", Token);
        Assert.False(await store.IsEmptyAsync(recordOnly, Token));
    }

    [Fact]
    public async Task DismissalPersistsWithoutRemovingReservationsOrMediaAndNewFailuresStillNotify()
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await File.WriteAllBytesAsync(target.MediaPath, Bytes, Token);
        await library.DismissPendingNoticesAsync(Token);
        var restarted = new ClipLibrary(LibraryRoot);
        var page = await restarted.GetPageAsync(0, Token);
        Assert.Equal(1, page.PendingSaves);
        Assert.Equal(0, page.PendingNotices);
        Assert.Equal(target, Assert.Single(await restarted.ListPendingAsync(Token)));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(target.MediaPath, Token));
        await restarted.ReserveSaveAsync(Recording, Token);
        page = await restarted.GetPageAsync(0, Token);
        Assert.Equal(2, page.PendingSaves);
        Assert.Equal(1, page.PendingNotices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleFinalizedMediaRecoversWithOrWithoutPublishedDestination(bool destinationPresent)
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        var privatePath = store.PrivateMediaPath(target.Id);
        await File.WriteAllBytesAsync(privatePath, Bytes, Token);
        await store.PublishAsync(target, Media, Token);
        if (!destinationPresent) File.Move(target.MediaPath, target.MediaPath + ".uncommitted");
        await store.DisposeAsync();
        await library.DismissPendingNoticesAsync(Token);
        var result = await library.ReconcilePendingAsync(BufferRoot, Token);
        Assert.Equal(new ClipPendingRecoveryResult(1, 0, 0, false), result);
        Assert.Equal(target.Id, Assert.Single((await library.GetPageAsync(0, Token)).Clips).Id);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(target.MediaPath, Token));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(privatePath, Token));
        Assert.True(File.Exists(Path.Combine(store.Workspace, $"clip-{target.Id:N}.json")));
        Assert.Equal(new ClipPendingRecoveryResult(0, 0, 0, false), await library.ReconcilePendingAsync(BufferRoot, Token));
    }

    [Fact]
    public async Task CollisionNeverReplacesDestinationOrDiscardsFinalizedSource()
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        await File.WriteAllBytesAsync(store.PrivateMediaPath(target.Id), Bytes, Token);
        var different = new byte[Bytes.Length];
        await File.WriteAllBytesAsync(target.MediaPath, different, Token);
        await Assert.ThrowsAsync<RecorderClientException>(() => store.PublishAsync(target, Media, Token));
        await store.DisposeAsync();
        var result = await library.ReconcilePendingAsync(BufferRoot, Token);
        Assert.Equal(0, result.Recovered);
        Assert.Equal(1, result.Remaining);
        Assert.Equal(different, await File.ReadAllBytesAsync(target.MediaPath, Token));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(store.PrivateMediaPath(target.Id), Token));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("metadata")]
    [InlineData("target")]
    public async Task RecoveryRejectsChangedSourceInvalidMetadataAndDifferentReservation(string change)
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        var privatePath = store.PrivateMediaPath(target.Id);
        await File.WriteAllBytesAsync(privatePath, Bytes, Token);
        await store.PublishAsync(target, Media, Token);
        await store.DisposeAsync();
        if (change == "source") File.SetLastWriteTimeUtc(privatePath, DateTime.UtcNow.AddMinutes(-5));
        else
        {
            var path = Path.Combine(store.Workspace, $"clip-{target.Id:N}.json");
            var record = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!;
            if (change == "metadata") record["media"]!["width"] = 1280;
            else record["target"]!["requestedAtUtc"] = target.RequestedAtUtc.AddSeconds(-1);
            await File.WriteAllTextAsync(path, record.ToJsonString(), Token);
        }
        var result = await library.ReconcilePendingAsync(BufferRoot, Token);
        Assert.Equal(0, result.Recovered);
        Assert.Equal(1, result.Remaining);
        Assert.Empty((await library.GetPageAsync(0, Token)).Clips);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(target.MediaPath, Token));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(privatePath, Token));
    }

    [Fact]
    public async Task InvalidMetadataKeepsItsFilesAndAllowsTheNextCandidateToRecover()
    {
        var library = new ClipLibrary(LibraryRoot);
        var invalid = await library.ReserveSaveAsync(Recording, Token);
        var valid = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        foreach (var target in new[] { invalid, valid })
        {
            await File.WriteAllBytesAsync(store.PrivateMediaPath(target.Id), Bytes, Token);
            await store.PublishAsync(target, Media, Token);
        }
        await store.DisposeAsync();
        var path = Path.Combine(store.Workspace, $"clip-{invalid.Id:N}.json");
        var record = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!;
        record["media"]!["width"] = 1280;
        await File.WriteAllTextAsync(path, record.ToJsonString(), Token);

        Assert.Equal(new ClipPendingRecoveryResult(1, 0, 1, false), await library.ReconcilePendingAsync(BufferRoot, Token));
        Assert.Equal(invalid.Id, Assert.Single(await library.ListPendingAsync(Token)).Id);
        Assert.Equal(valid.Id, Assert.Single((await library.GetPageAsync(0, Token)).Clips).Id);
        foreach (var target in new[] { invalid, valid })
        {
            Assert.Equal(Bytes, await File.ReadAllBytesAsync(target.MediaPath, Token));
            Assert.Equal(Bytes, await File.ReadAllBytesAsync(store.PrivateMediaPath(target.Id), Token));
        }
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task BusyWorkspaceCannotProveEmptyButIdleScanCanRemoveEmptyReservation()
    {
        var library = new ClipLibrary(LibraryRoot);
        await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        Assert.Equal(new ClipPendingRecoveryResult(0, 0, 1, true), await library.ReconcilePendingAsync(BufferRoot, Token));
        await store.DisposeAsync();
        Assert.Equal(new ClipPendingRecoveryResult(0, 1, 0, false), await library.ReconcilePendingAsync(BufferRoot, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOwnershipOrEntryLimitCannotProveAReservationEmpty(bool exceedLimit)
    {
        var library = new ClipLibrary(LibraryRoot);
        await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        await store.DisposeAsync();
        if (exceedLimit)
        {
            for (var index = 0; index < 128; index++)
                await File.WriteAllTextAsync(Path.Combine(store.Workspace, $"keep-{index}.txt"), "keep", Token);
        }
        else await File.WriteAllTextAsync(Path.Combine(store.Workspace, ClipBufferStore.OwnerName), "{}", Token);
        Assert.Equal(new ClipPendingRecoveryResult(0, 0, 1, true), await library.ReconcilePendingAsync(BufferRoot, Token));
    }

    [Fact]
    public async Task RecoveryWaitsUntilFinalizedFilesWorkspaceIsIdle()
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        await File.WriteAllBytesAsync(store.PrivateMediaPath(target.Id), Bytes, Token);
        await store.PublishAsync(target, Media, Token);
        Assert.Equal(new ClipPendingRecoveryResult(0, 0, 1, true), await library.ReconcilePendingAsync(BufferRoot, Token));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(store.PrivateMediaPath(target.Id), Token));
        Assert.Empty((await library.GetPageAsync(0, Token)).Clips);
    }

    [Fact]
    public async Task UnrecordedPrivateMediaIsKeptAndNeverAdvertisedAsPlayable()
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        await File.WriteAllBytesAsync(store.PrivateMediaPath(target.Id), Bytes, Token);
        await store.DisposeAsync();
        Assert.Equal(new ClipPendingRecoveryResult(0, 0, 1, false), await library.ReconcilePendingAsync(BufferRoot, Token));
        Assert.Empty((await library.GetPageAsync(0, Token)).Clips);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(store.PrivateMediaPath(target.Id), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseRenamedPrivateMediaOrRecordStillPreservesTheReservation(bool recordOnly)
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        var name = recordOnly ? $"CLIP-{target.Id:N}.JSON" : $"{target.Id:N}.MP4";
        var path = Path.Combine(store.Workspace, name);
        await File.WriteAllBytesAsync(path, Bytes, Token);
        await store.DisposeAsync();
        Assert.Equal(new ClipPendingRecoveryResult(0, 0, 1, false), await library.ReconcilePendingAsync(BufferRoot, Token));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(path, Token));
        Assert.Single(await library.ListPendingAsync(Token));
    }

    [Fact]
    public void RecoveryBudgetChargesEveryAttemptIncludingFailedCopies()
    {
        var bytes = new ClipRecoveryBudget();
        Assert.True(bytes.TryStart(ClipLibrary.MaximumMediaBytes / 2));
        // An attempted publication may fail; the next attempt has only half left.
        Assert.False(bytes.TryStart(ClipLibrary.MaximumMediaBytes / 2 + 1));
        Assert.True(bytes.TryStart(ClipLibrary.MaximumMediaBytes / 2));
        Assert.False(bytes.TryStart(1));

        var attempts = new ClipRecoveryBudget();
        for (var index = 0; index < ClipLibrary.PageSize; index++) Assert.True(attempts.TryStart(1));
        Assert.False(attempts.TryStart(1));
        Assert.False(new ClipRecoveryBudget().TryStart(0));
        Assert.False(new ClipRecoveryBudget().TryStart(long.MaxValue));
    }

    [Fact]
    public async Task MissingBufferRootRemovesOnlyEmptyReservationsAndBoundsEachPass()
    {
        var library = new ClipLibrary(LibraryRoot);
        for (var index = 0; index <= ClipLibrary.PageSize; index++) await library.ReserveSaveAsync(Recording, Token);
        Assert.Equal(new ClipPendingRecoveryResult(0, ClipLibrary.PageSize, 1, false), await library.ReconcilePendingAsync(BufferRoot, Token));
        Assert.Equal(new ClipPendingRecoveryResult(0, 1, 0, false), await library.ReconcilePendingAsync(BufferRoot, Token));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
