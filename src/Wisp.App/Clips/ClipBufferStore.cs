using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Wisp.App.Clips;

internal sealed record ClipBufferCleanup(int Examined, int Retained, ulong KnownRetainedBytes, bool RetainedBytesIncomplete, bool ScanLimitReached);
internal sealed record ClipBufferChunkOwner(string Name, ulong Volume, ulong FileIdLow, ulong FileIdHigh);

// Only a successful library commit may release its private finalized copy.
// This receipt is process-local and is never serialized into library metadata.
internal sealed class ClipBufferCommitReceipt(Action<SafeFileHandle> validate, Action commit)
{
    internal void Validate(SafeFileHandle file) => validate(file);
    internal void Commit() => commit();
}

internal sealed partial class ClipBufferStore : IAsyncDisposable
{
    internal const string OwnerName = "owner.json", LeaseName = "lease";
    private const string Format = "wisp.clip-buffer";
    private const int MaximumRecordBytes = 8192, MaximumWorkspaces = 64, MaximumEntries = 128;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SafeFileHandle _directory, _lease;
    private bool _disposed;
    internal string Workspace { get; }
    internal string SpoolDirectory => Path.Combine(Workspace, $".wisp-recorder-{Session:N}");
    internal Guid Session { get; }
    internal ClipBufferCleanup StartupCleanup { get; }

    private ClipBufferStore(string workspace, Guid session, SafeFileHandle directory, SafeFileHandle lease, ClipBufferCleanup cleanup)
    { Workspace = workspace; Session = session; _directory = directory; _lease = lease; StartupCleanup = cleanup; }

    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "ClipBuffer");

    internal static Task<ClipBufferStore> OpenAsync(Guid session, string? root, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (session == Guid.Empty) throw new ArgumentException("A recorder session is required.", nameof(session));
        var location = root ?? DefaultRoot;
        if (!ClipsSettings.TryNormalizeStorageDirectory(location, out location)) throw new RecorderClientException("buffer_storage_unavailable");
        try
        {
            using var parent = NativeFile.OpenDirectoryTree(location, create: true);
            var cleanup = CleanupOwned(location, parent, token);
            token.ThrowIfCancellationRequested();
            var directory = NativeFile.Open(parent, session.ToString("N"), NativeFile.DirectoryAccess, 3, 2, true);
            SafeFileHandle? lease = null;
            try
            {
                lease = NativeFile.Open(directory, LeaseName, NativeFile.ReadWriteDelete, 0, 2, false);
                WriteRecord(directory, OwnerName, new OwnerRecord(Format, 1, session));
                return new ClipBufferStore(Path.Combine(location, session.ToString("N")), session, directory, lease, cleanup);
            }
            catch { lease?.Dispose(); directory.Dispose(); throw; }
        }
        catch (Exception error) when (StorageError(error)) { throw StorageFailure(error, "buffer_storage_unavailable"); }
    }, token);

    internal string PrivateMediaPath(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A clip reservation is required.", nameof(id));
        return Path.Combine(Workspace, $"{id:N}.mp4");
    }

    internal static Task<ClipBufferCleanup> CleanupAsync(string root, CancellationToken token) => Task.Run(() =>
    {
        if (!ClipsSettings.TryNormalizeStorageDirectory(root, out var normalized)) throw new RecorderClientException("buffer_storage_unavailable");
        using var directory = NativeFile.OpenDirectoryTree(normalized, create: false);
        return CleanupOwned(normalized, directory, token);
    }, token);

    internal static bool TryReadChunkOwner(ReadOnlySpan<byte> bytes, Guid session, string companion, out ClipBufferChunkOwner? owner)
    {
        owner = null;
        if (bytes.Length != 128 || !bytes[..8].SequenceEqual("WSCOWN01"u8) || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 128 ||
            !bytes.Slice(16, 32).SequenceEqual(Encoding.ASCII.GetBytes(session.ToString("N")))) return false;
        foreach (var value in bytes[104..]) if (value != 0) return false;
        var nameBytes = bytes.Slice(72, 32);
        var end = nameBytes.IndexOf((byte)0);
        if (end <= 0) return false;
        foreach (var value in nameBytes[end..]) if (value != 0) return false;
        foreach (var value in nameBytes[..end]) if (value > 127) return false;
        var name = Encoding.ASCII.GetString(nameBytes[..end]);
        if (name != "configuration.bin" && !(name.Length == 21 && name[0] is 'a' or 'v' && name.EndsWith(".bin", StringComparison.Ordinal) &&
            name.AsSpan(1, 16).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))) return false;
        if (companion != name + ".owner") return false;
        owner = new(name, BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[64..]));
        return true;
    }

    internal async Task<FinalizedClipMedia> PublishAsync(ClipSaveTarget target, FinalizedClipMedia media, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var destinationWrite = false;
        var stage = "validate_destination";
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ClipsSettings.TryNormalizeStorageDirectory(Path.GetDirectoryName(target.MediaPath), out var library) ||
                !string.Equals(target.MediaPath, Path.Combine(library, $"{target.Id:N}.mp4"), StringComparison.OrdinalIgnoreCase))
                throw new RecorderClientException("clip_publish_failed");
            var name = $"{target.Id:N}.mp4";
            stage = "open_private_media";
            using var sourceHandle = NativeFile.Open(_directory, name, NativeFile.ReadAccess, 1, 1, false);
            stage = "private_media_identity";
            var identity = NativeFile.Identity(sourceHandle);
            if (identity.Bytes != (ulong)media.FileBytes || media.FileBytes is <= 0 or > ClipLibrary.MaximumMediaBytes)
                throw new RecorderClientException("clip_publish_failed");
            if (media.LosslessVideo != target.Recording.LosslessVideo || (media.SizeLimited && !media.LosslessVideo))
                throw new RecorderClientException("clip_publish_failed");
            var record = new ClipRecord(Format, 2, Session, target.Id, target, media, identity);
            stage = "write_recovery_record";
            WriteRecord(_directory, RecordName(target.Id), record);
            destinationWrite = true;
            stage = "open_destination";
            using var destinationDirectory = NativeFile.OpenDirectoryTree(library, create: false);
            var temporaryName = $".wisp-clip-publish-{Guid.NewGuid():N}.tmp";
            stage = "create_publication_temporary";
            using var temporary = NativeFile.Open(destinationDirectory, temporaryName, NativeFile.ReadWriteDelete, 1, 2, false);
            var published = false;
            try
            {
                // Relative handle opens keep both roots fixed throughout the copy.
                // A same-directory rename is atomic even when the source is on another drive.
                stage = "copy_media";
                await using var source = NativeFile.Stream(sourceHandle, FileAccess.Read);
                await using var output = NativeFile.Stream(temporary, FileAccess.ReadWrite);
                await source.CopyToAsync(output, 65536, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                stage = "verify_copy";
                if (output.Length != media.FileBytes || NativeFile.Identity(sourceHandle) != identity)
                    throw new RecorderClientException("clip_publish_failed");
                token.ThrowIfCancellationRequested();
                stage = "rename_publication";
                NativeFile.RenameNew(temporary, destinationDirectory, name);
                published = true;
                stage = "published_identity";
                var copiedIdentity = NativeFile.Identity(temporary);
                // Last-write metadata is final only after all writing handles close.
                await output.DisposeAsync().ConfigureAwait(false);
                temporary.Dispose();
                stage = "reopen_publication";
                using var finished = NativeFile.Open(destinationDirectory, name, NativeFile.ReadAccess, 1, 1, false);
                var publishedIdentity = NativeFile.Identity(finished);
                if (!NativeFile.SameFile(copiedIdentity, publishedIdentity) || publishedIdentity.Bytes != identity.Bytes)
                    throw new RecorderClientException("clip_publish_failed");
                // No cancellation check after publication: the completed file now exists.
                return media with
                {
                    PublicationReceipt = new ClipBufferCommitReceipt(file =>
                {
                    if (NativeFile.Identity(file) != publishedIdentity) throw new RecorderClientException("clip_publish_failed");
                }, () => Commit(record))
                };
            }
            finally
            {
                if (!published && !temporary.IsClosed)
                {
                    try { NativeFile.Delete(temporary); }
                    catch (IOException) { /* The owned temporary is preserved if deletion fails. */ }
                }
            }
        }
        catch (Exception error) when (StorageError(error))
        {
            var failure = StorageFailure(error, "clip_publish_failed", destinationWrite ? "clip_storage_full" : "buffer_storage_full");
            throw new RecorderClientException(failure.Reason) { StorageStage = stage, StorageHResult = error.HResult };
        }
        finally { _gate.Release(); }
    }

    private void Commit(ClipRecord record)
    {
        _gate.Wait();
        try
        {
            try
            {
                if (_disposed)
                {
                    using var directory = NativeFile.OpenDirectoryTree(Workspace, create: false);
                    using var lease = NativeFile.Open(directory, LeaseName, NativeFile.ReadWriteDelete, 0, 1, false);
                    if (NativeFile.Identity(lease).Bytes != 0 || ReadRecord<OwnerRecord>(directory, OwnerName) != new OwnerRecord(Format, 1, Session)) return;
                    WriteRecord(directory, CommitName(record.Clip), record);
                    DeleteCommitted(directory, record);
                }
                else
                {
                    WriteRecord(_directory, CommitName(record.Clip), record);
                    DeleteCommitted(_directory, record);
                }
            }
            catch (Exception error) when (StorageError(error)) { /* A committed library remains successful; retry owned cleanup next startup. */ }
        }
        finally { _gate.Release(); }
    }

    private static ClipBufferCleanup CleanupOwned(string root, SafeFileHandle parent, CancellationToken token)
    {
        var examined = 0; var retained = 0; ulong retainedBytes = 0; var limited = false; var incomplete = false;
        foreach (var path in Directory.EnumerateFileSystemEntries(root).Take(MaximumWorkspaces + 1))
        {
            token.ThrowIfCancellationRequested();
            if (examined == MaximumWorkspaces) { limited = true; break; }
            examined++;
            var name = Path.GetFileName(path);
            if (!Guid.TryParseExact(name, "N", out var session) || session == Guid.Empty) continue;
            try
            {
                using var directory = NativeFile.Open(parent, name, NativeFile.DirectoryAccess | NativeFile.DeleteAccess, 3, 1, true);
                using var lease = NativeFile.Open(directory, LeaseName, NativeFile.ReadWriteDelete, 0, 1, false);
                if (NativeFile.Identity(lease).Bytes != 0) continue;
                var owner = ReadRecord<OwnerRecord>(directory, OwnerName);
                if (owner != new OwnerRecord(Format, 1, session)) continue;
                var spoolName = $".wisp-recorder-{session:N}";
                if (Directory.Exists(Path.Combine(path, spoolName)))
                {
                    var spool = CleanupSpool(directory, path, spoolName, session, token);
                    retainedBytes = checked(retainedBytes + spool.Bytes);
                    incomplete |= spool.Incomplete; limited |= spool.Limited;
                }
                var entries = Directory.EnumerateFileSystemEntries(path).Take(MaximumEntries + 1).Select(Path.GetFileName).ToArray();
                if (entries.Length > MaximumEntries) { retained++; limited = true; incomplete = true; continue; }
                foreach (var entry in entries)
                {
                    if (entry is null || !entry.StartsWith("committed-", StringComparison.Ordinal) || !entry.EndsWith(".json", StringComparison.Ordinal)) continue;
                    if (!Guid.TryParseExact(entry.AsSpan(10, entry.Length - 15), "N", out var clip) || clip == Guid.Empty) continue;
                    try
                    {
                        var record = ReadRecord<ClipRecord>(directory, entry);
                        if (ValidRecord(record, session, clip)) DeleteCommitted(directory, record);
                    }
                    catch (Exception error) when (StorageError(error)) { }
                }
                var remaining = Directory.EnumerateFileSystemEntries(path).Take(MaximumEntries + 1).Select(Path.GetFileName).ToArray();
                if (remaining.Length == 2 && remaining.Contains(OwnerName) && remaining.Contains(LeaseName))
                {
                    using var marker = NativeFile.Open(directory, OwnerName, NativeFile.ReadAccess | NativeFile.DeleteAccess, 1, 1, false);
                    NativeFile.Delete(marker); marker.Dispose();
                    NativeFile.Delete(lease); lease.Dispose();
                    NativeFile.Delete(directory);
                }
                else
                {
                    retained++;
                    // This is a bounded lower bound: opaque native spool directories remain untouched.
                    foreach (var entry in remaining.Take(MaximumEntries))
                    {
                        if (entry is null || entry == spoolName) continue;
                        try { using var file = NativeFile.Open(directory, entry, NativeFile.ReadAccess, 3, 1, false); retainedBytes = checked(retainedBytes + NativeFile.Identity(file).Bytes); }
                        catch (Exception error) when (StorageError(error)) { incomplete = true; }
                    }
                }
            }
            catch (Exception error) when (StorageError(error))
            {
                // Busy, linked, unknown or corrupt workspaces are never cleaned.
                retained++; incomplete = true;
            }
        }
        return new(examined, retained, retainedBytes, incomplete || limited, limited);
    }

    private static (ulong Bytes, bool Incomplete, bool Limited) CleanupSpool(SafeFileHandle workspace, string path, string spoolName, Guid session, CancellationToken token)
    {
        const int maximumEntries = 2048;
        ulong retainedBytes = 0; var incomplete = false; var limited = false;
        try
        {
            using var directory = NativeFile.Open(workspace, spoolName, NativeFile.DirectoryAccess | NativeFile.DeleteAccess, 3, 1, true);
            var spoolPath = Path.Combine(path, spoolName);
            var entries = Directory.EnumerateFileSystemEntries(spoolPath).Take(maximumEntries + 1).Select(Path.GetFileName).ToArray();
            limited = entries.Length > maximumEntries;
            foreach (var name in entries.Take(maximumEntries))
            {
                token.ThrowIfCancellationRequested();
                if (name is null || !name.EndsWith(".owner", StringComparison.Ordinal)) continue;
                try
                {
                    using var companion = NativeFile.Open(directory, name, NativeFile.ReadAccess | NativeFile.DeleteAccess, 1, 1, false);
                    if (NativeFile.Identity(companion).Bytes != 128) continue;
                    using var stream = NativeFile.Stream(companion, FileAccess.Read);
                    var bytes = new byte[128]; stream.ReadExactly(bytes);
                    if (!TryReadChunkOwner(bytes, session, name, out var owner) || owner is null) continue;
                    try
                    {
                        using var data = NativeFile.Open(directory, owner.Name, NativeFile.ReadAccess | NativeFile.DeleteAccess, 1, 1, false);
                        var identity = NativeFile.Identity(data);
                        if (identity.Volume != owner.Volume || identity.FileIdLow != owner.FileIdLow || identity.FileIdHigh != owner.FileIdHigh) continue;
                        NativeFile.Delete(data);
                    }
                    catch (IOException error) when ((error.HResult & 0xffff) == 2) { }
                    NativeFile.Delete(companion);
                }
                catch (Exception error) when (StorageError(error)) { incomplete = true; }
            }
            var remaining = Directory.EnumerateFileSystemEntries(spoolPath).Take(maximumEntries + 1).Select(Path.GetFileName).ToArray();
            if (remaining.Length == 0) NativeFile.Delete(directory);
            else foreach (var name in remaining.Take(maximumEntries))
                {
                    if (name is null) continue;
                    try { using var data = NativeFile.Open(directory, name, NativeFile.ReadAccess, 3, 1, false); retainedBytes = checked(retainedBytes + NativeFile.Identity(data).Bytes); }
                    catch (Exception error) when (StorageError(error)) { incomplete = true; }
                }
        }
        catch (Exception error) when (StorageError(error)) { incomplete = true; }
        return (retainedBytes, incomplete || limited, limited);
    }

    private static bool ValidRecord(ClipRecord record, Guid session, Guid clip) => record.Format == Format && record.Version is 1 or 2 &&
        record.Session == session && record.Clip == clip && record.Target is not null && record.Target.Id == clip && record.Media is not null &&
        record.Target.Recording is not null && record.Media.LosslessVideo == record.Target.Recording.LosslessVideo &&
        (record.Version == 2 || !record.Media.LosslessVideo) &&
        (!record.Media.SizeLimited || record.Media.LosslessVideo) &&
        record.Media.FileBytes > 0 && record.Media.FileBytes <= ClipLibrary.MaximumMediaBytes && record.Identity is not null &&
        record.Identity.Bytes == (ulong)record.Media.FileBytes;

    private static void DeleteCommitted(SafeFileHandle directory, ClipRecord record)
    {
        try
        {
            using var media = NativeFile.Open(directory, $"{record.Clip:N}.mp4", NativeFile.ReadAccess | NativeFile.DeleteAccess, 1, 1, false);
            if (NativeFile.Identity(media) != record.Identity) return;
            NativeFile.Delete(media);
        }
        catch (IOException error) when ((error.HResult & 0xffff) == 2) { }
        DeleteRecord(directory, RecordName(record.Clip));
        DeleteRecord(directory, CommitName(record.Clip));
    }

    private static string RecordName(Guid clip) => $"clip-{clip:N}.json";
    private static string CommitName(Guid clip) => $"committed-{clip:N}.json";
    private static void DeleteRecord(SafeFileHandle directory, string name)
    {
        try { using var file = NativeFile.Open(directory, name, NativeFile.ReadAccess | NativeFile.DeleteAccess, 1, 1, false); NativeFile.Delete(file); }
        catch (IOException error) when ((error.HResult & 0xffff) == 2) { }
    }
    private static void WriteRecord<T>(SafeFileHandle directory, string name, T record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (bytes.Length > MaximumRecordBytes) throw new IOException("The clip recovery record is too large.");
        using var handle = NativeFile.Open(directory, name, NativeFile.ReadWriteDelete, 1, 2, false);
        using var stream = new FileStream(handle, FileAccess.ReadWrite);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }
    private static T ReadRecord<T>(SafeFileHandle directory, string name)
    {
        using var handle = NativeFile.Open(directory, name, NativeFile.ReadAccess, 1, 1, false);
        using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length is <= 0 or > MaximumRecordBytes) throw new IOException("The clip recovery record is not recognized.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != document.RootElement.EnumerateObject().Count())
            throw new IOException("The clip recovery record is not recognized.");
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new IOException("The clip recovery record is not recognized.");
    }
    private static bool StorageError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException;
    private static RecorderClientException StorageFailure(Exception error, string fallback, string full = "buffer_storage_full") =>
        error is RecorderClientException known ? known : new RecorderClientException((error.HResult & 0xffff) is 39 or 112 ? full : fallback);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; _lease.Dispose(); _directory.Dispose(); }
        finally { _gate.Release(); }
    }

    private sealed record OwnerRecord(string Format, int Version, Guid Session);
    private sealed record ClipRecord(string Format, int Version, Guid Session, Guid Clip, ClipSaveTarget Target, FinalizedClipMedia Media, NativeFile.Stamp Identity);

    // All destructive operations are relative to held local directory handles.
    // OBJ_DONT_REPARSE rejects links during the actual open, including ancestors.
    private static class NativeFile
    {
        internal const uint DeleteAccess = 0x10000, ReadAccess = 0x81, ReadWriteDelete = 0x10083, DirectoryAccess = 0xA7;
        internal sealed record Stamp(ulong Volume, ulong FileIdLow, ulong FileIdHigh, ulong Bytes, ulong LastWrite);
        internal static bool SameFile(Stamp first, Stamp second) => first.Volume == second.Volume && first.FileIdLow == second.FileIdLow && first.FileIdHigh == second.FileIdHigh;
        internal static SafeFileHandle OpenDirectoryTree(string path, bool create)
        {
            var root = Path.GetPathRoot(path) ?? throw new IOException("The clip drive is unavailable.");
            var current = Open(null, "\\??\\" + root, 0xA1, 3, 1, true);
            try
            {
                foreach (var component in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    var next = Open(current, component, 0xA1, 3, create ? 3u : 1u, true);
                    current.Dispose(); current = next;
                }
                var final = new char[32768];
                var count = GetFinalPathNameByHandleW(current, final, (uint)final.Length, 0x9);
                if (count == 0 || count >= final.Length || !new string(final, 0, (int)count).StartsWith(@"\\?\Volume{", StringComparison.Ordinal))
                    throw new IOException("Clips need an available local drive.");
                return current;
            }
            catch { current.Dispose(); throw; }
        }
        internal static SafeFileHandle Open(SafeFileHandle? parent, string name, uint access, uint sharing, uint disposition, bool directory)
        {
            if (name.Length is 0 or > 30000 || (parent is not null && (!ClipsSettings.IsSafePathComponent(name) || name.IndexOfAny(['\\', '/', ':']) >= 0)))
                throw new IOException("The clip storage entry is invalid.");
            var text = Marshal.StringToHGlobalUni(name); var pointer = IntPtr.Zero; var parentHeld = false;
            try
            {
                parent?.DangerousAddRef(ref parentHeld);
                var unicode = new UnicodeString { Buffer = text, Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)) };
                pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>()); Marshal.StructureToPtr(unicode, pointer, false);
                var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent?.DangerousGetHandle() ?? IntPtr.Zero, ObjectName = pointer, Attributes = 0x1040 };
                var status = NtCreateFile(out var handle, access | 0x100000, ref attributes, out _, IntPtr.Zero, directory ? 0x10u : 0x80u,
                    sharing, disposition, 0x20u | (directory ? 1u : 0x40u), IntPtr.Zero, 0);
                if (status < 0) { handle.Dispose(); throw Error(RtlNtStatusToDosError(status)); }
                try
                {
                    if (!GetFileInformationByHandle(handle, out var information)) throw Error((uint)Marshal.GetLastWin32Error());
                    if ((information.Attributes & 0x400) != 0 || ((information.Attributes & 0x10) != 0) != directory)
                        throw new IOException("Clips cannot use linked or online-only files.");
                    return handle;
                }
                catch { handle.Dispose(); throw; }
            }
            finally { if (parentHeld) parent!.DangerousRelease(); if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(text); }
        }
        internal static Stamp Identity(SafeFileHandle file)
        {
            if (!GetFileInformationByHandle(file, out var info)) throw Error((uint)Marshal.GetLastWin32Error());
            if (!GetFileInformationByHandleEx(file, 18, out var identity, 24)) throw Error((uint)Marshal.GetLastWin32Error());
            return new(identity.Volume, identity.Low, identity.High, ((ulong)info.SizeHigh << 32) | info.SizeLow, ((ulong)info.WriteHigh << 32) | info.WriteLow);
        }
        internal static FileStream Stream(SafeFileHandle handle, FileAccess access)
        {
            if (!DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out var copy, 0, false, 2))
                throw Error((uint)Marshal.GetLastWin32Error());
            try { return new FileStream(copy, access, 65536, isAsync: false); }
            catch { copy.Dispose(); throw; }
        }
        internal static void Delete(SafeFileHandle file)
        {
            var data = Marshal.AllocHGlobal(1);
            try { Marshal.WriteByte(data, 1); if (!SetFileInformationByHandle(file, 4, data, 1)) throw Error((uint)Marshal.GetLastWin32Error()); }
            finally { Marshal.FreeHGlobal(data); }
        }
        internal static void RenameNew(SafeFileHandle file, SafeFileHandle parent, string name)
        {
            var bytes = Encoding.Unicode.GetBytes(name);
            var offset = Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FirstCharacter)).ToInt32();
            var size = checked(Marshal.SizeOf<RenameInformation>() + bytes.Length);
            var pointer = Marshal.AllocHGlobal(size);
            var held = false;
            try
            {
                parent.DangerousAddRef(ref held);
                for (var index = 0; index < size; index++) Marshal.WriteByte(pointer, index, 0);
                Marshal.WriteIntPtr(pointer, Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.Root)).ToInt32(), parent.DangerousGetHandle());
                Marshal.WriteInt32(pointer, Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.Bytes)).ToInt32(), bytes.Length);
                Marshal.Copy(bytes, 0, IntPtr.Add(pointer, offset), bytes.Length);
                // Native FileRenameInformation resolves this single name against the held
                // parent, without Win32 path conversion or replacement of an existing file.
                var status = NtSetInformationFile(file, out _, pointer, (uint)size, 10);
                if (status < 0) throw Error(RtlNtStatusToDosError(status));
            }
            finally { if (held) parent.DangerousRelease(); Marshal.FreeHGlobal(pointer); }
        }
        private static IOException Error(uint code) => new("Clip storage is unavailable; check local folder access and free space.", unchecked((int)(0x80070000 | code)));
        [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { internal int Length; internal IntPtr RootDirectory, ObjectName; internal uint Attributes; internal IntPtr SecurityDescriptor, SecurityQualityOfService; }
        [StructLayout(LayoutKind.Sequential)] private struct IoStatus { internal IntPtr Status; internal UIntPtr Information; }
        [StructLayout(LayoutKind.Sequential)] private struct Information { internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedIdentity { internal ulong Volume, Low, High; }
        [StructLayout(LayoutKind.Sequential)] private struct RenameInformation { internal uint Flags; internal IntPtr Root; internal uint Bytes; internal ushort FirstCharacter; }
        [DllImport("ntdll.dll", ExactSpelling = true)] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatus status, IntPtr allocationSize, uint fileAttributes, uint sharing, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
        [DllImport("ntdll.dll", ExactSpelling = true)] private static extern uint RtlNtStatusToDosError(int status);
        [DllImport("ntdll.dll", ExactSpelling = true)] private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatus status, IntPtr information, uint bytes, int informationClass);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out ExtendedIdentity information, uint bytes);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, IntPtr information, uint bytes);
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint size, uint flags);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess, out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    }
}
