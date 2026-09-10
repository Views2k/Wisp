using Wisp.Update;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ApplicationVersionInfoTests
{
    [Theory]
    [InlineData("1.1.0", "1.1")]
    [InlineData("2.0.0", "2.0")]
    [InlineData("1.0.12", "1.0.12")]
    [InlineData("1.1.10", "1.1.10")]
    public void DisplayFormattingDoesNotChangeMachineIdentity(string machine, string display)
    {
        var version = SemanticVersion.Parse(machine);
        Assert.Equal(display, ApplicationVersionInfo.Format(version));
        Assert.Equal(display, ApplicationVersionInfo.Format(version.ToSystemVersion()));
        Assert.Equal(machine, version.ToString());
    }

    [Fact]
    public void CurrentVersionLabelsShareTheAssemblyVersion()
    {
        Assert.Equal("1.2.1", ApplicationVersionInfo.MachineVersion);
        Assert.Equal("1.2.1", ApplicationVersionInfo.DisplayVersion);
        Assert.Null(ApplicationVersionInfo.DiagnosticBuildId);
        Assert.Null(ApplicationVersionInfo.DiagnosticBuildLabel);
        Assert.EndsWith("PANEL 1.2.1", ApplicationVersionInfo.FooterText);
        Assert.Contains("The current 1.2.1 entry covers this release.", ApplicationVersionInfo.ReleaseHistoryIntroduction);
        Assert.Equal(ApplicationVersionInfo.DisplayVersion, ReleaseNotesCatalog.Entries[0].Version);
    }
}
