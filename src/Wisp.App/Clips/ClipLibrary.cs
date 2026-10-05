using System.IO;
using System.Globalization;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisp.App.Clips;

public sealed record ClipRecordingSpec(int LengthSeconds, int ResolutionHeight, int FrameRate, int Quality,
    bool CaptureSystemAudio = false, bool LosslessVideo = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool PreserveHdrRecording = false);
public sealed record ClipSaveTarget(Guid Id, DateTimeOffset RequestedAtUtc, ClipRecordingSpec Recording, string MediaPath);

// The native writer supplies this only after successful mux finalization and
// closing its file. Container/codec validation remains the native writer's
// contract; this library validates ownership, size and the reported metadata.
public sealed record FinalizedClipMedia(long FileBytes, int Width, int Height, int FrameRate,
    long ActualStart100ns, long ActualEnd100ns, bool HasAudio, bool LosslessVideo = false, bool SizeLimited = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool HdrVideo = false)
{
    // HDR clips use HEVC with BT.2020/PQ color. Existing records omit this flag
    // and retain their H.264 SDR interpretation.
    [JsonIgnore]
    public bool RequiresMpvPlayer => LosslessVideo || HdrVideo;

    [JsonIgnore]
    internal ClipBufferCommitReceipt? PublicationReceipt { get; init; }
}

public sealed record ClipEntry(Guid Id, DateTimeOffset SavedAtUtc, ClipRecordingSpec Recording,
    FinalizedClipMedia Media, DateTimeOffset? ViewedAtUtc = null, DateTimeOffset? ExportedAtUtc = null)
{
    // Names are stored beside the index, so earlier Wisp versions can still read the library.
    [JsonIgnore]
    public string? Name { get; init; }

    [JsonIgnore]
    public double DurationSeconds => (Media.ActualEnd100ns - Media.ActualStart100ns) / 10_000_000d;

    [JsonIgnore]
    public string SuggestedExportName => ExportBaseName + ".mp4";

    [JsonIgnore]
    internal string ExportBaseName => ClipLibrary.FileNameFor(Name) ?? DefaultExportBaseName;

    // Several clips can share a name, so a named clip falls back to its name plus its ID.
    [JsonIgnore]
    internal string UniqueExportBaseName => ClipLibrary.FileNameFor(Name) is { } name ? $"{name}-{Id.ToString("N")[..8]}" : DefaultExportBaseName;

    private string DefaultExportBaseName =>
        $"Wisp-{SavedAtUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Id.ToString("N")[..8]}";
}

public sealed record ClipLibraryPage(IReadOnlyList<ClipEntry> Clips, int PageIndex, int PageCount,
    int TotalClips, int PendingSaves, int UnviewedClips, int UnexportedClips, int NewClips)
{
    public int PendingNotices { get; init; } = PendingSaves;
    // Clips matching the search. Equals TotalClips when no search is applied.
    public int MatchingClips { get; init; } = TotalClips;
}
public sealed record ClipExportResult(bool FileCreated, bool ExportStateSaved);
public sealed record ClipImportResult(int Imported, int AlreadyPresent, int Remaining, int PendingLegacySaves);

public sealed partial class ClipLibrary
{
    public const int PageSize = 25;
    public const int MaximumEntries = 5000;
    public const long MaximumMediaBytes = 16L * 1024 * 1024 * 1024;
    internal const int MaximumImportClips = 25;
    internal const long MaximumImportBytes = 1024L * 1024 * 1024;
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
    private readonly ICompatibleClipExporter _compatibleExporter;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClipLibrary(string directory, ICompatibleClipExporter? compatibleExporter = null)
    {
        if (!ClipsSettings.TryNormalizeStorageDirectory(directory, out _directory))
            throw new ArgumentException("Choose a clip folder on a local drive using a full path.", nameof(directory));
        _compatibleExporter = compatibleExporter ?? new CompatibleClipExporter();
    }

    public Task<ClipLibraryPage> GetPageAsync(int pageIndex, CancellationToken cancellationToken = default) =>
        GetPageAsync(pageIndex, null, cancellationToken);

    public Task<ClipLibraryPage> GetPageAsync(int pageIndex, string? search, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        var terms = SearchTerms(search);
        return InBackground(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            await ReconcileMissingClipsAsync(index, token).ConfigureAwait(false);
            var names = await ReadNamesAsync(token).ConfigureAwait(false) ?? [];
            var ordered = index.Clips.OrderByDescending(clip => clip.SavedAtUtc).ThenBy(clip => clip.Id)
                .Select(clip => WithName(clip, names)).ToArray();
            var matching = terms.Length == 0 ? ordered : ordered.Where(clip => Matches(clip, terms)).ToArray();
            var pages = (matching.Length + PageSize - 1) / PageSize;
            var selectedPage = Math.Min(pageIndex, Math.Max(0, pages - 1));
            return new ClipLibraryPage(matching.Skip(selectedPage * PageSize).Take(PageSize).ToArray(), selectedPage,
                pages, ordered.Length, index.Pending.Count, ordered.Count(clip => clip.ViewedAtUtc is null),
                ordered.Count(clip => clip.ExportedAtUtc is null),
                ordered.Count(clip => clip.ViewedAtUtc is null && clip.ExportedAtUtc is null))
            { PendingNotices = index.Pending.Count(item => !item.NoticeDismissed), MatchingClips = matching.Length };
        }, cancellationToken);
    }

    private async Task ReconcileMissingClipsAsync(LibraryIndex index, CancellationToken token)
    {
        if (index.Clips.Count == 0) return;
        // Probe only indexed names relative to a verified, held directory. A
        // missing path/device or an unreadable file is not proof of deletion.
        using var directory = ClipLibraryFiles.OpenDirectory(_directory, create: false);
        var missing = new HashSet<Guid>();
        foreach (var clip in index.Clips)
        {
            token.ThrowIfCancellationRequested();
            try { using var media = ClipLibraryFiles.OpenRead(directory, $"{clip.Id:N}.mp4"); }
            catch (IOException error) when (IsConfirmedMissingClipFile(error)) { missing.Add(clip.Id); }
            catch (Exception error) when (IsFileError(error)) { /* Keep inaccessible or otherwise unverified entries. */ }
        }
        if (missing.Count == 0) return;
        token.ThrowIfCancellationRequested();
        index.Clips.RemoveAll(clip => missing.Contains(clip.Id));
        await WriteIndexAsync(index, token).ConfigureAwait(false);
    }

    internal static bool IsConfirmedMissingClipFile(IOException error) => (error.HResult & 0xffff) == 2;

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
            media.PublicationReceipt?.Validate(file.SafeFileHandle);
            var clip = new ClipEntry(id, DateTimeOffset.UtcNow, pending.Recording, media with { PublicationReceipt = null });
            index.Pending.Remove(pending);
            index.Clips.Add(clip);
            await WriteIndexAsync(index, token).ConfigureAwait(false);
            media.PublicationReceipt?.Commit();
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
            var names = await ReadNamesAsync(token).ConfigureAwait(false) ?? [];
            if (clip.ViewedAtUtc is not null) return WithName(clip, names);
            var updated = clip with { ViewedAtUtc = DateTimeOffset.UtcNow };
            index.Clips[index.Clips.IndexOf(clip)] = updated;
            await WriteIndexAsync(index, token).ConfigureAwait(false);
            return WithName(updated, names);
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
        return ExportCoreAsync(id, _ => destination, reuseIdentical: false, cancellationToken);
    }

    public Task<ClipExportResult> ExportToDirectoryAsync(Guid id, string directory, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        if (!ClipsSettings.TryNormalizeDirectory(directory, out var normalized))
            throw new ArgumentException("Choose an export folder using a full path.", nameof(directory));
        return ExportCoreAsync(id, clip => Path.Combine(normalized, clip.UniqueExportBaseName + ".mp4"), reuseIdentical: true, cancellationToken);
    }

    private Task<ClipExportResult> ExportCoreAsync(Guid id, Func<ClipEntry, string> destinationFor, bool reuseIdentical, CancellationToken cancellationToken) =>
        InBackground(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            var clip = Find(index, id);
            var destination = destinationFor(WithName(clip, await ReadNamesAsync(token).ConfigureAwait(false) ?? []));
            await using var source = OpenMedia(id, clip.Media.FileBytes);
            var created = await ClipLibraryFiles.CopyOrVerifyAsync(source, Path.GetDirectoryName(destination)!, Path.GetFileName(destination), reuseIdentical, token,
                reuseIdentical ? $"{id:N}.mp4" : null).ConfigureAwait(false);
            return await SaveExportStateAsync(index, clip, created).ConfigureAwait(false);
        }, cancellationToken);

    public Task<ClipExportResult> ExportCompatibleAsync(Guid id, string newFilePath, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        var destination = ValidateExportPath(newFilePath);
        return InBackground(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            var clip = Find(index, id);
            if (!clip.Media.LosslessVideo && !clip.Media.HdrVideo) throw new InvalidOperationException("This clip already uses compatible video.");
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The export filename is already in use.");
            using var directory = ClipLibraryFiles.OpenDirectory(_directory, create: false);
            await using var source = OpenMedia(id, clip.Media.FileBytes);
            var temporary = Path.Combine(_directory, $".wisp-compatible-{Guid.NewGuid():N}.mp4");
            var created = false;
            try
            {
                CheckPath(temporary);
                await using (var staged = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.ReadWrite, 65536, FileOptions.Asynchronous))
                {
                    created = true;
                    await _compatibleExporter.ExportAsync(clip, MediaPath(id), temporary, progress, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (staged.Length is <= 0 or > MaximumMediaBytes) throw new InvalidDataException("The compatible copy is incomplete.");
                    await staged.FlushAsync(token).ConfigureAwait(false); staged.Flush(flushToDisk: true);
                    await ClipLibraryFiles.CopyOrVerifyAsync(staged, Path.GetDirectoryName(destination)!, Path.GetFileName(destination),
                        reuseIdentical: false, token).ConfigureAwait(false);
                }
                var result = await SaveExportStateAsync(index, clip, created: true).ConfigureAwait(false);
                progress?.Report(100);
                return result;
            }
            finally { if (created) DeleteOwnTemporary(temporary); }
        }, cancellationToken);
    }

    private async Task<ClipExportResult> SaveExportStateAsync(LibraryIndex index, ClipEntry clip, bool created)
    {
        if (clip.ExportedAtUtc is not null) return new(created, true);
        index.Clips[index.Clips.IndexOf(clip)] = clip with { ExportedAtUtc = DateTimeOffset.UtcNow };
        try
        {
            // Publication is complete; a later cancellation/index failure must
            // not turn the existing destination into an absent-file claim.
            await WriteIndexAsync(index, CancellationToken.None).ConfigureAwait(false);
            return new(created, true);
        }
        catch (Exception error) when (IsFileError(error)) { return new(created, false); }
    }

    // Only entries named by a valid existing legacy index are imported. The
    // original index remains held read-only throughout the bounded batch.
    public Task<ClipImportResult> ImportLegacyAsync(string legacyDirectory, CancellationToken cancellationToken = default)
    {
        if (!ClipsSettings.TryNormalizeStorageDirectory(legacyDirectory, out var legacy) ||
            string.Equals(legacy, _directory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a different existing legacy clip library.", nameof(legacyDirectory));
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Microsoft.Win32.SafeHandles.SafeFileHandle sourceDirectory;
            try { sourceDirectory = ClipLibraryFiles.OpenDirectory(legacy, create: false); }
            catch (IOException error) when (ClipLibraryFiles.IsMissing(error)) { return new ClipImportResult(0, 0, 0, 0); }
            using (sourceDirectory)
            {
                FileStream sourceIndex;
                try { sourceIndex = ClipLibraryFiles.OpenRead(sourceDirectory, IndexFileName); }
                catch (IOException error) when (ClipLibraryFiles.IsMissing(error)) { return new ClipImportResult(0, 0, 0, 0); }
                await using (sourceIndex)
                {
                    var old = await ReadValidatedIndexAsync(sourceIndex, cancellationToken).ConfigureAwait(false);
                    return await InBackground(async token =>
                    {
                        var index = await ReadIndexAsync(token).ConfigureAwait(false);
                        var present = index.Clips.ToDictionary(clip => clip.Id);
                        var pending = index.Pending.Select(clip => clip.Id).ToHashSet();
                        var candidates = new List<ClipEntry>();
                        var already = 0;
                        foreach (var clip in old.Clips)
                        {
                            token.ThrowIfCancellationRequested();
                            if (pending.Contains(clip.Id)) throw new InvalidDataException("A legacy clip conflicts with a pending private save. All files have been kept.");
                            if (!present.TryGetValue(clip.Id, out var existing)) { candidates.Add(clip); continue; }
                            if (existing.SavedAtUtc != clip.SavedAtUtc || existing.Recording != clip.Recording || existing.Media != clip.Media)
                                throw new InvalidDataException("A legacy clip identity conflicts with this library. All files have been kept.");
                            using var held = OpenMedia(existing.Id, existing.Media.FileBytes);
                            already++;
                        }
                        var imported = 0; long copiedBytes = 0;
                        foreach (var clip in candidates.OrderBy(clip => clip.SavedAtUtc).ThenBy(clip => clip.Id))
                        {
                            token.ThrowIfCancellationRequested();
                            if (imported == MaximumImportClips || imported > 0 && clip.Media.FileBytes > MaximumImportBytes - copiedBytes) break;
                            if (index.Clips.Count + index.Pending.Count == MaximumEntries)
                                throw new InvalidOperationException("The private clip library has reached its 5,000-record limit. Legacy files have been kept.");
                            await using var source = ClipLibraryFiles.OpenRead(sourceDirectory, $"{clip.Id:N}.mp4");
                            if (source.Length != clip.Media.FileBytes) throw new InvalidDataException("A legacy clip size differs from its index. All files have been kept.");
                            await ClipLibraryFiles.CopyOrVerifyAsync(source, _directory, $"{clip.Id:N}.mp4", reuseIdentical: true, token).ConfigureAwait(false);
                            index.Clips.Add(clip with { Media = clip.Media with { PublicationReceipt = null } });
                            await WriteIndexAsync(index, token).ConfigureAwait(false);
                            imported++; copiedBytes = checked(copiedBytes + clip.Media.FileBytes);
                        }
                        return new ClipImportResult(imported, already, candidates.Count - imported, old.Pending.Count);
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
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
            return new LibraryIndex { Format = IndexFormat, Version = 2 };
        }
        using var directory = ClipLibraryFiles.OpenDirectory(_directory, create: false);
        await using var file = ClipLibraryFiles.OpenRead(directory, IndexFileName);
        return await ReadValidatedIndexAsync(file, token).ConfigureAwait(false);
    }

    private static async Task<LibraryIndex> ReadValidatedIndexAsync(FileStream file, CancellationToken token)
    {
        if (file.Length is <= 0 or > MaximumIndexBytes) throw new InvalidDataException("The clip index is empty or too large. Its file has been kept.");
        var index = await JsonSerializer.DeserializeAsync<LibraryIndex>(file, JsonOptions, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The clip index is invalid. Its file has been kept.");
        ValidateIndex(index);
        index.WasPersisted = true;
        return index;
    }

    private async Task WriteIndexAsync(LibraryIndex index, CancellationToken token)
    {
        // Read legacy libraries without touching them; migrate only with a successful write.
        index.Version = 2;
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
        using var directory = ClipLibraryFiles.OpenDirectory(_directory, create: false);
        var file = ClipLibraryFiles.OpenRead(directory, $"{id:N}.mp4");
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

    internal static void ValidateMedia(FinalizedClipMedia media, ClipRecordingSpec recording)
    {
        ArgumentNullException.ThrowIfNull(media);
        // 480p uses an even 854-pixel width; all other choices are exact 16:9.
        var expectedWidth = recording.ResolutionHeight == 480 ? 854 : recording.ResolutionHeight * 16 / 9;
        if (media.FileBytes is <= 0 or > MaximumMediaBytes || media.Width != expectedWidth ||
            media.Height != recording.ResolutionHeight || media.FrameRate != recording.FrameRate ||
            media.LosslessVideo != recording.LosslessVideo ||
            (media.SizeLimited && !media.LosslessVideo) ||
            media.ActualStart100ns < 0 || media.ActualEnd100ns <= media.ActualStart100ns ||
            media.ActualEnd100ns - media.ActualStart100ns > 300L * 10_000_000)
            throw new InvalidDataException("The finalized clip metadata is invalid or differs from its recording settings.");
    }

    private static void ValidateIndex(LibraryIndex index)
    {
        if (index.Format != IndexFormat || index.Version is not (1 or 2) || index.Clips is null || index.Pending is null ||
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
            if (index.Version == 1 && (clip.Recording.LosslessVideo || clip.Media.HdrVideo))
                throw new InvalidDataException("The legacy clip index cannot describe lossless or HDR video. Its file has been kept.");
        }
        foreach (var pending in index.Pending)
        {
            if (pending is null || pending.Id == Guid.Empty || !ids.Add(pending.Id) || pending.RequestedAtUtc == default)
                throw new InvalidDataException("The clip index contains invalid or duplicate reservations.");
            ValidateRecording(pending.Recording);
            if (index.Version == 1 && pending.Recording.LosslessVideo)
                throw new InvalidDataException("The legacy clip index cannot reserve lossless video. Its file has been kept.");
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
                    throw new IOException("This linked or cloud-managed folder is unsupported. Choose a local folder without links; existing clips are kept.");
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

    private sealed record PendingClip(Guid Id, DateTimeOffset RequestedAtUtc, ClipRecordingSpec Recording,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool NoticeDismissed = false);
}
