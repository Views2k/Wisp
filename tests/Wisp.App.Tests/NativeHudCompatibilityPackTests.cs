using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeHudCompatibilityPackTests
{
    private const string ExecutableHash = "B62B5EC1933B2D11A6B80941AE0D2B38C4A5AAEFDD880E487453D178081D7B44";
    private const uint ImageSize = 188_293_120;
    private const long ExecutableLength = 183_853_016;

    [Fact]
    public void ValidPackPreservesEveryGuardAndCurrentTypedField()
    {
        var pack = Parse(ValidDocument());

        Assert.Equal(2, pack.SchemaVersion);
        Assert.Equal(2, pack.ReaderVersion);
        Assert.Equal("fh6-6.430.771.0-r2", pack.Id);
        Assert.Equal(2, pack.Revision);
        Assert.Equal("6.430.771.0", pack.GameVersion);
        Assert.Equal(ExecutableLength, pack.ExecutableLength);
        Assert.Equal(ExecutableHash, pack.ExecutableSha256);
        Assert.Equal(ImageSize, pack.ImageSize);
        Assert.Equal(0x0A8D9A60UL, pack.SourceVectorRva);
        Assert.Equal(0x063C9984UL, pack.ThresholdRva);
        Assert.Equal(0x0678A940UL, pack.LeadVtableRva);
        Assert.Equal(0x3DCCCCCDU, pack.ExpectedThresholdBits);
        Assert.Equal(9, pack.RequiredVtableSlots.Count);
        Assert.Equal(0x01F15590UL, pack.RequiredVtableSlots[0x0210]);
        Assert.Equal(0x01F15E30UL, pack.RequiredVtableSlots[0x1078]);

        var expectedFields = ValidDocument()["fields"]!.AsObject();
        var fieldProperties = typeof(NativeHudFieldLayout).GetProperties();
        Assert.Equal(24, fieldProperties.Length);
        Assert.All(fieldProperties, property =>
        {
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            Assert.Equal(
                expectedFields[jsonName]!.GetValue<ulong>(),
                Assert.IsType<ulong>(property.GetValue(pack.Fields)));
        });
        Assert.Equal(pack.Fields.LcSecondary, pack.Fields.TcrSecondary);
        Assert.Null(pack.NativeGauge);

        var layout = Assert.IsType<NativeGameplayVisibilityLayout>(pack.GameplayVisibility);
        var expectedVisibility = ValidDocument()["gameplayVisibility"]!.AsObject();
        var visibilityProperties = typeof(NativeGameplayVisibilityLayout).GetProperties();
        Assert.Equal(12, visibilityProperties.Length);
        Assert.All(visibilityProperties, property =>
        {
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            Assert.Equal(
                expectedVisibility[jsonName]!.GetValue<ulong>(),
                Assert.IsType<ulong>(property.GetValue(layout)));
        });
    }

    [Fact]
    public void LegacyPackKeepsAllExistingGuardsWithoutClaimingGameplayVisibility()
    {
        var current = Parse(ValidDocument());
        var legacy = Parse(ValidLegacyDocument());

        Assert.Equal(1, legacy.SchemaVersion);
        Assert.Equal(1, legacy.ReaderVersion);
        Assert.Equal(1, legacy.Revision);
        Assert.Null(legacy.GameplayVisibility);
        Assert.Null(legacy.NativeGauge);
        Assert.Equal(current.GameVersion, legacy.GameVersion);
        Assert.Equal(current.ExecutableLength, legacy.ExecutableLength);
        Assert.Equal(current.ExecutableSha256, legacy.ExecutableSha256);
        Assert.Equal(current.ImageSize, legacy.ImageSize);
        Assert.Equal(current.SourceVectorRva, legacy.SourceVectorRva);
        Assert.Equal(current.ThresholdRva, legacy.ThresholdRva);
        Assert.Equal(current.LeadVtableRva, legacy.LeadVtableRva);
        Assert.Equal(current.ExpectedThresholdBits, legacy.ExpectedThresholdBits);
        Assert.Equal(current.RequiredVtableSlots.OrderBy(pair => pair.Key), legacy.RequiredVtableSlots.OrderBy(pair => pair.Key));
        Assert.All(typeof(NativeHudFieldLayout).GetProperties(), property =>
            Assert.Equal(property.GetValue(current.Fields), property.GetValue(legacy.Fields)));
    }

    [Fact]
    public void BundledPackPreservesTheGeneratedCurrentReaderContract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "src", "Wisp.App", "NativeCompatibility", "fh6-6.430.771.0.json");
        var pack = NativeHudCompatibilityPack.Parse(File.ReadAllBytes(path));
        var expected = Parse(ValidDocument());

        Assert.Equal(3, pack.SchemaVersion);
        Assert.Equal(3, pack.ReaderVersion);
        Assert.Equal(5, pack.Revision);
        Assert.Equal(expected.GameVersion, pack.GameVersion);
        Assert.Equal(expected.ExecutableLength, pack.ExecutableLength);
        Assert.Equal(expected.ExecutableSha256, pack.ExecutableSha256);
        Assert.Equal(expected.ImageSize, pack.ImageSize);
        Assert.Equal(expected.SourceVectorRva, pack.SourceVectorRva);
        Assert.Equal(expected.ThresholdRva, pack.ThresholdRva);
        Assert.Equal(expected.LeadVtableRva, pack.LeadVtableRva);
        Assert.Equal(expected.ExpectedThresholdBits, pack.ExpectedThresholdBits);
        Assert.Equal(expected.RequiredVtableSlots.OrderBy(pair => pair.Key), pack.RequiredVtableSlots.OrderBy(pair => pair.Key));
        Assert.All(typeof(NativeHudFieldLayout).GetProperties(), property =>
            Assert.Equal(property.GetValue(expected.Fields), property.GetValue(pack.Fields)));
        Assert.IsType<NativeGameplayVisibilityLayout>(pack.GameplayVisibility);
        Assert.All(typeof(NativeGameplayVisibilityLayout).GetProperties(), property =>
            Assert.Equal(property.GetValue(expected.GameplayVisibility), property.GetValue(pack.GameplayVisibility)));
        Assert.IsType<NativeGaugeLayout>(pack.NativeGauge);
    }

    [Fact]
    public void ParsedModelHasNoPublicMutationPathAndRetainsNoMutableInput()
    {
        var document = ValidDocument();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        var pack = NativeHudCompatibilityPack.Parse(bytes);
        Array.Fill(bytes, (byte)0);
        document["fields"]!["providerRpm"] = 0;
        document["gameplayVisibility"]!["pageUiVisibleOffset"] = 0;
        document["requiredVtableSlots"]!.AsArray().Clear();

        Assert.Equal(0x01B0UL, pack.Fields.ProviderRpm);
        Assert.Equal(0x03C4UL, pack.GameplayVisibility!.PageUiVisibleOffset);
        Assert.Equal(9, pack.RequiredVtableSlots.Count);
        Assert.IsAssignableFrom<FrozenDictionary<ulong, ulong>>(pack.RequiredVtableSlots);
        Assert.All(typeof(NativeHudCompatibilityPack).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.All(typeof(NativeHudFieldLayout).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.All(typeof(NativeGameplayVisibilityLayout).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Empty(typeof(NativeHudCompatibilityPack).GetConstructors());
        Assert.Empty(typeof(NativeHudFieldLayout).GetConstructors());
        Assert.Empty(typeof(NativeGameplayVisibilityLayout).GetConstructors());
    }

    [Fact]
    public void IdentityComparisonRequiresVersionLengthAndHashTogether()
    {
        var document = ValidDocument();
        document["executableSha256"] = ExecutableHash.ToLowerInvariant();
        var pack = Parse(document);

        Assert.Equal(ExecutableHash, pack.ExecutableSha256);
        Assert.True(pack.Matches("6.430.771.0", ExecutableLength, ExecutableHash.ToLowerInvariant()));
        Assert.True(pack.Matches(" 6.430.771.0 ", ExecutableLength, " " + ExecutableHash + " "));
        Assert.False(pack.Matches("6.430.771.1", ExecutableLength, ExecutableHash));
        Assert.False(pack.Matches("6.430.771.0", ExecutableLength + 1, ExecutableHash));
        Assert.False(pack.Matches("6.430.771.0", ExecutableLength, new string('0', 64)));
        Assert.False(pack.Matches(null, ExecutableLength, ExecutableHash));
        Assert.False(pack.Matches("6.430.771.0", ExecutableLength, null));
    }

    [Fact]
    public void FutureBuildIdentityDoesNotRequireACarWhitelist()
    {
        var document = ValidDocument();
        document["id"] = "fh6-future-r2";
        document["revision"] = 2;
        document["gameVersion"] = "6.999.1.0";
        document["executableSha256"] = new string('A', 64);
        var pack = Parse(document);

        Assert.Equal("6.999.1.0", pack.GameVersion);
        Assert.Equal(2, pack.Revision);
        Assert.Equal(0x740CUL, pack.Fields.SourceCarOrdinal);
    }

    [Theory]
    [MemberData(nameof(TopLevelPropertyNames))]
    public void EveryTopLevelPropertyIsRequired(string propertyName)
    {
        foreach (var document in ValidDocuments())
        {
            if (propertyName == "gameplayVisibility" && document["schemaVersion"]!.GetValue<int>() == 1)
            {
                continue;
            }

            Assert.True(document.Remove(propertyName));
            Assert.Throws<FormatException>(() => Parse(document));
        }
    }

    [Theory]
    [MemberData(nameof(FieldPropertyNames))]
    public void EveryTypedFieldIsRequired(string propertyName)
    {
        foreach (var document in ValidDocuments())
        {
            Assert.True(document["fields"]!.AsObject().Remove(propertyName));
            Assert.Throws<FormatException>(() => Parse(document));
        }
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("SchemaVersion")]
    [InlineData("carOrdinals")]
    [InlineData("allowedCars")]
    [InlineData("cars")]
    [InlineData("carList")]
    public void UnknownPropertiesAndCarListsAreRejected(string propertyName)
    {
        foreach (var document in ValidDocuments())
        {
            document[propertyName] = new JsonArray(1, 2, 3);
            Assert.Throws<FormatException>(() => Parse(document));
        }
    }

    [Theory]
    [InlineData("", "id", "\"fh6-duplicate\"")]
    [InlineData("", "\\u0069d", "\"fh6-duplicate\"")]
    [InlineData("", "schemaVersion", "1")]
    [InlineData("", "readerVersion", "1")]
    [InlineData("fields", "sourceProvider", "30528")]
    [InlineData("fields", "source\\u0050rovider", "30528")]
    [InlineData("requiredVtableSlots", "offset", "528")]
    [InlineData("requiredVtableSlots", "targetRva", "32593296")]
    [InlineData("gameplayVisibility", "uiServiceRva", "176558664")]
    [InlineData("gameplayVisibility", "pageUiVisibleOffset", "964")]
    [InlineData("gameplayVisibility", "pageUiVisible\\u004Fffset", "964")]
    public void DuplicatePropertiesAreRejectedAtEveryObjectLevel(string container, string property, string value)
    {
        foreach (var document in ValidDocuments())
        {
            if (container == "gameplayVisibility" && document["schemaVersion"]!.GetValue<int>() == 1)
            {
                continue;
            }

            var json = document.ToJsonString();
            var marker = container.Length == 0 ? "{" : $"\"{container}\":{{";
            if (container == "requiredVtableSlots")
            {
                marker = "\"requiredVtableSlots\":[{";
            }

            var insertion = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            Assert.True(insertion >= marker.Length);
            json = json.Insert(insertion, $"\"{property}\":{value},");

            Assert.Throws<FormatException>(() => NativeHudCompatibilityPack.Parse(Encoding.UTF8.GetBytes(json)));
        }
    }

    [Theory]
    [InlineData("schemaVersion", "0")]
    [InlineData("schemaVersion", "4")]
    [InlineData("schemaVersion", "\"1\"")]
    [InlineData("readerVersion", "0")]
    [InlineData("readerVersion", "3")]
    [InlineData("readerVersion", "null")]
    [InlineData("revision", "0")]
    [InlineData("revision", "-1")]
    [InlineData("revision", "2147483648")]
    [InlineData("revision", "1.5")]
    [InlineData("id", "null")]
    [InlineData("id", "true")]
    [InlineData("gameVersion", "[]")]
    [InlineData("executableLength", "-1")]
    [InlineData("executableLength", "4095")]
    [InlineData("executableLength", "2147483649")]
    [InlineData("executableLength", "9223372036854775808")]
    [InlineData("executableLength", "\"183853016\"")]
    [InlineData("executableSha256", "null")]
    [InlineData("imageSize", "0")]
    [InlineData("imageSize", "4095")]
    [InlineData("imageSize", "1073741825")]
    [InlineData("imageSize", "4294967296")]
    [InlineData("imageSize", "-1")]
    [InlineData("sourceVectorRva", "18446744073709551616")]
    [InlineData("sourceVectorRva", "-1")]
    [InlineData("sourceVectorRva", "1.5")]
    [InlineData("thresholdRva", "\"104634756\"")]
    [InlineData("leadVtableRva", "{}")]
    [InlineData("expectedThresholdBits", "0")]
    [InlineData("expectedThresholdBits", "1036831948")]
    [InlineData("expectedThresholdBits", "4294967296")]
    [InlineData("fields", "[]")]
    [InlineData("requiredVtableSlots", "{}")]
    public void InvalidMetadataAndNumericTypesAreRejected(string propertyName, string rawJson)
    {
        foreach (var document in ValidDocuments())
        {
            document[propertyName] = JsonNode.Parse(rawJson);
            Assert.Throws<FormatException>(() => Parse(document));
        }
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void SchemaAndReaderVersionsMustMatch(int schemaVersion, int readerVersion)
    {
        var document = schemaVersion == 1 ? ValidLegacyDocument() : ValidDocument();
        document["readerVersion"] = readerVersion;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Fact]
    public void LegacyPacksRejectGameplayVisibilityEvenWhenNull()
    {
        foreach (var rawJson in new[] { "null", ValidDocument()["gameplayVisibility"]!.ToJsonString() })
        {
            var document = ValidLegacyDocument();
            document["gameplayVisibility"] = JsonNode.Parse(rawJson);
            Assert.Throws<FormatException>(() => Parse(document));
        }
    }

    [Fact]
    public void VersionOneAndTwoPacksCannotClaimNativeGaugeSupport()
    {
        foreach (var document in ValidDocuments())
        {
            document["nativeGauge"] = new JsonObject();
            Assert.Throws<FormatException>(() => Parse(document));
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("\"layout\"")]
    public void VersionTwoRequiresAVisibilityObject(string rawJson)
    {
        var document = ValidDocument();
        document["gameplayVisibility"] = JsonNode.Parse(rawJson);

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [MemberData(nameof(GameplayVisibilityPropertyNames))]
    public void EveryGameplayVisibilityPropertyIsRequired(string propertyName)
    {
        var document = ValidDocument();
        Assert.True(document["gameplayVisibility"]!.AsObject().Remove(propertyName));

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData("settledState")]
    [InlineData("visibleValue")]
    [InlineData("pageType")]
    [InlineData("UiServiceRva")]
    public void GameplayVisibilityRejectsUnknownPropertiesAndEditableReaderSemantics(string propertyName)
    {
        var document = ValidDocument();
        document["gameplayVisibility"]![propertyName] = 6;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [MemberData(nameof(GameplayVisibilityPropertyNames))]
    public void EveryGameplayVisibilityValueRequiresAnUnsignedInteger(string propertyName)
    {
        foreach (var rawJson in new[] { "18446744073709551616", "-1", "null", "\"64\"", "1.5", "true" })
        {
            var document = ValidDocument();
            document["gameplayVisibility"]![propertyName] = JsonNode.Parse(rawJson);
            Assert.Throws<FormatException>(() => Parse(document));
        }
    }

    [Theory]
    [MemberData(nameof(GameplayVisibilityRvaPropertyNames))]
    public void GameplayVisibilityRvasRequireAnAlignedPointerSpanInsideTheImage(string propertyName)
    {
        foreach (var rva in new[] { 0UL, 1UL, ImageSize - 4UL, (ulong)ImageSize, ulong.MaxValue })
        {
            var document = ValidDocument();
            document["gameplayVisibility"]![propertyName] = rva;
            Assert.Throws<FormatException>(() => Parse(document));
        }

        var boundary = ValidDocument();
        boundary["gameplayVisibility"]![propertyName] = ImageSize - 8UL;
        _ = Parse(boundary);
    }

    [Theory]
    [MemberData(nameof(GameplayVisibilityRvaPropertyNames))]
    public void GameplayVisibilityRvasCannotAliasDifferentObjectIdentities(string propertyName)
    {
        var document = ValidDocument();
        var otherName = propertyName == "uiServiceRva" ? "uiServiceVtableRva" : "uiServiceRva";
        document["gameplayVisibility"]![propertyName] = document["gameplayVisibility"]![otherName]!.GetValue<ulong>();

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [MemberData(nameof(GameplayVisibilityOffsetPropertyNames))]
    public void GameplayVisibilityFieldsRejectObjectOverflowAndVtableOverlap(string propertyName)
    {
        foreach (var offset in new[] { 0UL, 4UL, 7UL, 65536UL, ulong.MaxValue })
        {
            var document = ValidDocument();
            document["gameplayVisibility"]![propertyName] = offset;
            Assert.Throws<FormatException>(() => Parse(document));
        }
    }

    [Theory]
    [InlineData("serviceDependencyOffset", 0xA4UL)]
    [InlineData("rootTransitionManagerOffset", 0x3CUL)]
    [InlineData("managerOwnerOffset", 0xC4UL)]
    [InlineData("managerCurrentPageOffset", 0x94UL)]
    [InlineData("managerStateOffset", 0x69UL)]
    [InlineData("pageTransitionManagerOffset", 0x294UL)]
    public void GameplayVisibilityFieldAlignmentFollowsItsScalarType(string propertyName, ulong offset)
    {
        var document = ValidDocument();
        document["gameplayVisibility"]![propertyName] = offset;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData("managerOwnerOffset", 0x90UL)]
    [InlineData("managerCurrentPageOffset", 0xC0UL)]
    [InlineData("managerStateOffset", 0x90UL)]
    [InlineData("managerStateOffset", 0x94UL)]
    [InlineData("managerOwnerOffset", 0x68UL)]
    [InlineData("pageUiVisibleOffset", 0x290UL)]
    [InlineData("pageUiVisibleOffset", 0x297UL)]
    [InlineData("pageTransitionManagerOffset", 0x3C0UL)]
    public void GameplayVisibilityFieldsCannotOverlapWithinAnObject(string propertyName, ulong offset)
    {
        var document = ValidDocument();
        document["gameplayVisibility"]![propertyName] = offset;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Fact]
    public void GameplayVisibilityFieldSpansIncludeTheEntireInlineManager()
    {
        foreach (var (name, lastValidOffset, overflowOffset) in new (string, ulong, ulong)[]
                 {
                     ("serviceDependencyOffset", 65528, 65536),
                     ("rootTransitionManagerOffset", 65536 - 0xC8, 65536 - 0xC0),
                     ("managerOwnerOffset", 65536 - 0x38 - 8, 65536 - 0x38),
                     ("managerCurrentPageOffset", 65536 - 0x38 - 8, 65536 - 0x38),
                     ("managerStateOffset", 65536 - 0x38 - 4, 65536 - 0x38),
                     ("pageTransitionManagerOffset", 65528, 65536),
                     ("pageUiVisibleOffset", 65535, 65536)
                 })
        {
            var boundary = ValidDocument();
            boundary["gameplayVisibility"]![name] = lastValidOffset;
            _ = Parse(boundary);

            var overflow = ValidDocument();
            overflow["gameplayVisibility"]![name] = overflowOffset;
            Assert.Throws<FormatException>(() => Parse(overflow));
        }
    }

    [Fact]
    public void GameplayVisibilityOffsetsMayBeReusedOnlyAcrossSeparateObjects()
    {
        var document = ValidDocument();
        document["gameplayVisibility"]!["serviceDependencyOffset"] = 0xC0UL;
        document["gameplayVisibility"]!["pageTransitionManagerOffset"] = 0xC0UL;
        document["gameplayVisibility"]!["rootTransitionManagerOffset"] = 0xC0UL;
        document["gameplayVisibility"]!["pageUiVisibleOffset"] = 0xC9UL;
        var layout = Parse(document).GameplayVisibility!;

        Assert.Equal(layout.ManagerOwnerOffset, layout.ServiceDependencyOffset);
        Assert.Equal(layout.ManagerOwnerOffset, layout.RootTransitionManagerOffset);
        Assert.Equal(layout.ManagerOwnerOffset, layout.PageTransitionManagerOffset);
        Assert.Equal(0xC9UL, layout.PageUiVisibleOffset);
    }

    [Fact]
    public void RootAndManagerOffsetsShareOneContainingObjectBudget()
    {
        var document = ValidDocument();
        document["gameplayVisibility"]!["rootTransitionManagerOffset"] = 0x8000UL;
        document["gameplayVisibility"]!["managerOwnerOffset"] = 0x7FF8UL;
        _ = Parse(document);

        // Each offset fits independently; the second owner span exceeds the
        // dependency object's budget only after adding the inline-manager base.
        document["gameplayVisibility"]!["managerOwnerOffset"] = 0x8000UL;
        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../pack")]
    [InlineData("pack/child")]
    [InlineData("pack\\child")]
    [InlineData("pack:stream")]
    [InlineData("pack id")]
    [InlineData("pack\n")]
    [InlineData("pack.")]
    [InlineData("pack..id")]
    [InlineData(".pack")]
    [InlineData("CON")]
    [InlineData("com1.json")]
    [InlineData("LPT9")]
    [InlineData("NUL.json")]
    public void UnsafeIdsAreRejected(string id)
    {
        var document = ValidDocument();
        document["id"] = id;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Fact]
    public void IdLengthIsBounded()
    {
        var document = ValidDocument();
        document["id"] = new string('a', 80);
        Assert.Equal(80, Parse(document).Id.Length);
        document["id"] = new string('a', 81);
        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData("6.430.771")]
    [InlineData("6.430.771.0.1")]
    [InlineData("6..771.0")]
    [InlineData("6.430.771.-1")]
    [InlineData("6.430.771.+1")]
    [InlineData("6.430.771.65536")]
    [InlineData("6.430.771.0 ")]
    [InlineData("6.430.771.0-beta")]
    public void InvalidGameVersionsAreRejected(string version)
    {
        var document = ValidDocument();
        document["gameVersion"] = version;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData(0, 'A')]
    [InlineData(63, 'A')]
    [InlineData(65, 'A')]
    [InlineData(64, 'G')]
    [InlineData(64, ' ')]
    public void MalformedExecutableHashesAreRejected(int length, char digit)
    {
        var document = ValidDocument();
        document["executableSha256"] = new string(digit, length);

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [MemberData(nameof(FieldPropertyNames))]
    public void EveryFieldRejectsUnboundedOffsetsAndNonIntegralTypes(string propertyName)
    {
        foreach (var rawJson in new[] { "65536", "18446744073709551615", "-1", "null", "\"64\"", "1.5" })
        {
            var document = ValidDocument();
            document["fields"]![propertyName] = JsonNode.Parse(rawJson);
            Assert.Throws<FormatException>(() => Parse(document));
        }
    }

    [Theory]
    [InlineData("sourceProvider", 0x7744UL)]
    [InlineData("sourceCarOrdinal", 0x740DUL)]
    [InlineData("providerRpm", 0x01B1UL)]
    [InlineData("providerSimRedlineAngularVelocity", 0x0249UL)]
    [InlineData("firstWheelPointer", 0x0BA4UL)]
    [InlineData("secondWheelPointer", 0x0BACUL)]
    [InlineData("thirdWheelPointer", 0x0BB4UL)]
    [InlineData("tcrWheelValues", 0xC2CAUL)]
    [InlineData("wheelId", 0x05A1UL)]
    public void FieldAlignmentFollowsItsScalarType(string propertyName, ulong offset)
    {
        var document = ValidDocument();
        document["fields"]![propertyName] = offset;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData("sourceCarOrdinal", 0x7744UL)]
    [InlineData("sourceProvider", 0x7408UL)]
    [InlineData("providerRpm", 0x0248UL)]
    [InlineData("localPlayerFlag", 0x0BA4UL)]
    [InlineData("lcAvailable", 0x17B4UL)]
    [InlineData("tcrWheelValues", 0xC218UL)]
    [InlineData("tcrPrimary", 0xC2D4UL)]
    [InlineData("firstWheelPointer", 0x0BA8UL)]
    [InlineData("lcSecondary", 0xC224UL)]
    [InlineData("providerRpm", 0UL)]
    [InlineData("localPlayerFlag", 7UL)]
    public void FieldsCannotOverlapOtherFieldsArraysOrTheProviderVtable(string propertyName, ulong offset)
    {
        var document = ValidDocument();
        document["fields"]![propertyName] = offset;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Fact]
    public void FieldBoundsIncludeTheCompleteReadWidth()
    {
        foreach (var (name, lastValidOffset) in new (string, ulong)[]
                 {
                     ("sourceProvider", 65528), ("sourceCarOrdinal", 65532),
                     ("providerRpm", 65532), ("localPlayerFlag", 65535),
                     ("tcrWheelValues", 65520), ("wheelId", 65532)
                 })
        {
            var document = ValidDocument();
            document["fields"]![name] = lastValidOffset;
            _ = Parse(document);
        }

        var invalidArray = ValidDocument();
        invalidArray["fields"]!["tcrWheelValues"] = 65524UL;
        Assert.Throws<FormatException>(() => Parse(invalidArray));
    }

    [Fact]
    public void DifferentObjectsMayReuseOffsetsAndOnlyTheKnownSecondaryFieldsMayAlias()
    {
        var document = ValidDocument();
        document["fields"]!["sourceProvider"] = 0x0BA0UL;
        document["fields"]!["sourceCarOrdinal"] = 0x01B0UL;
        document["fields"]!["wheelId"] = 0x01B0UL;
        document["fields"]!["lcSecondary"] = 0xC300UL;
        document["fields"]!["tcrSecondary"] = 0xC300UL;
        var pack = Parse(document);

        Assert.Equal(pack.Fields.ProviderRpm, pack.Fields.SourceCarOrdinal);
        Assert.Equal(pack.Fields.ProviderRpm, pack.Fields.WheelId);
        Assert.Equal(pack.Fields.LcSecondary, pack.Fields.TcrSecondary);
    }

    [Theory]
    [MemberData(nameof(InvalidImageSpans))]
    public void EveryRvaMustKeepItsWholeReadInsideTheImage(string propertyName, ulong rva)
    {
        var document = ValidDocument();
        document[propertyName] = rva;

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Fact]
    public void ImageSpansMayEndExactlyAtTheImageBoundary()
    {
        foreach (var (name, width) in new (string, ulong)[]
                 {
                     ("sourceVectorRva", 24), ("thresholdRva", 4), ("leadVtableRva", 0x1080)
                 })
        {
            var document = ValidDocument();
            document[name] = ImageSize - width;
            _ = Parse(document);
        }
    }

    [Theory]
    [InlineData("offset", "0")]
    [InlineData("offset", "536")]
    [InlineData("offset", "529")]
    [InlineData("offset", "18446744073709551615")]
    [InlineData("offset", "-1")]
    [InlineData("offset", "\"528\"")]
    [InlineData("targetRva", "0")]
    [InlineData("targetRva", "268435456")]
    [InlineData("targetRva", "18446744073709551615")]
    [InlineData("targetRva", "-1")]
    [InlineData("targetRva", "1.5")]
    [InlineData("targetRva", "null")]
    public void InvalidVtableOffsetsAndTargetsAreRejected(string propertyName, string rawJson)
    {
        var document = ValidDocument();
        document["requiredVtableSlots"]![0]![propertyName] = JsonNode.Parse(rawJson);

        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Fact]
    public void VtableGuardsCannotBeRemovedDuplicatedOrExtended()
    {
        var missing = ValidDocument();
        missing["requiredVtableSlots"]!.AsArray().RemoveAt(0);
        Assert.Throws<FormatException>(() => Parse(missing));

        var duplicate = ValidDocument();
        duplicate["requiredVtableSlots"]![1]!["offset"] = 0x0210UL;
        Assert.Throws<FormatException>(() => Parse(duplicate));

        var extra = ValidDocument();
        extra["requiredVtableSlots"]!.AsArray().Add(Slot(0x0218, 0x01F15590));
        Assert.Throws<FormatException>(() => Parse(extra));
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("targetRva")]
    public void EveryVtableGuardPropertyIsRequired(string propertyName)
    {
        foreach (var document in ValidDocuments())
        {
            Assert.True(document["requiredVtableSlots"]![0]!.AsObject().Remove(propertyName));
            Assert.Throws<FormatException>(() => Parse(document));
        }
    }

    [Fact]
    public void NestedUnknownPropertiesAreRejected()
    {
        foreach (var document in ValidDocuments())
        {
            var fieldDocument = document.DeepClone().AsObject();
            fieldDocument["fields"]!["unsupportedField"] = 64;
            Assert.Throws<FormatException>(() => Parse(fieldDocument));

            var slotDocument = document.DeepClone().AsObject();
            slotDocument["requiredVtableSlots"]![0]!["optional"] = true;
            Assert.Throws<FormatException>(() => Parse(slotDocument));
        }
    }

    [Fact]
    public void FunctionEntryTargetsDoNotRequirePointerOrSixteenByteAlignment()
    {
        var document = ValidDocument();
        document["requiredVtableSlots"]![0]!["targetRva"] = 0x01F15591UL;
        document["requiredVtableSlots"]![1]!["targetRva"] = ImageSize - 1UL;
        var pack = Parse(document);

        Assert.Equal(0x01F15591UL, pack.RequiredVtableSlots[0x0210]);
        Assert.Equal(ImageSize - 1UL, pack.RequiredVtableSlots[0x02A8]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("\"text\"")]
    [InlineData("[[[[[[0]]]]]]")]
    public void InvalidDocumentsAlwaysFailAsFormatErrors(string json)
    {
        Assert.Throws<FormatException>(() => NativeHudCompatibilityPack.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void TrailingJsonCommentsInvalidUtf16AndTrailingCommasAreRejected()
    {
        var json = ValidDocument().ToJsonString();
        foreach (var invalid in new[]
                 {
                     json + "{}", "/*comment*/" + json, json[..^1] + ",}",
                     json.Replace("fh6-6.430.771.0-r2", "\\uD800", StringComparison.Ordinal),
                     json.Replace("\"id\"", "\"\\uD800\"", StringComparison.Ordinal)
                 })
        {
            Assert.Throws<FormatException>(() => NativeHudCompatibilityPack.Parse(Encoding.UTF8.GetBytes(invalid)));
        }
    }

    [Fact]
    public void JsonByteLimitIsEnforcedBeforeParsing()
    {
        var json = ValidDocument().ToJsonString();
        var maximum = Encoding.UTF8.GetBytes(json.PadRight(NativeHudCompatibilityPack.MaximumJsonBytes));
        Assert.Equal(65536, maximum.Length);
        Assert.Equal(2, NativeHudCompatibilityPack.Parse(maximum).SchemaVersion);

        var oversized = new byte[maximum.Length + 1];
        maximum.CopyTo(oversized, 0);
        oversized[^1] = (byte)' ';
        Assert.Throws<FormatException>(() => NativeHudCompatibilityPack.Parse(oversized));
    }

    public static IEnumerable<object[]> InvalidImageSpans()
    {
        foreach (var propertyName in new[] { "sourceVectorRva", "thresholdRva", "leadVtableRva" })
        {
            foreach (var rva in new[] { 0UL, (ulong)ImageSize, ulong.MaxValue, 1UL })
            {
                yield return new object[] { propertyName, rva };
            }
        }

        yield return new object[] { "sourceVectorRva", ImageSize - 16UL };
        yield return new object[] { "thresholdRva", ImageSize - 2UL };
        yield return new object[] { "leadVtableRva", ImageSize - 0x1078UL };
    }

    public static IEnumerable<object[]> TopLevelPropertyNames() =>
        ValidDocument().Select(property => new object[] { property.Key });

    public static IEnumerable<object[]> FieldPropertyNames() =>
        ValidDocument()["fields"]!.AsObject().Select(property => new object[] { property.Key });

    public static IEnumerable<object[]> GameplayVisibilityPropertyNames() =>
        ValidDocument()["gameplayVisibility"]!.AsObject().Select(property => new object[] { property.Key });

    public static IEnumerable<object[]> GameplayVisibilityRvaPropertyNames() =>
        GameplayVisibilityPropertyNames().Where(values => ((string)values[0]).EndsWith("Rva", StringComparison.Ordinal));

    public static IEnumerable<object[]> GameplayVisibilityOffsetPropertyNames() =>
        GameplayVisibilityPropertyNames().Where(values => ((string)values[0]).EndsWith("Offset", StringComparison.Ordinal));

    private static IEnumerable<JsonObject> ValidDocuments()
    {
        yield return ValidDocument();
        yield return ValidLegacyDocument();
    }

    private static JsonObject ValidLegacyDocument()
    {
        var document = ValidDocument();
        document["schemaVersion"] = 1;
        document["readerVersion"] = 1;
        document["id"] = "fh6-6.430.771.0-r1";
        document["revision"] = 1;
        document.Remove("gameplayVisibility");
        return document;
    }

    private static NativeHudCompatibilityPack Parse(JsonObject document) =>
        NativeHudCompatibilityPack.Parse(JsonSerializer.SerializeToUtf8Bytes(document));

    private static JsonObject ValidDocument() => new()
    {
        ["schemaVersion"] = 2,
        ["readerVersion"] = 2,
        ["id"] = "fh6-6.430.771.0-r2",
        ["revision"] = 2,
        ["gameVersion"] = "6.430.771.0",
        ["executableLength"] = ExecutableLength,
        ["executableSha256"] = ExecutableHash,
        ["imageSize"] = ImageSize,
        ["sourceVectorRva"] = 0x0A8D9A60UL,
        ["thresholdRva"] = 0x063C9984UL,
        ["leadVtableRva"] = 0x0678A940UL,
        ["expectedThresholdBits"] = 0x3DCCCCCDU,
        ["fields"] = new JsonObject
        {
            ["sourceProvider"] = 0x7740UL,
            ["sourceCarOrdinal"] = 0x740CUL,
            ["providerRpm"] = 0x01B0UL,
            ["providerSimRedlineAngularVelocity"] = 0x0248UL,
            ["providerTachometerMaximumAngularVelocity"] = 0x024CUL,
            ["localPlayerFlag"] = 0x1464UL,
            ["localPlayerProviderFlag"] = 0xC330UL,
            ["stmState"] = 0x1430UL,
            ["absState"] = 0x1434UL,
            ["stmAvailable"] = 0x17B4UL,
            ["tcrAvailable"] = 0x17B5UL,
            ["absAvailable"] = 0x17B6UL,
            ["lcAvailable"] = 0x17B7UL,
            ["lcPrimary"] = 0x14ECUL,
            ["lcMode"] = 0x1F7CUL,
            ["lcSecondary"] = 0xC220UL,
            ["tcrSecondary"] = 0xC220UL,
            ["tcrPrimary"] = 0xC224UL,
            ["tcrTertiary"] = 0xC228UL,
            ["tcrWheelValues"] = 0xC2C8UL,
            ["firstWheelPointer"] = 0x0BA0UL,
            ["secondWheelPointer"] = 0x0BA8UL,
            ["thirdWheelPointer"] = 0x0BB0UL,
            ["wheelId"] = 0x05A0UL
        },
        ["gameplayVisibility"] = new JsonObject
        {
            ["uiServiceRva"] = 0x0A861248UL,
            ["uiServiceVtableRva"] = 0x066BC8F8UL,
            ["dependencyVtableRva"] = 0x066A8A00UL,
            ["transitionManagerVtableRva"] = 0x066A8990UL,
            ["hudPageVtableRva"] = 0x06E849E8UL,
            ["serviceDependencyOffset"] = 0xA0UL,
            ["rootTransitionManagerOffset"] = 0x38UL,
            ["managerOwnerOffset"] = 0xC0UL,
            ["managerCurrentPageOffset"] = 0x90UL,
            ["managerStateOffset"] = 0x68UL,
            ["pageTransitionManagerOffset"] = 0x290UL,
            ["pageUiVisibleOffset"] = 0x3C4UL
        },
        ["requiredVtableSlots"] = new JsonArray
        {
            Slot(0x0210, 0x01F15590),
            Slot(0x02A8, 0x01F19EB0),
            Slot(0x02B0, 0x01F19040),
            Slot(0x02B8, 0x01F12730),
            Slot(0x0680, 0x01F15580),
            Slot(0x1058, 0x01F198C0),
            Slot(0x1060, 0x01F11FB0),
            Slot(0x1068, 0x01F15DA0),
            Slot(0x1078, 0x01F15E30)
        }
    };

    private static JsonObject Slot(ulong offset, ulong targetRva) => new()
    {
        ["offset"] = offset,
        ["targetRva"] = targetRva
    };
}
