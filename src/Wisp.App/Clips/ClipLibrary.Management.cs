using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Wisp.App.Clips;

// Names live in a separate file so earlier Wisp versions, which reject unknown
// index fields, can still open the library after a downgrade.
public sealed partial class ClipLibrary
{
    public const int MaximumNameLength = 80;
    internal const string NamesFileName = ".wisp-clip-names.json";
    private const string NamesFormat = "wisp.clip-names";
    private const int MaximumNamesBytes = 1024 * 1024;
    private const int MaximumSearchTerms = 8;

    public static string? NormalizeName(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0) return null;
        if (name.Length > MaximumNameLength || name.Any(char.IsControl))
            throw new ArgumentException($"Use a name of up to {MaximumNameLength} characters on one line.", nameof(value));
        return name;
    }

    // Returns a Windows-safe file name stem for a clip name, or null to use the default.
    internal static string? FileNameFor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim()) builder.Append(invalid.Contains(character) || char.IsControl(character) ? '-' : character);
        var stem = builder.ToString().TrimEnd('.', ' ');
        if (stem.Length > MaximumNameLength) stem = stem[..MaximumNameLength].TrimEnd('.', ' ');
        return stem.Length > 0 && ClipsSettings.IsSafePathComponent(stem + ".mp4") ? stem : null;
    }

    public Task<ClipEntry> RenameAsync(Guid id, string? name, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        var normalized = NormalizeName(name);
        return InBackground(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            var clip = Find(index, id);
            var names = await ReadNamesAsync(token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Saved clip names could not be read. Their file has been kept.");
            if (normalized is null) names.Remove(id);
            else names[id] = normalized;
            await WriteNamesAsync(names, index.Clips.Select(item => item.Id).ToHashSet(), token).ConfigureAwait(false);
            return WithName(clip, names);
        }, cancellationToken);
    }

    // Deletes the video first, then its record, so a failure never leaves an
    // unlisted file behind. A record whose file is already gone is removed.
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return InBackground(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            var clip = Find(index, id);
            using (var directory = ClipLibraryFiles.OpenDirectory(_directory, create: false))
                ClipLibraryFiles.DeleteMedia(directory, $"{id:N}.mp4", clip.Media.FileBytes);
            index.Clips.Remove(clip);
            await WriteIndexAsync(index, CancellationToken.None).ConfigureAwait(false);
            try
            {
                var names = await ReadNamesAsync(CancellationToken.None).ConfigureAwait(false);
                if (names is not null && names.Remove(id))
                    await WriteNamesAsync(names, index.Clips.Select(item => item.Id).ToHashSet(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (IsFileError(error) || error is InvalidDataException) { /* Stale names are ignored. */ }
            try { await new ClipPlaybackCache(_directory).RemoveForClipAsync(id, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { /* Unused playback copies are evicted later. */ }
            return true;
        }, cancellationToken);
    }

    private static ClipEntry WithName(ClipEntry clip, IReadOnlyDictionary<Guid, string> names) =>
        names.TryGetValue(clip.Id, out var name) ? clip with { Name = name } : clip;

    internal static string[] SearchTerms(string? search) => (search ?? "")
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Take(MaximumSearchTerms).ToArray();

    // Matches the clip's name and the details shown on its card.
    internal static bool Matches(ClipEntry clip, IReadOnlyList<string> terms)
    {
        var culture = CultureInfo.CurrentCulture;
        var saved = clip.SavedAtUtc.ToLocalTime();
        var text = string.Join(' ', clip.Name ?? "",
            saved.ToString("MMM d · h:mm tt", culture), saved.ToString("MMMM d yyyy dddd", culture),
            saved.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            $"{clip.Media.Height}p", $"{clip.Media.FrameRate} fps", clip.Media.Height == 2160 ? "4K" : "",
            clip.Media.LosslessVideo ? "lossless" : "", clip.Media.HdrVideo ? "HDR" : "SDR");
        return terms.All(term => culture.CompareInfo.IndexOf(text, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
    }

    // Null means the names file exists but cannot be trusted; listing then shows default titles.
    private async Task<Dictionary<Guid, string>?> ReadNamesAsync(CancellationToken token)
    {
        var path = Path.Combine(_directory, NamesFileName);
        CheckPath(path);
        if (!File.Exists(path)) return Directory.Exists(path) ? null : [];
        try
        {
            using var directory = ClipLibraryFiles.OpenDirectory(_directory, create: false);
            await using var file = ClipLibraryFiles.OpenRead(directory, NamesFileName);
            if (file.Length is <= 0 or > MaximumNamesBytes) return null;
            var stored = await JsonSerializer.DeserializeAsync<NameIndex>(file, JsonOptions, token).ConfigureAwait(false);
            if (stored is not { Format: NamesFormat, Version: 1, Names: not null } || stored.Names.Count > MaximumEntries) return null;
            foreach (var (id, name) in stored.Names)
                if (id == Guid.Empty || name is null || NormalizeName(name) != name) return null;
            return stored.Names;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException || IsFileError(error)) { return null; }
    }

    private async Task WriteNamesAsync(Dictionary<Guid, string> names, ISet<Guid> clips, CancellationToken token)
    {
        var kept = names.Where(pair => clips.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new NameIndex { Format = NamesFormat, Version = 1, Names = kept }, JsonOptions);
        if (bytes.Length > MaximumNamesBytes) throw new InvalidDataException("Clip names have reached their size limit. No names were changed.");
        var destination = Path.Combine(_directory, NamesFileName);
        var temporary = Path.Combine(_directory, $".wisp-clip-names-{Guid.NewGuid():N}.tmp");
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
            File.Move(temporary, destination, overwrite: true);
            createdTemporary = false;
        }
        finally { if (createdTemporary) DeleteOwnTemporary(temporary); }
    }

    private sealed class NameIndex
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public Dictionary<Guid, string> Names { get; set; } = [];
    }
}
