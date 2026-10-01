using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using Wisp.Core.Tunes;

namespace Wisp.App.Tunes;

internal static class NativeTuneCapture
{
    internal const string SupportedHash = "FEC4A63CDEAD26F6528564F0E33C3D7FD02CD887337ED6E1AEACA79EB2843FCD";
    internal static bool Supports(NativeHudCompatibilityPack pack) =>
        pack.GameVersion == "6.440.853.0" && pack.ExecutableLength == 184055768 &&
        pack.ImageSize == 188497920 && string.Equals(pack.ExecutableSha256, SupportedHash, StringComparison.OrdinalIgnoreCase);

    internal static TuneDecodeInput Read(INativeHudProcessMemory memory, TuneAssetMetadata metadata,
        CancellationToken cancellationToken)
    {
        var pack = memory.CompatibilityPack;
        if (!Supports(pack)) throw new InvalidDataException("Tune reading is unavailable for this game build.");
        var read = new NativeTuneRead(memory, cancellationToken);
        var module = memory.ModuleBase;
        var fields = pack.Fields;
        var sourceVector = read.Bytes(module + pack.SourceVectorRva, 24);
        var begin = BinaryPrimitives.ReadUInt64LittleEndian(sourceVector);
        var end = BinaryPrimitives.ReadUInt64LittleEndian(sourceVector.AsSpan(8));
        var capacity = BinaryPrimitives.ReadUInt64LittleEndian(sourceVector.AsSpan(16));
        Require(begin <= end && end <= capacity && end - begin is > 0 and <= 1024 &&
            begin % 8 == 0 && end % 8 == 0 && capacity % 8 == 0 &&
            NativeHudProcessMemory.IsValidReadSpan(capacity, 8));
        var sources = read.Bytes(begin, checked((int)(end - begin)));
        var candidates = new List<(ulong Actor, ulong Provider)>();
        for (var index = 0; index < sources.Length; index += 8)
        {
            var actor = BinaryPrimitives.ReadUInt64LittleEndian(sources.AsSpan(index));
            Require(NativeHudProcessMemory.IsValidReadSpan(actor, 0x8000));
            var provider = read.UInt64(actor + fields.SourceProvider);
            if (!NativeHudProcessMemory.IsValidReadSpan(provider, 0xC000)) continue;
            var table = read.UInt64(provider);
            if (table != module + pack.LeadVtableRva) continue;
            var valid = true;
            foreach (var slot in pack.RequiredVtableSlots)
                valid &= read.UInt64(table + slot.Key) == module + slot.Value;
            if (valid && read.Bytes(provider + fields.LocalPlayerFlag, 1)[0] == 1 &&
                read.Bytes(provider + fields.LocalPlayerProviderFlag, 1)[0] == 1)
                candidates.Add((actor, provider));
        }
        Require(candidates.Count == 1);
        var (source, selected) = candidates[0];
        var ordinal = read.Int32(source + fields.SourceCarOrdinal);
        Require(ordinal > 0);
        var drive = read.Int32(selected + 0xB9C);
        Require(drive is >= 0 and <= 2);
        var gearCount = read.Int32(selected + 0x24);
        Require(gearCount is >= 2 and <= 11);
        var ratios = read.Bytes(selected + 0x28, gearCount * 20);
        for (var i = 0; i < gearCount; i++)
        {
            var ratio = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(ratios.AsSpan(i * 20)));
            Require(float.IsFinite(ratio) && Math.Abs(ratio) < 30 && (i == 0 ? ratio < 0 : ratio > 0));
        }
        Require(read.Single(selected + 0xB68) is > 0 and < 30);
        var wheels = new HashSet<ulong>();
        var wheelIds = new HashSet<uint>();
        foreach (var offset in new[] { fields.FirstWheelPointer, fields.SecondWheelPointer,
                     fields.ThirdWheelPointer, fields.ThirdWheelPointer + 8 })
        {
            var wheel = read.Pointer(selected + offset);
            var id = read.UInt32(wheel + fields.WheelId);
            Require(id < 4 && wheels.Add(wheel) && wheelIds.Add(id));
        }

        var descriptor = read.Pointer(source + 0x2E0);
        Require(read.Int32(descriptor) == ordinal);
        var otherDescriptor = read.Pointer(source + 0x2D8);
        var copies = new[] { source + 0x71F4, otherDescriptor + 0x1A0, descriptor + 0x1A0 }
            .Select(address => Words(read.Bytes(address, 0xB8))).ToImmutableArray();

        var global = read.Pointer(module + 0xA7DB9E8);
        TuneRange Range(ulong owner, ulong low, ulong high) => new(read.Single(owner + low), read.Single(owner + high));
        var bounds = new TuneGlobalBounds(Range(global, 0x43C, 0x440), Range(global, 0x444, 0x448),
            Range(global, 0x47C, 0x480), Range(global, 0x484, 0x488),
            Range(global, 0x48C, 0x490), Range(global, 0x494, 0x498));
        var attributes = source + 0x310;
        var carRanges = ImmutableDictionary.CreateBuilder<TuneFieldId, TuneRange>();
        foreach (var (id, low, high) in CarRanges)
            carRanges.Add(id, Range(attributes, low, high));
        Require(selected >= 0x570);
        var scale = read.Single(selected - 0x570 + 8);
        Require(scale is > 0 and < 10000000);

        var locale = read.Pointer(module + 0xA862060);
        var preference = read.Int32(locale + 0x34);
        if (preference == -1)
        {
            var profileIndex = read.Int32(locale + 0x28);
            Require(profileIndex is >= 0 and < 8);
            preference = read.Int32(locale + (ulong)profileIndex * 0x94 + 0x9C);
        }
        Require(preference is >= 0 and < 7);
        var units = read.Pointer(module + 0xA862058);
        Require(read.Bytes(module + 0xA861342, 1)[0] == 1);
        var conversions = ImmutableDictionary.CreateBuilder<TuneQuantity, TuneConversion>();
        foreach (var (quantity, category) in Quantities)
        {
            var unitOverride = read.Int32(units + 0x14D0 + category * 4);
            ulong entry;
            if (unitOverride != -1)
            {
                Require(unitOverride is >= 0 and < 256);
                entry = read.Pointer(units + 0x1568) + (ulong)unitOverride * 20;
            }
            else entry = units + ((ulong)preference * 0x26 + category) * 20 + 8;
            var unitId = read.Int32(entry + 4);
            Require(unitId is >= 0 and < 128);
            var conversion = module + 0xA861470 + (ulong)unitId * 40;
            Require(read.Int32(conversion) == unitId);
            var factor = read.Double(conversion + 0x10);
            var callback = read.UInt64(conversion + 0x18);
            Require(callback == 0 || callback >= module && callback < module + pack.ImageSize);
            conversions.Add(quantity, new TuneConversion(unitId, factor, callback != 0));
        }
        var format = new TuneFormatConstants(read.Single(module + 0x64BA6E8), read.Double(module + 0x6446D18),
            read.Double(module + 0x65AC730), read.Double(module + 0x64FBE80), read.Double(module + 0x640EE78));
        var indices = read.Pointer(module + 0x8F12A78);
        var installed = ImmutableArray.CreateBuilder<TunePart>();
        foreach (var (kind, category) in PartCategories)
        {
            var index = read.Int32(indices + category * 4);
            Require(index is >= 0 and < 100);
            installed.Add(new TunePart(kind, read.Int32(descriptor + 0x10 + (ulong)index * 4), null));
        }
        var resolved = metadata.Resolve(ordinal, installed.ToImmutable());
        Require(resolved.All(part => part.Level.HasValue));
        read.VerifyStable();
        return new TuneDecodeInput
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            GameVersion = pack.GameVersion,
            ExecutableSha256 = pack.ExecutableSha256,
            ExecutableVerified = true,
            CaptureComplete = true,
            Coherent = true,
            LocalProviderCount = 1,
            CarOrdinal = ordinal,
            Drivetrain = (TuneDrivetrain)drive,
            ObservedGearEntryCount = gearCount,
            UnitPreference = preference,
            NormalizedCopies = copies,
            Bounds = bounds,
            Conversions = conversions.ToImmutable(),
            Format = format,
            CarRanges = carRanges.ToImmutable(),
            SpringScale = scale,
            PartLevelsResolved = resolved.All(part => part.Level.HasValue),
            Parts = resolved
        };
    }

    private static ImmutableArray<uint> Words(byte[] bytes)
    {
        var words = ImmutableArray.CreateBuilder<uint>(46);
        for (var i = 0; i < bytes.Length; i += 4)
            words.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)));
        return words.MoveToImmutable();
    }
    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidDataException("The current car's tune is not available yet.");
    }
    private static readonly (TuneFieldId Id, ulong Low, ulong High)[] CarRanges =
    [
        (TuneFieldId.FrontDownforce, 0x3A4, 0x3AC), (TuneFieldId.RearDownforce, 0x404, 0x40C),
        (TuneFieldId.FrontRideHeight, 0x534, 0x538), (TuneFieldId.RearRideHeight, 0x688, 0x68C),
        (TuneFieldId.FrontSprings, 0x554, 0x558), (TuneFieldId.RearSprings, 0x6A8, 0x6AC),
        (TuneFieldId.FrontBump, 0x560, 0x564), (TuneFieldId.RearBump, 0x6B4, 0x6B8),
        (TuneFieldId.FrontRebound, 0x584, 0x588), (TuneFieldId.RearRebound, 0x6D8, 0x6DC),
        (TuneFieldId.FrontAntiroll, 0x5F0, 0x5F4), (TuneFieldId.RearAntiroll, 0x744, 0x748)
    ];
    private static readonly (TuneQuantity Quantity, ulong Category)[] Quantities =
    [
        (TuneQuantity.Number, 0), (TuneQuantity.Percentage, 1), (TuneQuantity.RideHeight, 8),
        (TuneQuantity.Pressure, 0x14), (TuneQuantity.Angle, 0x16),
        (TuneQuantity.SpringRate, 0x1B), (TuneQuantity.Downforce, 0x1E)
    ];
    private static readonly (TunePartId Kind, ulong Category)[] PartCategories =
    [
        (TunePartId.Engine, 0), (TunePartId.Drivetrain, 1), (TunePartId.CarBody, 2), (TunePartId.Motor, 3),
        (TunePartId.Brakes, 4), (TunePartId.SpringDamper, 5), (TunePartId.FrontAntiroll, 6),
        (TunePartId.RearAntiroll, 7), (TunePartId.RearAero, 9), (TunePartId.Transmission, 0x1F),
        (TunePartId.Differential, 0x21), (TunePartId.FrontAero, 0x22)
    ];
}
