using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wisp.App;

// Store executables cannot be opened for a file hash. This contract combines
// OS package provenance with exact, release-owned guards on the reader's code.
public sealed class NativeHudStoreBuildIdentity
{
    private readonly CodeGuard[] _guards;

    private NativeHudStoreBuildIdentity(string packageFullName, uint timestamp, uint imageSize, CodeGuard[] guards)
    {
        PackageFullName = packageFullName;
        TimeDateStamp = timestamp;
        ImageSize = imageSize;
        _guards = guards;
    }

    public string PackageFullName { get; }
    public uint TimeDateStamp { get; }
    public uint ImageSize { get; }

    internal static NativeHudStoreBuildIdentity Parse(JsonElement element, string gameVersion, uint imageSize)
    {
        var fields = NativeCompatibilityJson.ReadObject(element, "packageFullName", "timeDateStamp", "codeGuards");
        var name = NativeCompatibilityJson.ReadString(fields["packageFullName"]);
        if (name != $"Microsoft.ForteBaseGame_{gameVersion}_x64__8wekyb3d8bbwe" ||
            !fields["timeDateStamp"].TryGetUInt32(out var timestamp) || timestamp == 0 ||
            fields["codeGuards"].ValueKind != JsonValueKind.Array || fields["codeGuards"].GetArrayLength() is < 2 or > 8)
        {
            throw new FormatException("The Store build identity is invalid.");
        }

        var guards = new List<CodeGuard>();
        foreach (var item in fields["codeGuards"].EnumerateArray())
        {
            var guard = NativeCompatibilityJson.ReadObject(item, "rva", "length", "sha256");
            if (!guard["rva"].TryGetUInt32(out var rva) || !guard["length"].TryGetInt32(out var length) ||
                length is < 16 or > 256 || rva < 4096 || rva >= imageSize || length > imageSize - rva ||
                guards.Any(existing => rva < existing.Rva + existing.Length && existing.Rva < rva + length))
            {
                throw new FormatException("A Store code guard is outside its bounds or overlaps another guard.");
            }

            guards.Add(new CodeGuard(rva, length, Convert.FromHexString(NativeCompatibilityJson.ReadHash(guard["sha256"]))));
        }

        return new NativeHudStoreBuildIdentity(name, timestamp, imageSize, guards.ToArray());
    }

    internal bool MatchesImage(IReadOnlyProcessMemory memory, ulong moduleBase)
    {
        if (!NativeHudProcessMemory.IsValidModuleRange(moduleBase, ImageSize))
        {
            return false;
        }

        Span<byte> header = stackalloc byte[4096];
        if (!memory.TryReadBytes(moduleBase, header) || BinaryPrimitives.ReadUInt16LittleEndian(header) != 0x5A4D)
        {
            return false;
        }

        var pe = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        if (pe is < 64 or > 2048 || BinaryPrimitives.ReadUInt32LittleEndian(header[pe..]) != 0x4550 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 4)..]) != 0x8664 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[(pe + 8)..]) != TimeDateStamp ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 24)..]) != 0x20B ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[(pe + 80)..]) != ImageSize)
        {
            return false;
        }

        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 6)..]);
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 20)..]);
        var sections = pe + 24 + optionalSize;
        if (sectionCount is < 1 or > 32 || optionalSize < 112 || sections + sectionCount * 40 > header.Length)
        {
            return false;
        }

        Span<byte> code = stackalloc byte[256];
        Span<byte> hash = stackalloc byte[32];
        foreach (var guard in _guards)
        {
            var executableSection = false;
            for (var index = 0; index < sectionCount; index++)
            {
                var section = header[(sections + index * 40)..];
                var size = BinaryPrimitives.ReadUInt32LittleEndian(section[8..]);
                var rva = BinaryPrimitives.ReadUInt32LittleEndian(section[12..]);
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(section[36..]);
                if (rva >= 4096 && rva < ImageSize && size <= ImageSize - rva &&
                    guard.Rva >= rva && guard.Rva - rva <= size && guard.Length <= size - (guard.Rva - rva) &&
                    (flags & 0x20000000) != 0 && (flags & 0x80000000) == 0)
                {
                    executableSection = true;
                    break;
                }
            }

            if (!executableSection || !memory.TryReadBytes(moduleBase + guard.Rva, code[..guard.Length]))
            {
                return false;
            }

            SHA256.HashData(code[..guard.Length], hash);
            if (!CryptographicOperations.FixedTimeEquals(hash, guard.Hash))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record CodeGuard(uint Rva, int Length, byte[] Hash);
}
