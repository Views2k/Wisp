using System.Text.Json.Nodes;
using Wisp.App.Drift;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DriftZoneBuildKeyTests
{
    [Fact]
    public void SteamAndStoreBuildsAreNamedByTheFieldsTheScoringTableMatches()
    {
        var steam = NativeHudBuildContract.UpdatedSteamBuiltIn;
        var store = NativeHudBuildContract.UpdatedStoreBuiltIn;

        Assert.Equal(
            $"steam:{steam.GameVersion}:{steam.ExecutableLength}:{steam.ImageSize}:{steam.ExecutableSha256.ToUpperInvariant()}",
            DriftZoneBuildKey.For(steam));
        Assert.Equal(
            $"store:{store.StoreIdentity!.PackageFullName}:{store.ImageSize}:{store.StoreIdentity.TimeDateStamp}",
            DriftZoneBuildKey.For(store));
        Assert.NotEqual(DriftZoneBuildKey.For(steam), DriftZoneBuildKey.For(NativeHudBuildContract.BuiltIn));
        Assert.NotEqual(DriftZoneBuildKey.For(store), DriftZoneBuildKey.For(NativeHudBuildContract.StoreBuiltIn));
    }

    [Theory]
    [InlineData("gameVersion")]
    [InlineData("executableLength")]
    [InlineData("executableSha256")]
    [InlineData("imageSize")]
    public void AnyChangedIdentityFieldNamesAnotherBuild(string field)
    {
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.UpdatedSteam.json")!;
        var document = JsonNode.Parse(stream)!.AsObject();
        var original = DriftZoneBuildKey.For(NativeHudCompatibilityPack.FromRuntimeValidation(JsonNode.Parse(document.ToJsonString())!.AsObject()));
        document[field] = field switch
        {
            "gameVersion" => JsonValue.Create("6.999.1.0"),
            "executableLength" => JsonValue.Create(document[field]!.GetValue<long>() + 1),
            "executableSha256" => JsonValue.Create(new string('A', 64)),
            _ => JsonValue.Create(document[field]!.GetValue<uint>() + 4096)
        };

        Assert.NotEqual(original, DriftZoneBuildKey.For(NativeHudCompatibilityPack.FromRuntimeValidation(document)));
    }

    [Fact]
    public void TheKnownCurveIsTheOneTheScoringTableGrants()
    {
        Assert.Equal(DriftZoneProfileCatalog.ForBuild(NativeHudBuildContract.BuiltIn), DriftZoneProfileCatalog.KnownCurve);
        Assert.Contains("drift-zone-check", Wisp.App.Supplementary.ControlledFeatures.All);
    }
}
