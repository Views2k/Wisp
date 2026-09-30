using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisp.App.Clips;

public sealed record ClipRecordingSpec(int LengthSeconds, int ResolutionHeight, int FrameRate, int Quality);
public sealed record ClipSaveTarget(Guid Id, DateTimeOffset RequestedAtUtc, ClipRecordingSpec Recording, string MediaPath);

// The native writer supplies this only after successful mux finalization and
// closing its file. Container/codec validation remains the native writer's
// contract; this library validates ownership, size and the reported metadata.
public sealed record FinalizedClipMedia(long FileBytes, int Width, int Height, int FrameRate,
    long ActualStart100ns, long ActualEnd100ns, bool HasAudio);

public sealed record ClipEntry(Guid Id, DateTimeOffset SavedAtUtc, ClipRecordingSpec Recording,
    FinalizedClipMedia Media, DateTimeOffset? ViewedAtUtc = null, DateTimeOffset? ExportedAtUtc = null)
{
    [JsonIgnore]
    public double DurationSeconds => (Media.ActualEnd100ns - Media.ActualStart100ns) / 10_000_000d;
}

public sealed record ClipLibraryPage(IReadOnlyList<ClipEntry> Clips, int PageIndex, int PageCount,
    int TotalClips, int PendingSaves, int UnviewedClips, int UnexportedClips, int NewClips);
public sealed record ClipExportResult(bool FileCreated, bool ExportStateSaved);

public sealed class ClipLibrary
{
    public const int PageSize = 25;
    public const int MaximumEntries = 5000;
    public const long MaximumMediaBytes = 16L * 1024 * 1024 * 1024;
    internal const int MaximumIndexBytes = 8 * 1024 * 1024;
    internal const string IndexFileName = ".wisp-clips.json";
    private const string IndexFormat = "wisp.clip-library";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClipLibrary(string directory)
    {
        if (!ClipsSettings.TryNormalizeStorageDirectory(directory, out _directory))
            throw new ArgumentException("Choose a clip folder on a local drive using a full path.", nameof(directory));
    }

    public Task<ClipLibraryPage> GetPageAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        return InBackground(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            var ordered = index.Clips.OrderByDescending(clip => clip.SavedAtUtc).ThenBy(clip => clip.Id).ToArray();
            var pages = (ordered.Length + PageSize - 1) / PageSize;
            var selectedPage = Math.Min(pageIndex, Math.Max(0, pages - 1));
            return new ClipLibraryPage(ordered.Skip(selectedPage * PageSize).Take(PageSize).ToArray(), selectedPage,
                pages, ordered.Length, index.Pending.Count, ordered.Count(clip => clip.ViewedAtUtc is null),
                ordered.Count(clip => clip.ExportedAtUtc is null),
                ordered.Count(clip => clip.ViewedAtUtc is null && clip.ExportedAtUtc is null));
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ClipSaveTarget>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        InBackground<IReadOnlyList<ClipSaveTarget>>(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            return index.Pending.Select(Target).ToArray();
        }, cancellationToken);

    public Task<ClipSaveTarget> ReserveSaveAsync(ClipRecordingSpec recording, CancellationToken cancellationToken = default)
    {
        ValidateRecording(recording);
        return InBackground(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            if (index.Clips.Count + index.Pending.Count >= MaximumEntries)
                throw new InvalidOperationException("The clip library has reached its 5,000-record limit. Existing clips and pending saves have been kept.");
            var reservation = new PendingClip(Guid.NewGuid(), DateTimeOffset.UtcNow, recording);
            if (File.Exists(MediaPath(reservation.Id)) || Directory.Exists(MediaPath(reservation.Id)))
                throw new IOException("The new clip filename is already in use. No file was replaced.");
            index.Pending.Add(reservation);
            await WriteIndexAsync(index, token).ConfigureAwait(false);
            return Target(reservation);
        }, cancellationToken);
    }

    public Task<ClipEntry> CommitFinalizedAsync(Guid id, FinalizedClipMedia media, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        ArgumentNullException.ThrowIfNull(media);
        return InBackground(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            var pending = index.Pending.SingleOrDefault(item => item.Id == id)
                ?? throw new InvalidOperationException("That clip has no pending save reservation.");
            ValidateMedia(media, pending.Recording);
            await using var file = OpenMedia(id, media.FileBytes);
            var clip = new ClipEntry(id, DateTimeOffset.UtcNow, pending.Recording, media);
            index.Pending.Remove(pending);
            index.Clips.Add(clip);
            await WriteIndexAsync(index, token).ConfigureAwait(false);
            return clip;
        }, cancellationToken);
    }

    // Call after the player's successful open/play action, never merely when a
    // thumbnail enters the viewport or a file is selected.
    public Task<ClipEntry> MarkViewedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return InBackground(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            var clip = Find(index, id);
            if (clip.ViewedAtUtc is not null) return clip;
            var updated = clip with { ViewedAtUtc = DateTimeOffset.UtcNow };
            index.Clips[index.Clips.IndexOf(clip)] = updated;
            await WriteIndexAsync(index, token).ConfigureAwait(false);
            return updated;
        }, cancellationToken);
    }

    public Task<string> GetMediaPathAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return InBackground(async token =>
        {
            var clip = Find(await ReadIndexAsync(token).ConfigureAwait(false), id);
            using var file = OpenMedia(id, clip.Media.FileBytes);
            return MediaPath(id);
        }, cancellationToken);
    }

    // Byte-for-byte export preserves the recorded resolution and compressed
    // quality. The final export is kept even if updating its state later fails.
    public Task<ClipExportResult> ExportAsync(Guid id, string newFilePath, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        var destination = ValidateExportPath(newFilePath);
        return InBackground(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            var clip = Find(index, id);
            CheckPath(destination);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("The export file already exists. Choose a new filename.");
            var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".wisp-clip-export-{Guid.NewGuid():N}.tmp");
            var createdTemporary = false;
            try
            {
                await using (var source = OpenMedia(id, clip.Media.FileBytes))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    createdTemporary = true;
                    await source.CopyToAsync(output, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                CheckPath(destination);
                CheckPath(temporary);
                File.Move(temporary, destination, overwrite: false);
                createdTemporary = false;
            }
            finally { if (createdTemporary) DeleteOwnTemporary(temporary); }

            index.Clips[index.Clips.IndexOf(clip)] = clip with { ExportedAtUtc = DateTimeOffset.UtcNow };
            try
            {
                // The copy is already committed; cancellation must not falsely
                // report an absent export or remove the user's completed file.
                await WriteIndexAsync(index, CancellationToken.None).ConfigureAwait(false);
                return new ClipExportResult(true, true);
            }
            catch (Exception error) when (IsFileError(error)) { return new ClipExportResult(true, false); }
        }, cancellationToken);
    }

    private Task<T> InBackground<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(_directory)!);
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
                    throw new IOException("Choose a clip folder on a local drive. Network drives cannot be used for recording.");
                CheckPath(_directory);
                Directory.CreateDirectory(_directory);
                var lockPath = Path.Combine(_directory, ".wisp-clips.lock");
                CheckPath(lockPath);
                // A second library/process fails explicitly as busy instead of
                // writing a stale index. The lock file is never truncated/deleted.
                using var fileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (fileLock.Length != 0) throw new InvalidDataException("The clip library lock file is not recognized.");
                cancellationToken.ThrowIfCancellationRequested();
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }, cancellationToken);

    private async Task<LibraryIndex> ReadIndexAsync(CancellationToken token)
    {
        var path = Path.Combine(_directory, IndexFileName);
        CheckPath(path);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new InvalidDataException("The clip index path is not a file.");
            return new LibraryIndex { Format = IndexFormat, Version = 1 };
        }
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length is <= 0 or > MaximumIndexBytes) throw new InvalidDataException("The clip index is empty or too large. Its file has been kept.");
        var index = await JsonSerializer.DeserializeAsync<LibraryIndex>(file, JsonOptions, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The clip index is invalid. Its file has been kept.");
        ValidateIndex(index);
        index.WasPersisted = true;
        return index;
    }

    private async Task WriteIndexAsync(LibraryIndex index, CancellationToken token)
    {
        ValidateIndex(index);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions);
        if (bytes.Length > MaximumIndexBytes) throw new InvalidDataException("The clip index has reached its size limit. No clips were removed.");
        var destination = Path.Combine(_directory, IndexFileName);
        var temporary = Path.Combine(_directory, $".wisp-clips-{Guid.NewGuid():N}.tmp");
        var createdTemporary = false;
        try
        {
            CheckPath(destination);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                createdTemporary = true;
                await file.WriteAsync(bytes, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            CheckPath(destination);
            CheckPath(temporary);
            File.Move(temporary, destination, overwrite: index.WasPersisted);
            createdTemporary = false;
            index.WasPersisted = true;
        }
        finally { if (createdTemporary) DeleteOwnTemporary(temporary); }
    }

    private FileStream OpenMedia(Guid id, long expectedBytes)
    {
        var path = MediaPath(id);
        CheckPath(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length == expectedBytes) return file;
        file.Dispose();
        throw new InvalidDataException("The clip file size does not match its saved metadata. The file has been kept.");
    }

    private ClipSaveTarget Target(PendingClip pending)
    {
        var path = MediaPath(pending.Id);
        CheckPath(path);
        return new(pending.Id, pending.RequestedAtUtc, pending.Recording, path);
    }
    private string MediaPath(Guid id) => Path.Combine(_directory, $"{id:N}.mp4");
    private static ClipEntry Find(LibraryIndex index, Guid id) => index.Clips.SingleOrDefault(clip => clip.Id == id)
        ?? throw new InvalidOperationException("That saved clip is not in this library.");

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Choose a saved clip.", nameof(id));
    }

    private static void ValidateRecording(ClipRecordingSpec recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (!ClipsSettings.LengthChoices.Contains(recording.LengthSeconds) ||
            !ClipsSettings.ResolutionChoices.Contains(recording.ResolutionHeight) ||
            !ClipsSettings.FrameRateChoices.Contains(recording.FrameRate) || recording.Quality is < 10 or > 100)
            throw new InvalidDataException("The clip recording settings are invalid.");
    }

    private static void ValidateMedia(FinalizedClipMedia media, ClipRecordingSpec recording)
    {
        ArgumentNullException.ThrowIfNull(media);
        // 480p uses an even 854-pixel width; all other choices are exact 16:9.
        var expectedWidth = recording.ResolutionHeight == 480 ? 854 : recording.ResolutionHeight * 16 / 9;
        if (media.FileBytes is <= 0 or > MaximumMediaBytes || media.Width != expectedWidth ||
            media.Height != recording.ResolutionHeight || media.FrameRate != recording.FrameRate ||
            media.ActualStart100ns < 0 || media.ActualEnd100ns <= media.ActualStart100ns ||
            media.ActualEnd100ns - media.ActualStart100ns > 300L * 10_000_000)
            throw new InvalidDataException("The finalized clip metadata is invalid or differs from its recording settings.");
    }

    private static void ValidateIndex(LibraryIndex index)
    {
        if (index.Format != IndexFormat || index.Version != 1 || index.Clips is null || index.Pending is null ||
            index.Clips.Count > MaximumEntries || index.Pending.Count > MaximumEntries - index.Clips.Count)
            throw new InvalidDataException("The clip index format or capacity is invalid. Its file has been kept.");
        var ids = new HashSet<Guid>();
        foreach (var clip in index.Clips)
        {
            if (clip is null || clip.Id == Guid.Empty || !ids.Add(clip.Id) || clip.SavedAtUtc == default ||
                clip.ViewedAtUtc == default(DateTimeOffset) || clip.ExportedAtUtc == default(DateTimeOffset))
                throw new InvalidDataException("The clip index contains invalid or duplicate records.");
            ValidateRecording(clip.Recording);
            ValidateMedia(clip.Media, clip.Recording);
        }
        foreach (var pending in index.Pending)
        {
            if (pending is null || pending.Id == Guid.Empty || !ids.Add(pending.Id) || pending.RequestedAtUtc == default)
                throw new InvalidDataException("The clip index contains invalid or duplicate reservations.");
            ValidateRecording(pending.Recording);
        }
    }

    private static string ValidateExportPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase) ||
            !ClipsSettings.TryNormalizeDirectory(Path.GetDirectoryName(path), out var directory) ||
            !ClipsSettings.IsSafePathComponent(Path.GetFileName(path)))
            throw new ArgumentException("Choose a new .mp4 file using a full path.", nameof(path));
        return Path.Combine(directory, Path.GetFileName(path));
    }

    internal static void CheckPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Clips cannot use linked folders or files.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private static void DeleteOwnTemporary(string path)
    {
        try { CheckPath(path); File.Delete(path); }
        catch (Exception error) when (IsFileError(error)) { }
    }

    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or SecurityException;

    private sealed class LibraryIndex
    {
        public LibraryIndex() { }
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public List<ClipEntry> Clips { get; set; } = [];
        public List<PendingClip> Pending { get; set; } = [];
        [JsonIgnore]
        public bool WasPersisted { get; set; }
    }

    private sealed record PendingClip(Guid Id, DateTimeOffset RequestedAtUtc, ClipRecordingSpec Recording);
}
