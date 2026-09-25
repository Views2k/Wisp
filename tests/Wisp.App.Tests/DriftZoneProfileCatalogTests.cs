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
    [InlineData("gameVersion")]
    [InlineData("imageSize")]
    [InlineData("timeDateStamp")]
    public void ChangedStoreBuildIdentityHasNoAngleGuide(string field)
    {
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.Store.json")!;
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
    [InlineData("gameVersion")]
    [InlineData("executableLength")]
    [InlineData("executableSha256")]
    public void EachBuildIdentityGuardIsRequired(string field)
    {
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.BuiltIn.json")!;
        var document = JsonNode.Parse(stream)!.AsObject();
        document[field] = field switch
        {
            "gameVersion" => JsonValue.Create("6.440.854.0"),
            "executableLength" => JsonValue.Create(184055769),
            _ => JsonValue.Create(new string('A', 64))
        };
        var pack = NativeHudCompatibilityPack.Parse(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Assert.Null(DriftZoneProfileCatalog.ForBuild(pack));
    }
}
