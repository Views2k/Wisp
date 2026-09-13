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
    public void MissingPreviousAndStoreBuildsCannotClaimVerifiedGuidance()
    {
        Assert.Null(DriftZoneProfileCatalog.ForBuild(null));
        foreach (var pack in NativeHudBuildContract.AdditionalBuiltIns)
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
