using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeHudStoreBuildIdentityTests
{
    private const string StoreVersion = "3.430.771.0";
    private const string KnownPackageFullName = "Microsoft.ForteBaseGame_3.430.771.0_x64__8wekyb3d8bbwe";
    private const uint ImageSize = 0x5000;
    private const uint Timestamp = 1787153332;
    private const int Pe = 0x80;
    private const int Section = Pe + 24 + 240;
    private const ulong Module = 0x140000000;

    [Fact]
    public void ExactSyntheticImageMatchesWithOnlyHeaderAndTwoGuardReads()
    {
        var memory = new Memory();
        var identity = ParseIdentity(IdentityDocument(memory));

        Assert.True(identity.MatchesImage(memory, Module));
        Assert.Equal(KnownPackageFullName, identity.PackageFullName);
        Assert.Equal(Timestamp, identity.TimeDateStamp);
        Assert.Equal(ImageSize, identity.ImageSize);
        Assert.Equal(new[] { (Module, 4096), (Module + 0x1100, 32), (Module + 0x1200, 64) }, memory.Reads);
    }

    [Theory]
    [InlineData("missing-name")]
    [InlineData("extra-property")]
    [InlineData("wrong-name")]
    [InlineData("wrong-version")]
    [InlineData("wrong-architecture")]
    [InlineData("wrong-publisher")]
    [InlineData("null-name")]
    [InlineData("timestamp-zero")]
    [InlineData("timestamp-negative")]
    [InlineData("timestamp-string")]
    [InlineData("timestamp-overflow")]
    [InlineData("guards-null")]
    [InlineData("guards-object")]
    [InlineData("one-guard")]
    [InlineData("nine-guards")]
    [InlineData("missing-length")]
    [InlineData("guard-extra")]
    [InlineData("guard-null")]
    [InlineData("rva-before-code")]
    [InlineData("rva-at-image-end")]
    [InlineData("rva-overflow")]
    [InlineData("rva-negative")]
    [InlineData("rva-string")]
    [InlineData("length-zero")]
    [InlineData("length-small")]
    [InlineData("length-large")]
    [InlineData("length-string")]
    [InlineData("length-crosses-image")]
    [InlineData("overlap")]
    [InlineData("same-guard")]
    [InlineData("hash-short")]
    [InlineData("hash-nonhex")]
    [InlineData("hash-null")]
    public void MalformedStoreContractIsRejectedThroughPublicPackParser(string fault)
    {
        var document = StorePackDocument();
        var store = document["storeIdentity"]!.AsObject();
        var guards = store["codeGuards"]!.AsArray();
        var first = guards[0]!.AsObject();
        switch (fault)
        {
            case "missing-name": store.Remove("packageFullName"); break;
            case "extra-property": store["other"] = true; break;
            case "wrong-name": store["packageFullName"] = "Microsoft.OtherGame_3.430.771.0_x64__8wekyb3d8bbwe"; break;
            case "wrong-version": document["gameVersion"] = "3.430.772.0"; break;
            case "wrong-architecture": store["packageFullName"] = "Microsoft.ForteBaseGame_3.430.771.0_x86__8wekyb3d8bbwe"; break;
            case "wrong-publisher": store["packageFullName"] = "Microsoft.ForteBaseGame_3.430.771.0_x64__other"; break;
            case "null-name": store["packageFullName"] = null; break;
            case "timestamp-zero": store["timeDateStamp"] = 0; break;
            case "timestamp-negative": store["timeDateStamp"] = -1; break;
            case "timestamp-string": store["timeDateStamp"] = "1787153332"; break;
            case "timestamp-overflow": store["timeDateStamp"] = (ulong)uint.MaxValue + 1; break;
            case "guards-null": store["codeGuards"] = null; break;
            case "guards-object": store["codeGuards"] = new JsonObject(); break;
            case "one-guard": guards.RemoveAt(1); break;
            case "nine-guards":
                for (var index = 2; index < 9; index++)
                {
                    var extra = first.DeepClone().AsObject();
                    extra["rva"] = 0x2000 + index * 256;
                    guards.Add(extra);
                }
                break;
            case "missing-length": first.Remove("length"); break;
            case "guard-extra": first["extra"] = 0; break;
            case "guard-null": guards[0] = null; break;
            case "rva-before-code": first["rva"] = 4095; break;
            case "rva-at-image-end": first["rva"] = document["imageSize"]!.DeepClone(); break;
            case "rva-overflow": first["rva"] = (ulong)uint.MaxValue + 1; break;
            case "rva-negative": first["rva"] = -1; break;
            case "rva-string": first["rva"] = "4352"; break;
            case "length-zero": first["length"] = 0; break;
            case "length-small": first["length"] = 15; break;
            case "length-large": first["length"] = 257; break;
            case "length-string": first["length"] = "32"; break;
            case "length-crosses-image": first["rva"] = document["imageSize"]!.GetValue<uint>() - 16; break;
            case "overlap": guards[1]!["rva"] = 0x111F; break;
            case "same-guard": guards[1] = first.DeepClone(); break;
            case "hash-short": first["sha256"] = new string('A', 63); break;
            case "hash-nonhex": first["sha256"] = new string('G', 64); break;
            case "hash-null": first["sha256"] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }

        Assert.Throws<FormatException>(() => ParsePack(document));
    }

    [Theory]
    [InlineData("packageFullName")]
    [InlineData("timeDateStamp")]
    [InlineData("codeGuards")]
    public void DuplicateIdentityPropertiesAreRejected(string property)
    {
        var document = StorePackDocument();
        var original = document["storeIdentity"]![property]!.ToJsonString();
        var json = document.ToJsonString().Replace($"\"{property}\":{original}",
            $"\"{property}\":{original},\"{property}\":{original}", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => NativeHudCompatibilityPack.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void AdjacentGuardsAtMinimumAndMaximumLengthAreAccepted()
    {
        var memory = new Memory();
        var document = IdentityDocument(memory);
        document["codeGuards"] = new JsonArray(Guard(memory, 4096, 16), Guard(memory, 4112, 256));
        Assert.True(ParseIdentity(document).MatchesImage(memory, Module));
    }

    [Fact]
    public void EightNonoverlappingGuardsAreAccepted()
    {
        var memory = new Memory();
        var document = IdentityDocument(memory);
        document["codeGuards"] = new JsonArray(Enumerable.Range(0, 8)
            .Select(index => (JsonNode)Guard(memory, (uint)(4096 + index * 32), 16)).ToArray());
        Assert.True(ParseIdentity(document).MatchesImage(memory, Module));
    }

    [Theory]
    [InlineData("dos")]
    [InlineData("pe-negative")]
    [InlineData("pe-small")]
    [InlineData("pe-large")]
    [InlineData("signature")]
    [InlineData("machine")]
    [InlineData("timestamp")]
    [InlineData("optional-magic")]
    [InlineData("image-size")]
    [InlineData("sections-zero")]
    [InlineData("sections-many")]
    [InlineData("optional-size-small")]
    [InlineData("section-table-overflow")]
    [InlineData("section-before-code")]
    [InlineData("section-past-image")]
    [InlineData("section-size-overflow")]
    [InlineData("guard-crosses-section")]
    [InlineData("section-not-executable")]
    [InlineData("section-writable")]
    [InlineData("first-hash-changed")]
    [InlineData("second-hash-changed")]
    public void CorruptOrMismatchedSyntheticImageIsRejected(string fault)
    {
        var memory = new Memory();
        var identity = ParseIdentity(IdentityDocument(memory));
        switch (fault)
        {
            case "dos": memory.U16(0, 0); break;
            case "pe-negative": memory.U32(0x3C, uint.MaxValue); break;
            case "pe-small": memory.U32(0x3C, 63); break;
            case "pe-large": memory.U32(0x3C, 2049); break;
            case "signature": memory.U32(Pe, 0); break;
            case "machine": memory.U16(Pe + 4, 0x14C); break;
            case "timestamp": memory.U32(Pe + 8, Timestamp + 1); break;
            case "optional-magic": memory.U16(Pe + 24, 0x10B); break;
            case "image-size": memory.U32(Pe + 80, ImageSize + 4096); break;
            case "sections-zero": memory.U16(Pe + 6, 0); break;
            case "sections-many": memory.U16(Pe + 6, 33); break;
            case "optional-size-small": memory.U16(Pe + 20, 111); break;
            case "section-table-overflow": memory.U16(Pe + 20, 4096); break;
            case "section-before-code": memory.U32(Section + 12, 4095); break;
            case "section-past-image": memory.U32(Section + 12, ImageSize); break;
            case "section-size-overflow": memory.U32(Section + 8, uint.MaxValue); break;
            case "guard-crosses-section": memory.U32(Section + 8, 0x11F); break;
            case "section-not-executable": memory.U32(Section + 36, 0x40000040); break;
            case "section-writable": memory.U32(Section + 36, 0xE0000020); break;
            case "first-hash-changed": memory.Bytes[0x1100] ^= 1; break;
            case "second-hash-changed": memory.Bytes[0x1200] ^= 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }

        Assert.False(identity.MatchesImage(memory, Module));
        Assert.InRange(memory.Reads.Count, 1, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x1100)]
    [InlineData(0x1200)]
    public void FailedOrPartialReadCannotAcceptStaleBufferContents(int failedRva)
    {
        var memory = new Memory();
        var identity = ParseIdentity(IdentityDocument(memory));
        memory.PartialReadAddress = Module + (ulong)failedRva;
        Assert.False(identity.MatchesImage(memory, Module));
        Assert.Equal(memory.PartialReadAddress, memory.Reads[^1].Address);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0xFFFFUL)]
    [InlineData(Module + 1)]
    [InlineData(0x00007FFFFFFFE000UL)]
    [InlineData(0x0000800000000000UL)]
    [InlineData(0xFFFFFFFFFFFFFFF8UL)]
    public void InvalidOrOverflowingModuleRangeIsRejectedWithoutReads(ulong module)
    {
        var memory = new Memory();
        var identity = ParseIdentity(IdentityDocument(memory));
        Assert.False(identity.MatchesImage(memory, module));
        Assert.Empty(memory.Reads);
    }

    [Fact]
    public void GuardHashesAreCopiedAndCaseInsensitive()
    {
        var memory = new Memory();
        var document = IdentityDocument(memory);
        document["codeGuards"]![0]!["sha256"] = document["codeGuards"]![0]!["sha256"]!.GetValue<string>().ToLowerInvariant();
        var identity = ParseIdentity(document);
        document["codeGuards"]!.AsArray().Clear();
        Assert.True(identity.MatchesImage(memory, Module));
        Assert.All(typeof(NativeHudStoreBuildIdentity).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void StorePackCannotBeSelectedAsAFileFingerprintAndSteamStillCan()
    {
        var store = ParsePack(StorePackDocument());
        var steam = NativeHudBuildContract.BuiltIn;
        Assert.NotNull(store.StoreIdentity);
        Assert.Equal(0, store.ExecutableLength);
        Assert.Equal(string.Empty, store.ExecutableSha256);
        Assert.False(store.Matches(store.GameVersion, 0, string.Empty));
        Assert.False(store.Matches(store.GameVersion, steam.ExecutableLength, steam.ExecutableSha256));
        Assert.Null(steam.StoreIdentity);
        Assert.True(steam.Matches(steam.GameVersion, steam.ExecutableLength, steam.ExecutableSha256));
    }

    [Theory]
    [InlineData("executableLength")]
    [InlineData("executableSha256")]
    public void StoreSchemaCannotCarryASyntheticFileFingerprint(string property)
    {
        var document = StorePackDocument();
        document[property] = property == "executableLength" ? JsonValue.Create(4096) : JsonValue.Create(new string('A', 64));
        Assert.Throws<FormatException>(() => ParsePack(document));
    }

    [Fact]
    public void StoreEnvelopeRequiresAPinnedPublisherAndPreservesItsImageGuards()
    {
        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var keyId = NativeCompatibilitySignature.GetKeyId(publicKey);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["format"] = 1,
            ["purpose"] = "wisp-native-hud-compatibility",
            ["issuedUtc"] = "2026-09-06T11:00:00Z",
            ["expiresUtc"] = "2026-09-07T12:00:00Z",
            ["pack"] = StorePackDocument()
        });
        var signature = key.SignData(NativeCompatibilitySignature.CreateSigningInput(payload),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["format"] = 1,
            ["keyId"] = keyId,
            ["payload"] = Convert.ToBase64String(payload),
            ["signature"] = Convert.ToBase64String(signature)
        });
        var verified = NativeCompatibilityEnvelope.Verify(envelope, new Dictionary<string, byte[]> { [keyId] = publicKey }, now);
        Assert.Single(verified.Packs);
        Assert.NotNull(verified.Pack.StoreIdentity);
        Assert.Equal(KnownPackageFullName, verified.Pack.StoreIdentity.PackageFullName);
        var memory = new Memory();
        BinaryPrimitives.WriteUInt32LittleEndian(memory.Bytes.AsSpan(Pe + 80), verified.Pack.ImageSize);
        Assert.True(verified.Pack.StoreIdentity.MatchesImage(memory, Module));
        memory.Bytes[0x1100] ^= 1;
        Assert.False(verified.Pack.StoreIdentity.MatchesImage(memory, Module));
        var exception = Assert.Throws<NativeCompatibilityEnvelopeException>(() => NativeCompatibilityEnvelope.Verify(
            envelope, new Dictionary<string, byte[]>(), now));
        Assert.Equal(NativeCompatibilityInstallCode.UntrustedPublisher, exception.Code);
    }

    private static JsonObject StorePackDocument()
    {
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.BuiltIn.json");
        Assert.NotNull(stream);
        var document = JsonNode.Parse(stream)!.AsObject();
        document["schemaVersion"] = 4;
        document["readerVersion"] = 4;
        document["gameVersion"] = StoreVersion;
        document.Remove("executableLength");
        document.Remove("executableSha256");
        document["storeIdentity"] = IdentityDocument(new Memory());
        document["nativeGauge"] = null;
        return document;
    }

    private static NativeHudCompatibilityPack ParsePack(JsonObject document) =>
        NativeHudCompatibilityPack.Parse(JsonSerializer.SerializeToUtf8Bytes(document));

    private static NativeHudStoreBuildIdentity ParseIdentity(JsonObject document)
    {
        using var json = JsonDocument.Parse(document.ToJsonString());
        return NativeHudStoreBuildIdentity.Parse(json.RootElement, StoreVersion, ImageSize);
    }

    private static JsonObject IdentityDocument(Memory memory) => new()
    {
        ["packageFullName"] = KnownPackageFullName,
        ["timeDateStamp"] = Timestamp,
        ["codeGuards"] = new JsonArray(Guard(memory, 0x1100, 32), Guard(memory, 0x1200, 64))
    };

    private static JsonObject Guard(Memory memory, uint rva, int length) => new()
    {
        ["rva"] = rva,
        ["length"] = length,
        ["sha256"] = Convert.ToHexString(SHA256.HashData(memory.Bytes.AsSpan((int)rva, length)))
    };

    private sealed class Memory : IReadOnlyProcessMemory
    {
        internal byte[] Bytes { get; } = new byte[ImageSize];
        internal List<(ulong Address, int Length)> Reads { get; } = [];
        internal ulong? PartialReadAddress { get; set; }

        internal Memory()
        {
            U16(0, 0x5A4D);
            U32(0x3C, Pe);
            U32(Pe, 0x4550);
            U16(Pe + 4, 0x8664);
            U16(Pe + 6, 1);
            U32(Pe + 8, Timestamp);
            U16(Pe + 20, 240);
            U16(Pe + 24, 0x20B);
            U32(Pe + 80, ImageSize);
            U32(Section + 8, 0x2000);
            U32(Section + 12, 0x1000);
            U32(Section + 36, 0x60000020);
            for (var index = 0x1000; index < 0x3000; index++) Bytes[index] = (byte)(index % 251);
        }

        internal void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(offset), value);
        internal void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(offset), value);

        public bool TryReadBytes(ulong address, Span<byte> destination)
        {
            Reads.Add((address, destination.Length));
            if (address < Module || address - Module > ImageSize || (ulong)destination.Length > ImageSize - (address - Module))
                return false;
            var source = Bytes.AsSpan((int)(address - Module), destination.Length);
            if (PartialReadAddress == address)
            {
                source[..(source.Length / 2)].CopyTo(destination);
                return false;
            }
            source.CopyTo(destination);
            return true;
        }

        public bool TryReadByte(ulong address, out byte value) => throw new InvalidOperationException("Unexpected scalar read.");
        public bool TryReadUInt32(ulong address, out uint value) => throw new InvalidOperationException("Unexpected scalar read.");
        public bool TryReadUInt64(ulong address, out ulong value) => throw new InvalidOperationException("Unexpected scalar read.");
        public bool TryReadSingle(ulong address, out float value) => throw new InvalidOperationException("Unexpected scalar read.");
    }
}
