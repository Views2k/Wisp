using System.Xml.Linq;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ElectricNativeRoutingTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Local = "clr-namespace:Wisp.App";

    [Fact]
    public void OverlayContainsOnePanelForEachNativePowertrainAndPresentation()
    {
        var document = XDocument.Load(Path.Combine(AppSourceDirectory(), "OverlayWindow.xaml"));

        AssertPanel(document, "NativeDigitalPanel", "NativeDigitalSpeedometer");
        AssertPanel(document, "NativeAnalogPanel", "NativeAnalogSpeedometer");
        AssertPanel(document, "NativeElectricDigitalPanel", "NativeElectricDigitalSpeedometer");
        AssertPanel(document, "NativeElectricAnalogPanel", "NativeElectricAnalogSpeedometer");
    }

    [Fact]
    public void AppControllerRoutesTheCurrentTelemetryPowertrainToTheOverlay()
    {
        var source = File.ReadAllText(Path.Combine(AppSourceDirectory(), "AppController.cs"));

        Assert.Contains(
            "Overlay?.SetElectricPowertrain(current.IsElectric);",
            source,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            source.Split(
                "SetElectricPowertrain(current.IsElectric)",
                StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData("NativeElectricDigitalSpeedometer.xaml", "Wisp.App.NativeElectricDigitalSpeedometer")]
    [InlineData("NativeElectricAnalogSpeedometer.xaml", "Wisp.App.NativeElectricAnalogSpeedometer")]
    public void ElectricNativeControlFilesDeclareTheirRuntimeClass(string fileName, string className)
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), fileName);

        Assert.True(File.Exists(xamlPath), $"Missing Electric native control: {fileName}");
        var document = XDocument.Load(xamlPath);
        Assert.Equal(className, document.Root?.Attribute(Xaml + "Class")?.Value);
        Assert.True(
            File.Exists(Path.ChangeExtension(xamlPath, ".xaml.cs")),
            $"Missing Electric native code-behind: {fileName}.cs");
    }

    private static void AssertPanel(XDocument document, string panelName, string controlName)
    {
        var panels = document.Descendants(Presentation + "Grid")
            .Where(element => element.Attribute(Xaml + "Name")?.Value == panelName)
            .ToArray();

        var panel = Assert.Single(panels);
        Assert.Equal("Collapsed", panel.Attribute("Visibility")?.Value);
        Assert.Single(panel.Elements(Local + controlName));
    }

    private static string AppSourceDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "Wisp.App");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Wisp.sln from the test output directory.");
    }
}
