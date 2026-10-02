using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wisp.Core.Tunes;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class TuneDecoderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [Fact]
    public void CapturedMiataReproducesAll33PublishedDisplayValuesAndIndependentPhysics()
    {
        var snapshot = Decode(Fixture("miata"));
        Assert.True(snapshot.IsComplete);
        var reference = Read<ReferenceRow[]>("miata-reference");
        Assert.Equal(33, reference.Length);
        Assert.Equal(33, snapshot.Fields.Count(field => field.Status == TuneFieldStatus.Available));
        foreach (var row in reference)
        {
            var field = snapshot.Fields.Single(field => field.Id == row.Field);
            Assert.Equal(row.Display, field.DisplayText);
            Assert.Equal(row.NormalizedBits, field.NormalizedBits);
            Assert.Equal(row.RangeBits, BitConverter.SingleToUInt32Bits(field.RangeValue!.Value));
            string converted = row.ReferenceFactor is { } factor
                ? TuneDecoder.FormatNumber(field.RangeValue!.Value * factor, field.DisplayDecimals)
                : field.DisplayText!;
            Assert.Equal(row.Reference, converted);
        }
        foreach (var row in Read<PhysicsRow[]>("miata-physics"))
        {
            Assert.Equal(TuneDecoder.FormatNumber(row.RangeValue * row.Factor, row.Decimals),
                snapshot.Fields.Single(field => field.Id == row.Field).DisplayText);
        }
    }

    [Theory]
    [InlineData("editable", 34, 7)]
    [InlineData("rwd", 25, 1)]
    public void OtherCapturedFixturesRetainTheirOwnApplicability(string fixture, int available, int gears)
    {
        var snapshot = Decode(Fixture(fixture));
        Assert.Equal(available, snapshot.Fields.Count(field => field.Status == TuneFieldStatus.Available));
        Assert.Equal(gears, snapshot.Identity.ForwardGearCount);
        Assert.True(snapshot.IsComplete);
        if (fixture == "editable")
        {
            Assert.Equal("467.6", Field(snapshot, TuneFieldId.FrontSprings).DisplayText);
            Assert.Equal("584.0", Field(snapshot, TuneFieldId.RearSprings).DisplayText);
            Assert.Equal("40.30", Field(snapshot, TuneFieldId.FrontAntiroll).DisplayText);
            Assert.Equal("-1.5", Field(snapshot, TuneFieldId.FrontCamber).DisplayText);
            Assert.Equal(3, snapshot.Parts.Single(part => part.Kind == TunePartId.Brakes).Level);
        }
        else
        {
            Assert.Equal(TuneFieldStatus.NotApplicable, Field(snapshot, TuneFieldId.FrontDiffAcceleration).Status);
            Assert.Equal(TuneFieldStatus.NotApplicable, Field(snapshot, TuneFieldId.CenterDiffBalance).Status);
            Assert.Equal("10", Field(snapshot, TuneFieldId.RearDiffDeceleration).DisplayText);
        }
    }

    [Fact]
    public void InvalidCaptureIdentityNeverProducesASnapshot()
    {
        var source = Fixture("miata");
        Reject(source with { CaptureComplete = false }, TuneDecodeFailure.IncompleteCapture);
        Reject(source with { GameVersion = "unknown" }, TuneDecodeFailure.UnsupportedBuild);
        Reject(source with { ExecutableVerified = false }, TuneDecodeFailure.UnverifiedExecutable);
        Reject(source with { ExecutableSha256 = new string('0', 64) }, TuneDecodeFailure.UnverifiedExecutable);
        Reject(source with { LocalProviderCount = 2 }, TuneDecodeFailure.AmbiguousVehicle);
        Reject(source with { Coherent = false }, TuneDecodeFailure.IncoherentCapture);
        Reject(source with { CarOrdinal = 0 }, TuneDecodeFailure.InvalidIdentity);
        Reject(source with { ObservedGearEntryCount = 12 }, TuneDecodeFailure.InvalidIdentity);
        Reject(source with { Drivetrain = (TuneDrivetrain)99 }, TuneDecodeFailure.InvalidIdentity);
        Reject(source with { NormalizedCopies = [] }, TuneDecodeFailure.InvalidStructure);
    }

    [Fact]
    public void InvalidCopiesRangesPartsAndNumbersAreRejected()
    {
        var source = Fixture("miata");
        uint original = source.NormalizedCopies[1][0x40 / 4];
        var inconsistent = source.NormalizedCopies.SetItem(1, source.NormalizedCopies[1].SetItem(0x40 / 4, original ^ 1));
        Assert.NotEqual(source.NormalizedCopies[0][0x40 / 4], inconsistent[1][0x40 / 4]);
        Reject(source with { NormalizedCopies = inconsistent }, TuneDecodeFailure.IncoherentCapture);
        Reject(ChangeNormalized(source, TuneFieldId.FrontSprings, float.NaN), TuneDecodeFailure.InvalidValue);
        Reject(ChangeNormalized(source, TuneFieldId.FrontSprings, 1.1f), TuneDecodeFailure.InvalidValue);
        Reject(source with { CarRanges = source.CarRanges.SetItem(TuneFieldId.FrontSprings, new(2, 1)) }, TuneDecodeFailure.InvalidRange);
        Reject(source with { CarRanges = source.CarRanges.Remove(TuneFieldId.FrontSprings) }, TuneDecodeFailure.InvalidRange);
        Reject(source with { SpringScale = float.PositiveInfinity }, TuneDecodeFailure.InvalidRange);
        Reject(source with { Parts = source.Parts.RemoveAt(0) }, TuneDecodeFailure.InvalidParts);
        Reject(source with { Parts = source.Parts.SetItem(0, source.Parts[1]) }, TuneDecodeFailure.InvalidParts);
        Reject(source with { Conversions = source.Conversions.SetItem(TuneQuantity.Pressure, new(41, double.NaN, false)) }, TuneDecodeFailure.InvalidConversion);
        Reject(source with { Format = source.Format with { PositiveHalf = .4 } }, TuneDecodeFailure.InvalidFormatter);
    }

    [Fact]
    public void UnresolvedStatesNeverInventValuesAndRemainUnsavable()
    {
        var source = Fixture("miata");
        var sentinel = Decode(ChangeNormalized(source, TuneFieldId.FrontSprings, -1));
        AssertUnavailable(sentinel, TuneFieldId.FrontSprings, TuneFieldStatus.UnresolvedDefault);
        var unknownParts = Decode(source with { PartLevelsResolved = false });
        AssertUnavailable(unknownParts, TuneFieldId.FrontSprings, TuneFieldStatus.UnresolvedPartLevel);
        Assert.Equal(TuneFieldStatus.Available, Field(unknownParts, TuneFieldId.FrontTirePressure).Status);
        var metric = Decode(source with { Conversions = source.Conversions.SetItem(TuneQuantity.SpringRate, new(56, .10197161961595322, false)) });
        AssertUnavailable(metric, TuneFieldId.FrontSprings, TuneFieldStatus.UnsupportedUnit);
        var callback = Decode(source with { Conversions = source.Conversions.SetItem(TuneQuantity.SpringRate, new(54, 1, true)) });
        AssertUnavailable(callback, TuneFieldId.FrontSprings, TuneFieldStatus.UnsupportedConversion);
    }

    [Theory]
    [InlineData(1, TuneFieldStatus.NotAdjustable, TuneFieldStatus.NotAdjustable)]
    [InlineData(2, TuneFieldStatus.Available, TuneFieldStatus.NotAdjustable)]
    [InlineData(3, TuneFieldStatus.Available, TuneFieldStatus.Available)]
    public void SyntheticDifferentialLevelsPreserveAvailabilityWithoutClaimingGameplayCoverage(
        int level, TuneFieldStatus acceleration, TuneFieldStatus deceleration)
    {
        var source = Fixture("miata");
        var snapshot = Decode(ChangePart(source, TunePartId.Differential, level));
        Assert.Equal(acceleration, Field(snapshot, TuneFieldId.RearDiffAcceleration).Status);
        Assert.Equal(deceleration, Field(snapshot, TuneFieldId.RearDiffDeceleration).Status);
        Assert.True(snapshot.IsComplete);
    }

    [Fact]
    public void SyntheticTransmissionAndAeroPredicatesFollowTheVerifiedMenuRules()
    {
        var source = Fixture("miata");
        var transmission = Decode(ChangePart(source, TunePartId.Transmission, 2));
        Assert.Equal(TuneFieldStatus.Available, Field(transmission, TuneFieldId.FinalDrive).Status);
        Assert.Equal(TuneFieldStatus.NotAdjustable, Field(transmission, TuneFieldId.Gear1).Status);
        Assert.Equal(TuneFieldStatus.NotAdjustable, Field(Decode(ChangePart(source, TunePartId.FrontAero, 4)), TuneFieldId.FrontDownforce).Status);
        var fwd = Decode(source with { Drivetrain = TuneDrivetrain.FrontWheelDrive });
        Assert.Equal(TuneFieldStatus.NotApplicable, Field(fwd, TuneFieldId.RearDiffAcceleration).Status);
        Assert.Equal(TuneFieldStatus.NotApplicable, Field(fwd, TuneFieldId.CenterDiffBalance).Status);
    }

    [Theory]
    [InlineData(1.25, 1, "1.3")]
    [InlineData(-1.25, 1, "-1.3")]
    [InlineData(-0.04, 1, "0.0")]
    [InlineData(-0.05, 1, "0.1")]
    [InlineData(-0.06, 1, "-0.1")]
    [InlineData(2.5, 0, "3")]
    public void FormatterPreservesTracedSignedHalfAndTruncation(double value, int digits, string expected) =>
        Assert.Equal(expected, TuneDecoder.FormatNumber(value, digits));

    [Fact]
    public void SnapshotRoundTripsAndRejectsTamperedContent()
    {
        var snapshot = Decode(Fixture("miata"));
        var restored = JsonSerializer.Deserialize<TuneSnapshot>(JsonSerializer.Serialize(snapshot, JsonOptions), JsonOptions)!;
        Assert.True(TuneSnapshotValidator.TryValidate(restored, out var reason), reason);
        Assert.True(TuneComparison.HaveSameSetupIdentity(snapshot, restored));
        var field = snapshot.Fields[0];
        Assert.False(TuneSnapshotValidator.TryValidate(snapshot with { Fields = snapshot.Fields.SetItem(0, field with { DisplayText = "999" }) }, out _));
        Assert.False(TuneSnapshotValidator.TryValidate(snapshot with { Fields = snapshot.Fields.SetItem(0, field with { Status = TuneFieldStatus.NotApplicable }) }, out _));
        Assert.False(TuneSnapshotValidator.TryValidate(snapshot with { Fields = snapshot.Fields.SetItem(0, snapshot.Fields[1]) }, out _));
        Assert.False(TuneSnapshotValidator.TryValidate(snapshot with { Identity = snapshot.Identity with { ReaderVersion = "unknown" } }, out _));
        Assert.False(TuneSnapshotValidator.TryValidate(snapshot with { Fields = default }, out _));
    }

    [Fact]
    public void ComparisonSeparatesRawChangesFromDisplayRoundingAndCaptureTime()
    {
        var input = Fixture("miata");
        var a = Decode(input);
        var recaptured = Decode(input with { CapturedAtUtc = input.CapturedAtUtc.AddHours(1) });
        Assert.True(TuneComparison.HaveSameSetupIdentity(a, recaptured));
        float current = BitConverter.UInt32BitsToSingle(Field(a, TuneFieldId.FrontTirePressure).NormalizedBits);
        var b = Decode(ChangeNormalized(input, TuneFieldId.FrontTirePressure, MathF.BitIncrement(current)));
        var row = TuneComparison.Compare(a, b).Single(row => row.Id == TuneFieldId.FrontTirePressure);
        Assert.Equal(row.A!.DisplayText, row.B!.DisplayText);
        Assert.False(row.RawEqual);
        Assert.False(TuneComparison.HaveSameSetupIdentity(a, b));
        var differentCar = Decode(input with { CarOrdinal = 1 });
        Assert.False(TuneComparison.HaveSameSetupIdentity(a, differentCar));
        var missing = TuneComparison.Compare(a, null);
        Assert.Equal(TuneDecoder.FieldCount, missing.Length);
        Assert.All(missing, row => { Assert.Null(row.B); Assert.Null(row.DisplayDelta); });
    }

    [Fact]
    public void ComparisonUsesCanonicalValuesRatherThanRoundedTextOrUnitFactors()
    {
        var a = Decode(Fixture("miata"));
        var field = Field(a, TuneFieldId.FrontTirePressure);
        var converted = field with
        {
            ConversionFactor = field.ConversionFactor * 2,
            DisplayValue = field.DisplayValue * 2,
            DisplayText = TuneDecoder.FormatNumber(field.DisplayValue!.Value * 2, field.DisplayDecimals)
        };
        var b = a with { Fields = a.Fields.SetItem(0, converted) };
        var row = TuneComparison.Compare(a, b)[0];
        Assert.True(row.RawEqual);
        Assert.Equal(0, row.DisplayDelta);
    }

    private static void AssertUnavailable(TuneSnapshot snapshot, TuneFieldId id, TuneFieldStatus status)
    {
        var field = Field(snapshot, id);
        Assert.Equal(status, field.Status);
        Assert.Null(field.DisplayValue);
        Assert.Null(field.DisplayText);
        Assert.False(snapshot.IsComplete);
    }

    private static TuneField Field(TuneSnapshot snapshot, TuneFieldId id) => snapshot.Fields.Single(field => field.Id == id);
    [Fact]
    public void StoreProvenancePersistsWithoutAnInventedExecutableHash()
    {
        var verification = new TuneVerification(TunePlatform.MicrosoftStore, TuneVerificationProfiles.StoreProfile,
            TuneVerificationProfiles.StoreLayoutSha256, "store-test", 1,
            "Microsoft.ForteBaseGame_3.440.853.0_x64__8wekyb3d8bbwe");
        var input = Fixture("miata") with
        {
            GameVersion = "3.440.853.0",
            ExecutableSha256 = null,
            ExecutableVerified = false,
            Verification = verification
        };
        var snapshot = Decode(input);
        var restored = JsonSerializer.Deserialize<TuneSnapshot>(JsonSerializer.Serialize(snapshot, JsonOptions), JsonOptions);
        Assert.NotNull(restored);
        Assert.True(TuneSnapshotValidator.TryValidate(restored, out _));
        Assert.Null(restored.Identity.ExecutableSha256);
        Assert.Equal(verification, restored.Identity.Verification);
        Assert.Equal(Decode(Fixture("miata")).Fields.ToArray(), restored.Fields.ToArray());
        Reject(input with { ExecutableVerified = true }, TuneDecodeFailure.UnverifiedExecutable);
        Reject(input with { ExecutableSha256 = TuneDecoder.SupportedExecutableSha256 }, TuneDecodeFailure.UnverifiedExecutable);
        Reject(input with { Verification = verification with { PackageFullName = null } }, TuneDecodeFailure.UnverifiedExecutable);
        Reject(input with { Verification = verification with { ProfileId = "unknown" } }, TuneDecodeFailure.UnverifiedExecutable);
        Reject(input with { Verification = verification with { LayoutSha256 = new string('A', 64) } }, TuneDecodeFailure.UnverifiedExecutable);
    }

    [Fact]
    public void VerifiedProfileProvenanceKeepsObservedVersionAndFileHash()
    {
        var verification = new TuneVerification(TunePlatform.Steam, TuneVerificationProfiles.SteamProfile,
            TuneVerificationProfiles.SteamLayoutSha256, "later-reviewed-pack", 2, null);
        var input = Fixture("miata") with
        {
            GameVersion = "6.441.1.0",
            ExecutableSha256 = new string('A', 64),
            Verification = verification
        };
        var snapshot = Decode(input);
        Assert.Equal("6.441.1.0", snapshot.Identity.GameVersion);
        Assert.Equal(new string('A', 64), snapshot.Identity.ExecutableSha256);
        Reject(input with { Verification = null }, TuneDecodeFailure.UnsupportedBuild);
        Reject(input with { Verification = verification with { Platform = TunePlatform.MicrosoftStore } }, TuneDecodeFailure.UnverifiedExecutable);
        Reject(input with { Verification = verification with { CompatibilityRevision = 0 } }, TuneDecodeFailure.UnverifiedExecutable);
    }

    [Fact]
    public void AddingVerificationDetailsDoesNotInvalidateALegacySavedSetupComparison()
    {
        var legacy = Decode(Fixture("miata"));
        var current = Decode(Fixture("miata") with
        {
            Verification = new(TunePlatform.Steam,
            TuneVerificationProfiles.SteamProfile, TuneVerificationProfiles.SteamLayoutSha256, "current", 1, null)
        });
        Assert.True(TuneComparison.HaveSameSetupIdentity(legacy, current));
        Assert.True(TuneSnapshotValidator.TryValidate(legacy, out _));
    }

    [Fact]
    public void OptionalCarNamePersistsButNeverChangesSetupIdentity()
    {
        var legacy = Decode(Fixture("miata"));
        var named = Decode(Fixture("miata") with { CarName = "1994 Mazda MX-5 Miata Forza Edition" });
        Assert.Equal("1994 Mazda MX-5 Miata Forza Edition", named.CarName);
        Assert.True(TuneComparison.HaveSameSetupIdentity(legacy, named));
        var restored = JsonSerializer.Deserialize<TuneSnapshot>(JsonSerializer.Serialize(named, JsonOptions), JsonOptions);
        Assert.Equal(named.CarName, restored!.CarName);
        Assert.True(TuneSnapshotValidator.TryValidate(restored, out _));
        Reject(Fixture("miata") with { CarName = "bad\nname" }, TuneDecodeFailure.InvalidIdentity);
        Assert.False(TuneSnapshotValidator.TryValidate(named with { CarName = new string('x', 201) }, out _));
    }

    [Fact]
    public void DescriptorFingerprintAloneCannotAuthorizeDecodingAndProvenanceSurvivesOfflineStorage()
    {
        var verification = new TuneVerification(TunePlatform.MicrosoftStore, TuneVerificationProfiles.DescriptorProfile,
            new string('B', 64), "reviewed-store-pack", 3, "Microsoft.ForteBaseGame_3.441.1.0_x64__8wekyb3d8bbwe")
        {
            Method = TuneVerificationMethod.AuthenticatedCompatibilityDescriptor,
            SemanticsVersion = 1,
            CompatibilityPackSha256 = new string('C', 64)
        };
        var input = Fixture("miata") with
        {
            GameVersion = "3.441.1.0",
            ExecutableSha256 = null,
            ExecutableVerified = false,
            Verification = verification
        };
        Reject(input, TuneDecodeFailure.UnverifiedExecutable);
        var verified = input with { CompatibilityDescriptorVerified = true };
        var snapshot = Decode(verified);
        var restored = JsonSerializer.Deserialize<TuneSnapshot>(JsonSerializer.Serialize(snapshot, JsonOptions), JsonOptions);
        Assert.True(TuneSnapshotValidator.TryValidate(restored, out _));
        Assert.Equal(verification, restored!.Identity.Verification);
        Reject(verified with { Verification = verification with { ProfileId = "unknown-reader" } }, TuneDecodeFailure.UnverifiedExecutable);
        Reject(verified with { Verification = verification with { SemanticsVersion = 2 } }, TuneDecodeFailure.UnverifiedExecutable);
        Reject(verified with { Verification = verification with { CompatibilityPackSha256 = null } }, TuneDecodeFailure.UnverifiedExecutable);
        Reject(verified with { Verification = verification with { Method = TuneVerificationMethod.KnownProfile } }, TuneDecodeFailure.UnverifiedExecutable);
        Assert.True(TuneComparison.HaveSameSetupIdentity(snapshot, snapshot with
        {
            Identity = snapshot.Identity with
            { Verification = verification with { CompatibilityPackSha256 = new string('D', 64), CompatibilityRevision = 4 } }
        }));
    }

    private static TuneDecodeInput Fixture(string name) => Read<TuneDecodeInput>(name);
    private static T Read<T>(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Wisp.Core.Tests.TuneFixtures.{name}.json");
        Assert.NotNull(stream);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)!;
    }
    private static TuneSnapshot Decode(TuneDecodeInput source)
    {
        Assert.True(TuneDecoder.TryDecode(source, out var snapshot, out var failure), failure.ToString());
        Assert.NotNull(snapshot);
        Assert.True(TuneSnapshotValidator.TryValidate(snapshot, out var reason), reason);
        return snapshot;
    }
    private static void Reject(TuneDecodeInput source, TuneDecodeFailure expected)
    {
        Assert.False(TuneDecoder.TryDecode(source, out var snapshot, out var failure));
        Assert.Null(snapshot);
        Assert.Equal(expected, failure);
    }
    private static TuneDecodeInput ChangeNormalized(TuneDecodeInput source, TuneFieldId id, float value)
    {
        int index = TuneDecoder.Definitions.Single(definition => definition.Id == id).Offset / 4;
        return source with
        {
            NormalizedCopies = source.NormalizedCopies.Select(copy =>
            copy.SetItem(index, BitConverter.SingleToUInt32Bits(value))).ToImmutableArray()
        };
    }
    private static TuneDecodeInput ChangePart(TuneDecodeInput source, TunePartId kind, int level) =>
        source with { Parts = source.Parts.Select(part => part.Kind == kind ? part with { Level = level } : part).ToImmutableArray() };
    private sealed record ReferenceRow(TuneFieldId Field, string Display, string Reference, double? ReferenceFactor,
        uint NormalizedBits, uint RangeBits);
    private sealed record PhysicsRow(TuneFieldId Field, double RangeValue, double Factor, int Decimals);
}
