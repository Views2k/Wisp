using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wisp.Core.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeTuneCompatibilityPackTests
{
    [Theory]
    [InlineData(false, 5, 58)]
    [InlineData(true, 6, 65)]
    public void CurrentDescriptorsRetainCompleteReviewedInputs(bool store, int schema, int guards)
    {
        var document = Document(store);
        var pack = Parse(document);
        var tune = Assert.IsType<NativeTuneCompatibilityLayout>(pack.Tune);
        Assert.Equal(schema, pack.SchemaVersion);
        Assert.Equal(schema, pack.ReaderVersion);
        Assert.Equal(NativeTuneCompatibilityLayout.SupportedProfileId, tune.ProfileId);
        Assert.Equal(19, tune.Rvas.Count);
        Assert.Equal(guards, tune.CodeGuards.Count);
        Assert.Equal(2, tune.ProviderSlots.Count);
        Assert.Equal(12, tune.Asset.Parts.Count);
        Assert.Equal(15409U, tune.Asset.HeaderPageCount);
        Assert.Equal(16221184, tune.Asset.MinimumLength);
        Assert.Equal(17269760, tune.Asset.MaximumLength);
        Assert.Equal(store, pack.StoreIdentity is not null);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document))), pack.Fingerprint);
        Assert.IsAssignableFrom<FrozenDictionary<ulong, ulong>>(tune.Rvas);
        Assert.IsAssignableFrom<FrozenDictionary<TunePartId, NativeTuneCompatibilityLayout.PartExpectation>>(tune.Asset.Parts);
        document["tune"]!["rvas"]!["0640EE78"] = 4096;
        Assert.NotEqual(4096UL, tune.Rvas[0x640EE78]);
    }

    [Fact]
    public void DescriptorFingerprintIgnoresJsonOrderingButIncludesEveryConsumedValue()
    {
        var document = Document(false);
        var original = Parse(document);
        var tune = document["tune"]!.AsObject();
        var reversed = new JsonObject(tune.Reverse().Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone())));
        document["tune"] = reversed;
        Assert.Equal(original.Tune!.Fingerprint, Parse(document).Tune!.Fingerprint);
        Assert.NotEqual(original.Fingerprint, Parse(document).Fingerprint);
        reversed["asset"]!["parts"]!["Engine"]!["rowsSha256"] = new string('A', 64);
        Assert.NotEqual(original.Tune.Fingerprint, Parse(document).Tune!.Fingerprint);
    }

    [Theory]
    [InlineData("semantics")]
    [InlineData("profile")]
    [InlineData("missing-rva")]
    [InlineData("extra-rva")]
    [InlineData("rva-case")]
    [InlineData("rva-alias")]
    [InlineData("rva-span")]
    [InlineData("missing-guard")]
    [InlineData("guard-role")]
    [InlineData("guard-duplicate")]
    [InlineData("guard-overlap")]
    [InlineData("guard-length")]
    [InlineData("guard-total")]
    [InlineData("guard-hash")]
    [InlineData("guard-extra")]
    [InlineData("slot-role")]
    [InlineData("slot-table-span")]
    [InlineData("missing-slot")]
    [InlineData("asset-size")]
    [InlineData("asset-page")]
    [InlineData("asset-alignment")]
    [InlineData("part-missing")]
    [InlineData("part-total")]
    [InlineData("part-hash")]
    [InlineData("sql")]
    [InlineData("null")]
    public void MalformedOrIncompleteDescriptorFailsClosed(string fault)
    {
        var document = Document(false);
        var tune = document["tune"]!.AsObject();
        var rvas = tune["rvas"]!.AsObject();
        var guards = tune["codeGuards"]!.AsArray();
        var slots = tune["providerSlots"]!.AsArray();
        var asset = tune["asset"]!.AsObject();
        var parts = asset["parts"]!.AsObject();
        switch (fault)
        {
            case "semantics": tune["semanticsVersion"] = 2; break;
            case "profile": tune["profileId"] = "unreviewed"; break;
            case "missing-rva": rvas.Remove("0640EE78"); break;
            case "extra-rva": rvas["0640EE79"] = 4096; break;
            case "rva-case": rvas["0640ee78"] = rvas["0640EE78"]!.DeepClone(); rvas.Remove("0640EE78"); break;
            case "rva-alias": rvas["0640EE78"] = rvas["06446D18"]!.DeepClone(); break;
            case "rva-span": rvas["08F75670"] = document["imageSize"]!.GetValue<uint>() - 4; break;
            case "missing-guard": guards.RemoveAt(0); break;
            case "guard-role": guards[0]!["role"] = "unreviewed"; break;
            case "guard-duplicate": guards[0]!["role"] = guards[1]!["role"]!.DeepClone(); break;
            case "guard-overlap": guards[0]!["rva"] = guards[1]!["rva"]!.DeepClone(); break;
            case "guard-length": guards[0]!["length"] = 8193; break;
            case "guard-total":
                for (var i = 0; i < guards.Count; i++) { guards[i]!["rva"] = 4096 + i * 16384; guards[i]!["length"] = 8192; }
                break;
            case "guard-hash": guards[0]!["sha256"] = new string('G', 64); break;
            case "guard-extra": guards[0]!["mask"] = "wildcard"; break;
            case "slot-role": slots[0]!["targetRva"] = guards[0]!["rva"]!.DeepClone(); break;
            case "slot-table-span": document["leadVtableRva"] = document["imageSize"]!.GetValue<uint>() - 0x1080; break;
            case "missing-slot": slots.RemoveAt(0); break;
            case "asset-size": asset["maximumLength"] = NativeTuneCompatibilityLayout.MaximumAssetBytes + 1024; break;
            case "asset-page": asset["headerPageCount"] = 17000; break;
            case "asset-alignment": asset["minimumLength"] = 16221185; break;
            case "part-missing": parts.Remove("Engine"); break;
            case "part-total": foreach (var part in parts) part.Value!["count"] = 250000; break;
            case "part-hash": parts["Engine"]!["rowsSha256"] = ""; break;
            case "sql": asset["sql"] = "SELECT anything"; break;
            case "null": tune["rvas"] = null; break;
        }
        Assert.Throws<FormatException>(() => Parse(document));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalDescriptorDoesNotAlterLegacyOrNullCapability(bool store)
    {
        var document = Document(store);
        document["tune"] = null;
        Assert.Null(Parse(document).Tune);
        document["schemaVersion"] = store ? 4 : 3;
        document["readerVersion"] = store ? 4 : 3;
        Assert.Throws<FormatException>(() => Parse(document));
        document.Remove("tune");
        Assert.Null(Parse(document).Tune);
    }

    [Fact]
    public void DuplicateDescriptorPropertyIsRejected()
    {
        var json = JsonSerializer.Serialize(Document(false));
        json = json.Replace("\"semanticsVersion\":1", "\"semanticsVersion\":1,\"semanticsVersion\":1", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => NativeHudCompatibilityPack.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void SignedEnvelopeAuthenticatesTheDescriptorAndBothPlatformsWithinExistingBounds()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var keyId = Convert.ToHexString(SHA256.HashData(publicKey));
        var now = DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var payload = new JsonObject
        {
            ["format"] = 2,
            ["purpose"] = "wisp-native-hud-compatibility",
            ["issuedUtc"] = "2026-10-01T00:00:00Z",
            ["expiresUtc"] = "2026-10-02T00:00:00Z",
            ["packs"] = new JsonArray(Document(false), Document(true))
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = key.SignData(NativeCompatibilitySignature.CreateSigningInput(bytes), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var envelope = new JsonObject
        {
            ["format"] = 1,
            ["keyId"] = keyId,
            ["payload"] = Convert.ToBase64String(bytes),
            ["signature"] = Convert.ToBase64String(signature)
        };
        var keys = new Dictionary<string, byte[]> { [keyId] = publicKey };
        Assert.All(NativeCompatibilityEnvelope.Verify(JsonSerializer.SerializeToUtf8Bytes(envelope), keys, now).Packs,
            pack => Assert.NotNull(pack.Tune));
        payload["packs"]![0]!["tune"]!["asset"]!["parts"]!["Engine"]!["rowsSha256"] = new string('A', 64);
        envelope["payload"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));
        Assert.Throws<NativeCompatibilityEnvelopeException>(() =>
            NativeCompatibilityEnvelope.Verify(JsonSerializer.SerializeToUtf8Bytes(envelope), keys, now));
    }

    private static NativeHudCompatibilityPack Parse(JsonObject document) =>
        NativeHudCompatibilityPack.Parse(JsonSerializer.SerializeToUtf8Bytes(document));

    private static JsonObject Document(bool store)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wisp.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(directory.FullName, "src", "Wisp.App", "NativeCompatibility",
            store ? "fh6-store-3.440.853.0.json" : "fh6-6.440.853.0.json")))!.AsObject();
    }
}
