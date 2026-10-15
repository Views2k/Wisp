using Xunit;

namespace Wisp.App.Tests;

public sealed class ReleaseNotesCatalogTests
{
    [Fact]
    public void CurrentReleaseAndEarlierVersionsAppearInDescendingOrder()
    {
        var entries = ReleaseNotesCatalog.Entries;
        var versions = entries.Select(entry => System.Version.Parse(entry.Version)).ToArray();
        Assert.Equal(versions.OrderByDescending(version => version), versions);
        Assert.Equal(versions.Length, versions.Distinct().Count());
        Assert.Equal(ApplicationVersionInfo.DisplayVersion, entries[0].Version);
        Assert.True(entries[0].IsCurrent);
        Assert.All(ReleaseNotesCatalog.Entries.Skip(1), entry => Assert.False(entry.IsCurrent));
        Assert.All(ReleaseNotesCatalog.Entries, entry =>
        {
            Assert.NotEmpty(entry.Groups);
            Assert.All(entry.Groups, group => Assert.NotEmpty(group.Items));
        });
    }

    [Fact]
    public void HistoricalHotfixDocumentsUpdatedStoreCompatibilityAndOptInVacuumDisplay()
    {
        var entry = ReleaseNotesCatalog.Entries.Single(entry => entry.Version == "1.1.3");
        Assert.Equal("1.1.3", entry.Version);
        Assert.Equal("COMPATIBILITY HOTFIX", entry.Label);
        Assert.False(entry.IsCurrent);
        Assert.Contains("3.440.853.0", entry.Summary, StringComparison.Ordinal);
        var text = string.Join(' ', entry.Groups.SelectMany(group => group.Items));
        Assert.Contains("Xbox app / Microsoft Store FH6 3.440.853.0 on Windows PC", text, StringComparison.Ordinal);
        Assert.Contains("retaining Store 3.430.771.0", text, StringComparison.Ordinal);
        Assert.Contains("signed compatibility-map updates introduced in 1.1.2", text, StringComparison.Ordinal);
        Assert.Contains("Show vacuum pressure", text, StringComparison.Ordinal);
        Assert.Contains("off by default", text, StringComparison.Ordinal);
        Assert.Contains("negative pressure reported by FH6", text, StringComparison.Ordinal);
        Assert.Contains("HUD profiles", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalCompatibilityEntryIdentifiesTheUpdatedGameBuild()
    {
        var entry = ReleaseNotesCatalog.Entries.Single(entry => entry.Version == "1.1.2");
        Assert.Equal("1.1.2", entry.Version);
        Assert.Equal("COMPATIBILITY HOTFIX", entry.Label);
        Assert.False(entry.IsCurrent);
        Assert.Contains("6.440.853.0", entry.Summary, StringComparison.Ordinal);
        var text = string.Join(' ', entry.Groups.SelectMany(group => group.Items));
        Assert.Contains("6.440.853.0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalStoreCompatibilityEntryIdentifiesTheSupportedPcBuild()
    {
        var entry = ReleaseNotesCatalog.Entries.Single(entry => entry.Version == "1.1.1");
        Assert.Equal("1.1.1", entry.Version);
        Assert.Equal("COMPATIBILITY HOTFIX", entry.Label);
        Assert.False(entry.IsCurrent);
        var text = string.Join(' ', entry.Groups.SelectMany(group => group.Items));
        Assert.Contains("3.430.771.0", text, StringComparison.Ordinal);
        Assert.Contains("Xbox app and Microsoft Store", text, StringComparison.Ordinal);
        Assert.Contains("Requires an exact match to the reviewed Store build", text, StringComparison.Ordinal);
        Assert.Contains("on Windows", entry.Summary, StringComparison.Ordinal);
        Assert.Contains("Steam compatibility path is preserved", text, StringComparison.Ordinal);
    }

    [Fact]
    public void QualityOfLifeReleaseDocumentsTheMajorWorkAndFixes()
    {
        var text = string.Join(
            ' ',
            ReleaseNotesCatalog.Entries.Single(entry => entry.Version == "1.0.10").Groups.SelectMany(group => group.Items));

        foreach (var expected in new[]
                 {
                     "torque", "top speed", "hotkey", "debug logging", "color", "HUD profiles",
                     "release summary", "tach", "wheel"
                 })
        {
            Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
