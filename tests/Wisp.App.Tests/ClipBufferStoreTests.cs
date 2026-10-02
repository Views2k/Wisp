using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipBufferStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WispClipBufferTests", Guid.NewGuid().ToString("N"));
    private string BufferRoot => Path.Combine(_root, "buffer");
    private string LibraryRoot => Path.Combine(_root, "clips");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ClipRecordingSpec Recording = new(60, 1080, 60, 75);
    // File ownership/publication contracts use synthetic bytes, not playable media.
    private static readonly byte[] Bytes = [1, 2, 3, 4, 5, 6];
    private static FinalizedClipMedia Media => new(Bytes.Length, 1920, 1080, 60, 0, 10_000_000, true);

    [Fact]
    public async Task ActiveLeaseAndOwnedDirectoryProtectAnotherRecorderInstance()
    {
        var session = Guid.NewGuid();
        await using var first = await ClipBufferStore.OpenAsync(session, BufferRoot, Token);
        await using var second = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        Assert.True(File.Exists(Path.Combine(first.Workspace, ClipBufferStore.OwnerName)));
        Assert.False(Directory.Exists(first.SpoolDirectory));
        Assert.StartsWith(BufferRoot, first.SpoolDirectory, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<RecorderClientException>(() => ClipBufferStore.OpenAsync(session, BufferRoot, Token));
        Assert.True(Directory.Exists(first.Workspace));
    }

    [Fact]
    public async Task CompletedFilePublishesBeforeLibraryEntryAndPrivateCopyWaitsForCommit()
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        await File.WriteAllBytesAsync(store.PrivateMediaPath(target.Id), Bytes, Token);
        var media = await PublishWithDiagnosticsAsync(store, target);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(target.MediaPath, Token));
        Assert.True(File.Exists(store.PrivateMediaPath(target.Id)));
        Assert.Empty((await library.GetPageAsync(0, Token)).Clips);
        var entry = await library.CommitFinalizedAsync(target.Id, media, Token);
        Assert.Null(entry.Media.PublicationReceipt);
        Assert.False(File.Exists(store.PrivateMediaPath(target.Id)));
        Assert.Single((await library.GetPageAsync(0, Token)).Clips);
        var index = await File.ReadAllTextAsync(Path.Combine(LibraryRoot, ClipLibrary.IndexFileName), Token);
        Assert.DoesNotContain("publicationReceipt", index, StringComparison.Ordinal);
        Assert.DoesNotContain("clip-buffer", index, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(LibraryRoot, "*.tmp"));
    }

    [Fact]
    public async Task ExistingDestinationIsNeverReplacedAndPrivateFinalizedFileSurvives()
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        await File.WriteAllBytesAsync(store.PrivateMediaPath(target.Id), Bytes, Token);
        await File.WriteAllBytesAsync(target.MediaPath, [42], Token);
        var failure = await Assert.ThrowsAsync<RecorderClientException>(() => store.PublishAsync(target, Media, Token));
        Assert.Equal("clip_publish_failed", failure.Reason);
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(target.MediaPath, Token));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(store.PrivateMediaPath(target.Id), Token));
        Assert.Single(await library.ListPendingAsync(Token));
        Assert.Empty(Directory.EnumerateFiles(LibraryRoot, "*.tmp"));
    }

    [Fact]
    public async Task CancelledPublicationKeepsSourceAndReservationWithoutFinalDestination()
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        await File.WriteAllBytesAsync(store.PrivateMediaPath(target.Id), Bytes, Token);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PublishAsync(target, Media, cancelled.Token));
        Assert.False(File.Exists(target.MediaPath));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(store.PrivateMediaPath(target.Id), Token));
        Assert.Single(await library.ListPendingAsync(Token));
    }

    [Fact]
    public async Task FailedIndexCommitKeepsBothFinalCopiesForRecovery()
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        await File.WriteAllBytesAsync(store.PrivateMediaPath(target.Id), Bytes, Token);
        var media = await store.PublishAsync(target, Media, Token);
        await File.WriteAllTextAsync(Path.Combine(LibraryRoot, ClipLibrary.IndexFileName), "{}", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => library.CommitFinalizedAsync(target.Id, media, Token));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(store.PrivateMediaPath(target.Id), Token));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(target.MediaPath, Token));
    }

    [Fact]
    public async Task SameSizeDestinationReplacementCannotCommitOrReleaseThePrivateCopy()
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        await File.WriteAllBytesAsync(store.PrivateMediaPath(target.Id), Bytes, Token);
        var media = await store.PublishAsync(target, Media, Token);
        File.Move(target.MediaPath, target.MediaPath + ".original");
        await File.WriteAllBytesAsync(target.MediaPath, new byte[Bytes.Length], Token);
        var error = await Assert.ThrowsAsync<RecorderClientException>(() => library.CommitFinalizedAsync(target.Id, media, Token));
        Assert.Equal("clip_publish_failed", error.Reason);
        Assert.Single(await library.ListPendingAsync(Token));
        Assert.Empty((await library.GetPageAsync(0, Token)).Clips);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(store.PrivateMediaPath(target.Id), Token));
    }

    [Fact]
    public async Task LateCommitAfterHelperDisposalCanReleaseOnlyItsOwnedPrivateCopy()
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        await File.WriteAllBytesAsync(store.PrivateMediaPath(target.Id), Bytes, Token);
        var media = await store.PublishAsync(target, Media, Token);
        await store.DisposeAsync();
        await library.CommitFinalizedAsync(target.Id, media, Token);
        Assert.False(File.Exists(store.PrivateMediaPath(target.Id)));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(target.MediaPath, Token));
    }

    [Fact]
    public async Task StartupCleansEmptyOwnedWorkspacesButPreservesUnknownFilesAndUncommittedMedia()
    {
        await using var empty = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        await empty.DisposeAsync();
        await using var retained = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        var privatePath = retained.PrivateMediaPath(Guid.NewGuid());
        await File.WriteAllBytesAsync(privatePath, Bytes, Token);
        await File.WriteAllTextAsync(Path.Combine(retained.Workspace, "notes.txt"), "keep", Token);
        await retained.DisposeAsync();
        var result = await ClipBufferStore.CleanupAsync(BufferRoot, Token);
        Assert.False(Directory.Exists(empty.Workspace));
        Assert.Equal(1, result.Retained);
        Assert.True(result.KnownRetainedBytes >= (ulong)Bytes.Length);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(privatePath, Token));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(retained.Workspace, "notes.txt"), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupRetriesCommittedCleanupOnlyForTheRecordedFileIdentity(bool modifyPrivateFile)
    {
        var library = new ClipLibrary(LibraryRoot);
        var target = await library.ReserveSaveAsync(Recording, Token);
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        var privatePath = store.PrivateMediaPath(target.Id);
        await File.WriteAllBytesAsync(privatePath, Bytes, Token);
        var media = await store.PublishAsync(target, Media, Token);
        using (var blockDeletion = new FileStream(privatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await library.CommitFinalizedAsync(target.Id, media, Token);
        Assert.True(File.Exists(privatePath));
        if (modifyPrivateFile) await File.WriteAllBytesAsync(privatePath, [9, 8, 7], Token);
        await store.DisposeAsync();
        await ClipBufferStore.CleanupAsync(BufferRoot, Token);
        Assert.Equal(modifyPrivateFile, File.Exists(privatePath));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(target.MediaPath, Token));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("session")]
    [InlineData("reserved")]
    [InlineData("padding")]
    [InlineData("traversal")]
    [InlineData("companion")]
    [InlineData("size")]
    public void ChunkOwnershipRequiresTheExactVersionSessionNameAndFixedRecord(string error)
    {
        var session = Guid.NewGuid();
        var record = ChunkRecord(session, "v0000000000000001.bin");
        var name = "v0000000000000001.bin.owner";
        Assert.True(ClipBufferStore.TryReadChunkOwner(record, session, name, out _));
        if (error == "version") record[8] = 2;
        if (error == "session") session = Guid.NewGuid();
        if (error == "reserved") record[127] = 1;
        if (error == "padding") record[103] = 1;
        if (error == "traversal") record = ChunkRecord(session, "..\\arbitrary.bin");
        if (error == "companion") name = "a0000000000000001.bin.owner";
        if (error == "size") record = record[..127];
        Assert.False(ClipBufferStore.TryReadChunkOwner(record, session, name, out _));
    }

    [Fact]
    public async Task UnprovenOldNativeChunksRemainInPrivateStorage()
    {
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        Directory.CreateDirectory(store.SpoolDirectory);
        var chunk = Path.Combine(store.SpoolDirectory, "v0000000000000001.bin");
        await File.WriteAllBytesAsync(chunk, Bytes, Token);
        await store.DisposeAsync();
        var cleanup = await ClipBufferStore.CleanupAsync(BufferRoot, Token);
        Assert.Equal(1, cleanup.Retained);
        Assert.True(cleanup.KnownRetainedBytes >= (ulong)Bytes.Length);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(chunk, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InactiveSpoolCleanupRequiresTheRecordedActualFileId(bool changeIdentity)
    {
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        Directory.CreateDirectory(store.SpoolDirectory);
        const string name = "v0000000000000001.bin";
        var chunk = Path.Combine(store.SpoolDirectory, name);
        await File.WriteAllBytesAsync(chunk, Bytes, Token);
        var owner = ChunkRecord(store.Session, name);
        using (var held = File.OpenHandle(chunk, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(GetFileInformationByHandleEx(held, 18, out var id, 24));
            BinaryPrimitives.WriteUInt64LittleEndian(owner.AsSpan(48), id.Volume);
            BinaryPrimitives.WriteUInt64LittleEndian(owner.AsSpan(56), id.Low);
            BinaryPrimitives.WriteUInt64LittleEndian(owner.AsSpan(64), id.High);
        }
        await File.WriteAllBytesAsync(chunk + ".owner", owner, Token);
        if (changeIdentity)
        {
            File.Move(chunk, Path.Combine(_root, "original-chunk.bin"));
            await File.WriteAllBytesAsync(chunk, Bytes, Token);
        }
        await store.DisposeAsync();
        var cleanup = await ClipBufferStore.CleanupAsync(BufferRoot, Token);
        Assert.Equal(changeIdentity, File.Exists(chunk));
        Assert.Equal(changeIdentity, File.Exists(chunk + ".owner"));
        Assert.Equal(changeIdentity ? 1 : 0, cleanup.Retained);
        if (!changeIdentity) Assert.False(Directory.Exists(store.Workspace));
    }

    [Fact]
    public async Task ValidOrphanCompanionCanBeRemovedAfterItsDataWasAlreadyDeleted()
    {
        await using var store = await ClipBufferStore.OpenAsync(Guid.NewGuid(), BufferRoot, Token);
        Directory.CreateDirectory(store.SpoolDirectory);
        const string name = "configuration.bin";
        await File.WriteAllBytesAsync(Path.Combine(store.SpoolDirectory, name + ".owner"), ChunkRecord(store.Session, name), Token);
        await store.DisposeAsync();
        var cleanup = await ClipBufferStore.CleanupAsync(BufferRoot, Token);
        Assert.Equal(0, cleanup.Retained);
        Assert.False(Directory.Exists(store.Workspace));
    }

    private static async Task<FinalizedClipMedia> PublishWithDiagnosticsAsync(ClipBufferStore store, ClipSaveTarget target)
    {
        try { return await store.PublishAsync(target, Media, Token); }
        catch (RecorderClientException error)
        {
            throw new Xunit.Sdk.XunitException($"Publication failed: reason={error.Reason}; stage={error.StorageStage}; hresult=0x{error.StorageHResult:X8}");
        }
    }

    private static byte[] ChunkRecord(Guid session, string name)
    {
        var result = new byte[128]; "WSCOWN01"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), 128);
        Encoding.ASCII.GetBytes(session.ToString("N")).CopyTo(result, 16);
        Encoding.ASCII.GetBytes(name).CopyTo(result, 72);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { internal ulong Volume, Low, High; }
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileIdentity identity, uint bytes);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
