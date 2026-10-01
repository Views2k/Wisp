using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wisp.Core.Tunes;

namespace Wisp.App.Tunes;

public sealed record SavedTune
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid Id { get; init; }
    public required TuneSnapshot Snapshot { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required DateTimeOffset ModifiedAtUtc { get; init; }
}

public sealed class TuneStore
{
    public const int MaximumNameLength = 40;
    public const int MaximumDescriptionLength = 2000;
    public const int MaximumEntries = 2000;
    public const int MaximumFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _utcNow;
    private string? _warning;

    public TuneStore(string directory) : this(directory, () => DateTimeOffset.UtcNow) { }
    internal TuneStore(string directory, Func<DateTimeOffset> utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public string? Warning => Volatile.Read(ref _warning);

    public Task<IReadOnlyList<SavedTune>> ListAsync(CancellationToken token = default) => InBackground<IReadOnlyList<SavedTune>>(async () =>
    {
        Volatile.Write(ref _warning, null);
        var entries = new List<SavedTune>();
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.wisptune"))
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) || id == Guid.Empty) continue;
            if (++count > MaximumEntries)
            {
                Volatile.Write(ref _warning, "The tune library is full. Additional files have been kept.");
                break;
            }
            try { entries.Add(await ReadAsync(id, token).ConfigureAwait(false)); }
            catch (Exception error) when (IsDataError(error))
            { Volatile.Write(ref _warning, "A saved tune could not be read. Its file has been kept."); }
        }
        return entries.OrderByDescending(item => item.SavedAtUtc).ThenBy(item => item.Id).ToArray();
    }, token);

    public Task<SavedTune> LoadAsync(Guid id, CancellationToken token = default) => InBackground(() => ReadAsync(id, token), token);

    public Task<SavedTune> SaveAsync(TuneSnapshot snapshot, string name, string description, CancellationToken token = default) => InBackground(async () =>
    {
        var metadata = NormalizeMetadata(name, description);
        ValidateSnapshot(snapshot);
        if (Directory.EnumerateFiles(_directory, "*.wisptune").Take(MaximumEntries).Count() >= MaximumEntries)
            throw new IOException("The tune library is full.");
        var now = _utcNow().ToUniversalTime();
        var saved = new SavedTune
        {
            Id = Guid.NewGuid(), Snapshot = snapshot, Name = metadata.Name, Description = metadata.Description,
            SavedAtUtc = now, ModifiedAtUtc = now
        };
        await WriteAsync(saved, replace: false, token).ConfigureAwait(false);
        return saved;
    }, token);

    public Task<SavedTune> UpdateMetadataAsync(Guid id, string name, string description, CancellationToken token = default) => InBackground(async () =>
    {
        var metadata = NormalizeMetadata(name, description);
        var saved = await ReadAsync(id, token).ConfigureAwait(false);
        var now = _utcNow().ToUniversalTime();
        var updated = saved with
        {
            Name = metadata.Name, Description = metadata.Description,
            ModifiedAtUtc = now < saved.ModifiedAtUtc ? saved.ModifiedAtUtc : now
        };
        await WriteAsync(updated, replace: true, token).ConfigureAwait(false);
        return updated;
    }, token);

    internal static (string Name, string Description) NormalizeMetadata(string name, string description)
    {
        if (name is null || description is null) throw new ArgumentException("Enter a name for this tune.");
        name = name.Trim(); description = description.Trim();
        if (name.Length == 0 || name.Length > MaximumNameLength || name.Any(char.IsControl))
            throw new ArgumentException($"Enter a name of 1 to {MaximumNameLength} characters on one line.");
        if (description.Length > MaximumDescriptionLength || description.Any(value => char.IsControl(value) && value is not ('\r' or '\n' or '\t')))
            throw new ArgumentException($"Keep the description within {MaximumDescriptionLength} characters.");
        return (name, description);
    }

    private Task<T> InBackground<T>(Func<Task<T>> action, CancellationToken token) => Task.Run(async () =>
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckPath(_directory);
            Directory.CreateDirectory(_directory);
            CheckPath(_directory);
            return await action().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }, token);

    private async Task<SavedTune> ReadAsync(Guid id, CancellationToken token)
    {
        var path = TunePath(id);
        CheckPath(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("This tune file is too large or empty.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicateProperties(document.RootElement);
        var saved = JsonSerializer.Deserialize<SavedTune>(bytes, JsonOptions) ?? throw new InvalidDataException("The saved tune is empty.");
        Validate(saved);
        if (saved.Id != id) throw new InvalidDataException("The saved tune identity is invalid.");
        return saved;
    }

    private async Task WriteAsync(SavedTune saved, bool replace, CancellationToken token)
    {
        Validate(saved);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(saved, JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("This tune is too large to save.");
        var destination = TunePath(saved.Id);
        var temporary = Path.Combine(_directory, $".tune-{Guid.NewGuid():N}.tmp");
        var backup = Path.Combine(_directory, $"{saved.Id:N}.bak");
        CheckPath(destination); CheckPath(temporary); CheckPath(backup);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            CheckPath(destination); CheckPath(temporary); CheckPath(backup);
            if (replace) File.Replace(temporary, destination, backup);
            else File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { CheckPath(temporary); File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void Validate(SavedTune saved)
    {
        if (saved.SchemaVersion != 1 || saved.Id == Guid.Empty || saved.Snapshot is null ||
            saved.SavedAtUtc == default || saved.ModifiedAtUtc < saved.SavedAtUtc ||
            saved.SavedAtUtc.Offset != TimeSpan.Zero || saved.ModifiedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("The saved tune format is invalid or unsupported.");
        try
        {
            var metadata = NormalizeMetadata(saved.Name, saved.Description);
            if (metadata.Name != saved.Name || metadata.Description != saved.Description)
                throw new InvalidDataException("The saved tune metadata is invalid.");
            ValidateSnapshot(saved.Snapshot);
        }
        catch (ArgumentException error) { throw new InvalidDataException("The saved tune data is invalid.", error); }
    }

    private string TunePath(Guid id) => id == Guid.Empty ? throw new ArgumentException("Choose a saved tune.", nameof(id)) :
        Path.Combine(_directory, $"{id:N}.wisptune");

    private static void ValidateSnapshot(TuneSnapshot snapshot)
    {
        if (!TuneSnapshotValidator.TryValidate(snapshot, out _) || !snapshot.IsComplete)
            throw new InvalidDataException("Read a complete supported tune before saving it.");
    }

    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The tune library must use a regular local folder.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The tune file has duplicate fields.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    private static bool IsDataError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
}
