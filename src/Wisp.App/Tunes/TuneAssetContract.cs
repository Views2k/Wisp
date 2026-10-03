using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Wisp.Core.Tunes;

namespace Wisp.App.Tunes;

internal static class TuneAssetContract
{
    // Derived from the exact-hash retained asset for TuneDecoder.SupportedExecutableSha256.
    // The 12 projections were independently equal in the live mutable stream on 2026-10-01.
    private sealed record Expected(int Count, string SchemaHash, string RowsHash);
    private static readonly ImmutableDictionary<TunePartId, Expected> Required = new Dictionary<TunePartId, Expected>
    {
        [TunePartId.Engine] = new(3017, "E6314E049A1BE4C8898EBCE703F0B31F288AA39F1C47B6032B410D484DEC0E0A", "79D1BFE7DA2780931D6137D743CB19FF6F0DC8E443E4681ACCF1B13FC02CD226"),
        [TunePartId.Drivetrain] = new(1375, "3CD97CEF1504E6B8E04DC4448BE2AD0263E7AA4489C441D3A865AB48D238FF13", "EC51BA165ECCA66BB98F524689F38D1A18FCB22FD4F9A48DFF6B3B2C15DCE9F9"),
        [TunePartId.CarBody] = new(790, "BEEB89638EAB54A01FD3166DC0FFAF60C8A0D1119522722595A186D09CCF27CA", "07657E94C9EE2C9CE2C36B9B0F81744E253001BFAFE94EDDF39D9C20DC6E22DB"),
        [TunePartId.Motor] = new(33, "A9919156A559A2EA0A68201FE104816E6FC79CD53E0D6F79028687D4E6EE1874", "B3E3B5F6E081DFE302E19C83995D69DD0A049F12612D816D63A15E7527C67731"),
        [TunePartId.Brakes] = new(1689, "886032B1355B0B44E501717FD0881ABF19DB1D92AAC346B975FC8B5413559FB0", "8EF8E68C52274C79F4787333AE2EF350EDFA046F985EDDA98F708E8F86321ACE"),
        [TunePartId.SpringDamper] = new(2752, "8F6E0ED5C83C9B8797ABCE81C23B48858DBA9A78DB0E24A817F5BDC22CB8BC83", "0B1466548BAC66BE2CA2B7BD64DABB4A42C18F869A180F5A0038D9A85AEBB869"),
        [TunePartId.FrontAntiroll] = new(1541, "E3B6F37D5B8D76DCC6BC90CF1E0052F111B3791105B804D34F06E6BA11D38DBA", "C66C582A721C2F63AC1A41998F6A8819488B0E400FD6E17F35B6A9C55E0617FC"),
        [TunePartId.RearAntiroll] = new(1578, "69FF2025387C187351DDC28A1ECD0D411090420582B0179B8C9433F691F27513", "001B3E9D30A1EB5FD2495E433ECC838E93412C1A62263E90304C25BC9C2C5EA3"),
        [TunePartId.RearAero] = new(1669, "6DEA2276DA4D9DA295CFF2E08CC3A25DBCD0BA4FE9153DC1D3EAF5A9A70BB89F", "F1AA81C45C123632A7CA41E2ACC43992B5A12A6601D46CB0DF5A3F9903C1C497"),
        [TunePartId.Transmission] = new(5069, "F098800665475BBCB86BE9904B0394E7181E2653EB4AEF46D3AF4AC355D40402", "7B4C56864B70E571E86AD14BF3CA546C8B774B44F0335BAC20A62314A3D2117A"),
        [TunePartId.Differential] = new(3491, "A6D6CA2C698CAC25A6B0A899C9AE05AB9CA65B0394C898D58D54BF440BE6B452", "B4FC1799AB299D9F8BE360C688E122C924EE381AE7A0960E0623ACF493B24B1F"),
        [TunePartId.FrontAero] = new(1761, "1354EB2F52D41DD4CEC441E3B3D50F39DEADDEF8127D321472F31227D19A3B0D", "A32536BA8A277A74710AA18D9CBB6EE5E71DFFB86C596CCCE6620587FB6EB002")
    }.ToImmutableDictionary();

    internal static void ValidateSchema(TunePartId kind, string schema)
    {
        if (!Required.TryGetValue(kind, out var expected) ||
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schema))) != expected.SchemaHash)
            throw new InvalidDataException("The required tuning table schema does not match the supported build.");
    }

    internal static void ValidateRows(TunePartId kind, IReadOnlyCollection<TuneAssetRow> rows,
        NativeTuneCompatibilityLayout.AssetContract? asset = null)
    {
        if (!Required.TryGetValue(kind, out var expected))
            throw new InvalidDataException("The required tuning values do not match the supported build.");
        var count = expected.Count;
        if (asset is not null)
        {
            if (!asset.Parts.TryGetValue(kind, out var described))
                throw new InvalidDataException("The required tuning projection is missing.");
            count = described.Count;
        }
        // Live databases may add or change rows. Reject implausibly incomplete projections;
        // SQLite still verifies each table, and the selected car must resolve every part.
        if (count <= 0 || rows.Count < ((long)count + 1) / 2 || rows.Count > 250000)
            throw new InvalidDataException("The required tuning projection has an invalid row count.");
        var identities = new HashSet<(int Parent, int Id)>();
        foreach (var row in rows)
            if (row.Kind != kind || !identities.Add((row.Parent, row.Id)))
                throw new InvalidDataException("The required tuning values contain an invalid or duplicate identity.");
    }

    internal static string HashRows(TunePartId kind, IReadOnlyCollection<TuneAssetRow> rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("WISP-TUNE-PARTS\0v1\0"u8);
        Span<byte> bytes = stackalloc byte[18];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)kind);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], rows.Count);
        hash.AppendData(bytes[..8]);
        (int Parent, int Id)? previous = null;
        foreach (var row in rows.OrderBy(row => row.Parent).ThenBy(row => row.Id))
        {
            if (row.Kind != kind || previous is { } identity && identity == (row.Parent, row.Id))
                throw new InvalidDataException("The required tuning values contain an invalid or duplicate identity.");
            previous = (row.Parent, row.Id);
            BinaryPrimitives.WriteInt32LittleEndian(bytes, row.Parent);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], row.Id);
            bytes[8] = row.Level.HasValue ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(bytes[9..], row.Level ?? 0);
            bytes[13] = row.ChildParent.HasValue ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(bytes[14..], row.ChildParent ?? 0);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
