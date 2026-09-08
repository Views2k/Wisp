using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeCompatibilityCatalogTests
{
    [Fact]
    public async Task RuntimeImportUsesTheSignedCatalogAndDoesNotChangeItsSourceFile()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog();
        var source = Path.Combine(fixture.CacheDirectory, "user-selected.json");
        var bytes = fixture.Envelope();
        File.WriteAllBytes(source, bytes);
        var writtenAt = File.GetLastWriteTimeUtc(source);

        var result = await NativeCompatibilityRuntime.ImportFileAsync(
            catalog, source, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(1, catalog.Generation);
        Assert.NotNull(Find(catalog, fixture.ParsePack()));
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(source));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(NativeCompatibilityEnvelope.MaximumEnvelopeBytes + 1)]
    public async Task RuntimeImportRejectsEmptyMalformedAndOversizedFiles(int length)
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog();
        var source = Path.Combine(fixture.CacheDirectory, "invalid.json");
        File.WriteAllBytes(source, new byte[length]);

        var result = await NativeCompatibilityRuntime.ImportFileAsync(
            catalog, source, TestContext.Current.CancellationToken);

        Assert.Equal(NativeCompatibilityInstallCode.InvalidEnvelope, result.Code);
        Assert.False(result.Success);
        Assert.Equal(0, catalog.Generation);
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(catalog, NativeHudBuildContract.BuiltIn));
    }

    [Fact]
    public async Task RuntimeImportCancellationCannotPublishAPack()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog();
        var source = Path.Combine(fixture.CacheDirectory, "cancelled.json");
        File.WriteAllBytes(source, fixture.Envelope());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeCompatibilityRuntime.ImportFileAsync(catalog, source, cancellation.Token));

        Assert.Equal(0, catalog.Generation);
    }

    [Fact]
    public async Task RuntimeImportReadFailureIsSanitizedAndLeavesTheCatalogUnchanged()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog();
        var result = await NativeCompatibilityRuntime.ImportFileAsync(
            catalog,
            Path.Combine(fixture.CacheDirectory, "does-not-exist.json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(NativeCompatibilityInstallCode.CacheUnavailable, result.Code);
        Assert.DoesNotContain(fixture.CacheDirectory, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, catalog.Generation);
    }

    [Fact]
    public async Task UnconfiguredRuntimeCannotImportAnUntrustedFile()
    {
        var catalog = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, null,
            new Dictionary<string, byte[]>());
        var result = await NativeCompatibilityRuntime.ImportFileAsync(
            catalog, "\0invalid-file", TestContext.Current.CancellationToken);

        Assert.Equal(NativeCompatibilityInstallCode.UntrustedPublisher, result.Code);
        Assert.Equal(0, catalog.Generation);
        Assert.Equal(new Uri("https://wispoverlay.com/compatibility/latest.json"), NativeCompatibilityRuntime.PublisherEndpoint);
        Assert.True(NativeCompatibilityRuntime.Catalog.HasTrustedPublishers);
    }

    [Fact]
    public void OfflineBuiltInNeedsNeitherPublisherNorCacheAndRequiresExactFingerprint()
    {
        var builtIn = NativeHudBuildContract.BuiltIn;
        var catalog = new NativeCompatibilityCatalog(builtIn, null, new Dictionary<string, byte[]>());

        Assert.Same(builtIn, Find(catalog, builtIn));
        Assert.Same(builtIn, catalog.Find(" " + builtIn.GameVersion + " ", builtIn.ExecutableLength,
            builtIn.ExecutableSha256.ToLowerInvariant()));
        Assert.Null(catalog.Find("6.999.999.0", builtIn.ExecutableLength, builtIn.ExecutableSha256));
        Assert.Null(catalog.Find(builtIn.GameVersion, builtIn.ExecutableLength + 1, builtIn.ExecutableSha256));
        Assert.Null(catalog.Find(builtIn.GameVersion, builtIn.ExecutableLength, new string('0', 64)));
        Assert.Null(catalog.Find(null, 0, null));
        Assert.Equal(0, catalog.Generation);
        Assert.False(catalog.CacheLoadHadErrors);
    }

    [Fact]
    public void ValidFreshPackInstallsAsImmutableExactBuildSnapshot()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog();
        var result = catalog.Install(fixture.Envelope(), fixture.Now);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(NativeCompatibilityInstallCode.Installed, result.Code);
        Assert.NotNull(result.Pack);
        Assert.Same(result.Pack, Find(catalog, result.Pack));
        Assert.Null(catalog.Find(result.Pack.GameVersion, result.Pack.ExecutableLength + 1, result.Pack.ExecutableSha256));
        Assert.Null(catalog.Find("6.999.999.0", result.Pack.ExecutableLength, result.Pack.ExecutableSha256));
        Assert.Null(catalog.Find(result.Pack.GameVersion, result.Pack.ExecutableLength, new string('B', 64)));
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(catalog, NativeHudBuildContract.BuiltIn));
        Assert.Equal(1, catalog.Generation);
    }

    [Fact]
    public void ReinstallingSameSignedPayloadIsIdempotent()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog();
        var bytes = fixture.Envelope();
        Assert.True(catalog.Install(bytes, fixture.Now).Changed);

        var again = catalog.Install(bytes, fixture.Now.AddSeconds(1));

        Assert.True(again.Success);
        Assert.False(again.Changed);
        Assert.Equal(NativeCompatibilityInstallCode.AlreadyInstalled, again.Code);
        Assert.Equal(1, catalog.Generation);
    }

    [Fact]
    public void PayloadTamperingCannotChangeTheExistingCatalog()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog();
        var envelope = JsonNode.Parse(fixture.Envelope())!.AsObject();
        var payload = JsonNode.Parse(Convert.FromBase64String(envelope["payload"]!.GetValue<string>()))!.AsObject();
        payload["pack"]!["revision"] = 9;
        envelope["payload"] = Convert.ToBase64String(Bytes(payload));

        var result = catalog.Install(Bytes(envelope), fixture.Now);

        Assert.False(result.Success);
        Assert.Equal(NativeCompatibilityInstallCode.InvalidSignature, result.Code);
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(catalog, NativeHudBuildContract.BuiltIn));
        Assert.Equal(0, catalog.Generation);
    }

    [Fact]
    public void SignatureTamperingAndUnpinnedPublisherAreRejected()
    {
        using var fixture = new Fixture();
        using var other = new Fixture();
        var catalog = fixture.Catalog();
        var envelope = JsonNode.Parse(fixture.Envelope())!.AsObject();
        var signature = Convert.FromBase64String(envelope["signature"]!.GetValue<string>());
        signature[10] ^= 0x40;
        envelope["signature"] = Convert.ToBase64String(signature);

        Assert.Equal(NativeCompatibilityInstallCode.InvalidSignature, catalog.Install(Bytes(envelope), fixture.Now).Code);
        Assert.Equal(NativeCompatibilityInstallCode.UntrustedPublisher, catalog.Install(other.Envelope(), other.Now).Code);
        Assert.Equal(0, catalog.Generation);
    }

    [Fact]
    public void ForgingTrustedKeyIdDoesNotMakeAnotherKeysSignatureValid()
    {
        using var fixture = new Fixture();
        using var other = new Fixture();
        var envelope = JsonNode.Parse(other.Envelope())!.AsObject();
        envelope["keyId"] = fixture.KeyId;

        Assert.Equal(NativeCompatibilityInstallCode.InvalidSignature,
            fixture.Catalog().Install(Bytes(envelope), fixture.Now).Code);
    }

    [Fact]
    public void SignatureRequiresTheExactDomainAndP1363Encoding()
    {
        using var fixture = new Fixture();
        var payload = Bytes(fixture.Payload());
        var noDomain = fixture.SignRaw(payload, includeDomain: false);
        var der = fixture.SignRaw(payload, signatureFormat: DSASignatureFormat.Rfc3279DerSequence);

        Assert.Equal("Wisp.NativeHud.Compatibility/v1\0", NativeCompatibilitySignature.DomainPrefix);
        var expected = Encoding.ASCII.GetBytes("Wisp.NativeHud.Compatibility/v1\0").Concat(payload).ToArray();
        Assert.Equal(expected, NativeCompatibilitySignature.CreateSigningInput(payload));
        Assert.Equal(NativeCompatibilityInstallCode.InvalidSignature, fixture.Catalog().Install(noDomain, fixture.Now).Code);
        Assert.Equal(NativeCompatibilityInstallCode.InvalidEnvelope, fixture.Catalog().Install(der, fixture.Now).Code);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.PublicKey)), fixture.KeyId);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("format-version")]
    [InlineData("format-string")]
    [InlineData("key-id")]
    [InlineData("base64-space")]
    [InlineData("short-signature")]
    [InlineData("null-signature")]
    public void StrictEnvelopeRejectsAmbiguousOrUnsupportedJson(string mutation)
    {
        using var fixture = new Fixture();
        var node = JsonNode.Parse(fixture.Envelope())!.AsObject();
        switch (mutation)
        {
            case "unknown": node["extra"] = 1; break;
            case "missing": node.Remove("signature"); break;
            case "format-version": node["format"] = 2; break;
            case "format-string": node["format"] = "1"; break;
            case "key-id": node["keyId"] = "../not-a-key"; break;
            case "base64-space": node["payload"] = " " + node["payload"]!.GetValue<string>(); break;
            case "short-signature": node["signature"] = Convert.ToBase64String(new byte[63]); break;
            case "null-signature": node["signature"] = null; break;
        }

        var json = node.ToJsonString();
        if (mutation == "duplicate")
        {
            json = "{\"format\":1," + json[1..];
        }

        var result = fixture.Catalog().Install(Encoding.UTF8.GetBytes(json), fixture.Now);
        Assert.False(result.Success);
        Assert.Equal(NativeCompatibilityInstallCode.InvalidEnvelope, result.Code);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("purpose")]
    [InlineData("format")]
    [InlineData("pack-type")]
    [InlineData("pack-unknown")]
    [InlineData("pack-missing")]
    [InlineData("pack-duplicate")]
    [InlineData("field-duplicate")]
    [InlineData("field-unknown")]
    [InlineData("slot-duplicate")]
    public void ValidSignatureStillRequiresStrictPayloadAndNestedPack(string mutation)
    {
        using var fixture = new Fixture();
        var payload = fixture.Payload();
        switch (mutation)
        {
            case "unknown": payload["extra"] = 1; break;
            case "missing": payload.Remove("expiresUtc"); break;
            case "purpose": payload["purpose"] = "another-product"; break;
            case "format": payload["format"] = 2; break;
            case "pack-type": payload["pack"] = "not-a-pack"; break;
            case "pack-unknown": payload["pack"]!["downloadUrl"] = "unsupported"; break;
            case "pack-missing": payload["pack"]!.AsObject().Remove("fields"); break;
            case "field-unknown": payload["pack"]!["fields"]!["unsupported"] = 8; break;
        }

        var json = payload.ToJsonString();
        json = mutation switch
        {
            "duplicate" => "{\"format\":1," + json[1..],
            "pack-duplicate" => json.Replace("\"pack\":{",
                $"\"pack\":{{\"schemaVersion\":{NativeHudBuildContract.BuiltIn.SchemaVersion},", StringComparison.Ordinal),
            "field-duplicate" => json.Replace("\"fields\":{", "\"fields\":{\"sourceProvider\":8,", StringComparison.Ordinal),
            "slot-duplicate" => json.Replace("\"offset\":528", "\"offset\":528,\"offset\":528", StringComparison.Ordinal),
            _ => json
        };

        var result = fixture.Catalog().Install(fixture.SignRaw(Encoding.UTF8.GetBytes(json)), fixture.Now);
        Assert.False(result.Success);
        Assert.Equal(NativeCompatibilityInstallCode.InvalidEnvelope, result.Code);
    }

    [Fact]
    public void OversizedEmptyDeepAndInvalidUtf8InputsAreBoundedAndRejected()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog();
        Assert.Equal(NativeCompatibilityInstallCode.InvalidEnvelope,
            catalog.Install(new byte[NativeCompatibilityEnvelope.MaximumEnvelopeBytes + 1], fixture.Now).Code);
        Assert.Equal(NativeCompatibilityInstallCode.InvalidEnvelope, catalog.Install([], fixture.Now).Code);

        var deep = "{\"format\":1,\"deep\":" + new string('[', 17) + "0" + new string(']', 17) + "}";
        Assert.Equal(NativeCompatibilityInstallCode.InvalidEnvelope,
            catalog.Install(fixture.SignRaw(Encoding.UTF8.GetBytes(deep)), fixture.Now).Code);
        Assert.Equal(NativeCompatibilityInstallCode.InvalidEnvelope,
            catalog.Install(fixture.SignRaw([0x7B, 0x22, 0xFF, 0x22, 0x3A, 0x31, 0x7D]), fixture.Now).Code);
        Assert.Equal(NativeCompatibilityInstallCode.InvalidEnvelope,
            catalog.Install(fixture.SignRaw(Encoding.UTF8.GetBytes("{\"\\uD800\":1}")), fixture.Now).Code);
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(catalog, NativeHudBuildContract.BuiltIn));
    }

    [Theory]
    [InlineData("expired", NativeCompatibilityInstallCode.Expired)]
    [InlineData("expires-now", NativeCompatibilityInstallCode.Expired)]
    [InlineData("future", NativeCompatibilityInstallCode.NotYetValid)]
    [InlineData("reversed", NativeCompatibilityInstallCode.InvalidEnvelope)]
    [InlineData("equal", NativeCompatibilityInstallCode.InvalidEnvelope)]
    [InlineData("offset", NativeCompatibilityInstallCode.InvalidEnvelope)]
    [InlineData("local", NativeCompatibilityInstallCode.InvalidEnvelope)]
    [InlineData("space", NativeCompatibilityInstallCode.InvalidEnvelope)]
    [InlineData("too-precise", NativeCompatibilityInstallCode.InvalidEnvelope)]
    public void FreshImportsEnforceStrictUtcValidity(string mutation, NativeCompatibilityInstallCode expected)
    {
        using var fixture = new Fixture();
        var payload = fixture.Payload();
        switch (mutation)
        {
            case "expired":
                payload["issuedUtc"] = Utc(fixture.Now.AddDays(-2));
                payload["expiresUtc"] = Utc(fixture.Now.AddDays(-1));
                break;
            case "expires-now": payload["expiresUtc"] = Utc(fixture.Now); break;
            case "future": payload["issuedUtc"] = Utc(fixture.Now.AddMinutes(6)); break;
            case "reversed": payload["issuedUtc"] = Utc(fixture.Now.AddDays(2)); break;
            case "equal": payload["expiresUtc"] = payload["issuedUtc"]!.DeepClone(); break;
            case "offset": payload["issuedUtc"] = "2026-08-01T00:00:00+00:00"; break;
            case "local": payload["issuedUtc"] = "2026-08-01T00:00:00"; break;
            case "space": payload["issuedUtc"] = "2026-08-01 00:00:00Z"; break;
            case "too-precise": payload["issuedUtc"] = "2026-08-01T00:00:00.12345678Z"; break;
        }

        Assert.Equal(expected, fixture.Catalog().Install(fixture.SignRaw(Bytes(payload)), fixture.Now).Code);
    }

    [Fact]
    public void SmallIssueClockSkewIsAllowedButDoesNotRelaxExpiry()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Envelope(issued: fixture.Now.AddMinutes(4), expires: fixture.Now.AddMinutes(10));
        Assert.True(fixture.Catalog().Install(bytes, fixture.Now).Success);
        Assert.Equal(NativeCompatibilityInstallCode.Expired,
            fixture.Catalog().Install(bytes, fixture.Now.AddMinutes(10)).Code);
    }

    [Fact]
    public void CachedPackAcceptedWhileFreshRemainsUsableOfflineAfterExpiryForOnlyItsExactBuild()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog(persistent: true);
        var bytes = fixture.Envelope(issued: fixture.Now.AddDays(-3), expires: fixture.Now.AddDays(-1));
        var accepted = catalog.Install(bytes, fixture.Now.AddDays(-2));
        Assert.True(accepted.Success);

        var offline = fixture.Catalog(persistent: true);
        Assert.NotNull(Find(offline, accepted.Pack!));
        Assert.False(offline.CacheLoadHadErrors);
        Assert.Null(offline.Find(accepted.Pack!.GameVersion, accepted.Pack.ExecutableLength, new string('F', 64)));
        Assert.Equal(NativeCompatibilityInstallCode.Expired, offline.Install(bytes, fixture.Now).Code);
        Assert.NotNull(Find(offline, accepted.Pack));
    }

    [Fact]
    public void LooseExpiredEnvelopeWithoutAnAcceptanceReceiptIsNeverLoaded()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Envelope(issued: fixture.Now.AddDays(-3), expires: fixture.Now.AddDays(-1));
        File.WriteAllBytes(fixture.EnvelopePath(bytes), bytes);

        var catalog = fixture.Catalog(persistent: true);

        Assert.Null(Find(catalog, fixture.ParsePack()));
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(catalog, NativeHudBuildContract.BuiltIn));
        Assert.False(catalog.CacheLoadHadErrors);
    }

    [Fact]
    public void RevokedPinnedKeyBlocksPreviouslyAcceptedSupersedingPackWithoutBuiltInFallback()
    {
        using var fixture = new Fixture();
        var pack = fixture.Pack(sameAsBuiltIn: true);
        Assert.True(fixture.Catalog(persistent: true).Install(fixture.Envelope(pack), fixture.Now).Success);

        var revoked = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, fixture.CacheDirectory,
            new Dictionary<string, byte[]>());

        Assert.Null(Find(revoked, NativeHudBuildContract.BuiltIn));
        Assert.True(revoked.CacheLoadHadErrors);
        Assert.Contains("no longer trusted", revoked.GetUnavailableReason(
            NativeHudBuildContract.SupportedVersion, NativeHudBuildContract.SupportedExecutableLength,
            NativeHudBuildContract.SupportedSha256));
        Assert.Contains("no longer trusted", NativeCompatibilityRuntime.DescribeCatalog(revoked));
        Assert.DoesNotContain("pack is ready", NativeCompatibilityRuntime.DescribeCatalog(revoked));
    }

    [Fact]
    public void CorruptAcceptedPackCannotSilentlyRevertToBuiltIn()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Envelope(fixture.Pack(sameAsBuiltIn: true));
        Assert.True(fixture.Catalog(persistent: true).Install(bytes, fixture.Now).Success);
        File.WriteAllText(fixture.EnvelopePath(bytes), "corrupt");

        var catalog = fixture.Catalog(persistent: true);

        Assert.Null(Find(catalog, NativeHudBuildContract.BuiltIn));
        Assert.True(catalog.CacheLoadHadErrors);
        Assert.NotNull(catalog.GetUnavailableReason(NativeHudBuildContract.SupportedVersion,
            NativeHudBuildContract.SupportedExecutableLength, NativeHudBuildContract.SupportedSha256));
    }

    [Fact]
    public void MissingLatestPackDoesNotLoadOlderOrphanOrForgetItsRevision()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog(persistent: true);
        var older = fixture.Envelope(fixture.Pack(fixture.NextRevision, sameAsBuiltIn: true));
        var newer = fixture.Envelope(fixture.Pack(checked(fixture.NextRevision + 1), sameAsBuiltIn: true));
        Assert.True(catalog.Install(older, fixture.Now).Success);
        Assert.True(catalog.Install(newer, fixture.Now).Success);
        File.Delete(fixture.EnvelopePath(newer));

        var offline = fixture.Catalog(persistent: true);

        Assert.True(File.Exists(fixture.EnvelopePath(older)));
        Assert.Null(Find(offline, NativeHudBuildContract.BuiltIn));
        Assert.Equal(NativeCompatibilityInstallCode.RollbackRejected, offline.Install(older, fixture.Now).Code);
        Assert.Null(Find(offline, NativeHudBuildContract.BuiltIn));
    }

    [Fact]
    public void MissingAcceptedEnvelopeCanBeRepairedByTheSameStillFreshSignedPayload()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Envelope(fixture.Pack(sameAsBuiltIn: true));
        Assert.True(fixture.Catalog(persistent: true).Install(bytes, fixture.Now).Success);
        File.Delete(fixture.EnvelopePath(bytes));
        var offline = fixture.Catalog(persistent: true);
        Assert.Null(Find(offline, NativeHudBuildContract.BuiltIn));

        var result = offline.Install(bytes, fixture.Now.AddSeconds(1));

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(fixture.NextRevision, Find(offline, NativeHudBuildContract.BuiltIn)!.Revision);
        Assert.False(offline.CacheLoadHadErrors);
        Assert.Null(offline.GetUnavailableReason(NativeHudBuildContract.SupportedVersion,
            NativeHudBuildContract.SupportedExecutableLength, NativeHudBuildContract.SupportedSha256));
    }

    [Fact]
    public void RevisionFloorSurvivesRestartAndSameRevisionCannotChangeOffsetsOrSignedMetadata()
    {
        using var fixture = new Fixture();
        var pack = fixture.Pack(5);
        var catalog = fixture.Catalog(persistent: true);
        Assert.True(catalog.Install(fixture.Envelope(pack), fixture.Now).Success);
        catalog = fixture.Catalog(persistent: true);

        Assert.Equal(NativeCompatibilityInstallCode.RollbackRejected,
            catalog.Install(fixture.Envelope(fixture.Pack(4)), fixture.Now).Code);
        var changedOffsets = pack.DeepClone().AsObject();
        changedOffsets["sourceVectorRva"] = changedOffsets["sourceVectorRva"]!.GetValue<ulong>() + 8;
        Assert.Equal(NativeCompatibilityInstallCode.RevisionConflict,
            catalog.Install(fixture.Envelope(changedOffsets), fixture.Now).Code);
        Assert.Equal(NativeCompatibilityInstallCode.RevisionConflict,
            catalog.Install(fixture.Envelope(pack, expires: fixture.Now.AddDays(2)), fixture.Now).Code);
        Assert.Equal(5, Find(catalog, fixture.ParsePack())!.Revision);
    }

    [Fact]
    public void ImportedPackMustSupersedeBuiltInRevisionForTheSameFingerprint()
    {
        using var fixture = new Fixture();
        var builtIn = NativeHudCompatibilityPack.Parse(Bytes(fixture.Pack(3, sameAsBuiltIn: true)));
        var catalog = new NativeCompatibilityCatalog(builtIn, null, fixture.Keys);

        Assert.Equal(NativeCompatibilityInstallCode.RevisionConflict,
            catalog.Install(fixture.Envelope(fixture.Pack(
                builtIn.Revision, sameAsBuiltIn: true)), fixture.Now).Code);
        Assert.Equal(NativeCompatibilityInstallCode.RollbackRejected,
            catalog.Install(fixture.Envelope(fixture.Pack(
                builtIn.Revision - 1, sameAsBuiltIn: true)), fixture.Now).Code);
        Assert.Same(builtIn, Find(catalog, builtIn));
        Assert.Equal(0, catalog.Generation);
    }

    [Fact]
    public void RevisionsAreIndependentAcrossDifferentExactFingerprints()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog();
        Assert.True(catalog.Install(fixture.Envelope(fixture.Pack(8)), fixture.Now).Success);
        var other = fixture.Pack(1);
        other["executableSha256"] = new string('B', 64);

        var result = catalog.Install(fixture.Envelope(other), fixture.Now);

        Assert.True(result.Success);
        Assert.Equal(1, result.Pack!.Revision);
        Assert.Equal(8, Find(catalog, fixture.ParsePack())!.Revision);
    }

    [Fact]
    public void FailedAtomicLedgerReplacementKeepsOldBytesAndSnapshotUsable()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog(persistent: true);
        var original = fixture.Envelope(fixture.Pack(2));
        Assert.True(catalog.Install(original, fixture.Now).Success);
        var before = File.ReadAllBytes(fixture.LedgerPath);

        // A reader which denies delete/rename forces the final atomic commit to fail on Windows.
        using (var blocker = new FileStream(fixture.LedgerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = catalog.Install(fixture.Envelope(fixture.Pack(3)), fixture.Now);
            Assert.False(result.Success);
            Assert.Equal(NativeCompatibilityInstallCode.CacheWriteFailed, result.Code);
            Assert.Equal(before, File.ReadAllBytes(fixture.LedgerPath));
            Assert.Equal(2, Find(catalog, fixture.ParsePack())!.Revision);
            Assert.Equal(1, catalog.Generation);
        }

        var restarted = fixture.Catalog(persistent: true);
        Assert.Equal(2, Find(restarted, fixture.ParsePack())!.Revision);
        Assert.False(restarted.CacheLoadHadErrors);
        Assert.Empty(Directory.EnumerateFiles(fixture.CacheDirectory, "*.tmp"));
    }

    [Fact]
    public void BusyCacheFailsWithoutModifyingAcceptedState()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog(persistent: true);
        Assert.True(catalog.Install(fixture.Envelope(), fixture.Now).Success);
        var before = File.ReadAllBytes(fixture.LedgerPath);
        using var blocker = new FileStream(Path.Combine(fixture.CacheDirectory, ".catalog.lock"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);

        var result = catalog.Install(fixture.Envelope(fixture.Pack(3)), fixture.Now);

        Assert.False(result.Success);
        Assert.Equal(NativeCompatibilityInstallCode.CacheUnavailable, result.Code);
        Assert.Equal(before, File.ReadAllBytes(fixture.LedgerPath));
        Assert.Equal(1, catalog.Generation);
    }

    [Fact]
    public void SeparateCatalogInstancesReloadTheDurableRevisionBeforeImport()
    {
        using var fixture = new Fixture();
        var first = fixture.Catalog(persistent: true);
        var second = fixture.Catalog(persistent: true);
        Assert.True(first.Install(fixture.Envelope(fixture.Pack(5)), fixture.Now).Success);

        Assert.Equal(NativeCompatibilityInstallCode.RollbackRejected,
            second.Install(fixture.Envelope(fixture.Pack(4)), fixture.Now).Code);
        Assert.Equal(5, Find(second, fixture.ParsePack())!.Revision);
        Assert.Equal(1, second.Generation);
    }

    [Fact]
    public void DeletingLedgerDoesNotResetAnAlreadyRunningCatalogsHighWatermark()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog(persistent: true);
        Assert.True(catalog.Install(fixture.Envelope(fixture.Pack(5)), fixture.Now).Success);
        File.Delete(fixture.LedgerPath);

        Assert.Equal(NativeCompatibilityInstallCode.CacheUnavailable,
            catalog.Install(fixture.Envelope(fixture.Pack(4)), fixture.Now).Code);
        Assert.Equal(5, Find(catalog, fixture.ParsePack())!.Revision);
        Assert.False(File.Exists(fixture.LedgerPath));
    }

    [Fact]
    public void CorruptLedgerDoesNotDisableBuiltInAndCannotBeOverwrittenByImport()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.LedgerPath, "{\"format\":1,\"entries\":\"corrupt\"}");
        var before = File.ReadAllBytes(fixture.LedgerPath);
        var catalog = fixture.Catalog(persistent: true);

        Assert.True(catalog.CacheLoadHadErrors);
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(catalog, NativeHudBuildContract.BuiltIn));
        Assert.Equal(NativeCompatibilityInstallCode.CacheUnavailable, catalog.Install(fixture.Envelope(), fixture.Now).Code);
        Assert.Equal(before, File.ReadAllBytes(fixture.LedgerPath));
    }

    [Fact]
    public void UnrelatedCorruptFilesCannotAffectTheBuiltInOrAcceptedSnapshot()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Catalog(persistent: true).Install(fixture.Envelope(), fixture.Now).Success);
        File.WriteAllText(Path.Combine(fixture.CacheDirectory, new string('F', 64) + ".pack.json"), "corrupt");
        File.WriteAllText(Path.Combine(fixture.CacheDirectory, ".compatibility-unfinished.tmp"), "partial");

        var catalog = fixture.Catalog(persistent: true);

        Assert.False(catalog.CacheLoadHadErrors);
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(catalog, NativeHudBuildContract.BuiltIn));
        Assert.NotNull(Find(catalog, fixture.ParsePack()));
    }

    [Fact]
    public void CacheAcceptanceReceiptCannotAuthorizeAnOriginallyExpiredPack()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Envelope(issued: fixture.Now.AddDays(-3), expires: fixture.Now.AddDays(-1));
        Assert.True(fixture.Catalog(persistent: true).Install(bytes, fixture.Now.AddDays(-2)).Success);
        var ledger = JsonNode.Parse(File.ReadAllBytes(fixture.LedgerPath))!.AsObject();
        ledger["entries"]![0]!["acceptedUtc"] = Utc(fixture.Now.AddHours(-1));
        File.WriteAllBytes(fixture.LedgerPath, Bytes(ledger));

        var catalog = fixture.Catalog(persistent: true);

        Assert.True(catalog.CacheLoadHadErrors);
        Assert.Null(Find(catalog, fixture.ParsePack()));
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(catalog, NativeHudBuildContract.BuiltIn));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("oversize")]
    [InlineData("too-many-entries")]
    [InlineData("duplicate-fingerprint")]
    public void CacheLedgerIsStrictAndBounded(string mutation)
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Catalog(persistent: true).Install(fixture.Envelope(), fixture.Now).Success);
        var ledger = JsonNode.Parse(File.ReadAllBytes(fixture.LedgerPath))!.AsObject();
        switch (mutation)
        {
            case "unknown": ledger["extra"] = 1; break;
            case "missing": ledger["entries"]![0]!.AsObject().Remove("payloadSha256"); break;
            case "duplicate-fingerprint":
                ledger["entries"]!.AsArray().Add(ledger["entries"]![0]!.DeepClone());
                break;
            case "too-many-entries":
                while (ledger["entries"]!.AsArray().Count <= NativeCompatibilityCatalog.MaximumCachedPacks)
                {
                    ledger["entries"]!.AsArray().Add(ledger["entries"]![0]!.DeepClone());
                }
                break;
        }

        var json = ledger.ToJsonString();
        if (mutation == "duplicate")
        {
            json = "{\"format\":1," + json[1..];
        }
        else if (mutation == "oversize")
        {
            json = new string(' ', 128 * 1024 + 1);
        }

        File.WriteAllText(fixture.LedgerPath, json);
        var catalog = fixture.Catalog(persistent: true);
        Assert.True(catalog.CacheLoadHadErrors);
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(catalog, NativeHudBuildContract.BuiltIn));
    }

    [Fact]
    public void NewerTrustedBuiltInCanSupersedeAnUnavailableOlderCacheRecord()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Envelope(fixture.Pack(2));
        Assert.True(fixture.Catalog(persistent: true).Install(bytes, fixture.Now).Success);
        File.Delete(fixture.EnvelopePath(bytes));
        var newerBuiltIn = NativeHudCompatibilityPack.Parse(Bytes(fixture.Pack(3)));

        var catalog = new NativeCompatibilityCatalog(newerBuiltIn, fixture.CacheDirectory, fixture.Keys);

        Assert.Same(newerBuiltIn, Find(catalog, newerBuiltIn));
        Assert.Null(catalog.GetUnavailableReason(newerBuiltIn.GameVersion, newerBuiltIn.ExecutableLength,
            newerBuiltIn.ExecutableSha256));
        Assert.True(catalog.CacheLoadHadErrors);
    }

    [Fact]
    public void PublisherPinsAreCopiedAndCannotBeMutatedAfterConstruction()
    {
        using var fixture = new Fixture();
        var inputKey = (byte[])fixture.PublicKey.Clone();
        var keys = new Dictionary<string, byte[]> { [fixture.KeyId.ToLowerInvariant()] = inputKey };
        var catalog = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, null, keys);
        Array.Clear(inputKey);
        keys.Clear();

        Assert.True(catalog.Install(fixture.Envelope(), fixture.Now).Success);
    }

    [Fact]
    public void IdenticalFreshPayloadCanBeRepinnedToAnotherAlreadyTrustedPublisherKey()
    {
        using var fixture = new Fixture();
        using var replacement = new Fixture();
        var original = fixture.Envelope();
        Assert.True(fixture.Catalog(persistent: true).Install(original, fixture.Now).Success);
        var payload = Convert.FromBase64String(JsonNode.Parse(original)!["payload"]!.GetValue<string>());
        var pins = new Dictionary<string, byte[]>
        {
            [fixture.KeyId] = fixture.PublicKey,
            [replacement.KeyId] = replacement.PublicKey
        };
        var rotating = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, fixture.CacheDirectory, pins);

        var result = rotating.Install(replacement.SignRaw(payload), fixture.Now);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        var afterOldKeyRemoved = new NativeCompatibilityCatalog(
            NativeHudBuildContract.BuiltIn, fixture.CacheDirectory, replacement.Keys);
        Assert.NotNull(Find(afterOldKeyRemoved, fixture.ParsePack()));
        Assert.False(afterOldKeyRemoved.CacheLoadHadErrors);
    }

    [Fact]
    public void AcceptedFingerprintCountIsBoundedWithoutEvictingRollbackRecords()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog();
        for (var index = 0; index < NativeCompatibilityCatalog.MaximumCachedPacks; index++)
        {
            var pack = fixture.Pack(1);
            pack["executableSha256"] = index.ToString("X64", CultureInfo.InvariantCulture);
            Assert.True(catalog.Install(fixture.Envelope(pack), fixture.Now).Success);
        }

        var result = catalog.Install(fixture.Envelope(), fixture.Now);

        Assert.Equal(NativeCompatibilityInstallCode.CatalogFull, result.Code);
        Assert.Equal(NativeCompatibilityCatalog.MaximumCachedPacks, catalog.Generation);
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(catalog, NativeHudBuildContract.BuiltIn));
    }

    [Fact]
    public void InvalidKeyIdAndNonP256KeyCannotBecomePins()
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, null,
            new Dictionary<string, byte[]> { [new string('0', 64)] = fixture.PublicKey }));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var otherCurve = p384.ExportSubjectPublicKeyInfo();
        Assert.Throws<CryptographicException>(() => NativeCompatibilitySignature.GetKeyId(otherCurve));
        var trailing = fixture.PublicKey.Concat(new byte[] { 0 }).ToArray();
        Assert.Throws<CryptographicException>(() => NativeCompatibilitySignature.GetKeyId(trailing));
    }

    [Fact]
    public void SignedCacheFileNamesComeOnlyFromDigestsNotPackIds()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Envelope();
        Assert.True(fixture.Catalog(persistent: true).Install(bytes, fixture.Now).Success);
        var files = Directory.EnumerateFiles(fixture.CacheDirectory).Select(Path.GetFileName).ToArray();

        Assert.Contains(Convert.ToHexString(SHA256.HashData(bytes)) + ".pack.json", files);
        Assert.Contains("accepted.json", files);
        Assert.Contains(".catalog.lock", files);
        Assert.Equal(3, files.Length);
    }

    [Fact]
    public async Task ConcurrentImportsPublishWholeSnapshotsAndRetainTheHighestAcceptedRevision()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog();
        // Create signatures before the parallel work: the test signer is intentionally not shared between threads.
        var envelopes = Enumerable.Range(1, 12).Select(revision => fixture.Envelope(fixture.Pack(revision))).ToArray();
        var results = await Task.WhenAll(envelopes.Select(bytes => Task.Run(() => catalog.Install(bytes, fixture.Now))));

        Assert.Equal(12, Find(catalog, fixture.ParsePack())!.Revision);
        Assert.All(results, result => Assert.True(result.Success || result.Code == NativeCompatibilityInstallCode.RollbackRejected));
        Assert.Equal(results.LongCount(result => result.Changed), catalog.Generation);
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(catalog, NativeHudBuildContract.BuiltIn));
    }

    [Fact]
    public void BundleInstallsBothStoresInOneGenerationAndReloadsOffline()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Bundle(fixture.Pack(), fixture.StorePack());
        var verified = NativeCompatibilityEnvelope.Verify(bytes, fixture.Keys, fixture.Now);
        Assert.Equal(2, verified.Packs.Count);
        Assert.Same(verified.Packs[0], verified.Pack);
        Assert.Throws<NotSupportedException>(() => ((IList<NativeHudCompatibilityPack>)verified.Packs).Clear());
        var catalog = fixture.Catalog(persistent: true);

        var result = catalog.Install(bytes, fixture.Now);

        Assert.True(result.Success);
        Assert.Equal(2, result.Packs.Count);
        Assert.Same(result.Packs[0], result.Pack);
        Assert.Equal(1, catalog.Generation);
        Assert.All(result.Packs, pack => Assert.Same(pack, Find(catalog, pack)));
        Assert.Equal(NativeCompatibilityInstallCode.AlreadyInstalled, catalog.Install(bytes, fixture.Now).Code);
        Assert.Equal(1, catalog.Generation);
        var reloaded = fixture.Catalog(persistent: true);
        Assert.False(reloaded.CacheLoadHadErrors);
        Assert.All(result.Packs, pack => Assert.Equal(pack.Revision, Find(reloaded, pack)!.Revision));
        Assert.Single(Directory.EnumerateFiles(fixture.CacheDirectory, "*.pack.json"));
        Assert.Equal(2, JsonNode.Parse(File.ReadAllBytes(fixture.LedgerPath))!["format"]!.GetValue<int>());
    }

    [Fact]
    public void LegacyStoreEnvelopeRequiresPinnedSignatureAndUsesSeparateExactIdentity()
    {
        using var fixture = new Fixture();
        var store = NativeHudCompatibilityPack.Parse(Bytes(fixture.StorePack()));
        var bytes = fixture.Envelope(fixture.StorePack());
        var catalog = fixture.Catalog(persistent: true);
        Assert.True(catalog.Install(bytes, fixture.Now).Success);
        Assert.NotNull(Find(fixture.Catalog(persistent: true), store));
        Assert.Null(catalog.Find(store.GameVersion, 0, string.Empty));
        Assert.Null(catalog.FindStore(store.StoreIdentity!.PackageFullName, store.ImageSize + 1));
        Assert.Null(catalog.FindStore(store.StoreIdentity.PackageFullName.ToLowerInvariant(), store.ImageSize));
        Assert.Null(catalog.FindStore(" " + store.StoreIdentity.PackageFullName, store.ImageSize));
        Assert.Null(catalog.FindStore(null, 0));
        var untrusted = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, null, new Dictionary<string, byte[]>());
        Assert.Equal(NativeCompatibilityInstallCode.UntrustedPublisher, untrusted.Install(bytes, fixture.Now).Code);
        Assert.Null(Find(untrusted, store));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("too-many")]
    [InlineData("duplicate")]
    [InlineData("duplicate-revision")]
    [InlineData("malformed-member")]
    [InlineData("unknown-field")]
    [InlineData("single-pack-field")]
    public void InvalidBundleCannotInstallEvenItsValidFirstMember(string mutation)
    {
        using var fixture = new Fixture();
        var payload = fixture.BundlePayload(fixture.Pack(), fixture.StorePack());
        var members = payload["packs"]!.AsArray();
        switch (mutation)
        {
            case "empty": members.Clear(); break;
            case "too-many":
                while (members.Count <= NativeCompatibilityEnvelope.MaximumPacks) members.Add(fixture.Pack());
                break;
            case "duplicate": members.Add(members[0]!.DeepClone()); break;
            case "duplicate-revision": members.Add(fixture.Pack(9)); break;
            case "malformed-member": members[1]!["imageSize"] = 0; break;
            case "unknown-field": payload["extra"] = true; break;
            case "single-pack-field": payload["pack"] = fixture.Pack(); break;
        }
        var catalog = fixture.Catalog(persistent: true);
        var result = catalog.Install(fixture.SignRaw(Bytes(payload)), fixture.Now);
        Assert.Equal(NativeCompatibilityInstallCode.InvalidEnvelope, result.Code);
        Assert.Equal(0, catalog.Generation);
        Assert.Null(Find(catalog, fixture.ParsePack()));
        Assert.False(File.Exists(fixture.LedgerPath));
        Assert.Empty(Directory.EnumerateFiles(fixture.CacheDirectory, "*.pack.json"));
    }

    [Fact]
    public void ChangingOnlySecondBundleMemberWithoutResigningRejectsEverything()
    {
        using var fixture = new Fixture();
        var outer = JsonNode.Parse(fixture.Bundle(fixture.Pack(), fixture.StorePack()))!.AsObject();
        var payload = JsonNode.Parse(Convert.FromBase64String(outer["payload"]!.GetValue<string>()))!;
        payload["packs"]![1]!["revision"] = 99;
        outer["payload"] = Convert.ToBase64String(Bytes(payload));
        var catalog = fixture.Catalog();
        Assert.Equal(NativeCompatibilityInstallCode.InvalidSignature, catalog.Install(Bytes(outer), fixture.Now).Code);
        Assert.Null(Find(catalog, fixture.ParsePack()));
        Assert.Equal(0, catalog.Generation);
    }

    [Theory]
    [InlineData(1, NativeCompatibilityInstallCode.RollbackRejected)]
    [InlineData(2, NativeCompatibilityInstallCode.RevisionConflict)]
    public void OneBundleMemberBelowItsFloorRejectsTheWholeTransaction(int storeRevision, NativeCompatibilityInstallCode expected)
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog(persistent: true);
        Assert.True(catalog.Install(fixture.Bundle(fixture.Pack(2), fixture.StorePack(2)), fixture.Now).Success);
        var ledger = File.ReadAllBytes(fixture.LedgerPath);
        var result = catalog.Install(fixture.Bundle(fixture.Pack(3), fixture.StorePack(storeRevision)), fixture.Now);
        Assert.Equal(expected, result.Code);
        Assert.Equal(1, catalog.Generation);
        Assert.Equal(ledger, File.ReadAllBytes(fixture.LedgerPath));
        Assert.Equal(2, Find(catalog, fixture.ParsePack())!.Revision);
        Assert.Single(Directory.EnumerateFiles(fixture.CacheDirectory, "*.pack.json"));
    }

    [Fact]
    public void FailedBundleLedgerReplacementKeepsBothPriorMembersAndReceipts()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog(persistent: true);
        Assert.True(catalog.Install(fixture.Bundle(fixture.Pack(2), fixture.StorePack(2)), fixture.Now).Success);
        var ledger = File.ReadAllBytes(fixture.LedgerPath);
        using (var held = new FileStream(fixture.LedgerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = catalog.Install(fixture.Bundle(fixture.Pack(3), fixture.StorePack(3)), fixture.Now);
            Assert.Equal(NativeCompatibilityInstallCode.CacheWriteFailed, result.Code);
        }
        Assert.Equal(ledger, File.ReadAllBytes(fixture.LedgerPath));
        Assert.Equal(1, catalog.Generation);
        foreach (var current in new[] { catalog, fixture.Catalog(persistent: true) })
        {
            Assert.Equal(2, Find(current, fixture.ParsePack())!.Revision);
            Assert.Equal(2, Find(current, NativeHudCompatibilityPack.Parse(Bytes(fixture.StorePack())))!.Revision);
        }
    }

    [Fact]
    public void MixedLedgerUpgradeRetainsLegacyReceiptAndExpiredBundleRemainsUsableOffline()
    {
        using var fixture = new Fixture();
        var accepted = fixture.Now.AddDays(-3);
        var catalog = fixture.Catalog(persistent: true);
        Assert.True(catalog.Install(fixture.Envelope(issued: accepted.AddHours(-1), expires: accepted.AddHours(1)), accepted).Success);
        Assert.Equal(1, JsonNode.Parse(File.ReadAllBytes(fixture.LedgerPath))!["format"]!.GetValue<int>());
        var payload = fixture.BundlePayload(fixture.StorePack());
        payload["issuedUtc"] = Utc(accepted.AddHours(-1));
        payload["expiresUtc"] = Utc(accepted.AddHours(1));
        var bytes = fixture.SignRaw(Bytes(payload));
        Assert.True(catalog.Install(bytes, accepted).Success);
        var reloaded = fixture.Catalog(persistent: true);
        Assert.False(reloaded.CacheLoadHadErrors);
        Assert.NotNull(Find(reloaded, fixture.ParsePack()));
        Assert.NotNull(Find(reloaded, NativeHudCompatibilityPack.Parse(Bytes(fixture.StorePack()))));
        Assert.Equal(NativeCompatibilityInstallCode.Expired, reloaded.Install(bytes, fixture.Now).Code);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("version")]
    [InlineData("size")]
    [InlineData("kind")]
    [InlineData("steam-fields")]
    [InlineData("missing-kind")]
    [InlineData("duplicate")]
    public void StoreReceiptIdentityIsStrictAndMustMatchResignedContent(string mutation)
    {
        using var fixture = new Fixture();
        var store = NativeHudCompatibilityPack.Parse(Bytes(fixture.StorePack()));
        Assert.True(fixture.Catalog(persistent: true).Install(fixture.Envelope(fixture.StorePack()), fixture.Now).Success);
        var ledger = JsonNode.Parse(File.ReadAllBytes(fixture.LedgerPath))!.AsObject();
        var entry = ledger["entries"]![0]!.AsObject();
        switch (mutation)
        {
            case "package": entry["packageFullName"] = "Untrusted.ForteBaseGame_3.430.772.0_x64__8wekyb3d8bbwe"; break;
            case "version": entry["gameVersion"] = "3.430.773.0"; entry["packageFullName"] = "Microsoft.ForteBaseGame_3.430.773.0_x64__8wekyb3d8bbwe"; break;
            case "size": entry["imageSize"] = store.ImageSize + 4096; break;
            case "kind": entry["kind"] = "steam"; break;
            case "steam-fields": entry["executableSha256"] = new string('0', 64); break;
            case "missing-kind": entry.Remove("kind"); break;
            case "duplicate": ledger["entries"]!.AsArray().Add(entry.DeepClone()); break;
        }
        File.WriteAllBytes(fixture.LedgerPath, Bytes(ledger));
        var reloaded = fixture.Catalog(persistent: true);
        Assert.True(reloaded.CacheLoadHadErrors);
        Assert.Null(Find(reloaded, store));
        Assert.Null(reloaded.FindStore(entry["packageFullName"]!.GetValue<string>(), entry["imageSize"]!.GetValue<uint>()));
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(reloaded, NativeHudBuildContract.BuiltIn));
    }

    [Fact]
    public void CorruptOrRevokedBundleBlocksBothHigherRevisionsIncludingStoreBuiltIn()
    {
        using var fixture = new Fixture();
        var store = NativeHudCompatibilityPack.Parse(Bytes(fixture.StorePack(1)));
        var bytes = fixture.Bundle(fixture.Pack(fixture.NextRevision, sameAsBuiltIn: true), fixture.StorePack(2));
        var catalog = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, fixture.CacheDirectory, fixture.Keys, [store]);
        Assert.True(catalog.Install(bytes, fixture.Now).Success);
        var revoked = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, fixture.CacheDirectory, new Dictionary<string, byte[]>(), [store]);
        Assert.Null(Find(revoked, store));
        Assert.Null(Find(revoked, NativeHudBuildContract.BuiltIn));
        File.WriteAllText(fixture.EnvelopePath(bytes), "{}");
        var corrupt = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, fixture.CacheDirectory, fixture.Keys, [store]);
        Assert.Null(Find(corrupt, store));
        Assert.NotNull(corrupt.GetStoreUnavailableReason(store.StoreIdentity!.PackageFullName, store.ImageSize));
        Assert.Null(Find(corrupt, NativeHudBuildContract.BuiltIn));
        Assert.Equal(2, corrupt.Diagnostics.Count);
    }

    [Fact]
    public void AdditionalBuiltInsAreCopiedBoundedExactAndHaveIndependentRevisionFloors()
    {
        using var fixture = new Fixture();
        var olderSteam = fixture.ParsePack();
        var store = NativeHudCompatibilityPack.Parse(Bytes(fixture.StorePack(2)));
        var source = new List<NativeHudCompatibilityPack> { olderSteam, store };
        var catalog = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, null, fixture.Keys, source);
        source.Clear();
        Assert.Same(NativeHudBuildContract.BuiltIn, Find(catalog, NativeHudBuildContract.BuiltIn));
        Assert.Same(olderSteam, Find(catalog, olderSteam));
        Assert.Same(store, Find(catalog, store));
        Assert.Null(catalog.Find(store.GameVersion, 0, string.Empty));
        Assert.Null(catalog.FindStore(olderSteam.GameVersion, olderSteam.ImageSize));
        Assert.Equal(NativeCompatibilityInstallCode.RevisionConflict, catalog.Install(fixture.Envelope(fixture.StorePack(2)), fixture.Now).Code);
        Assert.Equal(NativeCompatibilityInstallCode.RollbackRejected, catalog.Install(fixture.Envelope(fixture.Pack(1)), fixture.Now).Code);
        Assert.Throws<ArgumentException>(() => new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, null, fixture.Keys, [olderSteam, olderSteam]));
        Assert.Throws<ArgumentException>(() => new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, null, fixture.Keys, [NativeHudBuildContract.BuiltIn]));
    }

    [Fact]
    public void BundleKeepsNewerEmbeddedMemberWhileInstallingAnotherBuild()
    {
        using var fixture = new Fixture();
        var newer = NativeHudCompatibilityPack.Parse(Bytes(fixture.Pack(5)));
        var catalog = new NativeCompatibilityCatalog(newer, fixture.CacheDirectory, fixture.Keys);
        var result = catalog.Install(fixture.Bundle(fixture.Pack(2), fixture.StorePack()), fixture.Now);
        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Same(newer, result.Pack);
        Assert.Same(newer, result.Packs[0]);
        Assert.Same(newer, Find(catalog, newer));
        Assert.NotNull(Find(catalog, result.Packs[1]));
        Assert.Single(JsonNode.Parse(File.ReadAllBytes(fixture.LedgerPath))!["entries"]!.AsArray());
        Assert.Equal(NativeCompatibilityInstallCode.RollbackRejected, catalog.Install(fixture.Envelope(fixture.Pack(2)), fixture.Now).Code);
    }

    [Fact]
    public void BundleEntirelyCoveredByEmbeddedMapsIsNoOpWithoutReceipts()
    {
        using var fixture = new Fixture();
        var newer = NativeHudCompatibilityPack.Parse(Bytes(fixture.Pack(5)));
        var store = NativeHudCompatibilityPack.Parse(Bytes(fixture.StorePack(2)));
        var catalog = new NativeCompatibilityCatalog(newer, fixture.CacheDirectory, fixture.Keys, [store]);
        var result = catalog.Install(fixture.Bundle(fixture.Pack(2), fixture.StorePack(2)), fixture.Now);
        Assert.Equal(NativeCompatibilityInstallCode.AlreadyInstalled, result.Code);
        Assert.False(result.Changed);
        Assert.Equal(0, catalog.Generation);
        Assert.Same(newer, result.Packs[0]);
        Assert.Same(store, result.Packs[1]);
        Assert.False(File.Exists(fixture.LedgerPath));
        Assert.Empty(Directory.EnumerateFiles(fixture.CacheDirectory, "*.pack.json"));
    }

    [Fact]
    public void NewerEmbeddedMapCannotLetBundleBypassAcceptedRevisionFloor()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Catalog(persistent: true).Install(fixture.Envelope(fixture.Pack(3)), fixture.Now).Success);
        var newer = NativeHudCompatibilityPack.Parse(Bytes(fixture.Pack(5)));
        var catalog = new NativeCompatibilityCatalog(newer, fixture.CacheDirectory, fixture.Keys);
        var ledger = File.ReadAllBytes(fixture.LedgerPath);
        Assert.Equal(NativeCompatibilityInstallCode.RollbackRejected,
            catalog.Install(fixture.Bundle(fixture.StorePack(), fixture.Pack(2)), fixture.Now).Code);
        Assert.Equal(ledger, File.ReadAllBytes(fixture.LedgerPath));
        Assert.Null(Find(catalog, NativeHudCompatibilityPack.Parse(Bytes(fixture.StorePack()))));
        Assert.Same(newer, Find(catalog, newer));
    }

    [Fact]
    public void EightMemberBundleIsAcceptedButCapacityFailureNeverPartiallyInstalls()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog();
        for (var offset = 0; offset < NativeCompatibilityCatalog.MaximumCachedPacks - 8; offset += 8)
        {
            var members = Enumerable.Range(offset, 8).Select(index =>
            {
                var pack = fixture.Pack(1);
                pack["executableSha256"] = index.ToString("X64", CultureInfo.InvariantCulture);
                return pack;
            }).ToArray();
            Assert.True(catalog.Install(fixture.Bundle(members), fixture.Now).Success);
        }
        for (var index = 120; index < 127; index++)
        {
            var pack = fixture.Pack(1);
            pack["executableSha256"] = index.ToString("X64", CultureInfo.InvariantCulture);
            Assert.True(catalog.Install(fixture.Envelope(pack), fixture.Now).Success);
        }
        var generation = catalog.Generation;
        Assert.Equal(NativeCompatibilityInstallCode.CatalogFull,
            catalog.Install(fixture.Bundle(fixture.Pack(), fixture.StorePack()), fixture.Now).Code);
        Assert.Equal(generation, catalog.Generation);
        Assert.Null(Find(catalog, fixture.ParsePack()));
        Assert.Null(Find(catalog, NativeHudCompatibilityPack.Parse(Bytes(fixture.StorePack()))));
    }

    private static NativeHudCompatibilityPack? Find(NativeCompatibilityCatalog catalog, NativeHudCompatibilityPack pack) =>
        pack.StoreIdentity is null ? catalog.Find(pack.GameVersion, pack.ExecutableLength, pack.ExecutableSha256)
            : catalog.FindStore(pack.StoreIdentity.PackageFullName, pack.ImageSize);

    private static byte[] Bytes(JsonNode value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly JsonObject _builtInJson;

        public Fixture()
        {
            Now = DateTimeOffset.UtcNow;
            PublicKey = _signer.ExportSubjectPublicKeyInfo();
            KeyId = NativeCompatibilitySignature.GetKeyId(PublicKey);
            Keys = new Dictionary<string, byte[]> { [KeyId] = PublicKey };
            CacheDirectory = Path.Combine(Path.GetTempPath(), "wisp-compatibility-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(CacheDirectory);
            using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.BuiltIn.json")!;
            _builtInJson = JsonNode.Parse(stream)!.AsObject();
        }

        public DateTimeOffset Now { get; }
        public byte[] PublicKey { get; }
        public string KeyId { get; }
        public IReadOnlyDictionary<string, byte[]> Keys { get; }
        public string CacheDirectory { get; }
        public string LedgerPath => Path.Combine(CacheDirectory, "accepted.json");
        public int NextRevision => checked(NativeHudBuildContract.BuiltIn.Revision + 1);

        public NativeCompatibilityCatalog Catalog(bool persistent = false) =>
            new(NativeHudBuildContract.BuiltIn, persistent ? CacheDirectory : null, Keys);

        public JsonObject Pack(int? revision = null, bool sameAsBuiltIn = false)
        {
            var pack = _builtInJson.DeepClone().AsObject();
            pack["id"] = "fh6-signed-catalog-test";
            pack["revision"] = revision ?? (sameAsBuiltIn ? NextRevision : 2);
            if (!sameAsBuiltIn)
            {
                pack["gameVersion"] = "6.430.772.0";
                pack["executableSha256"] = new string('A', 64);
            }

            return pack;
        }

        public NativeHudCompatibilityPack ParsePack() => NativeHudCompatibilityPack.Parse(Bytes(Pack()));

        public JsonObject StorePack(int revision = 2)
        {
            using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.Store.json")!;
            var pack = JsonNode.Parse(stream)!.AsObject();
            pack["gameVersion"] = "3.430.772.0";
            pack["revision"] = revision;
            pack["storeIdentity"]!["packageFullName"] = "Microsoft.ForteBaseGame_3.430.772.0_x64__8wekyb3d8bbwe";
            return pack;
        }

        public JsonObject BundlePayload(params JsonObject[] packs) => new()
        {
            ["format"] = 2,
            ["purpose"] = "wisp-native-hud-compatibility",
            ["issuedUtc"] = Utc(Now.AddMinutes(-1)),
            ["expiresUtc"] = Utc(Now.AddDays(1)),
            ["packs"] = new JsonArray(packs.Select(pack => pack.DeepClone()).ToArray())
        };

        public byte[] Bundle(params JsonObject[] packs) => SignRaw(Bytes(BundlePayload(packs)));

        public JsonObject Payload(JsonObject? pack = null, DateTimeOffset? issued = null, DateTimeOffset? expires = null) => new()
        {
            ["format"] = 1,
            ["purpose"] = "wisp-native-hud-compatibility",
            ["issuedUtc"] = Utc(issued ?? Now.AddMinutes(-1)),
            ["expiresUtc"] = Utc(expires ?? Now.AddDays(1)),
            ["pack"] = (pack ?? Pack()).DeepClone()
        };

        public byte[] Envelope(JsonObject? pack = null, DateTimeOffset? issued = null, DateTimeOffset? expires = null) =>
            SignRaw(Bytes(Payload(pack, issued, expires)));

        public byte[] SignRaw(byte[] payload, bool includeDomain = true,
            DSASignatureFormat signatureFormat = DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
        {
            var signature = _signer.SignData(includeDomain ? NativeCompatibilitySignature.CreateSigningInput(payload) : payload,
                HashAlgorithmName.SHA256, signatureFormat);
            return Bytes(new JsonObject
            {
                ["format"] = 1,
                ["keyId"] = KeyId,
                ["payload"] = Convert.ToBase64String(payload),
                ["signature"] = Convert.ToBase64String(signature)
            });
        }

        public string EnvelopePath(byte[] envelope) =>
            Path.Combine(CacheDirectory, Convert.ToHexString(SHA256.HashData(envelope)) + ".pack.json");

        public void Dispose()
        {
            _signer.Dispose();
            // This directory is created uniquely by this fixture and contains test artifacts only.
            Directory.Delete(CacheDirectory, recursive: true);
        }
    }
}
