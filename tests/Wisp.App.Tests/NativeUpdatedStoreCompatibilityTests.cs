using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeUpdatedStoreCompatibilityTests
{
    private const string Version = "3.461.691.0";
    private const string PackageName = "Microsoft.ForteBaseGame_3.461.691.0_x64__8wekyb3d8bbwe";
    private const uint ImageSize = 188403712;

    [Fact]
    public void EmbeddedUpdatedStorePackRetainsItsExactPackageAndReaderContract()
    {
        var pack = NativeHudBuildContract.UpdatedStoreBuiltIn;
        var identity = Assert.IsType<NativeHudStoreBuildIdentity>(pack.StoreIdentity);
        Assert.Equal("fh6-store-3.461.691.0-x64", pack.Id);
        Assert.Equal(Version, pack.GameVersion);
        Assert.Equal(6, pack.SchemaVersion);
        Assert.Equal(6, pack.ReaderVersion);
        Assert.Equal(ImageSize, pack.ImageSize);
        Assert.Equal(PackageName, identity.PackageFullName);
        Assert.Equal(1790608750U, identity.TimeDateStamp);
        Assert.Same(pack, Catalog().FindStore(PackageName, ImageSize));
        Assert.NotNull(pack.NativeGauge);
        Assert.NotNull(pack.GameplayVisibility);
        Assert.NotNull(pack.Tune);
        Assert.Equal(176839376UL, pack.SourceVectorRva);
        Assert.Equal(113476336UL, pack.LeadVtableRva);
        Assert.Equal(0, pack.ExecutableLength);
        Assert.Empty(pack.ExecutableSha256);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("publisher")]
    [InlineData("architecture")]
    [InlineData("image-size")]
    [InlineData("missing-package")]
    public void UpdatedStorePackDoesNotAdmitAnUnrecognizedPackage(string fault)
    {
        var package = fault switch
        {
            "version" => PackageName.Replace(Version, "3.461.691.1", StringComparison.Ordinal),
            "publisher" => PackageName.Replace("8wekyb3d8bbwe", "unrecognized", StringComparison.Ordinal),
            "architecture" => PackageName.Replace("_x64_", "_arm64_", StringComparison.Ordinal),
            "missing-package" => string.Empty,
            _ => PackageName
        };
        Assert.Null(Catalog().FindStore(package, ImageSize + (fault == "image-size" ? 1U : 0U)));
    }

    [Fact]
    public void UpdatedStoreGaugeUsesEveryReviewedProviderGuardTarget()
    {
        var gauge = Assert.IsType<NativeGaugeLayout>(NativeHudBuildContract.UpdatedStoreBuiltIn.NativeGauge);
        var expected = new Dictionary<ulong, ulong>
        {
            [0x0298] = 51645184,
            [0x0358] = 51656048,
            [0x05B8] = 51671008,
            [0x0720] = 51655552,
            [0x0798] = 51665248,
            [0x0E28] = 51670768
        };
        Assert.Equal(expected.OrderBy(pair => pair.Key), gauge.RequiredProviderVtableSlots.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void UpdatedSteamAndStoreUseSeparateAdmissionIdentities()
    {
        var catalog = Catalog();
        var store = NativeHudBuildContract.UpdatedStoreBuiltIn;
        var steam = NativeHudBuildContract.UpdatedSteamBuiltIn;

        Assert.Same(steam, catalog.Find(steam.GameVersion, steam.ExecutableLength, steam.ExecutableSha256));
        Assert.Same(store, catalog.FindStore(PackageName, ImageSize));
        Assert.Null(catalog.Find(Version, 0, string.Empty));
        Assert.Null(catalog.Find(Version, steam.ExecutableLength, steam.ExecutableSha256));
        Assert.False(store.Matches(Version, steam.ExecutableLength, steam.ExecutableSha256));
        Assert.Null(catalog.FindStore(PackageName, steam.ImageSize));
        Assert.Null(catalog.FindStore(PackageName.Replace(Version, steam.GameVersion, StringComparison.Ordinal), ImageSize));
        Assert.Null(steam.StoreIdentity);
    }

    [Fact]
    public void AddingUpdatedStoreKeepsBothOlderPlatformMapsAndCurrentSteam()
    {
        var catalog = Catalog();
        var expected = new[]
        {
            NativeHudBuildContract.BuiltIn, NativeHudBuildContract.UpdatedSteamBuiltIn,
            NativeHudBuildContract.PreviousSteamBuiltIn, NativeHudBuildContract.StoreBuiltIn,
            NativeHudBuildContract.PreviousStoreBuiltIn, NativeHudBuildContract.UpdatedStoreBuiltIn
        };
        foreach (var pack in expected)
        {
            var selected = pack.StoreIdentity is { } store
                ? catalog.FindStore(store.PackageFullName, pack.ImageSize)
                : catalog.Find(pack.GameVersion, pack.ExecutableLength, pack.ExecutableSha256);
            Assert.Same(pack, selected);
        }
        Assert.Equal("6.440.853.0", NativeHudBuildContract.SupportedVersion);
        Assert.Equal("3.440.853.0", NativeHudBuildContract.StoreBuiltIn.GameVersion);
    }

    [Fact]
    public void UpdatedStoreTuneKeepsAllRequiredRolesBoundsAndAssetRequirements()
    {
        var pack = NativeHudBuildContract.UpdatedStoreBuiltIn;
        var tune = Assert.IsType<NativeTuneCompatibilityLayout>(pack.Tune);
        var previous = Assert.IsType<NativeTuneCompatibilityLayout>(NativeHudBuildContract.StoreBuiltIn.Tune);
        Assert.Equal(previous.ProfileId, tune.ProfileId);
        Assert.Equal(previous.SemanticsVersion, tune.SemanticsVersion);
        Assert.Equal(19, tune.Rvas.Count);
        Assert.Equal(previous.Rvas.Keys.Order(), tune.Rvas.Keys.Order());
        Assert.Equal(65, tune.CodeGuards.Count);
        Assert.Equal(previous.CodeGuards.Select(guard => (guard.Role, guard.Length)),
            tune.CodeGuards.Select(guard => (guard.Role, guard.Length)));
        foreach (var (role, definition) in NativeTuneCompatibilityLayout.RequiredRvaRoles)
        {
            var rva = tune.Rvas[role];
            Assert.Equal(0UL, rva % (ulong)definition.Alignment);
            Assert.True(rva + (ulong)definition.Width <= pack.ImageSize);
        }
        Assert.All(tune.CodeGuards, guard =>
        {
            Assert.InRange(guard.Length, 1, 8192);
            Assert.True(guard.Rva + (ulong)guard.Length <= pack.ImageSize);
            Assert.Matches("^[0-9A-F]{64}$", guard.Sha256);
        });
        Assert.True(tune.CodeGuards.Sum(guard => guard.Length) <= 48 * 1024);
        Assert.Equal(previous.ProviderSlots.Keys.Order(), tune.ProviderSlots.Keys.Order());
        Assert.Equal(51656144UL, tune.ProviderSlots[0x12E8]);
        Assert.Equal(51093152UL, tune.ProviderSlots[0]);
        Assert.Equal(previous.Asset.MinimumLength, tune.Asset.MinimumLength);
        Assert.Equal(previous.Asset.MaximumLength, tune.Asset.MaximumLength);
        Assert.Equal(previous.Asset.HeaderPageCount, tune.Asset.HeaderPageCount);
        Assert.Equal(previous.Asset.Parts.OrderBy(pair => pair.Key), tune.Asset.Parts.OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData("missing-store-guard")]
    [InlineData("store-guard-span")]
    [InlineData("missing-tune")]
    [InlineData("missing-tune-guard")]
    [InlineData("tune-guard-span")]
    [InlineData("provider-target")]
    public void UpdatedStoreDescriptorRejectsMissingOrUnboundedContractData(string fault)
    {
        var document = Document();
        switch (fault)
        {
            case "missing-store-guard": document["storeIdentity"]!["codeGuards"] = new JsonArray(); break;
            case "store-guard-span": document["storeIdentity"]!["codeGuards"]![0]!["rva"] = ImageSize - 1; break;
            case "missing-tune": document.Remove("tune"); break;
            case "missing-tune-guard": document["tune"]!["codeGuards"]!.AsArray().RemoveAt(0); break;
            case "tune-guard-span": document["tune"]!["codeGuards"]![0]!["rva"] = ImageSize - 1; break;
            case "provider-target": document["tune"]!["providerSlots"]![0]!["targetRva"] = 4096; break;
        }
        Assert.Throws<FormatException>(() => NativeHudCompatibilityPack.Parse(JsonSerializer.SerializeToUtf8Bytes(document)));
    }

    [Fact]
    public void ExternalCopyOfUpdatedStorePackStillRequiresASignedEnvelope()
    {
        var catalog = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, null,
            new Dictionary<string, byte[]>());
        var result = catalog.Install(JsonSerializer.SerializeToUtf8Bytes(Document()),
            new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));

        Assert.False(result.Success);
        Assert.Equal(NativeCompatibilityInstallCode.InvalidEnvelope, result.Code);
        Assert.Equal(0, catalog.Generation);
        Assert.Null(catalog.FindStore(PackageName, ImageSize));
    }

    private static JsonObject Document()
    {
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.UpdatedStore.json");
        Assert.NotNull(stream);
        return JsonNode.Parse(stream)!.AsObject();
    }

    private static NativeCompatibilityCatalog Catalog() => new(NativeHudBuildContract.BuiltIn, null,
        new Dictionary<string, byte[]>(), NativeHudBuildContract.AdditionalBuiltIns);
}
