using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Wisp.App.Tunes;

internal static class TuneCarNameResolver
{
    internal const int MaximumArchiveBytes = 8 * 1024 * 1024;
    internal const int MaximumTableBytes = 256 * 1024;
    private const int MaximumArchiveEntries = 1024;
    private const int MaximumStrings = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    // The capture worker supplies the archive under its verified installation. No path discovery or caching.
    internal static string? TryResolve(string englishArchivePath, int year, ulong modelToken, ulong makeToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (year is < 1800 or > 9999 || modelToken >> 32 != 0x434455F2 || makeToken >> 32 != 0x788FB611) return null;
        try
        {
            var archivePath = NormalizeLocalArchivePath(englishArchivePath);
            if (archivePath is null) return null;
            using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 22 or > MaximumArchiveBytes) return null;
            ValidateDirectory(file, cancellationToken);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            var model = ReadLabel(archive, "Data_Car", unchecked((uint)modelToken), cancellationToken);
            var make = ReadLabel(archive, "List_CarMake", unchecked((uint)makeToken), cancellationToken);
            if (model is null || make is null) return null;
            var label = year.ToString(CultureInfo.InvariantCulture) + " " + make + " " + model;
            return label.Length <= 200 ? label : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string? NormalizeLocalArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        var normalized = Path.GetFullPath(path);
        var root = Path.GetPathRoot(normalized);
        return root is { Length: 3 } && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] == '\\'
            ? normalized : null;
    }

    private static string? ReadLabel(ZipArchive archive, string table, uint token, CancellationToken cancellationToken)
    {
        ZipArchiveEntry? selected = null;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(entry.FullName, table + ".str", StringComparison.OrdinalIgnoreCase)) continue;
            if (selected is not null || entry.FullName != table + ".str") throw InvalidTable();
            selected = entry;
        }
        if (selected is null) return null;
        if (selected.Length is < 164 or > MaximumTableBytes) throw InvalidTable();
        using var stream = selected.Open();
        var bytes = new byte[(int)selected.Length];
        for (var offset = 0; offset < bytes.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(bytes.AsSpan(offset, Math.Min(4096, bytes.Length - offset)));
            if (count == 0) throw InvalidTable();
            offset += count;
        }
        if (stream.ReadByte() != -1) throw InvalidTable();
        return ResolveTable(bytes, table, token, cancellationToken);
    }

    private static string? ResolveTable(ReadOnlySpan<byte> bytes, string table, uint token, CancellationToken cancellationToken)
    {
        var name = Encoding.ASCII.GetBytes(table);
        if (bytes[0] != 0 || bytes[1] != 8 || !bytes.Slice(2, name.Length).SequenceEqual(name) ||
            bytes.Slice(2 + name.Length, 128 - name.Length).IndexOfAnyExcept((byte)0) >= 0 ||
            U16(bytes, 130) != 2 || U32(bytes, 132) != 140) throw InvalidTable();
        var second = U32(bytes, 136);
        if (second > (uint)bytes.Length - 12) throw InvalidTable();
        var first = ReadSection(bytes, 140, (int)second);
        var keys = ReadSection(bytes, (int)second, bytes.Length);
        if (first.Count != keys.Count) throw InvalidTable();
        var seen = new HashSet<uint>();
        var selectedOffset = -1;
        for (var index = 0; index < first.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = U32(bytes, 152 + index * 8);
            if (!seen.Add(id) || id != U32(bytes, (int)second + 12 + index * 8)) throw InvalidTable();
            var offset = ReadStringOffset(bytes, first, 152 + index * 8 + 4);
            _ = ReadStringOffset(bytes, keys, (int)second + 12 + index * 8 + 4);
            if (id == token) selectedOffset = offset;
        }
        if (selectedOffset < 0) return null;
        var remaining = bytes.Slice(first.PoolStart + selectedOffset, first.PoolLength - selectedOffset);
        var length = remaining.IndexOf((byte)0);
        if (length is < 1 or > 512) throw InvalidTable();
        var label = Utf8.GetString(remaining[..length]);
        if (label.Length is < 1 or > 160 || label != label.Trim() || label.Any(character =>
            char.IsControl(character) || char.GetUnicodeCategory(character) is UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)) throw InvalidTable();
        return label;
    }

    private static Section ReadSection(ReadOnlySpan<byte> bytes, int start, int end)
    {
        if (start < 140 || end - start < 12) throw InvalidTable();
        var size = U32(bytes, start);
        var poolBytes = U32(bytes, start + 4);
        var count = U32(bytes, start + 8);
        if (count is < 1 or > MaximumStrings || poolBytes is < 1 or > MaximumTableBytes || size != count * 8 + poolBytes ||
            size != (uint)(end - start - 12)) throw InvalidTable();
        var pool = start + 12 + (int)count * 8;
        if (bytes[end - 1] != 0) throw InvalidTable();
        _ = Utf8.GetCharCount(bytes.Slice(pool, (int)poolBytes));
        return new Section((int)count, pool, (int)poolBytes);
    }

    private static int ReadStringOffset(ReadOnlySpan<byte> bytes, Section section, int field)
    {
        var offset = U32(bytes, field);
        if (offset >= section.PoolLength || offset > 0 && bytes[section.PoolStart + (int)offset - 1] != 0)
            throw InvalidTable();
        return (int)offset;
    }

    // Bound the directory before ZipArchive allocates its entries. Installed tables use ordinary single-disk ZIP.
    private static void ValidateDirectory(FileStream file, CancellationToken cancellationToken)
    {
        var tail = new byte[(int)Math.Min(file.Length, 65_557)];
        file.Position = file.Length - tail.Length;
        file.ReadExactly(tail);
        for (var offset = tail.Length - 22; offset >= 0; offset--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = tail.AsSpan(offset);
            if (U32(end, 0) != 0x06054B50 || offset + 22 + U16(end, 20) != tail.Length) continue;
            var count = U16(end, 10);
            var directoryBytes = U32(end, 12);
            var directoryStart = U32(end, 16);
            var endOffset = file.Length - tail.Length + offset;
            if (U16(end, 4) != 0 || U16(end, 6) != 0 || U16(end, 8) != count ||
                count is < 1 or > MaximumArchiveEntries || directoryBytes > 256 * 1024 ||
                (long)directoryStart + directoryBytes != endOffset) throw InvalidTable();
            file.Position = directoryStart;
            var directory = new byte[(int)directoryBytes];
            file.ReadExactly(directory);
            var cursor = 0;
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (directory.Length - cursor < 46 || U32(directory, cursor) != 0x02014B50) throw InvalidTable();
                cursor += 46 + U16(directory, cursor + 28) + U16(directory, cursor + 30) + U16(directory, cursor + 32);
                if (cursor > directory.Length) throw InvalidTable();
            }
            if (cursor != directory.Length) throw InvalidTable();
            file.Position = 0;
            return;
        }
        throw InvalidTable();
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static InvalidDataException InvalidTable() => new("The optional car-name table is invalid.");
    private readonly record struct Section(int Count, int PoolStart, int PoolLength);
}
