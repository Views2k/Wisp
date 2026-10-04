using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using NativeFile = Wisp.App.Clips.ClipBufferStore.NativeFile;

namespace Wisp.App.Clips;

// This is disposable playback media, never a library entry or an export. The
// caller keeps the original and the native runtime lease throughout preparation.
internal sealed class ClipPlaybackCache
{
    internal const long MaximumBytes = 4L * 1024 * 1024 * 1024;
    internal const int MaximumEntries = 32;
    internal const string DirectoryName = ".playback-cache";
    private const string Revision = "hdr-main10-pq-420-qp18-v1";
    private const int MaximumRecordBytes = 8192;
    private const long RecordAllowance = 32768;
    private readonly string _library;
    private readonly string _root;
    private readonly ICompatibleClipExporter? _exporter;
    private readonly long _maximumBytes;
    private readonly int _maximumEntries;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    internal ClipPlaybackCache(string libraryDirectory, ICompatibleClipExporter? exporter = null,
        long maximumBytes = MaximumBytes, int maximumEntries = MaximumEntries, string? cacheDirectory = null)
    {
        if (!ClipsSettings.TryNormalizeStorageDirectory(libraryDirectory, out _library))
            throw new ArgumentException("The clip library needs a local folder.", nameof(libraryDirectory));
        if (maximumBytes is <= RecordAllowance or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (maximumEntries is < 1 or > MaximumEntries) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (!ClipsSettings.TryNormalizeStorageDirectory(cacheDirectory ?? Path.Combine(_library, DirectoryName), out _root) ||
            string.Equals(_root, _library, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The playback cache needs a separate local folder.", nameof(cacheDirectory));
        _exporter = exporter;
        _maximumBytes = maximumBytes;
        _maximumEntries = maximumEntries;
    }

    internal Task<ClipPlaybackLease> PrepareAsync(ClipEntry original, string sourcePath, FileStream heldSource,
        IProgress<double>? progress, CancellationToken token) => Task.Run(async () =>
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var lease = await PrepareCoreAsync(original, sourcePath, heldSource, progress, token).ConfigureAwait(false);
            try { token.ThrowIfCancellationRequested(); return lease; }
            catch { lease.Dispose(); throw; }
        }
        finally { _gate.Release(); }
    }, token);

    // Removes prepared copies of a deleted clip. A copy that is open stays until a later eviction.
    internal Task<int> RemoveForClipAsync(Guid clip, CancellationToken token) => Task.Run(async () =>
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_root)) return 0;
            using var parent = NativeFile.OpenDirectoryTree(_root, create: false);
            using var cacheLock = NativeFile.Open(parent, ".cache.lock", NativeFile.ReadWriteDelete, 0, 3, false);
            if (NativeFile.Identity(cacheLock).Bytes != 0) return 0;
            var removed = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(_root).Take(MaximumEntries * 2 + 2).ToArray())
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (name == ".cache.lock") continue;
                try
                {
                    Ownership owner;
                    using (var directory = NativeFile.Open(parent, name, NativeFile.DirectoryAccess, 3, 1, true))
                        owner = ReadRecord<Ownership>(directory, "owner.json");
                    if (owner.Clip == clip && TryDelete(parent, name)) removed++;
                }
                catch (Exception error) when (StorageError(error)) { }
            }
            return removed;
        }
        finally { _gate.Release(); }
    }, token);

    private async Task<ClipPlaybackLease> PrepareCoreAsync(ClipEntry original, string sourcePath, FileStream source,
        IProgress<double>? progress, CancellationToken token)
    {
        if (!original.Media.HdrVideo || !original.Media.LosslessVideo || original.Id == Guid.Empty ||
            !string.Equals(sourcePath, Path.Combine(_library, $"{original.Id:N}.mp4"), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an HDR lossless clip from this library.", nameof(original));
        ClipLibrary.ValidateMedia(original.Media, original.Recording);
        var sourceIdentity = NativeFile.Identity(source.SafeFileHandle);
        using var library = NativeFile.OpenDirectoryTree(_library, create: false);
        using (var current = NativeFile.Open(library, $"{original.Id:N}.mp4", NativeFile.ReadAccess, 1, 1, false))
            if (NativeFile.Identity(current) != sourceIdentity || sourceIdentity.Bytes != (ulong)original.Media.FileBytes)
                throw new IOException("The original clip changed. Reopen it to prepare playback.");
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Revision,
            original.Id,
            Source = sourceIdentity,
            original.Recording,
            Media = original.Media with { PublicationReceipt = null }
        }, JsonOptions))).ToLowerInvariant();
        using var parent = NativeFile.OpenDirectoryTree(_root, create: true);
        // A second Wisp process may inspect its library, but must not prepare or
        // evict this cache concurrently. No retry loop or directory watcher.
        using var cacheLock = NativeFile.Open(parent, ".cache.lock", NativeFile.ReadWriteDelete, 0, 3, false);
        if (NativeFile.Identity(cacheLock).Bytes != 0) throw Unavailable();
        token.ThrowIfCancellationRequested();
        var hit = TryLease(parent, key, original, cacheHit: true);
        if (hit is not null)
        {
            try { progress?.Report(100); return hit; }
            catch { hit.Dispose(); throw; }
        }
        RemoveIncomplete(parent, key);

        var scan = Scan(parent);
        var reserve = Math.Min(_maximumBytes, Math.Max(RecordAllowance + 1,
            Math.Min(_maximumBytes, Math.Max(256L * 1024 * 1024,
                (long)(original.DurationSeconds * original.Media.Width * original.Media.Height * original.Media.FrameRate / 16d)))));
        foreach (var item in scan.Entries.OrderBy(entry => entry.LastUsed))
        {
            token.ThrowIfCancellationRequested();
            if (scan.Count < _maximumEntries && scan.Bytes <= (ulong)(_maximumBytes - reserve)) break;
            if (!TryDelete(parent, item.Name)) continue;
            scan = Scan(parent);
        }
        if (scan.Count >= _maximumEntries || scan.Bytes >= (ulong)(_maximumBytes - RecordAllowance)) throw Unavailable();
        var available = _maximumBytes - checked((long)scan.Bytes) - RecordAllowance;
        var location = Path.Combine(_root, key);
        var created = CreateEntry(parent, key, original.Id);
        using var directory = created.Directory;
        var filename = $"{original.Id:N}.mp4";
        var identity = created.Media;
        var completed = false;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            var exceeded = false;
            var boundedProgress = new InlineProgress(value =>
            {
                using var writing = NativeFile.Open(directory, filename, NativeFile.ReadAccess, 3, 1, false);
                var writingIdentity = NativeFile.Identity(writing);
                if (!NativeFile.SameFile(identity, writingIdentity) || writingIdentity.Bytes > (ulong)available)
                { exceeded = true; limit.Cancel(); }
                progress?.Report(value);
            });
            try
            {
                await (_exporter ?? new CompatibleClipExporter(8, available, target: CompatibleClipTarget.PreserveHdr)).ExportAsync(original, sourcePath,
                    Path.Combine(location, filename), boundedProgress, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (exceeded && !token.IsCancellationRequested) { throw Unavailable(); }
            token.ThrowIfCancellationRequested();
            using (var finished = NativeFile.Open(directory, filename, NativeFile.ReadWriteDelete, 1, 1, false))
            {
                var finishedIdentity = NativeFile.Identity(finished);
                if (!NativeFile.SameFile(identity, finishedIdentity) || finishedIdentity.Bytes is 0 || finishedIdentity.Bytes > (ulong)available)
                    throw Unavailable();
                using (var stream = NativeFile.Stream(finished, FileAccess.ReadWrite)) stream.Flush(flushToDisk: true);
                finishedIdentity = NativeFile.Identity(finished);
                if (NativeFile.Identity(source.SafeFileHandle) != sourceIdentity) throw Unavailable();
                WriteRecord(directory, "complete.tmp", new Completion(Revision, key, original.Id, finishedIdentity));
                using var record = NativeFile.Open(directory, "complete.tmp", NativeFile.ReadWriteDelete, 1, 1, false);
                NativeFile.RenameNew(record, directory, "complete.json");
            }
            token.ThrowIfCancellationRequested();
            var lease = TryLease(parent, key, original, cacheHit: false) ?? throw Unavailable();
            try { progress?.Report(100); }
            catch { lease.Dispose(); throw; }
            completed = true;
            return lease;
        }
        finally
        {
            // ExportAsync returns only after joining its native encoder. Closing
            // the directory first lets checked deletion acquire DELETE access.
            directory.Dispose();
            if (!completed) TryDelete(parent, key);
        }
    }

    private ClipPlaybackLease? TryLease(SafeFileHandle parent, string key, ClipEntry original, bool cacheHit)
    {
        SafeFileHandle? directory = null;
        SafeFileHandle? root = null;
        FileStream? file = null;
        try
        {
            directory = NativeFile.Open(parent, key, NativeFile.DirectoryAccess, 3, 1, true);
            var owner = ReadRecord<Ownership>(directory, "owner.json");
            var complete = ReadRecord<Completion>(directory, "complete.json");
            if (!ValidOwner(owner, key) || complete.Media is null || complete.Revision != Revision || complete.Key != key || complete.Clip != original.Id ||
                owner.Clip != original.Id || !NativeFile.SameFile(owner.Media, complete.Media)) return null;
            using var handle = NativeFile.Open(directory, $"{original.Id:N}.mp4", NativeFile.ReadAccess, 1, 1, false);
            if (NativeFile.Identity(handle) != complete.Media || complete.Media.Bytes is 0 || complete.Media.Bytes > (ulong)_maximumBytes) return null;
            using (var usage = NativeFile.Open(directory, "used", NativeFile.ReadWriteDelete, 1, 1, false))
            {
                if (!NativeFile.SameFile(NativeFile.Identity(usage), owner.Usage)) return null;
                WriteUsage(usage);
            }
            root = NativeFile.OpenDirectoryTree(_root, create: false);
            file = NativeFile.Stream(handle, FileAccess.Read);
            var entry = original with
            {
                Recording = original.Recording with { LosslessVideo = false },
                Media = original.Media with { LosslessVideo = false, SizeLimited = false, FileBytes = checked((long)complete.Media.Bytes), PublicationReceipt = null }
            };
            var result = new ClipPlaybackLease(entry, Path.Combine(_root, key, $"{original.Id:N}.mp4"), cacheHit, file, directory, root);
            file = null; directory = null; root = null;
            return result;
        }
        catch (Exception error) when (StorageError(error)) { return null; }
        finally { file?.Dispose(); directory?.Dispose(); root?.Dispose(); }
    }

    private ScanResult Scan(SafeFileHandle parent)
    {
        ulong bytes = 0;
        var entries = new List<CacheItem>();
        var paths = Directory.EnumerateFileSystemEntries(_root).Take(MaximumEntries * 2 + 2).ToArray();
        if (paths.Length > MaximumEntries * 2 + 1) throw Unavailable();
        var count = 0;
        Span<byte> time = stackalloc byte[8];
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (name == ".cache.lock") continue;
            count++;
            try
            {
                using var directory = NativeFile.Open(parent, name, NativeFile.DirectoryAccess, 3, 1, true);
                var names = Directory.EnumerateFileSystemEntries(path).Take(9).Select(Path.GetFileName).ToArray();
                if (names.Length > 8) throw Unavailable();
                foreach (var child in names)
                {
                    using var handle = NativeFile.Open(directory, child!, NativeFile.ReadAccess, 3, 1, false);
                    bytes = checked(bytes + NativeFile.Identity(handle).Bytes);
                }
                try
                {
                    var owner = ReadRecord<Ownership>(directory, "owner.json");
                    if (!ValidOwner(owner, name)) continue;
                    using var usage = NativeFile.Open(directory, "used", NativeFile.ReadAccess, 1, 1, false);
                    if (!NativeFile.SameFile(NativeFile.Identity(usage), owner.Usage) || NativeFile.Identity(usage).Bytes != 8) continue;
                    using var stream = NativeFile.Stream(usage, FileAccess.Read);
                    stream.ReadExactly(time);
                    entries.Add(new(name, BitConverter.ToInt64(time)));
                }
                catch (Exception error) when (StorageError(error))
                { /* A partial initialization is counted, preserved and never reused. */ }
            }
            catch (Exception error) when (StorageError(error))
            {
                // Unknown entries remain untouched; refuse to assume their disk
                // usage is zero or to remove another program's files.
                throw Unavailable();
            }
        }
        return new(bytes, count, entries);
    }

    private void RemoveIncomplete(SafeFileHandle parent, string key)
    {
        var incomplete = false;
        try
        {
            using var directory = NativeFile.Open(parent, key, NativeFile.DirectoryAccess, 3, 1, true);
            try { using var completed = NativeFile.Open(directory, "complete.json", NativeFile.ReadAccess, 1, 1, false); }
            catch (IOException error) when ((error.HResult & 0xffff) == 2) { incomplete = true; }
            if (!incomplete)
            {
                var owner = ReadRecord<Ownership>(directory, "owner.json");
                if (!ValidOwner(owner, key)) return;
                try { using var media = NativeFile.Open(directory, $"{owner.Clip:N}.mp4", NativeFile.ReadAccess, 1, 1, false); }
                catch (IOException error) when ((error.HResult & 0xffff) == 2) { incomplete = true; }
            }
        }
        catch (Exception error) when (StorageError(error)) { }
        if (incomplete) TryDelete(parent, key);
    }

    private (SafeFileHandle Directory, NativeFile.Stamp Media) CreateEntry(SafeFileHandle parent, string key, Guid clip)
    {
        // A process crash before owner.json is flushed leaves only a unique
        // staging directory. It must not occupy this source's reusable key.
        var stagingName = "stage-" + Guid.NewGuid().ToString("N");
        using var staging = NativeFile.Open(parent, stagingName, NativeFile.DirectoryAccess | NativeFile.DeleteAccess, 3, 2, true);
        var directoryIdentity = NativeFile.Identity(staging);
        NativeFile.Stamp media;
        var initialized = false;
        try
        {
            media = InitializeEntry(staging, $"{clip:N}.mp4", key, clip);
            initialized = true;
            NativeFile.RenameNew(staging, parent, key);
        }
        catch
        {
            staging.Dispose();
            if (initialized) TryDelete(parent, stagingName, key);
            else DeleteEmptyDirectory(parent, stagingName, directoryIdentity);
            throw;
        }
        staging.Dispose();
        var directory = NativeFile.Open(parent, key, NativeFile.DirectoryAccess, 3, 1, true);
        if (NativeFile.SameFile(NativeFile.Identity(directory), directoryIdentity)) return (directory, media);
        directory.Dispose();
        throw Unavailable();
    }

    private static NativeFile.Stamp InitializeEntry(SafeFileHandle directory, string filename, string key, Guid clip)
    {
        var created = new List<SafeFileHandle>();
        try
        {
            var media = NativeFile.Open(directory, filename, NativeFile.ReadWriteDelete, 3, 2, false);
            created.Add(media);
            var identity = NativeFile.Identity(media);
            var usage = NativeFile.Open(directory, "used", NativeFile.ReadWriteDelete, 1, 2, false);
            created.Add(usage);
            WriteUsage(usage);
            var record = NativeFile.Open(directory, "owner.json", NativeFile.ReadWriteDelete, 1, 2, false);
            created.Add(record);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Ownership(Revision, key, clip, identity, NativeFile.Identity(usage)), JsonOptions);
            using var stream = NativeFile.Stream(record, FileAccess.ReadWrite);
            stream.Write(bytes); stream.Flush(flushToDisk: true);
            return identity;
        }
        catch
        {
            foreach (var file in created)
            {
                try { NativeFile.Delete(file); }
                catch (Exception error) when (StorageError(error)) { }
            }
            throw;
        }
        finally { foreach (var file in created) file.Dispose(); }
    }

    private static void DeleteEmptyDirectory(SafeFileHandle parent, string key, NativeFile.Stamp expected)
    {
        try
        {
            using var directory = NativeFile.Open(parent, key, NativeFile.DirectoryAccess | NativeFile.DeleteAccess, 3, 1, true);
            if (NativeFile.SameFile(NativeFile.Identity(directory), expected)) NativeFile.Delete(directory);
        }
        catch (Exception error) when (StorageError(error)) { }
    }

    private bool TryDelete(SafeFileHandle parent, string name, string? expectedKey = null)
    {
        try
        {
            using var directory = NativeFile.Open(parent, name, NativeFile.DirectoryAccess | NativeFile.DeleteAccess, 3, 1, true);
            var owner = ReadRecord<Ownership>(directory, "owner.json");
            if (!ValidOwner(owner, expectedKey ?? name)) return false;
            var allowed = new[] { "owner.json", "used", $"{owner.Clip:N}.mp4", "complete.json", "complete.tmp" };
            var names = Directory.EnumerateFileSystemEntries(Path.Combine(_root, name)).Take(6).Select(Path.GetFileName).ToArray();
            if (names.Length > 5 || names.Any(item => !allowed.Contains(item, StringComparer.Ordinal))) return false;
            Completion? complete = null;
            if (names.Contains("complete.json", StringComparer.Ordinal))
            {
                complete = ReadRecord<Completion>(directory, "complete.json");
                if (complete.Revision != Revision || complete.Key != name || complete.Clip != owner.Clip ||
                    complete.Media is null || !NativeFile.SameFile(complete.Media, owner.Media)) return false;
            }
            var opened = new List<SafeFileHandle>();
            var identities = new Dictionary<string, NativeFile.Stamp>(StringComparer.Ordinal);
            try
            {
                foreach (var item in names)
                {
                    var handle = NativeFile.Open(directory, item!, NativeFile.ReadAccess | NativeFile.DeleteAccess, 1, 1, false);
                    opened.Add(handle);
                    var stamp = NativeFile.Identity(handle);
                    if (item == $"{owner.Clip:N}.mp4" && !NativeFile.SameFile(stamp, owner.Media) ||
                        item == "used" && !NativeFile.SameFile(stamp, owner.Usage)) return false;
                    if (item == $"{owner.Clip:N}.mp4" && complete is not null && stamp != complete.Media) return false;
                    identities.Add(item!, stamp);
                }
            }
            finally { foreach (var handle in opened) handle.Dispose(); }
            opened.Clear();
            // Windows cannot rename the parent while child handles deny delete
            // sharing. Keep the directory held, close the validated children,
            // retire the key, then reopen and revalidate every exact identity.
            NativeFile.RenameNew(directory, parent, "retired-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var item in identities)
                {
                    var handle = NativeFile.Open(directory, item.Key, NativeFile.ReadAccess | NativeFile.DeleteAccess, 1, 1, false);
                    opened.Add(handle);
                    if (NativeFile.Identity(handle) != item.Value) return false;
                }
                foreach (var handle in opened) NativeFile.Delete(handle);
            }
            finally { foreach (var handle in opened) handle.Dispose(); }
            NativeFile.Delete(directory);
            return true;
        }
        catch (Exception error) when (StorageError(error)) { return false; }
    }

    private static bool ValidOwner(Ownership owner, string key) => owner.Revision == Revision && owner.Key == key &&
        key.Length == 64 && key.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') && owner.Clip != Guid.Empty &&
        owner.Media is not null && owner.Usage is not null;

    private static void WriteUsage(SafeFileHandle usage)
    {
        using var stream = NativeFile.Stream(usage, FileAccess.ReadWrite);
        stream.Position = 0; stream.Write(BitConverter.GetBytes(DateTimeOffset.UtcNow.UtcTicks)); stream.SetLength(8);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteRecord<T>(SafeFileHandle directory, string name, T record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (bytes.Length > MaximumRecordBytes) throw Unavailable();
        using var handle = NativeFile.Open(directory, name, NativeFile.ReadWriteDelete, 1, 2, false);
        using var stream = NativeFile.Stream(handle, FileAccess.ReadWrite);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }

    private static T ReadRecord<T>(SafeFileHandle directory, string name)
    {
        using var handle = NativeFile.Open(directory, name, NativeFile.ReadAccess, 1, 1, false);
        using var stream = NativeFile.Stream(handle, FileAccess.Read);
        if (stream.Length is <= 0 or > MaximumRecordBytes) throw Unavailable();
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != document.RootElement.EnumerateObject().Count())
            throw Unavailable();
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw Unavailable();
    }

    private static bool StorageError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException;
    private static IOException Unavailable() => new("The HDR playback cache could not be prepared. Close other clip players and check available local disk space. The original clip is kept.");
    private sealed record Ownership(string Revision, string Key, Guid Clip, NativeFile.Stamp Media, NativeFile.Stamp Usage);
    private sealed record Completion(string Revision, string Key, Guid Clip, NativeFile.Stamp Media);
    private sealed record CacheItem(string Name, long LastUsed);
    private sealed record ScanResult(ulong Bytes, int Count, List<CacheItem> Entries);
    private sealed class InlineProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
}

internal sealed class ClipPlaybackLease(ClipEntry entry, string path, bool cacheHit, FileStream media,
    SafeFileHandle directory, SafeFileHandle root) : IDisposable
{
    internal ClipEntry Entry { get; } = entry;
    internal string Path { get; } = path;
    internal bool CacheHit { get; } = cacheHit;
    public void Dispose() { media.Dispose(); directory.Dispose(); root.Dispose(); }
}
