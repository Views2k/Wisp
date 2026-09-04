using System.Xml.Linq;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DashboardQolContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void DashboardExposesUpdatePeakAndTorqueUnitControls()
    {
        var path = Path.Combine(ProjectRoot(), "src", "Wisp.App", "MainWindow.xaml");
        var layout = XDocument.Load(path);

        Assert.NotNull(layout.Descendants(Presentation + "Border")
            .SingleOrDefault(element => HasAttribute(element, "Name", "DashboardUpdateBanner")));
        Assert.Contains(layout.Descendants(), element =>
            (string?)element.Attribute("Text") == "{Binding DashboardPeakPower, StringFormat=PEAK {0}}");
        Assert.Contains(layout.Descendants(), element =>
            (string?)element.Attribute("Text") == "{Binding DashboardPeakTorque, StringFormat=PEAK {0}}");
        Assert.Contains(layout.Descendants(), element =>
            (string?)element.Attribute("Text") == "{Binding DashboardTopSpeed, StringFormat=TOP {0}}");
        Assert.Contains(layout.Descendants(), element =>
            HasAttribute(element, "Name", "NewtonMetersRadio"));
        Assert.Contains(layout.Descendants(), element =>
            HasAttribute(element, "Name", "PoundFeetRadio"));
        Assert.Contains(layout.Descendants(), element =>
            (string?)element.Attribute("Click") == "StarWispOnGitHub_Click");
    }

    private static bool HasAttribute(XElement element, string name, string value) =>
        element.Attributes().Any(attribute => attribute.Name.LocalName == name && attribute.Value == value);

    private static string ProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Wisp.sln.");
    }
}
