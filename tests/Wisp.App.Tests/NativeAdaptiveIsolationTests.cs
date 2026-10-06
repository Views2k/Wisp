using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeAdaptiveIsolationTests
{
    [Fact]
    public void VerifiedTuneCanBeAddedWithoutChangingResolvedCoreRoles()
    {
        using var fixture = new Fixture();
        var resolver = fixture.Resolver();
        var core = resolver.Resolve().ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.True(resolver.TryEnableCapability("tune", out _));
        var combined = resolver.Resolve();

        foreach (var pair in core) Assert.Equal(pair.Value, combined[pair.Key]);
        Assert.Equal(Fixture.TuneDescriptor, combined[Fixture.TunePath]);
        Assert.True(resolver.TryEnableCapability("tune", out _));
    }

    [Theory]
    [InlineData(0)] // Opcode.
    [InlineData(2)] // Member displacement.
    public void ChangedTuneOnlyTableAccessorLeavesCoreAvailableButRejectsTune(int changedByte)
    {
        using var fixture = new Fixture();
        fixture.Bytes[Fixture.TuneGetter + changedByte] ^= 1;
        var resolver = fixture.Resolver();

        Assert.Equal(Fixture.Table, resolver.Resolve()[Fixture.TablePath]);
        Assert.False(resolver.TryEnableCapability("tune", out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.False(resolver.HasTransientCapabilityFailure);
        Assert.DoesNotContain(Fixture.TunePath, resolver.Resolve().Keys);
        Assert.Equal(Fixture.CoreGetter, resolver.Resolve()[Fixture.CorePath]);

        // The Tune getter has no binding/check of its own: requiredBy on the
        // provider table is what must reject this changed optional accessor.
        var requiredTune = fixture.Resolver(["core", "tune"]);
        Assert.Throws<InvalidDataException>(() => requiredTune.Resolve());
    }

    [Fact]
    public void ASlotSharedWithCoreIsStillRequiredWhenTuneIsDisabled()
    {
        using var fixture = new Fixture(tuneSlotAlsoRequiredByCore: true);
        fixture.Bytes[Fixture.TuneGetter + 2] ^= 1;

        Assert.Throws<InvalidDataException>(() => fixture.Resolver().Resolve());
    }

    [Fact]
    public void FailedOptionalReaderRollsBackItsEvidenceButKeepsCoreEvidence()
    {
        using var fixture = new Fixture();
        fixture.Bytes[Fixture.TuneGetter + 2] ^= 1;
        var image = fixture.Image();
        var resolver = new NativeAdaptiveResolver(image, fixture.Profile.RootElement, ["core"]);
        resolver.Resolve();

        Assert.False(resolver.TryEnableCapability("tune", out _));
        Assert.DoesNotContain(Fixture.TunePath, resolver.Resolve().Keys);

        // This successfully matched before the later provider-slot failure.
        // Failed Tune admission must not leave it in the core proof.
        fixture.Bytes[Fixture.TuneDescriptor + 2] ^= 1;
        fixture.WritePointer(Fixture.Table + 8, Fixture.Module + Fixture.TuneDescriptor);
        var proof = image.FinishProof();
        Assert.True(proof.Matches(fixture.Read, Fixture.Module, fixture.Budget()));

        fixture.Bytes[Fixture.CoreGetter + 2] ^= 1;
        Assert.False(proof.Matches(fixture.Read, Fixture.Module, fixture.Budget()));
    }

    [Fact]
    public void FailedTuneDoesNotPreventAnotherOptionalReaderFromBeingVerified()
    {
        using var fixture = new Fixture();
        fixture.Bytes[Fixture.TuneGetter + 2] ^= 1;
        var resolver = fixture.Resolver();
        resolver.Resolve();

        Assert.False(resolver.TryEnableCapability("tune", out _));
        Assert.True(resolver.TryEnableCapability("gauge", out _));

        var resolved = resolver.Resolve();
        Assert.DoesNotContain(Fixture.TunePath, resolved.Keys);
        Assert.Equal(Fixture.GaugeGetter, resolved[Fixture.GaugePath]);
        Assert.Equal(Fixture.CoreGetter, resolved[Fixture.CorePath]);
    }

    [Fact]
    public void TemporarilyUnreadableOptionalGuardKeepsCoreAndRequestsRetry()
    {
        using var fixture = new Fixture(tuneGuardInUncachedSection: true);
        var resolver = fixture.Resolver();
        resolver.Resolve();
        fixture.RejectTuneDescriptorRead = true;

        Assert.False(resolver.TryEnableCapability("tune", out _));
        Assert.True(resolver.HasTransientCapabilityFailure);
        Assert.Equal(Fixture.CoreGetter, resolver.Resolve()[Fixture.CorePath]);
        Assert.DoesNotContain(Fixture.TunePath, resolver.Resolve().Keys);

        fixture.RejectTuneDescriptorRead = false;
        Assert.True(resolver.TryEnableCapability("tune", out _));
        Assert.Equal(0x7000U, resolver.Resolve()[Fixture.TunePath]);
    }

    [Fact]
    public void PreCancelledFactoryDoesNotInspectFilesOrAttachMemory()
    {
        var files = new CountingFingerprintFiles();
        var catalog = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn,
            null, new Dictionary<string, byte[]>());
        var admissionCalls = 0;
        var factory = new NativeHudProcessMemoryFactory(catalog, new NativeHudFingerprintCache(files), memory =>
        {
            admissionCalls++;
            return memory;
        });
        INativeHudProcessMemory? opened = null;
        var status = NativeAssistProviderStatus.Ready;

        Assert.Throws<OperationCanceledException>(() =>
            factory.TryOpen(new CancellationToken(true), out opened, out status));

        Assert.Null(opened);
        Assert.Equal(NativeAssistProviderStatus.GameNotRunning, status);
        Assert.Equal(0, files.Calls);
        Assert.Equal(0, admissionCalls);
    }

    private sealed class CountingFingerprintFiles : INativeHudFingerprintFileSystem
    {
        internal int Calls { get; private set; }
        public NativeHudFileMetadata ReadMetadata(string path)
        {
            Calls++;
            throw new IOException("No file inspection was expected.");
        }
        public string ComputeSha256(string path)
        {
            Calls++;
            throw new IOException("No file inspection was expected.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal const ulong Module = 0x140000000;
        internal const uint Table = 0x3300;
        internal const uint CoreGetter = 0x1220;
        internal const uint TuneGetter = 0x1420;
        internal const uint GaugeGetter = 0x1520;
        internal const uint TuneDescriptor = 0x1820;
        internal const string TablePath = "/leadVtableRva";
        internal const string CorePath = "/requiredVtableSlots/0/targetRva";
        internal const string TunePath = "/tune/codeGuards/0/rva";
        internal const string GaugePath = "/nativeGauge/requiredProviderVtableSlots/0/targetRva";
        internal byte[] Bytes { get; } = new byte[0x8000];
        internal JsonDocument Profile { get; }
        internal bool RejectTuneDescriptorRead { get; set; }
        private readonly uint _actualTuneDescriptor;

        internal Fixture(bool tuneSlotAlsoRequiredByCore = false, bool tuneGuardInUncachedSection = false)
        {
            _actualTuneDescriptor = tuneGuardInUncachedSection ? 0x7000U : TuneDescriptor;
            Write16(0, 0x5a4d); Write32(0x3c, 0x80); Write32(0x80, 0x4550);
            Write16(0x84, 0x8664); Write16(0x86, tuneGuardInUncachedSection ? (ushort)4 : (ushort)3); Write16(0x94, 0xf0);
            Write16(0x98, 0x20b); Write32(0xd0, Bytes.Length);
            Section(0, ".text", 0x1000, 0x2000, 0x60000020);
            Section(1, ".rdata", 0x3000, 0x2000, 0x40000040);
            Section(2, ".data", 0x5000, 0x2000, 0xc0000040);
            if (tuneGuardInUncachedSection) Section(3, ".extra", 0x7000, 0x1000, 0x40000040);
            var reference = Convert.FromHexString("55488D0500000000C3");
            reference.CopyTo(Bytes, 0x1100);
            Write32(0x1104, (int)Table - 0x1108);
            WritePointer(Table, Module + CoreGetter);
            WritePointer(Table + 8, Module + TuneGetter);
            WritePointer(Table + 16, Module + GaugeGetter);
            WritePointer(Table + 24, Module + _actualTuneDescriptor);
            var signatures = new List<object>
            {
                new
                {
                    id = "table-reference", referenceRva = 0x1000, length = reference.Length,
                    normalizedSha256 = Convert.ToHexString(SHA256.HashData(reference)),
                    anchorOffset = 0, anchorHex = "55488D05", section = "code",
                    relocations = new[] { new { offset = 4, width = 4, next = 8, kind = "relative",
                        targetRva = 0x3100, expectedTargetSection = "readonly" } }
                },
                Getter("core", 0x1200, CoreGetter, "8B8120000000C3"),
                Getter("tune", 0x1400, TuneGetter, "8B8124000000C3"),
                Getter("gauge", 0x1500, GaugeGetter, "8B8128000000C3"),
                Getter("tune-descriptor", 0x1800, _actualTuneDescriptor, "8B8130000000C3", tuneGuardInUncachedSection ? "readonly" : "code")
            };
            Profile = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                signatures,
                capabilities = new
                {
                    core = new { requiredPaths = new[] { TablePath, CorePath }, fieldWitnessComplete = true },
                    tune = new { requiredPaths = new[] { TunePath }, fieldWitnessComplete = true },
                    gauge = new { requiredPaths = new[] { GaugePath }, fieldWitnessComplete = true }
                },
                bindings = new object[]
                {
                    new { path = TablePath, kind = "reference", signature = "table-reference", operandOffset = 4 },
                    new
                    {
                        path = TablePath, kind = "table", anchors = Array.Empty<int>(), scalarSlots = Array.Empty<object>(),
                        slots = new[]
                        {
                            new { offset = 0, signature = "core", required = true, requiredBy = new[] { CorePath } },
                            new { offset = 8, signature = "tune", required = true,
                                requiredBy = tuneSlotAlsoRequiredByCore ? new[] { TunePath, CorePath } : new[] { TunePath } },
                            new { offset = 16, signature = "gauge", required = true, requiredBy = new[] { GaugePath } }
                        }
                    },
                    new { path = CorePath, kind = "slot", tablePath = TablePath, slotOffset = 0, signature = "core" },
                    tuneGuardInUncachedSection
                        ? (object)new { path = TunePath, kind = "slot", tablePath = TablePath, slotOffset = 24, signature = "tune-descriptor" }
                        : new { path = TunePath, kind = "signature", signature = "tune-descriptor" },
                    new { path = GaugePath, kind = "slot", tablePath = TablePath, slotOffset = 16, signature = "gauge" }
                },
                checks = new[]
                {
                    new { path = CorePath, kind = "bytes", signature = "core" },
                    new { path = TunePath, kind = "bytes", signature = "tune-descriptor" },
                    new { path = GaugePath, kind = "bytes", signature = "gauge" }
                }
            }));
        }

        private object Getter(string id, uint referenceRva, uint actualRva, string hex, string section = "code")
        {
            var bytes = Convert.FromHexString(hex);
            bytes.CopyTo(Bytes, (int)actualRva);
            return new
            {
                id,
                referenceRva,
                length = bytes.Length,
                normalizedSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                anchorOffset = 0,
                anchorHex = hex,
                section,
                relocations = Array.Empty<object>()
            };
        }

        internal bool Read(ulong address, Span<byte> destination)
        {
            if (RejectTuneDescriptorRead && address == Module + _actualTuneDescriptor) return false;
            if (address < Module || address - Module >= (ulong)Bytes.Length ||
                (ulong)destination.Length > (ulong)Bytes.Length - (address - Module)) return false;
            Bytes.AsSpan((int)(address - Module), destination.Length).CopyTo(destination);
            return true;
        }

        internal NativeAdaptiveBudget Budget() => new(TimeSpan.FromSeconds(5));
        internal NativeAdaptiveImage Image() => new(Read, Module, (uint)Bytes.Length, false, Budget());
        internal NativeAdaptiveResolver Resolver(string[]? capabilities = null) =>
            new(Image(), Profile.RootElement, capabilities ?? ["core"]);
        internal void WritePointer(uint offset, ulong value) =>
            BinaryPrimitives.WriteUInt64LittleEndian(Bytes.AsSpan((int)offset), value);
        private void Write32(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(offset), value);
        private void Write16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(offset), value);
        private void Section(int index, string name, int rva, int size, uint flags)
        {
            var offset = 0x188 + 40 * index;
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(Bytes, offset);
            Write32(offset + 8, size); Write32(offset + 12, rva); Write32(offset + 36, unchecked((int)flags));
        }
        public void Dispose() => Profile.Dispose();
    }
}
