using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Wisp.Core.Tunes;

namespace Wisp.App.Tunes;

internal static class NativeTunePerformance
{
    internal static TunePerformance? TryRead(INativeHudProcessMemory memory, ulong actor,
        CancellationToken cancellationToken, out string? setupKey)
    {
        setupKey = null;
        var profile = NativeTunePerformanceProfiles.Resolve(memory.CompatibilityPack);
        if (profile is null || memory.CompatibilityPack.GameplayVisibility is not { } ui) return null;
        try
        {
            NativeTuneLayout.VerifyGuards(memory, profile.Guards, cancellationToken);
            var setupRead = new NativeTuneRead(memory, cancellationToken, 4096, TimeSpan.FromMilliseconds(250));
            var setup = ReadSetup(setupRead, actor, memory.CompatibilityPack.Fields.SourceCarOrdinal);
            if (setup is null) return null;
            TunePerformance? result = null;
            try { result = ReadPage(); }
            catch (Exception error) when (error is IOException or InvalidDataException or OverflowException) { }
            setupRead.VerifyStable();
            setupKey = setup.Key;
            return result;

            TunePerformance? ReadPage()
            {
                var read = new NativeTuneRead(memory, cancellationToken, 32 * 1024, TimeSpan.FromMilliseconds(250));
                var module = memory.ModuleBase;
                void Table(ulong address, ulong rva)
                {
                    if (read.UInt64(address) != module + rva) throw new InvalidDataException();
                }
                var service = read.Pointer(module + ui.UiServiceRva);
                Table(service, ui.UiServiceVtableRva);
                var dependency = read.Pointer(service + ui.ServiceDependencyOffset);
                Table(dependency, ui.DependencyVtableRva);
                var rootManager = dependency + ui.RootTransitionManagerOffset;
                Table(rootManager, ui.TransitionManagerVtableRva);
                var root = read.Pointer(rootManager + ui.ManagerCurrentPageOffset);
                Table(root, profile.RootPageVtable);
                var manager = read.Pointer(root + ui.PageTransitionManagerOffset);
                Table(manager, ui.TransitionManagerVtableRva);
                var page = read.Pointer(manager + ui.ManagerCurrentPageOffset);
                Table(page, profile.TunePageVtable);
                var benchmark = read.Pointer(page + 0x4B8);
                Table(benchmark, profile.BenchmarkVtable);
                return ReadResults(read, benchmark, setup);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OverflowException)
        {
            // Optional benchmark availability must not interrupt the current tune.
            return null;
        }
    }

    // The live descriptor has a version word followed by 100 installed part IDs.
    private const int PartBytes = sizeof(int) * 101;
    private const int BenchmarkPartBytes = 0x188;
    private const int SettingBytes = 0xB8;
    internal sealed record Setup(int Ordinal, byte[] Payload)
    {
        internal string Key => Convert.ToHexString(SHA256.HashData(Payload));
    }

    internal static Setup? ReadSetup(NativeTuneRead read, ulong actor, ulong ordinalOffset)
    {
        var ordinal = read.Int32(actor + ordinalOffset);
        var descriptor = read.Pointer(actor + 0x2E0);
        if (ordinal <= 0 || read.Int32(descriptor) != ordinal) return null;
        return new(ordinal, [.. read.Bytes(descriptor + 0xC, PartBytes), .. read.Bytes(actor + 0x71F4, SettingBytes)]);
    }

    internal static TunePerformance? ReadResults(NativeTuneRead read, ulong benchmark, Setup setup)
    {
        if (read.Int32(benchmark + 0xB10) != 13 || read.Bytes(benchmark + 0xB14, 1)[0] != 1 ||
            read.Int32(benchmark + 0x90C) != setup.Ordinal)
            return null;
        if (!setup.Payload.AsSpan(0, BenchmarkPartBytes).SequenceEqual(read.Bytes(benchmark + 0x134, BenchmarkPartBytes)) ||
            !setup.Payload.AsSpan(PartBytes).SequenceEqual(read.Bytes(benchmark + 0x2C8, SettingBytes)))
            return null;
        float? Result(ulong offset)
        {
            var pair = read.Bytes(benchmark + offset, 8);
            var state = BinaryPrimitives.ReadUInt32LittleEndian(pair.AsSpan(4));
            if (state < 2) throw new InvalidDataException();
            if (state > 2) return null;
            var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(pair));
            if (!float.IsFinite(value)) throw new InvalidDataException();
            return value;
        }
        var result = new TunePerformance(Result(0x954), Result(0x944), Result(0x96C), Result(0x974),
            Result(0x914), Result(0x924), Result(0x93C), Result(0xAD8), Result(0xAD0), Result(0xAC8));
        if (!result.IsValid) return null;
        read.VerifyStable();
        return result;
    }
}
