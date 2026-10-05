using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeUpdatedSteamCompatibilityTests
{
    private const string Version = "6.461.691.0";
    private const long ExecutableLength = 184038360;
    private const string ExecutableHash = "B8C18EC88AB3143DA03C3BB9BD9D0761F23E00F809B1BD9D50CF07496E5D1CE5";

    [Fact]
    public void EmbeddedUpdatedSteamPackIsSelectedForItsExactIdentity()
    {
        var pack = NativeHudBuildContract.UpdatedSteamBuiltIn;
        Assert.Equal("fh6-steam-6.461.691.0-x64", pack.Id);
        Assert.Equal(188481536U, pack.ImageSize);
        Assert.Null(pack.StoreIdentity);
        Assert.Same(pack, Catalog().Find(Version, ExecutableLength, ExecutableHash));
        Assert.NotNull(pack.NativeGauge);
        Assert.NotNull(pack.GameplayVisibility);
        Assert.NotNull(pack.Tune);
        Assert.Equal(176870224UL, pack.SourceVectorRva);
        Assert.Equal(113554928UL, pack.LeadVtableRva);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("missing-hash")]
    public void UpdatedSteamPackDoesNotAdmitAnUnrecognizedExecutable(string fault)
    {
        var version = fault == "version" ? "6.461.691.1" : Version;
        var length = ExecutableLength + (fault == "length" ? 1 : 0);
        var hash = fault switch
        {
            "hash" => new string('A', 64),
            "missing-hash" => null,
            _ => ExecutableHash
        };
        Assert.Null(Catalog().Find(version, length, hash));
        Assert.False(NativeHudBuildContract.UpdatedSteamBuiltIn.Matches(version, length, hash));
    }

    [Fact]
    public void AdditionalUpdatedPackPreservesEveryExistingBuiltInAndLegacyContract()
    {
        var catalog = Catalog();
        foreach (var pack in new[]
                 {
                     NativeHudBuildContract.BuiltIn, NativeHudBuildContract.PreviousSteamBuiltIn,
                     NativeHudBuildContract.StoreBuiltIn, NativeHudBuildContract.PreviousStoreBuiltIn
                 })
        {
            var selected = pack.StoreIdentity is { } store
                ? catalog.FindStore(store.PackageFullName, pack.ImageSize)
                : catalog.Find(pack.GameVersion, pack.ExecutableLength, pack.ExecutableSha256);
            Assert.Same(pack, selected);
        }
        Assert.Equal("6.440.853.0", NativeHudBuildContract.SupportedVersion);
        Assert.Equal(184055768, NativeHudBuildContract.SupportedExecutableLength);
        Assert.Equal("FEC4A63CDEAD26F6528564F0E33C3D7FD02CD887337ED6E1AEACA79EB2843FCD", NativeHudBuildContract.SupportedSha256);
        Assert.Equal(176882448UL, NativeHudBuildContract.SourceVectorRva);
        Assert.Equal(113448576UL, NativeHudBuildContract.LeadVtableRva);
    }

    [Fact]
    public void UpdatedTuneRetainsReviewedRolesBoundsAndAssetRequirements()
    {
        var pack = NativeHudBuildContract.UpdatedSteamBuiltIn;
        var tune = Assert.IsType<NativeTuneCompatibilityLayout>(pack.Tune);
        var previous = Assert.IsType<NativeTuneCompatibilityLayout>(NativeHudBuildContract.BuiltIn.Tune);
        Assert.Equal(previous.ProfileId, tune.ProfileId);
        Assert.Equal(previous.SemanticsVersion, tune.SemanticsVersion);
        Assert.Equal(19, tune.Rvas.Count);
        Assert.Equal(previous.Rvas.Keys.Order(), tune.Rvas.Keys.Order());
        Assert.Equal(58, tune.CodeGuards.Count);
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
        Assert.Equal(52181200UL, tune.ProviderSlots[0x12E8]);
        Assert.Equal(51618208UL, tune.ProviderSlots[0]);
        Assert.Equal(previous.Asset.MinimumLength, tune.Asset.MinimumLength);
        Assert.Equal(previous.Asset.MaximumLength, tune.Asset.MaximumLength);
        Assert.Equal(previous.Asset.HeaderPageCount, tune.Asset.HeaderPageCount);
        Assert.Equal(previous.Asset.Parts.OrderBy(pair => pair.Key), tune.Asset.Parts.OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData("missing-guard")]
    [InlineData("guard-span")]
    [InlineData("provider-target")]
    public void UpdatedDescriptorRejectsIncompleteOrUnboundedGuards(string fault)
    {
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.UpdatedSteam.json");
        Assert.NotNull(stream);
        var document = JsonNode.Parse(stream)!.AsObject();
        var tune = document["tune"]!;
        switch (fault)
        {
            case "missing-guard": tune["codeGuards"]!.AsArray().RemoveAt(0); break;
            case "guard-span": tune["codeGuards"]![0]!["rva"] = document["imageSize"]!.GetValue<uint>() - 1; break;
            case "provider-target": tune["providerSlots"]![0]!["targetRva"] = 4096; break;
        }
        Assert.Throws<FormatException>(() => NativeHudCompatibilityPack.Parse(JsonSerializer.SerializeToUtf8Bytes(document)));
    }

    private static NativeCompatibilityCatalog Catalog() => new(NativeHudBuildContract.BuiltIn, null,
        new Dictionary<string, byte[]>(), NativeHudBuildContract.AdditionalBuiltIns);
}
