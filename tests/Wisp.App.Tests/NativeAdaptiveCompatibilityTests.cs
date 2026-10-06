using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeAdaptiveCompatibilityTests
{
    [Theory]
    [InlineData("steam", "BuiltIn")]
    [InlineData("store", "Store")]
    public void EmbeddedProfilesMatchTheirReferenceAndDeclareIndependentCapabilities(string platform, string referenceName)
    {
        var assembly = typeof(NativeHudBuildContract).Assembly;
        using var resource = assembly.GetManifestResourceStream($"Wisp.NativeCompatibility.Adaptive.{platform}.json");
        Assert.NotNull(resource);
        Assert.InRange(resource.Length, 1, 8 * 1024 * 1024);
        using var profile = JsonDocument.Parse(resource);
        using var reference = assembly.GetManifestResourceStream($"Wisp.NativeCompatibility.{referenceName}.json")!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(reference)), profile.RootElement.GetProperty("referencePackSha256").GetString());
        Assert.Equal(platform, profile.RootElement.GetProperty("platform").GetString());
        _ = new NativeAdaptiveResolver(new Fixture().Image(), profile.RootElement, ["core"]);
        var capabilities = profile.RootElement.GetProperty("capabilities");
        var corePaths = capabilities.GetProperty("core").GetProperty("requiredPaths").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.DoesNotContain(corePaths, p => p!.StartsWith("/tune/", StringComparison.Ordinal) || p.StartsWith("/nativeGauge/", StringComparison.Ordinal));
        Assert.Equal(platform == "store", capabilities.GetProperty("gauge").GetProperty("fieldWitnessComplete").GetBoolean());
        if (platform == "store")
        {
            Assert.DoesNotContain("/storeIdentity/codeGuards/2/rva", corePaths);
            Assert.Contains("/storeIdentity/codeGuards/2/rva", capabilities.GetProperty("gauge").GetProperty("requiredPaths").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal(18, profile.RootElement.GetProperty("fieldWitnesses").GetArrayLength());
        }
    }

    [Fact]
    public void RelocatedCodeAndDataResolveWithoutAGameVersionAllowlist()
    {
        var fixture = new Fixture();
        var resolved = fixture.Resolve();
        Assert.Equal(0x3580U, resolved["/thresholdRva"]);
        Assert.Equal(0x1120U, resolved["/leadVtableRva"]);
    }

    [Theory]
    [InlineData(2)] // Member displacement.
    [InlineData(0)] // Instruction opcode.
    [InlineData(13)] // Return instruction.
    public void AChangedInstructionOrMemberOffsetIsRejected(int offset)
    {
        var fixture = new Fixture();
        fixture.Bytes[0x1120 + offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => fixture.Resolve());
    }

    [Fact]
    public void DuplicateMatchingFunctionsAreAmbiguous()
    {
        var fixture = new Fixture();
        fixture.Bytes.AsSpan(0x1120, 14).CopyTo(fixture.Bytes.AsSpan(0x1220));
        fixture.Write32(0x1220 + 9, 0x3580 - (0x1220 + 13));
        Assert.Throws<InvalidDataException>(() => fixture.Resolve());
    }

    [Fact]
    public void AReferenceToTheWrongSectionIsRejected()
    {
        var fixture = new Fixture();
        fixture.Write32(0x1120 + 9, 0x5180 - (0x1120 + 13));
        Assert.Throws<InvalidDataException>(() => fixture.Resolve());
    }

    [Fact]
    public void GuardedBytesAreRecheckedAfterDiscovery()
    {
        var fixture = new Fixture();
        var image = fixture.Image();
        new NativeAdaptiveResolver(image, fixture.Profile.RootElement).Resolve();
        var proof = image.FinishProof();
        fixture.Bytes[0x1122] ^= 1;
        Assert.False(proof.Matches(fixture.Read, Fixture.Module, new NativeAdaptiveBudget(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void AChangingAdmissionSnapshotIsRejectedAsTransient()
    {
        var fixture = new Fixture();
        var image = fixture.Image();
        new NativeAdaptiveResolver(image, fixture.Profile.RootElement).Resolve();
        fixture.Bytes[0x1122] ^= 1;
        Assert.Throws<IOException>(() => image.FinishProof());
        fixture.Bytes[0x1122] ^= 1;
        Assert.True(image.FinishProof().Matches(fixture.Read, Fixture.Module,
            new NativeAdaptiveBudget(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void CancellationDuringTheFinalProofReadCannotAdmitTheReader()
    {
        using var cancellation = new CancellationTokenSource();
        var bytes = new byte[] { 1, 2, 3, 4 };
        bool Read(ulong address, Span<byte> destination)
        {
            bytes.CopyTo(destination);
            cancellation.Cancel();
            return true;
        }
        var proof = new NativeAdaptiveProof([new(0x1000, bytes.Length, SHA256.HashData(bytes))]);
        Assert.Throws<OperationCanceledException>(() => proof.Matches(Read, Fixture.Module,
            new NativeAdaptiveBudget(TimeSpan.FromSeconds(5), cancellation.Token)));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void ReferencesIntoAnotherGuardMustRetainTheirInteriorOffset(int targetOffset, bool valid)
    {
        var fixture = new Fixture();
        var second = Convert.FromHexString("488B8124000000C3");
        second.CopyTo(fixture.Bytes, 0x2120);
        fixture.Write32(0x1120 + 9, 0x2120 + targetOffset - (0x1120 + 13));
        var profile = JsonNode.Parse(fixture.Profile.RootElement.GetRawText())!;
        profile["signatures"]![0]!["relocations"]![0]!["expectedTargetSection"] = "code";
        profile["signatures"]![0]!["relocations"]![0]!["targetRva"] = 0x2103;
        profile["signatures"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
        {
            id = "second",
            referenceRva = 0x2100,
            length = second.Length,
            normalizedSha256 = Convert.ToHexString(SHA256.HashData(second)),
            anchorOffset = 0,
            anchorHex = Convert.ToHexString(second),
            section = "code",
            relocations = Array.Empty<object>()
        }));
        profile["bindings"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { path = "/secondaryRva", kind = "signature", signature = "second" }));
        using var document = JsonDocument.Parse(profile.ToJsonString());
        var resolver = new NativeAdaptiveResolver(fixture.Image(), document.RootElement);
        if (valid) Assert.Equal(0x2120U, resolver.Resolve()["/secondaryRva"]);
        else Assert.Throws<InvalidDataException>(() => resolver.Resolve());
    }

    [Fact]
    public void WritableExecutableSectionsAndPartialReadsAreRejected()
    {
        var fixture = new Fixture();
        fixture.Write32(0x188 + 36, unchecked((int)0xe0000020));
        Assert.Throws<InvalidDataException>(() => fixture.Image());
        fixture = new Fixture { RejectReads = true };
        Assert.Throws<IOException>(() => fixture.Image());
    }

    [Fact]
    public void CancellationAndTimeLimitStopDiscovery()
    {
        var fixture = new Fixture();
        Assert.Throws<OperationCanceledException>(() => new NativeAdaptiveImage(fixture.Read,
            Fixture.Module, (uint)fixture.Bytes.Length, false, new NativeAdaptiveBudget(TimeSpan.FromSeconds(5), new CancellationToken(true))));
        Assert.Throws<TimeoutException>(() => new NativeAdaptiveImage(fixture.Read,
            Fixture.Module, (uint)fixture.Bytes.Length, false, new NativeAdaptiveBudget(TimeSpan.Zero)));
    }

    [Fact]
    public void ExternalJsonCannotRequestRuntimeOwnedGaugeIdentity()
    {
        using var source = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.BuiltIn.json")!;
        var json = JsonNode.Parse(source)!.AsObject();
        json["nativeGauge"]!["hudTypeTokenRva"] = 0;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(json);
        Assert.Throws<FormatException>(() => NativeHudCompatibilityPack.Parse(bytes));
        Assert.True(NativeHudCompatibilityPack.FromRuntimeValidation(json).IsRuntimeValidated);
    }

    private sealed class Fixture
    {
        internal const ulong Module = 0x140000000;
        internal byte[] Bytes { get; } = new byte[0x8000];
        internal bool RejectReads { get; init; }
        internal JsonDocument Profile { get; }

        internal Fixture()
        {
            Write16(0, 0x5a4d); Write32(0x3c, 0x80); Write32(0x80, 0x4550);
            Write16(0x84, 0x8664); Write16(0x86, 3); Write16(0x94, 0xf0);
            Write16(0x98, 0x20b); Write32(0xd0, Bytes.Length);
            Section(0, ".text", 0x1000, 0x2000, 0x60000020);
            Section(1, ".rdata", 0x3000, 0x2000, 0x40000040);
            Section(2, ".data", 0x5000, 0x2000, 0xc0000040);
            var body = Convert.FromHexString("8B8120000000488D1500000000C3");
            body.CopyTo(Bytes, 0x1120);
            Write32(0x1120 + 9, 0x3580 - (0x1120 + 13));
            var signature = new
            {
                id = "reader",
                referenceRva = 0x1100,
                length = body.Length,
                normalizedSha256 = Convert.ToHexString(SHA256.HashData(body)),
                anchorOffset = 0,
                anchorHex = "8B8120000000488D15",
                section = "code",
                relocations = new[] { new { offset = 9, width = 4, next = 13, kind = "relative", targetRva = 0x3500, expectedTargetSection = "readonly" } }
            };
            Profile = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                signatures = new[] { signature },
                bindings = new object[] {
                    new { path = "/thresholdRva", kind = "reference", signature = "reader", operandOffset = 9 },
                    new { path = "/leadVtableRva", kind = "signature", signature = "reader" }
                },
                checks = new object[] { new { path = "/leadVtableRva", kind = "bytes", signature = "reader" } }
            }));
        }

        internal bool Read(ulong address, Span<byte> destination)
        {
            if (RejectReads || address < Module || address - Module >= (ulong)Bytes.Length ||
                (ulong)destination.Length > (ulong)Bytes.Length - (address - Module)) return false;
            Bytes.AsSpan((int)(address - Module), destination.Length).CopyTo(destination);
            return true;
        }
        internal NativeAdaptiveImage Image() => new(Read, Module, (uint)Bytes.Length, false, new NativeAdaptiveBudget(TimeSpan.FromSeconds(5)));
        internal IReadOnlyDictionary<string, uint> Resolve() => new NativeAdaptiveResolver(Image(), Profile.RootElement).Resolve();
        internal void Write32(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(offset), value);
        private void Write16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(offset), value);
        private void Section(int index, string name, int rva, int size, uint flags)
        {
            var offset = 0x188 + 40 * index;
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(Bytes, offset);
            Write32(offset + 8, size); Write32(offset + 12, rva); Write32(offset + 36, unchecked((int)flags));
        }
    }
}
