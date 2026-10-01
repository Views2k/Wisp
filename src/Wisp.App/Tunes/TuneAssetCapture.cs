using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace Wisp.App.Tunes;

internal static class TuneAssetCapture
{
    internal const int ExpectedLength = 16221184;
    internal const int MaximumLength = ExpectedLength + 1024 * 1024;
    // Retained fixture identities; live acceptance uses exact required table projections.
    internal const string EncodedHash = "8A529AAFC28DFC39EC18230E75297D56CF219F4E0A64EEE2C1134E8526BCC369";
    internal const string DecodedHash = "E0E5979B99ED4BEABA8405634E0484CF1C46BB51FEC4F1E8F22109535242F9C6";
    internal const string CrcHash = "12F3E0576D447EB37B36D82BA0C1C5481B8F0D12FDC70347CE4A076B229D4C86";
    internal const string FoldHash = "00C700F38385659BA060672F86D4A9A5376EADF9ED1CABB1C63290A0FDEFE36A";

    // The stream includes mutable database state. Only validated integer projections
    // survive extraction, once per process session; no database is shipped or written to disk.
    internal static byte[] ReadDecoded(INativeHudProcessMemory memory, CancellationToken cancellationToken)
    {
        if (!NativeTuneCapture.Supports(memory.CompatibilityPack)) Fail();
        var read = new NativeTuneRead(memory, cancellationToken, 20 * 1024 * 1024, TimeSpan.FromSeconds(5));
        var module = memory.ModuleBase;
        var owner = read.Pointer(module + 0xA8AF088);
        var wrapper = read.Pointer(owner + 0x160);
        ExpectTable(read, wrapper, module + 0x6C7A570);
        var connection = read.Pointer(wrapper + 0x10);
        Require(read.UInt32(connection + 0x48) is 0x4B771290 or 0xA029A697 or 0xF03B7906);
        Require(read.Int32(connection + 8) is >= 1 and <= 4);
        var entries = read.Pointer(connection + 0x10);
        var btree = read.Pointer(entries + 8);
        var shared = read.Pointer(btree + 8);
        var pager = read.Pointer(shared);
        var file = read.Pointer(pager + 0x40);
        ExpectTable(read, file, module + 0x6C79E70);
        var stream = read.Pointer(read.Pointer(file + 8));
        ExpectTable(read, stream, module + 0x6C7B3D8);
        var adapter = read.Pointer(stream + 0x30);
        ExpectTable(read, adapter, module + 0x6C7BC10);
        var inner = read.Pointer(adapter + 0x30);
        ExpectTable(read, inner, module + 0x6C7B9B0);
        var chunkSize = read.UInt32(inner + 0x30);
        var allocated = read.UInt32(inner + 0x34);
        var length = read.UInt64(inner + 0x40);
        var begin = read.Pointer(inner + 0x48);
        var end = read.Pointer(inner + 0x50);
        var flag = read.Bytes(inner + 0x60, 1)[0];
        // Both pointers have already passed the user-address bounds check.
        ValidateStreamShape(flag, chunkSize, allocated, length, (long)end - (long)begin);
        var count = checked((int)((end - begin) / 8));
        Require((ulong)count * chunkSize >= length);
        var chunks = new ulong[count];
        for (var i = 0; i < count; i++) chunks[i] = read.Pointer(begin + (ulong)i * 8);
        var crc = read.Bytes(module + 0x8F75670, 1024);
        var fold = read.Bytes(module + 0x8F75A70, 256);
        var encoded = new byte[checked((int)length)];
        try
        {
            var filled = 0;
            foreach (var chunk in chunks)
            {
                var amount = Math.Min(checked((int)chunkSize), encoded.Length - filled);
                for (var offset = 0; offset < amount; offset += 4096)
                {
                    var block = read.Bytes(chunk + (ulong)offset, Math.Min(4096, amount - offset), remember: false);
                    try { block.CopyTo(encoded, filled + offset); }
                    finally { CryptographicOperations.ZeroMemory(block); }
                }
                filled += amount;
                if (filled == encoded.Length) break;
            }
            read.VerifyStable();
            return Decode(encoded, crc, fold, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(crc);
            CryptographicOperations.ZeroMemory(fold);
        }
    }

    internal static void ValidateStreamShape(byte flag, uint chunkSize, uint allocated, ulong length, long vectorBytes)
    {
        if (flag != 0 || length < ExpectedLength || length > MaximumLength || length % 1024 != 0 ||
            chunkSize is < 1024 or > 64 * 1024 * 1024 || allocated < length || allocated > 64 * 1024 * 1024 ||
            vectorBytes is < 8 or > 512 * 1024 || vectorBytes % 8 != 0)
            throw new TuneAssetStreamValidationException(flag, chunkSize, allocated, length, vectorBytes);
    }

    internal static byte[] Decode(byte[] encoded, byte[] crcBytes, byte[] fold, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(encoded.Length is >= ExpectedLength and <= MaximumLength && encoded.Length % 1024 == 0 &&
            crcBytes.Length == 1024 && fold.Length == 256 &&
            Convert.ToHexString(SHA256.HashData(crcBytes)) == CrcHash &&
            Convert.ToHexString(SHA256.HashData(fold)) == FoldHash);
        var crc = new uint[256];
        for (var i = 0; i < crc.Length; i++) crc[i] = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes.AsSpan(i * 4));
        var keyBytes = BitConverter.GetBytes(0x9DFBA741U);
        for (var i = 0; i < 4; i++) keyBytes[i] = unchecked((byte)((keyBytes[i] ^ 0x35) - (i ^ 0xBC)));
        var key = BinaryPrimitives.ReadUInt32LittleEndian(keyBytes);
        var decoded = new byte[encoded.Length];
        try
        {
            for (var index = 0; index < encoded.Length / 4; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var value = unchecked(((uint)index + 1) * key + (uint)index);
                var mask = uint.MaxValue;
                for (var shift = 0; shift < 32; shift += 8)
                    mask = (mask >> 8) ^ crc[(fold[(value >> shift) & 255] ^ mask) & 255];
                var result = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(index * 4)) ^ ~mask;
                BinaryPrimitives.WriteUInt32LittleEndian(decoded.AsSpan(index * 4), result);
            }
            ValidateHeader(decoded);
            return decoded; // Not accepted metadata until TuneAssetSqlite verifies every projection.
        }
        catch { CryptographicOperations.ZeroMemory(decoded); throw; }
        finally { Array.Clear(crc); CryptographicOperations.ZeroMemory(keyBytes); }
    }

    internal static void ValidateHeader(byte[] decoded)
    {
        Require(decoded.Length is >= ExpectedLength and <= MaximumLength && decoded.Length % 1024 == 0 &&
            decoded.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8) &&
            BinaryPrimitives.ReadUInt16BigEndian(decoded.AsSpan(16)) == 1024 &&
            decoded[18] == 1 && decoded[19] == 1 && decoded[20] == 0 &&
            decoded[21] == 64 && decoded[22] == 32 && decoded[23] == 32 &&
            BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(28)) == 15409 &&
            BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(44)) == 4 &&
            BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(56)) == 1);
    }

    private static void ExpectTable(NativeTuneRead read, ulong owner, ulong expected) => Require(read.UInt64(owner) == expected);
    private static void Require(bool condition) { if (!condition) Fail(); }
    private static void Fail() => throw new InvalidDataException("The game's tuning metadata could not be verified.");
}

internal sealed class TuneAssetStreamValidationException(byte flag, uint chunkSize, uint allocated, ulong length,
    long vectorBytes) : IOException("The tuning asset stream shape does not match the supported layout.")
{
    internal byte Flag { get; } = flag;
    internal uint ChunkSize { get; } = chunkSize;
    internal uint Allocated { get; } = allocated;
    internal ulong Length { get; } = length;
    internal long VectorBytes { get; } = vectorBytes;
}
