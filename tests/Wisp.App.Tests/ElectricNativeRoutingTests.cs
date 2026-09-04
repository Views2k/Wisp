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

        AssertPanel(document, "NativeDigitalPanel", "NativeDigitalSpeedometer", "320", "160");
        AssertPanel(document, "NativeAnalogPanel", "NativeAnalogSpeedometer", "293", "293.5");
        AssertPanel(document, "NativeElectricDigitalPanel", "NativeElectricDigitalSpeedometer", "320", "160");
        AssertPanel(document, "NativeElectricAnalogPanel", "NativeElectricAnalogSpeedometer", "345", "345");
    }

    [Fact]
    public void DigitalBoostRailsReuseTheNativeRailBoundsWithoutMovingNativeControls()
    {
        var document = XDocument.Load(Path.Combine(AppSourceDirectory(), "OverlayWindow.xaml"));
        var root = document.Descendants(Presentation + "Grid")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "RootPanel");

        var rail = document.Descendants(Local + "DigitalBoostRailView")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "AttachedDigitalBoost");
        Assert.Equal("302", rail.Attribute("Width")?.Value);
        Assert.Equal("88", rail.Attribute("Height")?.Value);
        Assert.Equal("11,132,0,0", rail.Attribute("Margin")?.Value);
        Assert.Equal("{Binding DigitalBoostGaugeColorNumber}", rail.Attribute("ColorNumber")?.Value);
        Assert.Equal("{Binding DigitalBoostGaugeStockColors}", rail.Attribute("UseStockColors")?.Value);
        Assert.Equal("{Binding SelectedBoostPressureUnit}", rail.Attribute("PressureUnit")?.Value);
        Assert.Equal("Left", rail.Attribute("HorizontalAlignment")?.Value);
        Assert.Same(root, rail.Parent);
    }

    [Fact]
    public void AttachedAnalogueBoostGaugesAreNotClippedByNativePanels()
    {
        var document = XDocument.Load(Path.Combine(AppSourceDirectory(), "OverlayWindow.xaml"));
        var root = document.Descendants(Presentation + "Grid")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "RootPanel");

        var gauge = document.Descendants(Local + "AnalogBoostGaugeView")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "AttachedAnalogBoost");
        Assert.Equal("Left", gauge.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Top", gauge.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("{Binding BoostGaugeColorNumber}", gauge.Attribute("ColorNumber")?.Value);
        Assert.Equal("{Binding SelectedBoostPressureUnit}", gauge.Attribute("PressureUnit")?.Value);
        Assert.Same(root, gauge.Parent);

        Assert.DoesNotContain(
            document.Descendants(Local + "DigitalBoostRailView"),
            element => element.Attribute(Xaml + "Name")?.Value?.Contains("Electric", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            document.Descendants(Local + "AnalogBoostGaugeView"),
            element => element.Attribute(Xaml + "Name")?.Value?.Contains("Electric", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void TireTemperatureGaugesShareTheNativeRootAndRemainAvailableForElectricLayouts()
    {
        var document = XDocument.Load(Path.Combine(AppSourceDirectory(), "OverlayWindow.xaml"));
        var root = document.Descendants(Presentation + "Grid")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "RootPanel");

        var digital = document.Descendants(Local + "DigitalTireTemperatureGaugeView")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "AttachedDigitalTireTemperature");
        var analogue = document.Descendants(Local + "AnalogTireTemperatureGaugeView")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "AttachedAnalogTireTemperature");

        Assert.Same(root, digital.Parent);
        Assert.Same(root, analogue.Parent);
        Assert.Equal("{Binding TireTemperatureDisplay}", digital.Attribute("Display")?.Value);
        Assert.Equal("{Binding TireTemperatureDisplay}", analogue.Attribute("Display")?.Value);
        Assert.Equal("{Binding NativeGaugeFrame.IsElectric}", analogue.Attribute("IsElectricMaterial")?.Value);
        Assert.DoesNotContain("IsElectric", digital.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AppearanceOffersIndependentAnalogueAndDigitalPsiColorToggles()
    {
        var document = XDocument.Load(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var toggles = document.Descendants(Presentation + "CheckBox")
            .Where(element => element.Attribute("IsChecked")?.Value is
                "{Binding BoostGaugeColorNumber}" or
                "{Binding DigitalBoostGaugeColorNumber}")
            .ToArray();

        Assert.Equal(2, toggles.Length);
        Assert.Contains(toggles, element =>
            element.Attribute("AutomationProperties.Name")?.Value ==
            "Color the analogue boost number by load");
        Assert.Contains(toggles, element =>
            element.Attribute("AutomationProperties.Name")?.Value ==
            "Color the digital boost number by load");
    }

    [Fact]
    public void AppearanceOffersAnIndependentStockDigitalBoostMaterialToggle()
    {
        var document = XDocument.Load(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var toggle = document.Descendants(Presentation + "CheckBox")
            .Single(element => element.Attribute("IsChecked")?.Value ==
                               "{Binding DigitalBoostGaugeStockColors}");

        Assert.Equal(
            "Use the stock tachometer material on the digital boost rail",
            toggle.Attribute("AutomationProperties.Name")?.Value);
    }

    [Fact]
    public void AppearanceOffersBarBoostPressureToggle()
    {
        var document = XDocument.Load(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var toggle = document.Descendants(Presentation + "CheckBox")
            .Single(element => element.Attribute("IsChecked")?.Value ==
                               "{Binding UseBarBoostPressure}");

        Assert.Equal("Display boost pressure in bar", toggle.Attribute("Content")?.Value);
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

    private static void AssertPanel(
        XDocument document,
        string panelName,
        string controlName,
        string width,
        string height)
    {
        var panels = document.Descendants(Presentation + "Grid")
            .Where(element => element.Attribute(Xaml + "Name")?.Value == panelName)
            .ToArray();

        var panel = Assert.Single(panels);
        Assert.Equal("Collapsed", panel.Attribute("Visibility")?.Value);
        Assert.Equal(width, panel.Attribute("Width")?.Value);
        Assert.Equal(height, panel.Attribute("Height")?.Value);
        Assert.Equal("False", panel.Attribute("ClipToBounds")?.Value);

        var control = Assert.Single(panel.Elements(Local + controlName));
        Assert.Equal(width, control.Attribute("Width")?.Value);
        Assert.Equal(height, control.Attribute("Height")?.Value);
        Assert.Equal("Left", control.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Top", control.Attribute("VerticalAlignment")?.Value);
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
