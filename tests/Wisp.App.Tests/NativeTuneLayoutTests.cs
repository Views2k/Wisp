using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Wisp.App;
using Wisp.App.Tunes;
using Wisp.Core.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeTuneLayoutTests
{
    [Fact]
    public void CompleteCodeRangeIsCheckedInBoundedReads()
    {
        using var memory = new Memory(5001);
        NativeTuneLayout.VerifyGuards(memory, [memory.Guard], TestContext.Current.CancellationToken);
        Assert.Equal(4096 + 5001, memory.ReadBytes);
        Assert.Equal(4096, memory.LargestRead);
        memory.Code[^1] ^= 1;
        Assert.Equal(TuneLayoutFailure.CodeGuardHash, Assert.Throws<TuneLayoutException>(() =>
            NativeTuneLayout.VerifyGuards(memory, [memory.Guard], TestContext.Current.CancellationToken)).Failure);
    }

    [Theory]
    [InlineData(0xC0000000U)]
    [InlineData(0x40000000U)]
    public void WritableOrNonExecutableSectionStopsBeforeCodeRead(uint flags)
    {
        using var memory = new Memory(8);
        BinaryPrimitives.WriteUInt32LittleEndian(memory.Header.AsSpan(0x188 + 36), flags);
        Assert.Equal(TuneLayoutFailure.CodeGuardSection, Assert.Throws<TuneLayoutException>(() =>
            NativeTuneLayout.VerifyGuards(memory, [memory.Guard], TestContext.Current.CancellationToken)).Failure);
        Assert.Equal(4096, memory.ReadBytes);
    }

    [Fact]
    public void OverlappingExecutableSectionsAreAmbiguous()
    {
        using var memory = new Memory(8);
        BinaryPrimitives.WriteUInt16LittleEndian(memory.Header.AsSpan(0x86), 2);
        memory.Header.AsSpan(0x188, 40).CopyTo(memory.Header.AsSpan(0x188 + 40));
        Assert.Throws<TuneLayoutException>(() => NativeTuneLayout.VerifyGuards(memory, [memory.Guard], TestContext.Current.CancellationToken));
        Assert.Equal(4096, memory.ReadBytes);
    }

    [Fact]
    public void CancellationAndExcessiveManifestStopBeforeMemory()
    {
        using var memory = new Memory(8);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativeTuneLayout.VerifyGuards(memory, [memory.Guard], cancelled.Token));
        Assert.Throws<TuneLayoutException>(() => NativeTuneLayout.VerifyGuards(memory,
            Enumerable.Repeat(memory.Guard with { Length = 8192 }, 7).ToArray(), TestContext.Current.CancellationToken));
        Assert.Equal(0, memory.ReadBytes);
    }

    [Fact]
    public void DeclaredLayoutSupportsANewerAdmittedIdentityOnlyWhileAllGuardsAndPackIdentityMatch()
    {
        var pack = DescriptorPack();
        using var memory = new Memory(8) { Pack = pack, UseDescriptor = true };
        BinaryPrimitives.WriteUInt32LittleEndian(memory.Header.AsSpan(0x188 + 8), pack.ImageSize - 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(memory.Header.AsSpan(0x188 + 12), 4096);
        var layout = NativeTuneLayout.Resolve(memory, TestContext.Current.CancellationToken);
        Assert.Equal(pack.Tune!.Fingerprint, layout.Digest);
        Assert.Equal(pack.Tune.Rvas[0xA8AF088], layout.Rva(0xA8AF088));
        var provenance = layout.Provenance(pack);
        Assert.Equal(TuneVerificationMethod.AuthenticatedCompatibilityDescriptor, provenance.Method);
        Assert.Equal(pack.Fingerprint, provenance.CompatibilityPackSha256);
        Assert.True(TuneVerificationProfiles.IsSupported(pack.GameVersion, pack.ExecutableSha256, provenance));
        memory.ChangedCode = true;
        Assert.Equal(TuneLayoutFailure.CodeGuardHash, Assert.Throws<TuneLayoutException>(() =>
            layout.Verify(memory, TestContext.Current.CancellationToken)).Failure);
        memory.ChangedCode = false;
        memory.Pack = DescriptorPack(revision: pack.Revision + 1);
        var reads = memory.ReadBytes;
        Assert.Equal(pack.Tune.Fingerprint, memory.Pack.Tune!.Fingerprint);
        Assert.Equal(TuneLayoutFailure.DescriptorChanged, Assert.Throws<TuneLayoutException>(() =>
            layout.Verify(memory, TestContext.Current.CancellationToken)).Failure);
        Assert.Equal(reads, memory.ReadBytes);
    }

    [Fact]
    public void NewIdentityWithoutADescriptorDoesNotFallBackToOldAddresses()
    {
        using var memory = new Memory(8) { Pack = DescriptorPack(includeDescriptor: false) };
        Assert.Equal(TuneLayoutFailure.UnsupportedIdentity, Assert.Throws<TuneLayoutException>(() =>
            NativeTuneLayout.Resolve(memory, TestContext.Current.CancellationToken)).Failure);
        Assert.Equal(0, memory.ReadBytes);
    }

    private static NativeHudCompatibilityPack DescriptorPack(bool includeDescriptor = true, int revision = 100)
    {
        using var source = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.BuiltIn.json");
        using var reader = new StreamReader(source!);
        var value = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        value["gameVersion"] = "6.441.1.0";
        value["executableSha256"] = new string('A', 64);
        value["revision"] = revision;
        foreach (var guard in value["tune"]!["codeGuards"]!.AsArray())
            guard!["sha256"] = Convert.ToHexString(SHA256.HashData(new byte[guard["length"]!.GetValue<int>()]));
        if (!includeDescriptor) value["tune"] = null;
        return NativeHudCompatibilityPack.Parse(Encoding.UTF8.GetBytes(value.ToJsonString()));
    }

    private sealed class Memory : INativeHudProcessMemory
    {
        internal byte[] Header { get; } = new byte[4096];
        internal byte[] Code { get; }
        internal NativeTuneLayout.CodeGuard Guard { get; }
        internal int ReadBytes { get; private set; }
        internal int LargestRead { get; private set; }
        internal NativeHudCompatibilityPack Pack { get; set; } = NativeHudBuildContract.BuiltIn;
        internal bool UseDescriptor { get; init; }
        internal bool ChangedCode { get; set; }
        public NativeHudCompatibilityPack CompatibilityPack => Pack;
        public ulong ModuleBase => 0x140000000;

        internal Memory(int length)
        {
            Code = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
            Guard = new(0x2000, length, Convert.ToHexString(SHA256.HashData(Code)));
            BinaryPrimitives.WriteUInt16LittleEndian(Header, 0x5A4D);
            BinaryPrimitives.WriteInt32LittleEndian(Header.AsSpan(0x3C), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(0x80), 0x4550);
            BinaryPrimitives.WriteUInt16LittleEndian(Header.AsSpan(0x84), 0x8664);
            BinaryPrimitives.WriteUInt16LittleEndian(Header.AsSpan(0x86), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(Header.AsSpan(0x94), 0xF0);
            BinaryPrimitives.WriteUInt16LittleEndian(Header.AsSpan(0x98), 0x20B);
            BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(0xD0), NativeHudBuildContract.BuiltIn.ImageSize);
            BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(0x188 + 8), 0x10000);
            BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(0x188 + 12), 0x2000);
            BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(0x188 + 36), 0x60000000);
        }

        public bool TryReadBytes(ulong address, Span<byte> destination)
        {
            ReadBytes += destination.Length;
            LargestRead = Math.Max(LargestRead, destination.Length);
            if (address == ModuleBase && destination.Length == Header.Length) Header.CopyTo(destination);
            else if (UseDescriptor && Pack.Tune is { } descriptor)
            {
                foreach (var (slot, target) in descriptor.ProviderSlots)
                    if (address == ModuleBase + Pack.LeadVtableRva + slot && destination.Length == 8)
                    { BinaryPrimitives.WriteUInt64LittleEndian(destination, ModuleBase + target); return true; }
                var requestedLength = destination.Length;
                if (!descriptor.CodeGuards.Any(guard => address >= ModuleBase + guard.Rva &&
                    address - ModuleBase - guard.Rva + (ulong)requestedLength <= (ulong)guard.Length)) return false;
                destination.Clear();
                if (ChangedCode) destination[^1] = 1;
            }
            else if (address >= ModuleBase + 0x2000 && address - ModuleBase - 0x2000 + (ulong)destination.Length <= (ulong)Code.Length)
                Code.AsSpan((int)(address - ModuleBase - 0x2000), destination.Length).CopyTo(destination);
            else return false;
            return true;
        }
        public bool TryReadUInt32(ulong address, out uint value) { value = 0; return false; }
        public bool TryReadByte(ulong address, out byte value) { value = 0; return false; }
        public bool TryReadUInt64(ulong address, out ulong value) { value = 0; return false; }
        public bool TryReadSingle(ulong address, out float value) { value = 0; return false; }
        public void Dispose() { }
    }
}
