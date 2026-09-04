using Xunit;

namespace Wisp.App.Tests;

public sealed class ReleaseNotesCatalogTests
{
    [Fact]
    public void CatalogCoversEveryDocumentedPostLaunchVersionInDescendingOrder()
    {
        Assert.Equal(
            ["1.0.11", "1.0.10", "1.0.8", "1.0.7", "1.0.6", "1.0.5", "1.0.4", "1.0.3", "1.0.2", "1.0.1"],
            ReleaseNotesCatalog.Entries.Select(entry => entry.Version));
        Assert.True(ReleaseNotesCatalog.Entries[0].IsCurrent);
        Assert.All(ReleaseNotesCatalog.Entries.Skip(1), entry => Assert.False(entry.IsCurrent));
        Assert.All(ReleaseNotesCatalog.Entries, entry =>
        {
            Assert.NotEmpty(entry.Groups);
            Assert.All(entry.Groups, group => Assert.NotEmpty(group.Items));
        });
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
