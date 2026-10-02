using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wisp.Core.Tunes;

namespace Wisp.App;

// Declarative inputs to one fixed reader. This model does not admit a process.
public sealed class NativeTuneCompatibilityLayout
{
    public const string SupportedProfileId = "fh6-tune-imperial-v1";
    public const int MaximumJsonBytes = 32 * 1024;
    public const int MaximumAssetBytes = 17_269_760;

    internal static readonly FrozenDictionary<ulong, (int Width, int Alignment)> RequiredRvaRoles =
        new Dictionary<ulong, (int, int)>
        {
            [0x640EE78] = (8, 8), [0x6446D18] = (8, 8), [0x64BA6E8] = (4, 4),
            [0x64FBE80] = (8, 8), [0x65AC730] = (8, 8), [0x6C79E70] = (8, 8),
            [0x6C7A570] = (8, 8), [0x6C7B3D8] = (8, 8), [0x6C7B9B0] = (8, 8),
            [0x6C7BC10] = (8, 8), [0x8F12A78] = (8, 8), [0x8F75670] = (1024, 4),
            [0x8F75A70] = (256, 1), [0xA7DB9E8] = (8, 8), [0xA861342] = (1, 1),
            [0xA861470] = (128 * 40, 8), [0xA862058] = (8, 8),
            [0xA862060] = (8, 8), [0xA8AF088] = (8, 8)
        }.ToFrozenDictionary();

    // Stable names for reviewed ranges, including partial/chained ranges. These
    // names remain fixed when the signed descriptor relocates a reviewed range.
    private const string SteamRanges = "7D4830 7D6F90 7E5770 8268A0 8F3790 8F37E0 A702F0 A72FF0 A74830 A74870 A74940 A74980 A74BF0 A74CD0 A74CF0 A75AF0 A76E40 A76F70 A782F0 A7A5E0 A7AAE0 A7AB00 A7AB20 A7AB40 1812E20 1812EAA 1812FB6 1813064 1813690 1813FA0 1814670 1816680 1819680 18196E0 1819750 2A9FBD0 2A9FBE0 2A9FC60 2BCEB40 3123480 31AC820 31ACBB0 34E5090 34EAB00 34EAFE0 34EAFED 34ECE40 34F0D30 34F3C50 34F8180 34F8260 34F89C0 34F8A20 34FB0D0 34FB190 34FB1E0 34FB210 34FB240";
    private const string StoreRanges = "4C1890 4C4140 75C530 75EC90 76D470 7AE5A0 87BC00 87BC50 9F86C0 9FB3C0 9FCC00 9FCC40 9FCD10 9FCD50 9FCFC0 9FD0A0 9FD0C0 9FDEC0 9FF210 9FF340 A006C0 A029B0 A02EB0 A02ED0 A02EF0 A02F10 179C280 179C30A 179C416 179C4C4 179CAF0 179D400 179DAD0 179FAE0 17A3100 17A3160 17A31D0 2A10E70 2A16030 2A22BC0 2A22BD0 2A22C50 2A29280 2B25E40 2B4D8B0 30A4750 312DAF0 312DE80 347DF20 3483990 3483E20 3483E70 3483E7D 3485CD0 3489BC0 348CAE0 3491010 34910F0 3491850 34918B0 3493F60 3494020 3494070 34940A0 34940D0";
    private static readonly FrozenSet<string> SteamGuardRoles = Roles(SteamRanges, "steam");
    private static readonly FrozenSet<string> StoreGuardRoles = Roles(StoreRanges, "store");
    internal static IReadOnlySet<string> RequiredGuardRoles(bool store) => store ? StoreGuardRoles : SteamGuardRoles;
    private static FrozenSet<string> Roles(string ranges, string platform) => ranges.Split(' ')
        .Select(value => $"{platform}-range-{value.PadLeft(8, '0')}").ToFrozenSet(StringComparer.Ordinal);

    private NativeTuneCompatibilityLayout(FrozenDictionary<ulong, ulong> rvas, ImmutableArray<CodeGuard> guards,
        FrozenDictionary<ulong, ulong> slots, AssetContract asset, bool store)
    {
        Rvas = rvas;
        CodeGuards = guards;
        ProviderSlots = slots;
        Asset = asset;
        // Length-prefixed BinaryWriter strings and fixed-width numbers, in fixed
        // role order, make JSON whitespace/key order irrelevant to provenance.
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("WISP-TUNE-DESCRIPTOR\0v1\0");
            writer.Write(store);
            writer.Write(SemanticsVersion);
            writer.Write(ProfileId);
            foreach (var (role, rva) in rvas.OrderBy(pair => pair.Key)) { writer.Write(role); writer.Write(rva); }
            foreach (var guard in guards.OrderBy(guard => guard.Role, StringComparer.Ordinal))
            { writer.Write(guard.Role); writer.Write(guard.Rva); writer.Write(guard.Length); writer.Write(guard.Sha256); }
            foreach (var (slot, target) in slots.OrderBy(pair => pair.Key)) { writer.Write(slot); writer.Write(target); }
            writer.Write(asset.MinimumLength); writer.Write(asset.MaximumLength); writer.Write(asset.HeaderPageCount);
            foreach (var (kind, part) in asset.Parts.OrderBy(pair => pair.Key))
            { writer.Write((int)kind); writer.Write(part.Count); writer.Write(part.RowsSha256); }
        }
        Fingerprint = Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }

    public int SemanticsVersion => 1;
    public string ProfileId => SupportedProfileId;
    public string Fingerprint { get; }
    public IReadOnlyDictionary<ulong, ulong> Rvas { get; }
    public IReadOnlyList<CodeGuard> CodeGuards { get; }
    public IReadOnlyDictionary<ulong, ulong> ProviderSlots { get; }
    public AssetContract Asset { get; }
    public sealed record CodeGuard(string Role, ulong Rva, int Length, string Sha256);
    public sealed record PartExpectation(int Count, string RowsSha256);
    public sealed record AssetContract(int MinimumLength, int MaximumLength, uint HeaderPageCount,
        IReadOnlyDictionary<TunePartId, PartExpectation> Parts);

    internal static NativeTuneCompatibilityLayout Parse(JsonElement element, uint imageSize, ulong providerTable, bool store)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(element).Length > MaximumJsonBytes) Fail();
        var value = NativeCompatibilityJson.ReadObject(element, "semanticsVersion", "profileId", "rvas", "codeGuards", "providerSlots", "asset");
        if (NativeCompatibilityJson.ReadInt32(value["semanticsVersion"]) != 1 ||
            NativeCompatibilityJson.ReadString(value["profileId"]) != SupportedProfileId) Fail();
        var names = RequiredRvaRoles.Keys.Select(role => role.ToString("X8", CultureInfo.InvariantCulture)).ToArray();
        var entries = NativeCompatibilityJson.ReadObject(value["rvas"], names);
        var rvas = new Dictionary<ulong, ulong>();
        foreach (var (role, definition) in RequiredRvaRoles)
        {
            var rva = UInt64(entries[role.ToString("X8", CultureInfo.InvariantCulture)]);
            Span(rva, definition.Width, definition.Alignment, imageSize);
            rvas.Add(role, rva);
        }
        if (rvas.Values.Distinct().Count() != rvas.Count) Fail();
        var requiredGuards = RequiredGuardRoles(store);
        var guardElements = value["codeGuards"];
        if (guardElements.ValueKind != JsonValueKind.Array || guardElements.GetArrayLength() != requiredGuards.Count) Fail();
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var guards = ImmutableArray.CreateBuilder<CodeGuard>();
        var total = 0;
        foreach (var entry in guardElements.EnumerateArray())
        {
            var guard = NativeCompatibilityJson.ReadObject(entry, "role", "rva", "length", "sha256");
            var role = NativeCompatibilityJson.ReadString(guard["role"]);
            if (!requiredGuards.Contains(role) || !roles.Add(role)) Fail();
            var rva = UInt64(guard["rva"]);
            var length = NativeCompatibilityJson.ReadInt32(guard["length"]);
            if (length is < 1 or > 8192 || (total += length) > 48 * 1024) Fail();
            Span(rva, length, 1, imageSize);
            if (guards.Any(previous => rva < previous.Rva + (ulong)previous.Length && previous.Rva < rva + (ulong)length)) Fail();
            guards.Add(new(role, rva, length, NativeCompatibilityJson.ReadHash(guard["sha256"])));
        }
        var slotElements = value["providerSlots"];
        if (slotElements.ValueKind != JsonValueKind.Array || slotElements.GetArrayLength() != 2) Fail();
        var slots = new Dictionary<ulong, ulong>();
        foreach (var entry in slotElements.EnumerateArray())
        {
            var slot = NativeCompatibilityJson.ReadObject(entry, "offset", "targetRva");
            var offset = UInt64(slot["offset"]);
            var target = UInt64(slot["targetRva"]);
            if (offset is not (0 or 0x12E8) || !slots.TryAdd(offset, target)) Fail();
            Span(providerTable, checked((int)offset + 8), 8, imageSize);
            Span(target, 1, 1, imageSize);
            var targetRole = store
                ? offset == 0 ? "store-range-030A4750" : "store-range-0312DE80"
                : offset == 0 ? "steam-range-03123480" : "steam-range-031ACBB0";
            if (!guards.Any(guard => guard.Role == targetRole && guard.Rva == target)) Fail();
        }
        var assetValue = NativeCompatibilityJson.ReadObject(value["asset"], "minimumLength", "maximumLength", "headerPageCount", "parts");
        var minimum = NativeCompatibilityJson.ReadInt32(assetValue["minimumLength"]);
        var maximum = NativeCompatibilityJson.ReadInt32(assetValue["maximumLength"]);
        var pages = UInt64(assetValue["headerPageCount"]);
        if (minimum < 1024 || maximum < minimum || maximum > MaximumAssetBytes ||
            minimum % 1024 != 0 || maximum % 1024 != 0 || pages < 1 || pages > (ulong)minimum / 1024) Fail();
        var kinds = Enum.GetValues<TunePartId>();
        var partValues = NativeCompatibilityJson.ReadObject(assetValue["parts"], kinds.Select(kind => kind.ToString()).ToArray());
        var parts = new Dictionary<TunePartId, PartExpectation>();
        var rows = 0;
        foreach (var kind in kinds)
        {
            var part = NativeCompatibilityJson.ReadObject(partValues[kind.ToString()], "count", "rowsSha256");
            var count = NativeCompatibilityJson.ReadInt32(part["count"]);
            if (count is < 1 or > 250_000 || (rows += count) > 250_000) Fail();
            parts.Add(kind, new(count, NativeCompatibilityJson.ReadHash(part["rowsSha256"])));
        }
        return new(rvas.ToFrozenDictionary(), guards.ToImmutable(), slots.ToFrozenDictionary(),
            new(minimum, maximum, checked((uint)pages), parts.ToFrozenDictionary()), store);
    }

    private static ulong UInt64(JsonElement value) => value.TryGetUInt64(out var result)
        ? result : throw new FormatException("A Tune layout integer is invalid.");
    private static void Span(ulong rva, int length, int alignment, uint imageSize)
    {
        if (rva < 4096 || rva >= imageSize || rva % (ulong)alignment != 0 || (ulong)length > imageSize - rva) Fail();
    }
    private static void Fail() => throw new FormatException("The Tune compatibility descriptor is incomplete or outside this reader's bounds.");
}
