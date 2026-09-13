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

        Assert.Single(layout.Descendants(XName.Get("OrbitSurface", "clr-namespace:Wisp.App")),
            element => HasAttribute(element, "Name", "DashboardUpdateBanner"));
        AssertLabeledValue(layout, "DashboardPeakPower", "Peak ");
        AssertLabeledValue(layout, "DashboardPeakTorque", "Peak ");
        AssertLabeledValue(layout, "DashboardTopSpeed", "Top ");
        Assert.Single(layout.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Click") == "ResetDashboardPeaks_Click");
        Assert.Contains(layout.Descendants(), element =>
            HasAttribute(element, "Name", "NewtonMetersRadio"));
        Assert.Contains(layout.Descendants(), element =>
            HasAttribute(element, "Name", "PoundFeetRadio"));
        Assert.Contains(layout.Descendants(), element =>
            (string?)element.Attribute("Click") == "StarWispOnGitHub_Click");
    }

    [Fact]
    public void DashboardMetricRunsOnlyReadTheirViewModelValues()
    {
        var path = Path.Combine(ProjectRoot(), "src", "Wisp.App", "MainWindow.xaml");
        var layout = XDocument.Load(path);
        var dashboard = Assert.Single(layout.Descendants(Presentation + "TabItem"), element =>
            HasAttribute(element, "Name", "DashboardTab"));
        var bindings = dashboard.Descendants(Presentation + "Run")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text?.StartsWith("{Binding ", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(bindings);
        Assert.All(bindings, text => Assert.Matches(@",\s*Mode=OneWay\s*[,}]", text!));
    }

    private static void AssertLabeledValue(XDocument layout, string property, string label)
    {
        var value = Assert.Single(layout.Descendants(Presentation + "Run"), element =>
            (string?)element.Attribute("Text") == $"{{Binding {property}, Mode=OneWay}}");
        Assert.Equal(Presentation + "TextBlock", value.Parent!.Name);
        Assert.Equal(new[] { label, $"{{Binding {property}, Mode=OneWay}}" },
            value.Parent.Elements(Presentation + "Run").Select(element => (string?)element.Attribute("Text")));
        Assert.Equal("SemiBold", (string?)value.Attribute("FontWeight"));
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
