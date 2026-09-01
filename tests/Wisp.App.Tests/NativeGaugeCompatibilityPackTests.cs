using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeGaugeCompatibilityPackTests
{
    private const uint ImageSize = 188_293_120;

    private static readonly string[] DataRvaProperties =
    [
        "registryGlobalRva", "registryWrapperVtableRva", "registryContextVtableRva",
        "registryContextControlVtableRva", "hudVtableRva", "hudControlVtableRva",
        "hudSubobjectVtableRva", "hudTypeTokenRva", "outerControlVtableRva",
        "outerPrimaryVtableRva", "outerSecondaryVtableRva", "childVtableRva"
    ];

    [Fact]
    public void VersionThreePreservesEveryNativeGaugeGuard()
    {
        var document = BuiltInDocument();
        var pack = Parse(document);
        var layout = Assert.IsType<NativeGaugeLayout>(pack.NativeGauge);
        var expected = document["nativeGauge"]!.AsObject();

        Assert.Equal(3, pack.SchemaVersion);
        Assert.Equal(3, pack.ReaderVersion);
        Assert.Equal(5, pack.Revision);
        foreach (var property in typeof(NativeGaugeLayout).GetProperties()
                     .Where(property => property.PropertyType == typeof(ulong)))
        {
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            Assert.Equal(
                expected[jsonName]!.GetValue<ulong>(),
                Assert.IsType<ulong>(property.GetValue(layout)));
        }

        Assert.Equal("488D4170C3", layout.HudSubobjectSlotZeroPrologueHex);
        Assert.Equal(6, layout.RequiredProviderVtableSlots.Count);
        Assert.Equal(0x01F12AD0UL, layout.RequiredProviderVtableSlots[0x0298]);
        Assert.Equal(0x01F18F00UL, layout.RequiredProviderVtableSlots[0x0E28]);
        Assert.Equal(64UL, layout.HudTypeVectorMaximumCount);
        Assert.Equal(1UL, layout.HudTypeInstanceCount);
        Assert.Equal(0x10UL, layout.SharedControlObjectOffset);
        Assert.Equal(0xFCUL, layout.ChildSpeedDigitOneOffset);
        Assert.Equal(0x100UL, layout.ChildSpeedDigitTenOffset);
        Assert.Equal(0x104UL, layout.ChildSpeedDigitHundredOffset);
        Assert.Equal(0x108UL, layout.ChildSpeedLessOrEqualOneOffset);
        Assert.Equal(0x109UL, layout.ChildSpeedLessTenOffset);
        Assert.Equal(0x10AUL, layout.ChildSpeedLessHundredOffset);
        Assert.Equal(0x168UL, layout.ChildSpeedUnitObjectOffset);
        Assert.Equal(0x04UL, layout.SpeedUnitEnumOffset);
        Assert.Equal(0x16U, layout.SpeedUnitMphValue);
        Assert.Equal(0x17U, layout.SpeedUnitKphValue);
        Assert.Equal(0x10CUL, layout.ChildHeadlightsOnOffset);
        Assert.Equal(0x130UL, layout.ChildRegenOffset);
        Assert.Equal(0x134UL, layout.ChildPowerOffset);
        Assert.Equal(0x110UL, layout.ChildGearOffset);
        Assert.Equal(0x118UL, layout.ChildGearNextOffset);
        Assert.Equal(0x114UL, layout.ChildGearPreviousOffset);
        Assert.Equal(0x138UL, layout.ChildGearGaugeStateOffset);
        Assert.Equal(0x13CUL, layout.ChildUseDriveFor1Offset);
        Assert.Equal(0x23B0UL, layout.ProviderPowerLimitFirstOffset);
        Assert.Equal(0x23BCUL, layout.ProviderPowerLimitSecondOffset);
        Assert.Equal(0x14ECUL, layout.ProviderElectricSpeedOffset);
        Assert.Equal(0.3f, BitConverter.UInt32BitsToSingle(layout.ChildlessRegenPowerRatioBits));
        Assert.Equal(0.01f, BitConverter.UInt32BitsToSingle(layout.ProviderPowerDenominatorScaleBits));
        Assert.Equal(-1.25f, BitConverter.UInt32BitsToSingle(layout.ProviderRegenScaleBits));
        Assert.Equal(0.25f, BitConverter.UInt32BitsToSingle(layout.ProviderRegenUpperBaseBits));
    }

    [Fact]
    public void NativeGaugeModelIsImmutableAndRetainsNoMutableInput()
    {
        var document = BuiltInDocument();
        var pack = Parse(document);
        var layout = pack.NativeGauge!;
        document["nativeGauge"]!["childAngleOffset"] = 0;
        document["nativeGauge"]!["hudSubobjectSlotZeroPrologueHex"] = "0000000000";
        document["nativeGauge"]!["requiredProviderVtableSlots"]!.AsArray().Clear();

        Assert.Equal(0xF4UL, layout.ChildAngleOffset);
        Assert.Equal("488D4170C3", layout.HudSubobjectSlotZeroPrologueHex);
        Assert.Equal(6, layout.RequiredProviderVtableSlots.Count);
        Assert.IsAssignableFrom<FrozenDictionary<ulong, ulong>>(layout.RequiredProviderVtableSlots);
        Assert.All(typeof(NativeGaugeLayout).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Empty(typeof(NativeGaugeLayout).GetConstructors());
    }

    [Fact]
    public void EarlierSchemasRemainReadableWithoutClaimingNativeGaugeSupport()
    {
        var versionTwo = BuiltInDocument();
        versionTwo["schemaVersion"] = 2;
        versionTwo["readerVersion"] = 2;
        versionTwo.Remove("nativeGauge");
        Assert.Null(Parse(versionTwo).NativeGauge);

        var versionOne = versionTwo.DeepClone().AsObject();
        versionOne["schemaVersion"] = 1;
        versionOne["readerVersion"] = 1;
        versionOne.Remove("gameplayVisibility");
        Assert.Null(Parse(versionOne).NativeGauge);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("\"native\"")]
    public void VersionThreeRequiresANativeGaugeObject(string rawJson)
    {
        var document = BuiltInDocument();
        document["nativeGauge"] = JsonNode.Parse(rawJson);

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [MemberData(nameof(NativeGaugePropertyNames))]
    public void EveryNativeGaugePropertyIsRequired(string propertyName)
    {
        var document = BuiltInDocument();
        Assert.True(document["nativeGauge"]!.AsObject().Remove(propertyName));

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("RegistryGlobalRva")]
    [InlineData("liveVectorCount")]
    [InlineData("allowedModes")]
    public void NativeGaugeRejectsUnknownOrEditableReaderSemantics(string propertyName)
    {
        var document = BuiltInDocument();
        document["nativeGauge"]![propertyName] = 1;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData("registryGlobalRva", "176282936")]
    [InlineData("registry\\u0047lobalRva", "176282936")]
    [InlineData("childAngleOffset", "244")]
    [InlineData("hudSubobjectSlotZeroPrologueHex", "\"488D4170C3\"")]
    public void DuplicateNativeGaugePropertiesAreRejected(string propertyName, string rawValue)
    {
        var json = BuiltInDocument().ToJsonString();
        const string marker = "\"nativeGauge\":{";
        var insertion = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        json = json.Insert(insertion, $"\"{propertyName}\":{rawValue},");

        Assert.Throws<FormatException>(() =>
            NativeHudCompatibilityPack.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [MemberData(nameof(NativeGaugeNumericPropertyNames))]
    public void NativeGaugeNumericValuesRequireUnsignedIntegers(string propertyName)
    {
        foreach (var rawJson in new[] { "18446744073709551616", "-1", "null", "\"64\"", "1.5", "true" })
        {
            var document = BuiltInDocument();
            document["nativeGauge"]![propertyName] = JsonNode.Parse(rawJson);
            Assert.Throws<FormatException>(() => Parse(document));
        }
    }

    [Theory]
    [MemberData(nameof(DataRvaPropertyNames))]
    public void NativeGaugeDataRvasRequireAlignedPointerSpansInsideTheImage(string propertyName)
    {
        foreach (var rva in new[] { 0UL, 1UL, ImageSize - 4UL, (ulong)ImageSize, ulong.MaxValue })
        {
            var document = BuiltInDocument();
            document["nativeGauge"]![propertyName] = rva;
            Assert.Throws<FormatException>(() => Parse(document));
        }
    }

    [Theory]
    [MemberData(nameof(DataRvaPropertyNames))]
    public void NativeGaugeObjectRvasCannotAlias(string propertyName)
    {
        var document = BuiltInDocument();
        var other = propertyName == "registryGlobalRva" ? "registryWrapperVtableRva" : "registryGlobalRva";
        document["nativeGauge"]![propertyName] = document["nativeGauge"]![other]!.GetValue<ulong>();

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Fact]
    public void FunctionEntryRvasRemainByteAlignedButCannotAliasGuardedObjects()
    {
        var boundary = BuiltInDocument();
        boundary["nativeGauge"]!["hudSubobjectSlotZeroTargetRva"] = ImageSize - 1UL;
        Assert.Equal(
            ImageSize - 1UL,
            Parse(boundary).NativeGauge!.HudSubobjectSlotZeroTargetRva);

        var alias = BuiltInDocument();
        alias["nativeGauge"]!["hudSubobjectSlotZeroTargetRva"] =
            alias["nativeGauge"]!["registryGlobalRva"]!.GetValue<ulong>();
        Assert.Throws<FormatException>(() => Parse(alias));
    }

    [Theory]
    [MemberData(nameof(NativeGaugeAlignedOffsetPropertyNames))]
    public void NativeGaugeOffsetsRejectMisalignmentAndObjectOverflow(string propertyName)
    {
        var current = BuiltInDocument()["nativeGauge"]![propertyName]!.GetValue<ulong>();

        var misaligned = BuiltInDocument();
        misaligned["nativeGauge"]![propertyName] = current + 1;
        Assert.Throws<FormatException>(() => Parse(misaligned));

        var overflow = BuiltInDocument();
        overflow["nativeGauge"]![propertyName] = 65536UL;
        Assert.Throws<FormatException>(() => Parse(overflow));
    }

    [Fact]
    public void NativeGaugeBooleanOffsetsAreByteAlignedAndObjectBounded()
    {
        foreach (var propertyName in new[]
                 {
                     "childSpeedLessOrEqualOneOffset", "childSpeedLessTenOffset",
                     "childSpeedLessHundredOffset", "childHeadlightsOnOffset", "childUseDriveFor1Offset"
                 })
        {
            var byteAligned = BuiltInDocument();
            byteAligned["nativeGauge"]![propertyName] = 0x13DUL;
            var layout = Parse(byteAligned).NativeGauge!;
            var modelPropertyName = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
            Assert.Equal(
                0x13DUL,
                Assert.IsType<ulong>(typeof(NativeGaugeLayout).GetProperty(modelPropertyName)!.GetValue(layout)));

            var overflow = BuiltInDocument();
            overflow["nativeGauge"]![propertyName] = 65536UL;
            Assert.Throws<FormatException>(() => Parse(overflow));
        }
    }

    [Theory]
    [InlineData("registryContextControlOffset", 0x08UL)]
    [InlineData("registryCountOffset", 0x1F8UL)]
    [InlineData("registryBucketNodeOffset", 0x00UL)]
    [InlineData("registryNodeHashOffset", 0x08UL)]
    [InlineData("hudSubobjectPointerOffset", 0x30UL)]
    [InlineData("hudTypeVectorEndOffset", 0x00UL)]
    [InlineData("hudTypeInstancesBeginOffset", 0x00UL)]
    [InlineData("hudTypeInstanceControlOffset", 0x00UL)]
    [InlineData("outerHudBackReferenceOffset", 0x08UL)]
    [InlineData("childAngleOffset", 0xE4UL)]
    [InlineData("childHeadlightsOnOffset", 0xE4UL)]
    [InlineData("providerPowerDenominatorOffset", 0x1F8UL)]
    public void NativeGaugeFieldsCannotOverlapWithinTheSameObject(string propertyName, ulong offset)
    {
        var document = BuiltInDocument();
        document["nativeGauge"]![propertyName] = offset;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Fact]
    public void NativeGaugeContainingSpansIncludeNestedVectorHeaders()
    {
        var outerOverflow = BuiltInDocument();
        outerOverflow["nativeGauge"]!["hudSubobjectOffset"] = 65528UL;
        Assert.Throws<FormatException>(() => Parse(outerOverflow));

        var innerOverflow = BuiltInDocument();
        innerOverflow["nativeGauge"]!["hudTypeVectorOffset"] = 65512UL;
        Assert.Throws<FormatException>(() => Parse(innerOverflow));
    }

    [Theory]
    [InlineData("registryBucketStride", 8UL)]
    [InlineData("hudTypeVectorEntryStride", 24UL)]
    [InlineData("hudTypeInstanceStride", 8UL)]
    [InlineData("registryBucketStride", 17UL)]
    [InlineData("hudTypeVectorEntryStride", 4097UL)]
    public void NativeGaugeStridesMustContainTheirRecordsAndRemainBounded(
        string propertyName,
        ulong value)
    {
        var document = BuiltInDocument();
        document["nativeGauge"]![propertyName] = value;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData("hudTypeVectorMaximumCount", 0UL)]
    [InlineData("hudTypeVectorMaximumCount", 1025UL)]
    [InlineData("hudTypeInstanceCount", 0UL)]
    [InlineData("hudTypeInstanceCount", 2UL)]
    public void NativeGaugeTraversalCountsAreStrictlyBounded(string propertyName, ulong value)
    {
        var document = BuiltInDocument();
        document["nativeGauge"]![propertyName] = value;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData("")]
    [InlineData("488D4170")]
    [InlineData("488D4170C300")]
    [InlineData("488D4170CG")]
    [InlineData("48 8D 41 70 C3")]
    public void NativeGaugeFunctionPrologueRequiresFiveHexadecimalBytes(string value)
    {
        var document = BuiltInDocument();
        document["nativeGauge"]!["hudSubobjectSlotZeroPrologueHex"] = value;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Fact]
    public void NativeGaugeRegistryHashCannotBeZero()
    {
        var document = BuiltInDocument();
        document["nativeGauge"]!["registryKeyHash"] = 0;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData("speedUnitMphValue", "2147483648")]
    [InlineData("speedUnitKphValue", "2147483648")]
    public void NativeSpeedUnitValuesMustFitTheSignedNativeEnum(string propertyName, string rawJson)
    {
        var document = BuiltInDocument();
        document["nativeGauge"]![propertyName] = JsonNode.Parse(rawJson);

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Fact]
    public void NativeSpeedUnitValuesMustRemainDistinct()
    {
        var document = BuiltInDocument();
        document["nativeGauge"]!["speedUnitKphValue"] =
            document["nativeGauge"]!["speedUnitMphValue"]!.GetValue<uint>();

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData("childlessRegenPowerRatioBits", 0x7FC00000U)]
    [InlineData("childlessRegenPowerRatioBits", 0xBF800000U)]
    [InlineData("childlessRegenPowerRatioBits", 0x40000000U)]
    [InlineData("providerPowerDenominatorScaleBits", 0x00000000U)]
    [InlineData("providerPowerDenominatorScaleBits", 0xBF800000U)]
    [InlineData("providerPowerDenominatorScaleBits", 0x7F800000U)]
    [InlineData("providerRegenScaleBits", 0x00000000U)]
    [InlineData("providerRegenScaleBits", 0x3F800000U)]
    [InlineData("providerRegenScaleBits", 0xFF800000U)]
    [InlineData("providerRegenUpperBaseBits", 0xBF800000U)]
    [InlineData("providerRegenUpperBaseBits", 0x40000000U)]
    [InlineData("providerRegenUpperBaseBits", 0x7FC00000U)]
    public void NativeGaugeFloatBitsMustPreserveReaderSemantics(string propertyName, uint bits)
    {
        var document = BuiltInDocument();
        document["nativeGauge"]![propertyName] = bits;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData("offset", "0")]
    [InlineData("offset", "672")]
    [InlineData("offset", "665")]
    [InlineData("targetRva", "0")]
    [InlineData("targetRva", "188293120")]
    [InlineData("targetRva", "-1")]
    [InlineData("targetRva", "null")]
    public void InvalidNativeGaugeProviderGuardsAreRejected(string propertyName, string rawJson)
    {
        var document = BuiltInDocument();
        document["nativeGauge"]!["requiredProviderVtableSlots"]![0]![propertyName] = JsonNode.Parse(rawJson);

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Fact]
    public void NativeGaugeProviderGuardsCannotBeRemovedDuplicatedExtendedOrAliased()
    {
        var missing = BuiltInDocument();
        missing["nativeGauge"]!["requiredProviderVtableSlots"]!.AsArray().RemoveAt(0);
        Assert.Throws<FormatException>(() => Parse(missing));

        var duplicateOffset = BuiltInDocument();
        duplicateOffset["nativeGauge"]!["requiredProviderVtableSlots"]![1]!["offset"] = 0x0298UL;
        Assert.Throws<FormatException>(() => Parse(duplicateOffset));

        var extra = BuiltInDocument();
        extra["nativeGauge"]!["requiredProviderVtableSlots"]!.AsArray().Add(Slot(0x02A0, 1));
        Assert.Throws<FormatException>(() => Parse(extra));

        var duplicateTarget = BuiltInDocument();
        duplicateTarget["nativeGauge"]!["requiredProviderVtableSlots"]![1]!["targetRva"] =
            duplicateTarget["nativeGauge"]!["requiredProviderVtableSlots"]![0]!["targetRva"]!.GetValue<ulong>();
        Assert.Throws<FormatException>(() => Parse(duplicateTarget));
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("targetRva")]
    public void EveryNativeGaugeProviderGuardPropertyIsRequired(string propertyName)
    {
        var document = BuiltInDocument();
        Assert.True(document["nativeGauge"]!["requiredProviderVtableSlots"]![0]!.AsObject().Remove(propertyName));

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Fact]
    public void NativeGaugeProviderGuardObjectsRejectUnknownAndDuplicateProperties()
    {
        var unknown = BuiltInDocument();
        unknown["nativeGauge"]!["requiredProviderVtableSlots"]![0]!["optional"] = true;
        Assert.Throws<FormatException>(() => Parse(unknown));

        var json = BuiltInDocument().ToJsonString();
        const string marker = "\"requiredProviderVtableSlots\":[{";
        var insertion = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        json = json.Insert(insertion, "\"offset\":664,");
        Assert.Throws<FormatException>(() =>
            NativeHudCompatibilityPack.Parse(Encoding.UTF8.GetBytes(json)));
    }

    public static IEnumerable<object[]> NativeGaugePropertyNames() =>
        BuiltInDocument()["nativeGauge"]!.AsObject().Select(property => new object[] { property.Key });

    public static IEnumerable<object[]> NativeGaugeNumericPropertyNames() =>
        BuiltInDocument()["nativeGauge"]!.AsObject()
            .Where(property => property.Key is not "hudSubobjectSlotZeroPrologueHex" and not "requiredProviderVtableSlots")
            .Select(property => new object[] { property.Key });

    public static IEnumerable<object[]> DataRvaPropertyNames() =>
        DataRvaProperties.Select(property => new object[] { property });

    public static IEnumerable<object[]> NativeGaugeAlignedOffsetPropertyNames() =>
        BuiltInDocument()["nativeGauge"]!.AsObject()
            .Where(property =>
                property.Key.EndsWith("Offset", StringComparison.Ordinal) &&
                property.Key is not (
                    "childSpeedLessOrEqualOneOffset" or "childSpeedLessTenOffset" or
                    "childSpeedLessHundredOffset" or "childHeadlightsOnOffset" or
                    "childUseDriveFor1Offset"))
            .Select(property => new object[] { property.Key });

    private static NativeHudCompatibilityPack Parse(JsonObject document) =>
        NativeHudCompatibilityPack.Parse(JsonSerializer.SerializeToUtf8Bytes(document));

    private static JsonObject BuiltInDocument()
    {
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream(
            "Wisp.NativeCompatibility.BuiltIn.json")!;
        return JsonNode.Parse(stream)!.AsObject();
    }

    private static JsonObject Slot(ulong offset, ulong targetRva) => new()
    {
        ["offset"] = offset,
        ["targetRva"] = targetRva
    };
}
