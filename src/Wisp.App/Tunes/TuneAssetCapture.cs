using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace Wisp.App.Tunes;

internal static class TuneAssetCapture
{
    internal const int ExpectedLength = 16221184;
    internal const string EncodedHash = "8A529AAFC28DFC39EC18230E75297D56CF219F4E0A64EEE2C1134E8526BCC369";
    internal const string DecodedHash = "E0E5979B99ED4BEABA8405634E0484CF1C46BB51FEC4F1E8F22109535242F9C6";

    // This static game asset is read once per supported build per Wisp lifetime.
    // Only the required integer metadata survives SQLite extraction; no database is shipped.
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
        Require(read.Bytes(inner + 0x60, 1)[0] == 0 && length == ExpectedLength &&
            chunkSize is >= 1024 and <= 64 * 1024 * 1024 && allocated >= length && allocated <= 64 * 1024 * 1024 &&
            end >= begin && (end - begin) % 8 == 0 && end - begin is >= 8 and <= 512 * 1024);
        var count = checked((int)((end - begin) / 8));
        Require((ulong)count * chunkSize >= length);
        var chunks = new ulong[count];
        for (var i = 0; i < count; i++) chunks[i] = read.Pointer(begin + (ulong)i * 8);
        var crc = read.Bytes(module + 0x8F75670, 1024);
        var fold = read.Bytes(module + 0x8F75A70, 256);
        var encoded = new byte[ExpectedLength];
        var filled = 0;
        foreach (var chunk in chunks)
        {
            var amount = Math.Min(checked((int)chunkSize), ExpectedLength - filled);
            for (var offset = 0; offset < amount; offset += 4096)
            {
                var block = read.Bytes(chunk + (ulong)offset, Math.Min(4096, amount - offset), remember: false);
                block.CopyTo(encoded, filled + offset);
            }
            filled += amount;
            if (filled == ExpectedLength) break;
        }
        read.VerifyStable();
        if (!Convert.ToHexString(SHA256.HashData(encoded)).Equals(EncodedHash, StringComparison.Ordinal)) Fail();
        return Decode(encoded, crc, fold, cancellationToken);
    }

    internal static byte[] Decode(byte[] encoded, byte[] crcBytes, byte[] fold, CancellationToken cancellationToken)
    {
        Require(encoded.Length == ExpectedLength && crcBytes.Length == 1024 && fold.Length == 256 &&
            Convert.ToHexString(SHA256.HashData(encoded)) == EncodedHash);
        var crc = new uint[256];
        for (var i = 0; i < crc.Length; i++) crc[i] = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes.AsSpan(i * 4));
        var keyBytes = BitConverter.GetBytes(0x9DFBA741U);
        for (var i = 0; i < 4; i++) keyBytes[i] = unchecked((byte)((keyBytes[i] ^ 0x35) - (i ^ 0xBC)));
        var key = BinaryPrimitives.ReadUInt32LittleEndian(keyBytes);
        var decoded = new byte[encoded.Length];
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
        Require(Convert.ToHexString(SHA256.HashData(decoded)) == DecodedHash &&
            decoded.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8) &&
            BinaryPrimitives.ReadUInt16BigEndian(decoded.AsSpan(16)) == 1024 &&
            BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(28)) == 15409);
        return decoded;
    }

    private static void ExpectTable(NativeTuneRead read, ulong owner, ulong expected) => Require(read.UInt64(owner) == expected);
    private static void Require(bool condition) { if (!condition) Fail(); }
    private static void Fail() => throw new InvalidDataException("The game's tuning metadata could not be verified.");
}
