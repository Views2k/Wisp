using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wisp.App.Runs;

public sealed record RunImportResult(IReadOnlyList<RunSummary> Imported, int SkippedDuplicates);
public sealed record RunDeletionBatch(Guid Id, int Count, int SkippedUnreadable = 0);

internal sealed record RunArchiveEntry(Guid Id, string Path, long Length, string Sha256);
internal sealed record RunArchiveManifest(string Format, int Version, RunArchiveEntry[] Runs);
internal sealed record StagedRunFile(string File, Guid? ExpectedId);

internal static class RunArchive
{
    internal const string Format = "Wisp Run Archive";
    internal const int Version = 1;
    internal const int MaximumManifestBytes = 1_048_576;
    internal const long MaximumPayloadBytes = RunStore.MaximumLibraryEntries * RunStore.MaximumFileBytes;
    private static readonly DateTimeOffset ZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static string EntryPath(Guid id) => $"runs/{id:N}.wisprun";

    internal static void ValidateEntries(RunArchiveEntry[]? entries)
    {
        if (entries is null || entries.Length > RunStore.MaximumLibraryEntries)
            throw new InvalidDataException("The run archive contains too many runs or has an invalid manifest.");
        var ids = new HashSet<Guid>();
        long length = 0;
        foreach (var entry in entries)
        {
            if (entry is null || entry.Id == Guid.Empty || !ids.Add(entry.Id) || entry.Path != EntryPath(entry.Id) ||
                entry.Length is <= 0 or > RunStore.MaximumFileBytes || entry.Sha256 is null ||
                entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("The run archive has invalid or duplicate entries.");
            length += entry.Length;
        }
        if (length > MaximumPayloadBytes) throw new InvalidDataException("The run archive is too large.");
    }

    internal static async Task<RunArchiveEntry> DescribeAsync(Guid id, string path)
    {
        RunStore.CheckPath(path);
        await using var input = File.OpenRead(path);
        if (input.Length is <= 0 or > RunStore.MaximumFileBytes)
            throw new InvalidDataException("A run file is empty or too large.");
        return new(id, EntryPath(id), input.Length, Convert.ToHexString(await SHA256.HashDataAsync(input).ConfigureAwait(false)));
    }

    internal static async Task CopyBoundedAsync(Stream input, string destination, long maximum, long? expected = null)
    {
        RunStore.CheckPath(destination);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("A run archive entry is too large.");
            await output.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
        }
        if (expected is { } size && total != size) throw new InvalidDataException("A run archive entry has an invalid size.");
        output.Flush(flushToDisk: true);
    }

    internal static async Task WriteAsync(string directory, RunArchiveEntry[] entries, string destination)
    {
        ValidateEntries(entries);
        if (!Path.IsPathFullyQualified(destination) || !string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .zip file using a full path.");
        RunStore.CheckPath(destination);
        if (File.Exists(destination)) throw new IOException("Choose a different export filename; that file already exists.");
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".wisp-export-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifest = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression);
                    manifest.LastWriteTime = ZipTimestamp;
                    await using (var stream = manifest.Open())
                        await JsonSerializer.SerializeAsync(stream, new RunArchiveManifest(Format, Version, entries), RunStore.JsonOptions).ConfigureAwait(false);
                    foreach (var item in entries)
                    {
                        var entry = archive.CreateEntry(item.Path, CompressionLevel.NoCompression);
                        entry.LastWriteTime = ZipTimestamp;
                        await using var input = File.OpenRead(Path.Combine(directory, $"{item.Id:N}.wisprun"));
                        await using var stream = entry.Open();
                        await input.CopyToAsync(stream).ConfigureAwait(false);
                    }
                }
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<IReadOnlyList<StagedRunFile>> ExtractAsync(string source, string staging, int maximumRuns)
    {
        RunStore.CheckPath(source);
        await using var file = File.OpenRead(source);
        if (file.Length is <= 0 || file.Length > MaximumPayloadBytes + 16 * 1024 * 1024)
            throw new InvalidDataException("The run archive is empty or too large.");
        ValidateCentralDirectory(file, maximumRuns + 1);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > RunStore.MaximumLibraryEntries + 1)
            throw new InvalidDataException("The run archive contains too many entries.");
        var unique = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
            if (!unique.TryAdd(entry.FullName, entry) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("The run archive has duplicate or linked entries.");
        if (!unique.TryGetValue("manifest.json", out var manifestEntry) || manifestEntry.FullName != "manifest.json" ||
            manifestEntry.Length is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException("Choose a Wisp run archive with a valid manifest.");
        var manifestPath = Path.Combine(staging, "manifest.json");
        await using (var stream = manifestEntry.Open())
            await CopyBoundedAsync(stream, manifestPath, MaximumManifestBytes, manifestEntry.Length).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<RunArchiveManifest>(await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false), RunStore.JsonOptions);
        if (manifest is null || manifest.Format != Format || manifest.Version != Version)
            throw new InvalidDataException("This run archive version is unsupported.");
        ValidateEntries(manifest.Runs);
        if (manifest.Runs.Length > maximumRuns) throw new InvalidDataException("Import no more than 2,000 runs at a time.");
        if (archive.Entries.Count != manifest.Runs.Length + 1)
            throw new InvalidDataException("The run archive has unexpected or missing files.");
        var result = new List<StagedRunFile>();
        foreach (var item in manifest.Runs)
        {
            if (!unique.TryGetValue(item.Path, out var entry) || entry.FullName != item.Path || entry.Length != item.Length)
                throw new InvalidDataException("The run archive has a missing or mismatched run.");
            var path = Path.Combine(staging, $"{item.Id:N}.wisprun");
            await using (var stream = entry.Open())
                await CopyBoundedAsync(stream, path, RunStore.MaximumFileBytes, item.Length).ConfigureAwait(false);
            var actual = await DescribeAsync(item.Id, path).ConfigureAwait(false);
            if (!string.Equals(actual.Sha256, item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A run archive checksum does not match. No runs were imported.");
            result.Add(new(path, item.Id));
        }
        return result;
    }

    // Inspect entry counts before ZipArchive allocates objects for an untrusted directory.
    private static void ValidateCentralDirectory(FileStream file, int maximumEntries)
    {
        var size = (int)Math.Min(file.Length, 65_557);
        var tail = new byte[size];
        file.Position = file.Length - size;
        file.ReadExactly(tail);
        for (var offset = size - 22; offset >= 0; offset--)
        {
            var span = tail.AsSpan(offset);
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span) != 0x06054b50 ||
                offset + 22 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span[20..]) != size) continue;
            var disk = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
            var directoryDisk = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
            var onDisk = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
            ulong count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
            ulong bytes = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
            ulong start = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
            var directoryEnd = (ulong)(file.Length - size + offset);
            if (disk != 0 || directoryDisk != 0 || onDisk != count)
                throw new InvalidDataException("The run archive has an unsupported or oversized ZIP directory.");
            if (offset >= 20 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset - 20)) == 0x07064b50)
            {
                var locator = tail.AsSpan(offset - 20, 20);
                var zip64Start = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1 || zip64Start > (ulong)file.Length - 56)
                    throw new InvalidDataException("The ZIP64 directory is invalid.");
                file.Position = (long)zip64Start;
                var extended = new byte[56];
                file.ReadExactly(extended);
                var data = extended.AsSpan();
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x06064b50 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[4..]) != 44 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != 0 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[20..]) != 0)
                    throw new InvalidDataException("The ZIP64 directory is invalid.");
                count = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[32..]);
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[24..]) != count)
                    throw new InvalidDataException("Split run archives are unsupported.");
                bytes = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[40..]);
                start = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[48..]);
                directoryEnd = zip64Start;
            }
            if (count > (ulong)maximumEntries || bytes > MaximumManifestBytes || start > directoryEnd || bytes != directoryEnd - start)
                throw new InvalidDataException("The run archive has an unsupported or oversized ZIP directory.");
            file.Position = (long)start;
            var central = new byte[(int)bytes];
            file.ReadExactly(central);
            var cursor = 0;
            for (ulong index = 0; index < count; index++)
            {
                if (central.Length - cursor < 46 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(cursor)) != 0x02014b50)
                    throw new InvalidDataException("The run archive directory is corrupt.");
                var record = central.AsSpan(cursor);
                cursor += 46 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(record[28..]) +
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(record[30..]) +
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
                if (cursor > central.Length) throw new InvalidDataException("The run archive directory is corrupt.");
            }
            if (cursor != central.Length) throw new InvalidDataException("The run archive directory has an invalid entry count.");
            file.Position = 0;
            return;
        }
        throw new InvalidDataException("The run archive has no valid ZIP directory.");
    }
}
