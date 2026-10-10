using System.Collections.Immutable;
using System.Text.Json;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementarySystemContextTests
{
    [Fact]
    public void MatchingProductPresenceDoesNotExposeProcessNamesOrImplyEnabledOverlay()
    {
        var queried = new List<string>();
        var values = SupplementarySystemContext.ReadSoftware(name => { queried.Add(name); return name == "Discord"; }, () => TimeSpan.Zero);
        Assert.Equal(6, values.Length);
        Assert.All(values, item => Assert.True(item.IsValid));
        Assert.Equal("present", values.Single(v => v.Family == "discord").State);
        Assert.All(values.Where(v => v.Family != "discord"), item => Assert.Equal("not-observed", item.State));
        Assert.DoesNotContain("DiscordCanary", queried);
        var encoded = JsonSerializer.Serialize(values, SupplementarySchema.Json);
        Assert.DoesNotContain("Discord", encoded);
        Assert.DoesNotContain("enabled", encoded);
    }

    [Fact]
    public void FailedLookupAndBudgetExhaustionRemainUnknown()
    {
        var queried = 0;
        var failures = SupplementarySystemContext.ReadSoftware(_ => { queried++; throw new InvalidOperationException(); }, () => TimeSpan.Zero);
        Assert.All(failures, item => Assert.Equal("unknown", item.State));
        Assert.Equal(9, queried);
        queried = 0;
        var exhausted = SupplementarySystemContext.ReadSoftware(_ => { queried++; return true; }, () => TimeSpan.FromSeconds(1));
        Assert.All(exhausted, item => Assert.Equal("unknown", item.State));
        Assert.Equal(0, queried);
    }

    [Fact]
    public void PrivateOrUnboundedContextIsRejectedButOlderSnapshotRemainsValid()
    {
        var now = DateTimeOffset.UtcNow;
        var baseline = new SupplementaryEnvironment(1, now, 26100, 8, "unavailable", "api-unavailable", [], [],
            new("gpu", "mph", "minimal", false), new("not-measured", "not-measured", "not-measured", "unavailable"));
        Assert.True(baseline.IsValid(now));
        Assert.True((baseline with { Power = new("custom", "unknown", "unknown"), Software = [] }).IsValid(now));
        Assert.False((baseline with { Power = new("private plan name", "ac", "disabled") }).IsValid(now));
        Assert.False((baseline with { Software = [new("private-program", "present")] }).IsValid(now));
        Assert.False((baseline with { Software = [new("obs", "enabled")] }).IsValid(now));
        Assert.False((baseline with { Software = [new("obs", "present"), new("obs", "unknown")] }).IsValid(now));
        Assert.False((baseline with { Software = default(ImmutableArray<SupplementarySoftware>) }).IsValid(now));
        Assert.Equal("balanced", SupplementarySystemContext.SchemeFamily(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e")));
        Assert.Equal("custom", SupplementarySystemContext.SchemeFamily(Guid.NewGuid()));
    }

    [Fact]
    public void CompiledIdentityWinsOverRepeatedPrivateLabelsAndLegacyRemainsExplicit()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        Assert.Equal(first.ToString("N"), SupplementarySchema.CompiledBuildIdentity(first, "reused-test-label"));
        Assert.NotEqual(SupplementarySchema.CompiledBuildIdentity(first, "reused-test-label"),
            SupplementarySchema.CompiledBuildIdentity(second, "reused-test-label"));
        Assert.Equal("legacy-build", SupplementarySchema.CompiledBuildIdentity(null, "legacy-build"));
        Assert.Equal("unknown", SupplementarySchema.CompiledBuildIdentity(Guid.Empty, "private path/not-a-build"));
    }

    [Theory]
    [InlineData("{invalid", "failure")]
    [InlineData("null", "failure")]
    [InlineData("{\"SettingsRevision\":9,\"Smoothing\":0.25,\"OverlayOpacity\":0.8}", "success")]
    public void SettingsFallbackOutcomeIsFixedAndLoadStillPreservesExistingBehavior(string contents, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp-settings-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            File.WriteAllText(path, contents);
            var service = new SettingsService(path);
            var loaded = service.Load();
            Assert.NotNull(loaded);
            Assert.Equal(expected, service.LoadOutcome);
            if (expected == "success") { Assert.Equal(0.25, loaded.Smoothing); Assert.Equal(0.8, loaded.OverlayOpacity); }
            File.Delete(path);
            Assert.NotNull(service.Load());
            Assert.Equal("none", service.LoadOutcome);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
