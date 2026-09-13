using System.Xml.Linq;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SetupPresentationTests
{
    [Fact]
    public void DisplayConfirmationUsesTheFullscreenSettingName()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var wizard = XDocument.Parse(Source("SetupWindow.xaml"));
        var confirmation = Assert.Single(wizard.Descendants(), element =>
            element.Attribute(x + "Name")?.Value == "DisplayConfirmation");
        Assert.Equal("I confirmed Fullscreen display mode", confirmation.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("I confirmed Fullscreen mode.", Assert.Single(confirmation.Elements()).Attribute("Text")?.Value);
        Assert.DoesNotContain("borderless", Source("SetupWindow.xaml"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("LegacyMainWindow.xaml")]
    public void ConnectionControlsRemainInDiagnosticsWithoutASeparateSetupPage(string filename)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Parse(Source(filename));
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Setup");
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "ListBoxItem" && element.Attribute("AutomationProperties.Name")?.Value == "Setup");
        var diagnostics = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Diagnostics");
        Assert.Single(diagnostics.Descendants(), element => element.Attribute(x + "Name")?.Value == "PortApplyFeedback");
        Assert.Single(diagnostics.Descendants(), element => element.Attribute("Click")?.Value == "ApplyPort_Click");
        var help = Assert.Single(diagnostics.Descendants(), element => element.Attribute(x + "Name")?.Value == "ConnectionHelp");
        Assert.Contains(help.Descendants(), element => element.Attribute("Text")?.Value?.Contains("Fullscreen", StringComparison.Ordinal) == true);
        Assert.Contains(help.Descendants(), element => element.Attribute("Text")?.Value?.Contains("{Binding UdpPort", StringComparison.Ordinal) == true);
        var appearance = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Appearance");
        Assert.Single(appearance.Descendants(), element => element.Attribute(x + "Name")?.Value == "LegacyInterfaceToggle");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(480, true)]
    [InlineData(739.9, true)]
    [InlineData(740, false)]
    [InlineData(840, false)]
    [InlineData(double.NaN, true)]
    [InlineData(double.PositiveInfinity, true)]
    public void AppearanceStacksOnlyWhenItsControlsWouldBecomeCramped(double width, bool stacked) =>
        Assert.Equal(stacked, SetupPresentation.UseStackedAppearance(width));

    [Fact]
    public void NamedHeadingsInheritTheSharedLightTextStyle()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = XDocument.Parse(Source("App.xaml"));
        foreach (var key in new[] { "SectionTitleStyle", "EyebrowTextStyle" })
        {
            var style = Assert.Single(resources.Descendants(), element => element.Attribute(x + "Key")?.Value == key);
            Assert.Equal("{StaticResource {x:Type TextBlock}}", style.Attribute("BasedOn")?.Value);
        }
    }

    [Fact]
    public void InstallerKeepsItsSceneAndAppUsesNoninteractiveParticlesWithoutAddingThemToHud()
    {
        var wizard = XDocument.Parse(Source("SetupWindow.xaml"));
        var backdrop = Assert.Single(wizard.Descendants(), element => element.Name.LocalName == "AmbientBackdrop");
        Assert.Equal("1", backdrop.Attribute("Grid.Row")?.Value);
        Assert.Equal("3", backdrop.Attribute("Grid.RowSpan")?.Value);
        Assert.Equal("{Binding AnimatedBackground}", backdrop.Attribute("IsAnimationEnabled")?.Value);
        foreach (var file in new[] { "MainWindow.xaml", "LegacyMainWindow.xaml" })
        {
            var appBackdrop = Assert.Single(XDocument.Parse(Source(file)).Descendants(), element => element.Name.LocalName == "AmbientBackdrop");
            Assert.Equal("True", appBackdrop.Attribute("ParticlesOnly")?.Value);
            Assert.Equal("False", appBackdrop.Attribute("IsPointerInteractionEnabled")?.Value);
            Assert.Equal("{DynamicResource AppParticleColor}", appBackdrop.Attribute("ParticleColor")?.Value);
            Assert.Equal("{Binding AnimatedBackground}", appBackdrop.Attribute("IsAnimationEnabled")?.Value);
        }
        Assert.DoesNotContain("A little atmosphere", Source("MainWindow.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("AmbientBackdrop", Source("OverlayWindow.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("AmbientBackdrop", Source("GForceWindow.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void WizardSurfacesUseOpaqueLocalWebsitePalette()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var wizard = XDocument.Parse(Source("SetupWindow.xaml"));
        var expected = new Dictionary<string, string>
        {
            ["WindowBrush"] = "#090D12",
            ["PanelBrush"] = "#0D1218",
            ["CardBrush"] = "#111822",
            ["RaisedBrush"] = "#1A2832",
            ["InputBrush"] = "#0A0F15",
            ["HoverBrush"] = "#161F29",
            ["StrokeBrush"] = "#35404F",
            ["AccentBrush"] = "#63D8D4",
            ["AccentBlueBrush"] = "#63D8D4"
        };

        foreach (var pair in expected)
        {
            var brush = Assert.Single(wizard.Descendants(), element =>
                element.Name.LocalName == "SolidColorBrush" && element.Attribute(x + "Key")?.Value == pair.Key);
            Assert.Equal(pair.Value, brush.Attribute("Color")?.Value);
        }

        Assert.Equal("{StaticResource CardBrush}", StyleSetter(wizard, x, "SetupCard", "Background"));
        Assert.Equal("{StaticResource InputBrush}", StyleSetter(wizard, x, "SetupChoice", "Background"));
        Assert.Equal("{StaticResource WindowBrush}", StyleSetter(wizard, x, "SetupSegmentGroup", "Background"));
        Assert.Equal("{StaticResource PanelBrush}", StyleSetter(wizard, x, "SetupStepBadge", "Background"));
        var numberPreview = Assert.Single(wizard.Descendants(), element =>
            element.Attribute(x + "Name")?.Value == "NumberPreview");
        Assert.Equal("{StaticResource PanelBrush}", numberPreview.Attribute("Background")?.Value);
        var previewStage = Assert.Single(wizard.Descendants(), element =>
            element.Attribute(x + "Name")?.Value == "PreviewStage");
        Assert.Equal("{StaticResource InputBrush}", previewStage.Parent?.Attribute("Background")?.Value);
    }

    [Fact]
    public void WizardBackdropHasOneDirectCompositorAndNoWholeSurfaceFade()
    {
        var backdrop = Source("AmbientBackdrop.cs");
        var window = Source("SetupWindow.xaml");
        var codeBehind = Source("SetupWindow.xaml.cs");

        Assert.DoesNotContain("CoolVignette", backdrop, StringComparison.Ordinal);
        Assert.DoesNotContain("TealVignette", backdrop, StringComparison.Ordinal);
        Assert.DoesNotContain("QuietEdge", backdrop, StringComparison.Ordinal);
        Assert.DoesNotContain("LinearGradientBrush", backdrop, StringComparison.Ordinal);
        Assert.DoesNotContain("DropShadowEffect", backdrop, StringComparison.Ordinal);
        Assert.DoesNotContain("BlurEffect", backdrop, StringComparison.Ordinal);
        Assert.DoesNotContain("OpacityMask", backdrop, StringComparison.Ordinal);
        Assert.DoesNotContain("Effect=", window, StringComparison.Ordinal);
        Assert.DoesNotContain("OpacityMask", window, StringComparison.Ordinal);
        Assert.DoesNotContain("DoubleAnimation", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginAnimation", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void NativePreviewReusesTheProductionGaugeControlsAndAllowsComparisonSetup()
    {
        var source = Source("SetupWindow.xaml");
        Assert.Contains("local:NativeDigitalSpeedometer", source, StringComparison.Ordinal);
        Assert.Contains("local:NativeAnalogSpeedometer", source, StringComparison.Ordinal);
        Assert.Contains("Leave it on while comparing the two", source, StringComparison.Ordinal);
        Assert.DoesNotContain("I turned off FH6's stock speedometer", source, StringComparison.Ordinal);
        Assert.Contains("not live FH6 data", source, StringComparison.Ordinal);
        Assert.Contains("not FH6's transmission setting", source, StringComparison.Ordinal);
    }

    private static string? StyleSetter(XDocument document, XNamespace x, string key, string property)
    {
        var style = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Style" && element.Attribute(x + "Key")?.Value == key);
        return Assert.Single(style.Elements(), element =>
            element.Name.LocalName == "Setter" && element.Attribute("Property")?.Value == property)
            .Attribute("Value")?.Value;
    }

    private static string Source(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "Wisp.App", name));
    }
}
