using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Wisp.App;

internal delegate bool NativeAdaptiveRead(ulong address, Span<byte> destination);

// A short-lived snapshot owned by the attachment worker. Never used by HUD ticks.
internal sealed class NativeAdaptiveImage
{
    internal const int MaximumBytes = 256 * 1024 * 1024;
    private readonly NativeAdaptiveRead _read;
    private readonly NativeAdaptiveBudget _budget;
    private readonly Dictionary<(uint Rva, int Length), byte[]> _proof = [];
    private int _proofBytes;

    internal NativeAdaptiveImage(NativeAdaptiveRead read, ulong moduleBase, uint imageSize,
        bool includeWritable, NativeAdaptiveBudget budget)
    {
        _read = read;
        _budget = budget;
        ModuleBase = moduleBase;
        ImageSize = imageSize;
        if (!NativeHudProcessMemory.IsValidModuleRange(moduleBase, imageSize) || imageSize > MaximumBytes * 2L)
            throw new InvalidDataException("Adaptive image bounds");
        var header = ReadLive(0, 4096);
        var pe = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3c));
        if (U16(header, 0) != 0x5a4d || pe is < 64 or > 2048 || U32(header, pe) != 0x4550 ||
            U16(header, pe + 4) != 0x8664 || U16(header, pe + 24) != 0x20b || U32(header, pe + 80) != imageSize)
            throw new InvalidDataException("Adaptive image header");
        TimeDateStamp = U32(header, pe + 8);
        var count = U16(header, pe + 6);
        var optional = U16(header, pe + 20);
        var start = pe + 24 + optional;
        if (count is < 1 or > 32 || optional < 112 || start + count * 40 > header.Length)
            throw new InvalidDataException("Adaptive image sections");
        var sections = new List<Section>();
        long allocated = 0;
        for (var i = 0; i < count; i++)
        {
            var offset = start + 40 * i;
            var size = U32(header, offset + 8);
            var name = Encoding.ASCII.GetString(header, offset, 8).TrimEnd('\0');
            var rva = U32(header, offset + 12);
            var flags = U32(header, offset + 36);
            if (size == 0) continue;
            if (rva < 4096 || rva >= imageSize || size > imageSize - rva ||
                sections.Any(s => rva < s.Rva + s.Size && s.Rva < rva + size) ||
                (flags & 0xa0000000) == 0xa0000000)
                throw new InvalidDataException("Adaptive section bounds or permissions");
            var kind = (flags & 0x40000000) == 0 ? "unreadable" :
                (flags & 0x20000000) != 0 ? "code" : (flags & 0x80000000) != 0 ? "data" : "readonly";
            byte[]? bytes = null;
            if (kind == "code" || kind == "readonly" && name is ".rdata" or ".pdata" || kind == "data" && includeWritable)
            {
                // Static data signatures live in initialized image bytes. Do not
                // scan the writable zero-fill tail containing runtime state.
                var scanSize = kind == "data" ? Math.Min(size, U32(header, offset + 16)) : size;
                allocated += scanSize;
                if (allocated > MaximumBytes) throw new InvalidDataException("Adaptive snapshot budget");
                if (scanSize > 0) bytes = ReadLive(rva, checked((int)scanSize));
            }
            sections.Add(new Section(rva, size, kind, bytes, name));
        }
        if (!sections.Any(s => s.Kind == "code") || !sections.Any(s => s.Kind == "readonly"))
            throw new InvalidDataException("Adaptive image has no reader sections");
        Sections = sections;
        Remember(0, header);
    }

    internal ulong ModuleBase { get; }
    internal uint ImageSize { get; }
    internal uint TimeDateStamp { get; }
    internal IReadOnlyList<Section> Sections { get; }
    internal void CheckBudget() => _budget.Check();

    internal string Kind(uint rva, int length = 1)
    {
        if (rva == 0 && length == 1) return "image";
        return FindSection(rva, length)?.Kind ?? "outside";
    }

    internal ReadOnlySpan<byte> Bytes(uint rva, int length, bool proof = false)
    {
        _budget.Check();
        var section = FindSection(rva, length) ?? throw new InvalidDataException("Adaptive read outside section");
        var bytes = section.Bytes is { } data && rva - section.Rva < data.Length && length <= data.Length - (rva - section.Rva)
            ? data.AsSpan(checked((int)(rva - section.Rva)), length)
            : ReadLive(rva, length).AsSpan();
        if (proof) Remember(rva, bytes);
        return bytes;
    }

    internal uint Pointer(uint rva, bool proof = false)
    {
        var pointer = BinaryPrimitives.ReadUInt64LittleEndian(Bytes(rva, 8, proof));
        if (pointer < ModuleBase || pointer - ModuleBase >= ImageSize)
            throw new InvalidDataException("Adaptive pointer outside image");
        return checked((uint)(pointer - ModuleBase));
    }

    internal uint UInt32(uint rva, bool proof = false) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(rva, 4, proof));

    internal void Remember(uint rva, ReadOnlySpan<byte> bytes)
    {
        var key = (rva, bytes.Length);
        if (_proof.ContainsKey(key)) return;
        _proofBytes += bytes.Length;
        if (_proofBytes > 4 * 1024 * 1024) throw new InvalidDataException("Adaptive evidence budget");
        _proof.Add(key, SHA256.HashData(bytes));
    }

    internal NativeAdaptiveProof FinishProof()
    {
        var result = new NativeAdaptiveProof(_proof.Select(p => new NativeAdaptiveProof.Range(p.Key.Rva, p.Key.Length, p.Value)).ToArray());
        // A changing snapshot is not evidence of a permanently unsupported
        // layout. Reject this admission, then allow the bounded transient retry.
        if (!result.Matches(_read, ModuleBase, _budget)) throw new IOException("Adaptive reader changed during validation");
        return result;
    }

    internal HashSet<(uint Rva, int Length)> SaveProof() => _proof.Keys.ToHashSet();

    internal void RestoreProof(HashSet<(uint Rva, int Length)> checkpoint)
    {
        foreach (var key in _proof.Keys.Where(key => !checkpoint.Contains(key)).ToArray())
        {
            _proof.Remove(key);
            _proofBytes -= key.Length;
        }
    }

    private Section? FindSection(uint rva, int length) => length > 0 ? Sections.FirstOrDefault(s =>
        rva >= s.Rva && rva - s.Rva < s.Size && (uint)length <= s.Size - (rva - s.Rva) && s.Kind != "unreadable") : null;

    private byte[] ReadLive(uint rva, int length)
    {
        if (rva >= ImageSize || length < 1 || length > ImageSize - rva)
            throw new InvalidDataException("Adaptive live read bounds");
        var result = new byte[length];
        for (var offset = 0; offset < length; offset += 64 * 1024)
        {
            _budget.Check();
            if (!_read(ModuleBase + rva + (ulong)offset, result.AsSpan(offset, Math.Min(64 * 1024, length - offset))))
                throw new IOException("Adaptive reader could not read the image");
        }
        return result;
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    internal bool IsUnwindEntry(uint target)
    {
        var section = Sections.SingleOrDefault(s => s.Name == ".pdata");
        var table = section?.Bytes;
        if (table is null || section is null) return false;
        for (var offset = 0; offset + 12 <= table.Length; offset += 12)
        {
            if (U32(table, offset) != target) continue;
            Remember(checked(section.Rva + (uint)offset), table.AsSpan(offset, 12));
            var end = U32(table, offset + 4);
            return end > target && end - target <= 1024 * 1024 && Kind(target, checked((int)(end - target))) == "code";
        }
        return false;
    }

    internal sealed record Section(uint Rva, uint Size, string Kind, byte[]? Bytes, string Name);
}

internal sealed class NativeAdaptiveBudget(TimeSpan limit, CancellationToken cancellationToken = default)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    internal void Check()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_clock.Elapsed > limit) throw new TimeoutException("Adaptive compatibility validation timed out");
    }
}

internal sealed class NativeAdaptiveProof(NativeAdaptiveProof.Range[] ranges)
{
    internal bool Matches(NativeAdaptiveRead read, ulong moduleBase, NativeAdaptiveBudget budget)
    {
        Span<byte> bytes = stackalloc byte[8192];
        foreach (var range in ranges)
        {
            budget.Check();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var offset = 0; offset < range.Length; offset += bytes.Length)
            {
                var block = bytes[..Math.Min(bytes.Length, range.Length - offset)];
                if (!read(moduleBase + range.Rva + (ulong)offset, block)) return false;
                hash.AppendData(block);
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), range.Hash)) return false;
        }
        budget.Check();
        return true;
    }
    internal sealed record Range(uint Rva, int Length, byte[] Hash);
}
