using System.Text;
using System.Text.Json.Nodes;
using Wisp.App.Drift;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DriftZoneProfileCatalogTests
{
    [Fact]
    public void ExactCapturedBuildReceivesTheVerifiedAngleCurve()
    {
        var profile = DriftZoneProfileCatalog.ForBuild(NativeHudBuildContract.BuiltIn);
        Assert.NotNull(profile);
        Assert.True(profile.IsValid);
        Assert.Equal(10, profile.MinimumAngleDegrees);
        Assert.Equal(110, profile.MaximumAngleDegrees);
        Assert.Equal((double)59.4f, profile.SaturationAngleDegrees);
        Assert.Equal(30, profile.MinimumAngleMultiplier);
        Assert.Equal(40, profile.MaximumAngleMultiplier);
    }

    [Fact]
    public void MissingAndPreviousBuildsHaveNoAngleGuidance()
    {
        Assert.Null(DriftZoneProfileCatalog.ForBuild(null));
        foreach (var pack in new[] { NativeHudBuildContract.PreviousSteamBuiltIn, NativeHudBuildContract.PreviousStoreBuiltIn })
            Assert.Null(DriftZoneProfileCatalog.ForBuild(pack));
    }

    [Fact]
    public void CurrentStoreBuildReceivesTheAngleGuide()
    {
        var profile = DriftZoneProfileCatalog.ForBuild(NativeHudBuildContract.StoreBuiltIn);
        Assert.NotNull(profile);
        Assert.Equal(DriftZoneProfileCatalog.ForBuild(NativeHudBuildContract.BuiltIn), profile);
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.Store.json")!;
        var document = JsonNode.Parse(stream)!.AsObject();
        var restored = NativeHudCompatibilityPack.Parse(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Assert.Equal(profile, DriftZoneProfileCatalog.ForBuild(restored));
    }

    [Theory]
    [InlineData(false, "6.461.691.0")]
    [InlineData(true, "3.461.691.0")]
    public void UpdatedOfficialBuildReceivesTheVerifiedAngleGuide(bool xboxStore, string expectedVersion)
    {
        var pack = xboxStore ? NativeHudBuildContract.UpdatedStoreBuiltIn : NativeHudBuildContract.UpdatedSteamBuiltIn;
        Assert.Equal(expectedVersion, pack.GameVersion);
        var profile = DriftZoneProfileCatalog.ForBuild(pack);
        Assert.NotNull(profile);
        Assert.True(profile.IsValid);
        var legacyPack = xboxStore ? NativeHudBuildContract.StoreBuiltIn : NativeHudBuildContract.BuiltIn;
        Assert.Equal(DriftZoneProfileCatalog.ForBuild(legacyPack), profile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewestBundledBuildMustIncludeDriftGuidance(bool xboxStore)
    {
        var latest = NativeHudBuildContract.AdditionalBuiltIns.Append(NativeHudBuildContract.BuiltIn)
            .Where(pack => (pack.StoreIdentity is not null) == xboxStore)
            .MaxBy(pack => Version.Parse(pack.GameVersion));

        Assert.NotNull(latest);
        Assert.NotNull(DriftZoneProfileCatalog.ForBuild(latest));
    }

    [Theory]
    [InlineData("gameVersion", false)]
    [InlineData("imageSize", false)]
    [InlineData("timeDateStamp", false)]
    [InlineData("gameVersion", true)]
    [InlineData("imageSize", true)]
    [InlineData("timeDateStamp", true)]
    public void ChangedStoreBuildIdentityHasNoAngleGuide(string field, bool updated)
    {
        var resource = updated ? "Wisp.NativeCompatibility.UpdatedStore.json" : "Wisp.NativeCompatibility.Store.json";
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream(resource)!;
        var document = JsonNode.Parse(stream)!.AsObject();
        if (field == "gameVersion")
        {
            document["gameVersion"] = "3.440.854.0";
            document["storeIdentity"]!["packageFullName"] = "Microsoft.ForteBaseGame_3.440.854.0_x64__8wekyb3d8bbwe";
        }
        else if (field == "imageSize") document[field] = document[field]!.GetValue<uint>() + 4096;
        else document["storeIdentity"]![field] = document["storeIdentity"]![field]!.GetValue<uint>() + 1;
        var pack = NativeHudCompatibilityPack.Parse(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Assert.Null(DriftZoneProfileCatalog.ForBuild(pack));
    }

    [Theory]
    [InlineData("gameVersion", false)]
    [InlineData("executableLength", false)]
    [InlineData("executableSha256", false)]
    [InlineData("gameVersion", true)]
    [InlineData("executableLength", true)]
    [InlineData("executableSha256", true)]
    public void EachBuildIdentityGuardIsRequired(string field, bool updated)
    {
        var resource = updated ? "Wisp.NativeCompatibility.UpdatedSteam.json" : "Wisp.NativeCompatibility.BuiltIn.json";
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream(resource)!;
        var document = JsonNode.Parse(stream)!.AsObject();
        document[field] = field switch
        {
            "gameVersion" => JsonValue.Create("6.440.854.0"),
            "executableLength" => JsonValue.Create(document[field]!.GetValue<long>() + 1),
            _ => JsonValue.Create(new string('A', 64))
        };
        var pack = NativeHudCompatibilityPack.Parse(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Assert.Null(DriftZoneProfileCatalog.ForBuild(pack));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdaptiveHudValidationAloneDoesNotClaimVerifiedScoring(bool xboxStore)
    {
        var resource = xboxStore ? "Wisp.NativeCompatibility.UpdatedStore.json" : "Wisp.NativeCompatibility.UpdatedSteam.json";
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream(resource)!;
        var document = JsonNode.Parse(stream)!.AsObject();
        document["gameVersion"] = xboxStore ? "3.999.1.0" : "6.999.1.0";
        if (xboxStore)
            document["storeIdentity"]!["packageFullName"] = "Microsoft.ForteBaseGame_3.999.1.0_x64__8wekyb3d8bbwe";
        else
            document["executableSha256"] = new string('A', 64);

        var pack = NativeHudCompatibilityPack.FromRuntimeValidation(document);

        Assert.True(pack.IsRuntimeValidated);
        Assert.Null(DriftZoneProfileCatalog.ForBuild(pack));
    }
}
