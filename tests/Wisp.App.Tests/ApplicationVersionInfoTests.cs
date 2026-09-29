using System.Xml.Linq;
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
        Assert.Equal("2.5.2", ApplicationVersionInfo.MachineVersion);
        Assert.Equal("2.5.2", ApplicationVersionInfo.DisplayVersion);
        var project = ProjectMetadata();
        Assert.Equal(project.GetValueOrDefault("WispDiagnosticBuildId"), ApplicationVersionInfo.DiagnosticBuildId);
        Assert.Equal(project.GetValueOrDefault("WispDiagnosticBuildLabel"), ApplicationVersionInfo.DiagnosticBuildLabel);
        if (ApplicationVersionInfo.DiagnosticBuildId is null)
        {
            Assert.Null(ApplicationVersionInfo.DiagnosticBuildLabel);
            Assert.Equal("WHEEL-INDICATED SPEED PANEL 2.5.2", ApplicationVersionInfo.FooterText);
            Assert.Contains("current 2.5.2 entry covers this release", ApplicationVersionInfo.ReleaseHistoryIntroduction);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(ApplicationVersionInfo.DiagnosticBuildId));
            Assert.False(string.IsNullOrWhiteSpace(ApplicationVersionInfo.DiagnosticBuildLabel));
            Assert.Equal($"WHEEL-INDICATED SPEED PANEL {ApplicationVersionInfo.DiagnosticBuildLabel} (private)", ApplicationVersionInfo.FooterText);
            Assert.Contains($"You are testing {ApplicationVersionInfo.DiagnosticBuildLabel}.", ApplicationVersionInfo.ReleaseHistoryIntroduction);
        }
        Assert.Equal(ApplicationVersionInfo.DisplayVersion, ReleaseNotesCatalog.Entries[0].Version);
    }

    private static Dictionary<string, string?> ProjectMetadata()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var project = Path.Combine(directory.FullName, "src", "Wisp.App", "Wisp.App.csproj");
            if (File.Exists(project))
                return XDocument.Load(project).Descendants("AssemblyMetadata")
                    .ToDictionary(element => (string)element.Attribute("Include")!, element => (string?)element.Attribute("Value"));
        }
        throw new DirectoryNotFoundException("The application project could not be located.");
    }
}
