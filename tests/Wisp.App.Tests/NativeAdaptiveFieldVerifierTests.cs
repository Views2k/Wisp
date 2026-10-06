using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeAdaptiveFieldVerifierTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AllEighteenFieldsRequireCompleteTypedProof(bool alias, bool getterUsesBody)
    {
        using var fixture = new Fixture(alias, getterUsesBody);
        var image = fixture.Image(TestContext.Current.CancellationToken);
        fixture.Validate(image);
        Assert.True(image.FinishProof().Matches(fixture.Read, Fixture.Module,
            new NativeAdaptiveBudget(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("name")]
    [InlineData("initializer-type")]
    [InlineData("getter-type")]
    [InlineData("internal-branch")]
    [InlineData("type-body")]
    public void ChangedFieldEvidenceIsRejected(string change)
    {
        using var fixture = new Fixture();
        switch (change)
        {
            case "member":
                fixture.Bytes[Fixture.Getter(0) + 52] ^= 1;
                break;
            case "name":
                fixture.Bytes[Fixture.Name(0)] ^= 1;
                break;
            case "initializer-type":
                fixture.Relative(Fixture.Initializer(0), 5, 9, Fixture.TypeBody + 8);
                break;
            case "getter-type":
                fixture.Relative(Fixture.Getter(0), 24, 28, Fixture.TypeBody + 8);
                break;
            case "internal-branch":
                fixture.Bytes[Fixture.Initializer(0) + 25] = 3;
                break;
            case "type-body":
                fixture.Bytes[Fixture.TypeBody + 6] ^= 1;
                break;
        }
        Assert.Throws<InvalidDataException>(() => fixture.Validate(fixture.Image(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void DuplicateCompleteTypedInitializersAreRejected()
    {
        using var fixture = new Fixture();
        const int duplicate = 0xf000;
        fixture.Bytes.AsSpan(Fixture.Initializer(0), Fixture.BodyLength).CopyTo(fixture.Bytes.AsSpan(duplicate));
        fixture.Relative(duplicate, 5, 9, Fixture.TypeEntry);
        fixture.Relative(duplicate, 12, 16, Fixture.Getter(0));
        fixture.Relative(duplicate, 19, 23, Fixture.Name(0));
        var error = Assert.Throws<InvalidDataException>(() => fixture.Validate(fixture.Image(TestContext.Current.CancellationToken)));
        Assert.Equal("ambiguous-complete-typed-match", error.Message);
    }

    [Fact]
    public void ASecondTypeJumpIsRejected()
    {
        using var fixture = new Fixture();
        const int intermediate = 0x1180;
        fixture.Bytes[intermediate] = 0xe9;
        fixture.Relative(intermediate, 1, 5, Fixture.TypeBody);
        fixture.Relative(Fixture.TypeEntry, 1, 5, intermediate);
        var error = Assert.Throws<InvalidDataException>(() => fixture.Validate(fixture.Image(TestContext.Current.CancellationToken)));
        Assert.Equal("type-alias-chain", error.Message);
    }

    [Fact]
    public void SharedTemplateReferencesCannotResolveToDifferentTargets()
    {
        using var fixture = new Fixture();
        fixture.Relative(Fixture.Getter(1), 83, 87, 0x1700);
        var error = Assert.Throws<InvalidDataException>(() => fixture.Validate(fixture.Image(TestContext.Current.CancellationToken)));
        Assert.Equal("template-shared-reference-conflict", error.Message);
    }

    [Fact]
    public void ReferencesIntoAnotherMatchedRangeMustKeepTheirInteriorOffset()
    {
        using var fixture = new Fixture();
        fixture.Relative(Fixture.Getter(1), 83, 87, Fixture.Initializer(0) + 31);
        var profile = JsonNode.Parse(fixture.Profile.RootElement.GetRawText())!;
        profile["signatures"]![3]!["relocations"]![1]!["targetRva"] = 0x200000 + 30;
        using var changed = JsonDocument.Parse(profile.ToJsonString());
        var error = Assert.Throws<InvalidDataException>(() => fixture.Validate(fixture.Image(TestContext.Current.CancellationToken), changed.RootElement));
        Assert.Equal("template-interior-reference-conflict", error.Message);
    }

    [Fact]
    public void ATemplateCannotHideAnIncorrectMemberDisplacement()
    {
        using var fixture = new Fixture();
        fixture.Bytes[Fixture.Getter(0) + 52] ^= 1;
        var profile = JsonNode.Parse(fixture.Profile.RootElement.GetRawText())!;
        var getter = profile["signatures"]![1]!;
        var bytes = fixture.Bytes.AsSpan(Fixture.Getter(0), Fixture.BodyLength).ToArray();
        bytes.AsSpan(24, 4).Clear();
        bytes.AsSpan(83, 4).Clear();
        getter["normalizedSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        using var changed = JsonDocument.Parse(profile.ToJsonString());
        Assert.Throws<InvalidDataException>(() => fixture.Validate(fixture.Image(TestContext.Current.CancellationToken), changed.RootElement));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void EveryExpectedFieldIsRequired(int removedIndex)
    {
        using var fixture = new Fixture();
        var profile = JsonNode.Parse(fixture.Profile.RootElement.GetRawText())!;
        profile["fieldWitnesses"]!.AsArray().RemoveAt(removedIndex);
        using var changed = JsonDocument.Parse(profile.ToJsonString());
        var error = Assert.Throws<InvalidDataException>(() => fixture.Validate(fixture.Image(TestContext.Current.CancellationToken), changed.RootElement));
        Assert.Equal("witness-count", error.Message);
    }

    [Theory]
    [InlineData("slot")]
    [InlineData("alias")]
    [InlineData("initializer")]
    [InlineData("getter")]
    [InlineData("name")]
    [InlineData("type")]
    public void AcceptedRangesRemainPartOfTheCallersFinalProof(string changedRange)
    {
        using var fixture = new Fixture();
        var image = fixture.Image(TestContext.Current.CancellationToken);
        fixture.Validate(image);
        var offset = changedRange switch
        {
            "slot" => Fixture.ChildVtable + 24,
            "alias" => Fixture.TypeEntry + 1,
            "initializer" => Fixture.Initializer(0),
            "getter" => Fixture.Getter(0),
            "name" => Fixture.Name(0),
            _ => Fixture.TypeBody
        };
        fixture.Bytes[offset] ^= 1;
        Assert.Throws<IOException>(() => image.FinishProof());
    }

    [Fact]
    public void UnreadableFinalProofIsTransient()
    {
        using var fixture = new Fixture();
        var image = fixture.Image(TestContext.Current.CancellationToken);
        fixture.Validate(image);
        fixture.RejectReads = true;
        Assert.Throws<IOException>(() => image.FinishProof());
    }

    [Fact]
    public void CancellationIsNotConvertedIntoStructuralRejection()
    {
        using var fixture = new Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var image = fixture.Image(cancellation.Token);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.Validate(image));
    }

    private sealed record Field(string Name, string Path, ulong Offset, string Kind);
    private sealed record Relocation(int Offset, int Width, int Next, uint ReferenceTarget, string Section, uint? Inner = null);

    private sealed class Fixture : IDisposable
    {
        internal const ulong Module = 0x140000000;
        internal const int ChildVtable = 0x11000;
        internal const int TypeEntry = 0x1100;
        internal const int TypeBody = 0x1200;
        internal const int BodyLength = 128;
        private const uint ReferenceType = 0x100100;
        private const uint ReferenceBody = 0x100000;
        private const int SharedHelper = 0x1600;
        private readonly NativeGaugeLayout _layout = NativeHudBuildContract.BuiltIn.NativeGauge!;
        internal byte[] Bytes { get; } = new byte[0x18000];
        internal bool RejectReads { get; set; }
        internal JsonDocument Profile { get; }
        private JsonDocument TypeTemplate { get; }

        internal static int Initializer(int index) => 0x2000 + index * 0x300;
        internal static int Getter(int index) => Initializer(index) + 0x100;
        internal static int Name(int index) => 0x11400 + index * 0x40;

        internal Fixture(bool alias = true, bool getterUsesBody = false)
        {
            Write16(0, 0x5a4d);
            Write32(0x3c, 0x80);
            Write32(0x80, 0x4550);
            Write16(0x84, 0x8664);
            Write16(0x86, 3);
            Write16(0x94, 0xf0);
            Write16(0x98, 0x20b);
            Write32(0xd0, Bytes.Length);
            Section(0, ".text", 0x1000, 0xf000, 0x60000020);
            Section(1, ".rdata", 0x10000, 0x4000, 0x40000040);
            Section(2, ".data", 0x14000, 0x2000, 0xc0000040);
            BinaryPrimitives.WriteUInt64LittleEndian(Bytes.AsSpan(ChildVtable + 24), Module + (uint)(alias ? TypeEntry : TypeBody));
            Bytes[TypeEntry] = 0xe9;
            Relative(TypeEntry, 1, 5, TypeBody);
            Convert.FromHexString("4831C0909090909090909090909090C3").CopyTo(Bytes, TypeBody);
            Bytes[SharedHelper] = 0xc3;
            Bytes[0x1700] = 0xc3;
            TypeTemplate = JsonDocument.Parse(JsonSerializer.Serialize(
                Signature("type", ReferenceBody, TypeBody, 16, [])));

            var signatures = new List<object>();
            var fields = new List<object>();
            var specs = Fields(_layout);
            for (var index = 0; index < specs.Length; index++)
            {
                var spec = specs[index];
                var initializer = Initializer(index);
                var getter = Getter(index);
                var name = Name(index);
                var referenceInitializer = checked((uint)(0x200000 + index * 0x300));
                var referenceGetter = referenceInitializer + 0x100;
                var referenceName = checked((uint)(0x400000 + index * 0x40));
                var typeTarget = alias ? TypeEntry : TypeBody;
                Bytes.AsSpan(initializer, BodyLength).Fill(0x90);
                Bytes.AsSpan(getter, BodyLength).Fill(0x90);
                Convert.FromHexString("554889E5").CopyTo(Bytes, initializer);
                Bytes[initializer + 4] = 0xe8;
                Relative(initializer, 5, 9, typeTarget);
                Convert.FromHexString("488D05").CopyTo(Bytes, initializer + 9);
                Relative(initializer, 12, 16, getter);
                Convert.FromHexString("488D0D").CopyTo(Bytes, initializer + 16);
                Relative(initializer, 19, 23, name);
                Bytes[initializer + 24] = 0xeb;
                Bytes[initializer + 25] = 2;
                Bytes[initializer + BodyLength - 1] = 0xc3;
                Convert.FromHexString("534889CB").CopyTo(Bytes, getter);
                Bytes[getter + 23] = 0xe8;
                Relative(getter, 24, 28, getterUsesBody ? TypeBody : typeTarget);
                var prefix = Convert.FromHexString(spec.Kind == "float" ? "F30F1087" : spec.Kind == "bool" ? "80BF" : "8B87");
                var instruction = spec.Kind == "bool" ? 57 : 48;
                prefix.CopyTo(Bytes, getter + instruction);
                BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(getter + instruction + prefix.Length), checked((uint)spec.Offset));
                if (spec.Kind == "bool") Bytes[getter + instruction + prefix.Length + 4] = 0;
                Convert.FromHexString("488D05").CopyTo(Bytes, getter + 80);
                Relative(getter, 83, 87, SharedHelper);
                Bytes[getter + BodyLength - 1] = 0xc3;
                var nameBytes = Encoding.ASCII.GetBytes(spec.Name + "\0");
                nameBytes.CopyTo(Bytes, name);
                signatures.Add(Signature("initializer-" + index, referenceInitializer, initializer, BodyLength,
                [
                    new(5, 4, 9, ReferenceType, "code"),
                    new(12, 4, 16, referenceGetter, "code"),
                    new(19, 4, 23, referenceName, "readonly"),
                    new(25, 1, 26, referenceInitializer + 28, "code", 28)
                ]));
                signatures.Add(Signature("getter-" + index, referenceGetter, getter, BodyLength,
                [
                    new(24, 4, 28, ReferenceType, "code"),
                    new(83, 4, 87, 0x500000, "code")
                ]));
                fields.Add(new
                {
                    path = "/nativeGauge/" + spec.Path,
                    name = spec.Name,
                    value = spec.Offset,
                    childTypeGetterSlotOffset = 24,
                    nameEncoding = "ascii",
                    nameHex = Convert.ToHexString(nameBytes),
                    initializerSignature = "initializer-" + index,
                    getterSignature = "getter-" + index,
                    getterOperandOffset = 12,
                    nameOperandOffset = 19,
                    typeGetterOperandOffset = 5
                });
            }
            Profile = JsonDocument.Parse(JsonSerializer.Serialize(new { platform = "store", signatures, fieldWitnesses = fields }));
        }

        internal NativeAdaptiveImage Image(CancellationToken token = default) =>
            new(Read, Module, (uint)Bytes.Length, false, new NativeAdaptiveBudget(TimeSpan.FromSeconds(5), token));

        internal void Validate(NativeAdaptiveImage image, JsonElement? profile = null) =>
            NativeAdaptiveFieldVerifier.Validate(image, profile ?? Profile.RootElement, _layout, ChildVtable, TypeTemplate.RootElement);

        internal bool Read(ulong address, Span<byte> destination)
        {
            if (RejectReads || address < Module || address - Module > (ulong)Bytes.Length ||
                (ulong)destination.Length > (ulong)Bytes.Length - (address - Module)) return false;
            Bytes.AsSpan(checked((int)(address - Module)), destination.Length).CopyTo(destination);
            return true;
        }

        internal void Relative(int rva, int offset, int next, int target) => Write32(rva + offset, target - (rva + next));

        private object Signature(string id, uint referenceRva, int actualRva, int length, Relocation[] relocations)
        {
            var normalized = Bytes.AsSpan(actualRva, length).ToArray();
            foreach (var relocation in relocations) normalized.AsSpan(relocation.Offset, relocation.Width).Clear();
            return new
            {
                id,
                referenceRva,
                length,
                normalizedSha256 = Convert.ToHexString(SHA256.HashData(normalized)),
                anchorOffset = 0,
                anchorHex = Convert.ToHexString(Bytes.AsSpan(actualRva, 4)),
                section = "code",
                relocations = relocations.Select(r => new
                {
                    offset = r.Offset,
                    width = r.Width,
                    next = r.Next,
                    kind = "relative",
                    targetRva = r.ReferenceTarget,
                    expectedTargetSection = r.Section,
                    internalOffset = r.Inner
                }).ToArray()
            };
        }

        private void Write16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(offset), value);
        private void Write32(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(offset), value);

        private void Section(int index, string name, int rva, int size, uint flags)
        {
            var at = 0x188 + index * 40;
            Encoding.ASCII.GetBytes(name).CopyTo(Bytes, at);
            Write32(at + 8, size);
            Write32(at + 12, rva);
            BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(at + 36), flags);
        }

        public void Dispose()
        {
            Profile.Dispose();
            TypeTemplate.Dispose();
        }
    }

    private static Field[] Fields(NativeGaugeLayout g) =>
    [
        new("NeedleAngle", "childAngleOffset", g.ChildAngleOffset, "float"),
        new("NeedleBlur", "childBlurOffset", g.ChildBlurOffset, "float"),
        new("SpeedDigitOne", "childSpeedDigitOneOffset", g.ChildSpeedDigitOneOffset, "int"),
        new("SpeedDigitTen", "childSpeedDigitTenOffset", g.ChildSpeedDigitTenOffset, "int"),
        new("SpeedDigitHundred", "childSpeedDigitHundredOffset", g.ChildSpeedDigitHundredOffset, "int"),
        new("SpeedLessOrEqualOne", "childSpeedLessOrEqualOneOffset", g.ChildSpeedLessOrEqualOneOffset, "bool"),
        new("SpeedLessTen", "childSpeedLessTenOffset", g.ChildSpeedLessTenOffset, "bool"),
        new("SpeedLessHundred", "childSpeedLessHundredOffset", g.ChildSpeedLessHundredOffset, "bool"),
        new("AreHeadlightsOn", "childHeadlightsOnOffset", g.ChildHeadlightsOnOffset, "bool"),
        new("Gear", "childGearOffset", g.ChildGearOffset, "int"),
        new("GearPrevious", "childGearPreviousOffset", g.ChildGearPreviousOffset, "int"),
        new("GearNext", "childGearNextOffset", g.ChildGearNextOffset, "int"),
        new("RegenFillAmount", "childRegenOffset", g.ChildRegenOffset, "float"),
        new("PowerFillAmount", "childPowerOffset", g.ChildPowerOffset, "float"),
        new("GearGaugeState", "childGearGaugeStateOffset", g.ChildGearGaugeStateOffset, "int"),
        new("UseDriveFor1", "childUseDriveFor1Offset", g.ChildUseDriveFor1Offset, "bool"),
        new("RegenPowerRatio", "childRatioOffset", g.ChildRatioOffset, "float"),
        new("SpeedometerOption", "childModeOffset", g.ChildModeOffset, "int")
    ];
}
