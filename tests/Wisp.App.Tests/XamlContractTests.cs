using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class XamlContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Local = "clr-namespace:Wisp.App";
    private static readonly Regex BindingPathPattern = new(
        @"\{Binding(?:\s+Path\s*=\s*|\s+)(?<path>[A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AncestorTypePattern = new(
        @"RelativeSource\s*=\s*\{RelativeSource\s+AncestorType\s*=\s*\{x:Type\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)\s*\}\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> RoutedHandlerAttributes =
    [
        "Click",
        "Checked",
        "Unchecked",
        "PreviewKeyDown",
        "LostKeyboardFocus",
        "ValueChanged",
        "SelectionChanged",
        "SelectedColorChanged",
        "ResetRequested"
    ];

    private static readonly HashSet<string> InteractiveElements =
    [
        "Button",
        "CheckBox",
        "ListBox",
        "ListBoxItem",
        "RadioButton",
        "Slider",
        "TabItem",
        "TextBox"
    ];

    [Fact]
    public void EveryDeclaredRoutedHandlerExistsOnItsXamlClass()
    {
        var failures = new List<string>();
        var appAssembly = typeof(DiagnosticsViewModel).Assembly;

        foreach (var xamlPath in AppXamlFiles())
        {
            var document = LoadXaml(xamlPath);
            var className = document.Root?.Attribute(Xaml + "Class")?.Value;
            if (string.IsNullOrWhiteSpace(className))
            {
                continue;
            }

            var ownerType = appAssembly.GetType(className);
            if (ownerType is null)
            {
                failures.Add($"{RelativePath(xamlPath)}: x:Class '{className}' does not resolve in Wisp.App.");
                continue;
            }

            foreach (var attribute in document.Root!.DescendantsAndSelf().Attributes().Where(IsRoutedHandlerAttribute))
            {
                var handlerName = attribute.Value.Trim();
                if (handlerName.Length == 0 || handlerName.StartsWith('{'))
                {
                    continue;
                }

                var handlers = ownerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => method.Name == handlerName && method.GetParameters().Length == 2)
                    .ToArray();
                if (handlers.Length != 1)
                {
                    failures.Add(
                        $"{Location(xamlPath, attribute)}: {attribute.Name.LocalName}=\"{handlerName}\" " +
                        $"must resolve to one two-parameter method on {className}; found {handlers.Length}.");
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void EveryBindingPathResolvesOnItsDeclaredSource()
    {
        var failures = new List<string>();

        foreach (var xamlPath in AppXamlFiles())
        {
            var document = LoadXaml(xamlPath);
            foreach (var attribute in document.Root!.DescendantsAndSelf().Attributes())
            {
                foreach (Match match in BindingPathPattern.Matches(attribute.Value))
                {
                    var path = match.Groups["path"].Value;
                    var sourceType = BindingSourceType(attribute.Value, document, attribute.Parent);
                    if (sourceType is null)
                    {
                        failures.Add($"{Location(xamlPath, attribute)}: Binding source is not covered by this contract.");
                    }
                    else if (!BindingPathResolves(sourceType, path, BindingSourceElement(attribute.Value, document)))
                    {
                        failures.Add(
                            $"{Location(xamlPath, attribute)}: Binding path '{path}' does not resolve on " +
                            $"{sourceType.Name}.");
                    }
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void AncestorBindingContractValidatesControlPropertiesWithoutSkippingThem()
    {
        var button = BindingSourceType("{Binding Foreground, RelativeSource={RelativeSource AncestorType={x:Type Button}}}");
        var group = BindingSourceType("{Binding IsMouseOver, RelativeSource={RelativeSource AncestorType={x:Type StackPanel}}}");

        Assert.Equal(typeof(System.Windows.Controls.Button), button);
        Assert.Equal(typeof(System.Windows.Controls.StackPanel), group);
        Assert.True(BindingPathResolves(button!, "Foreground"));
        Assert.True(BindingPathResolves(group!, "IsMouseOver"));
        Assert.False(BindingPathResolves(button!, "ForegroundTypo"));
        Assert.False(BindingPathResolves(group!, "IsMouseOverTypo"));
        Assert.Null(BindingSourceType("{Binding Foreground, RelativeSource={RelativeSource Self}}"));
        Assert.Null(BindingSourceType("{Binding Foreground, ElementName=MissingControl}"));
        Assert.Equal(typeof(DiagnosticsViewModel), BindingSourceType("{Binding StatusText}"));
    }

    [Fact]
    public void NamedElementBindingContractValidatesTheNamedControlsProperties()
    {
        var document = XDocument.Parse("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <TabControl x:Name="RootTabs">
                    <TabItem Header="Dashboard" />
                    <TabItem Header="Extras" />
                </TabControl>
            </Grid>
            """);
        var tabs = BindingSourceType("{Binding SelectedIndex, ElementName=RootTabs}", document);

        Assert.Equal(typeof(System.Windows.Controls.TabControl), tabs);
        Assert.True(BindingPathResolves(tabs!, "SelectedIndex"));
        Assert.False(BindingPathResolves(tabs!, "SelectedIndexTypo"));
        var source = BindingSourceElement("{Binding SelectedItem.Header, ElementName=RootTabs}", document);
        Assert.True(BindingPathResolves(tabs!, "SelectedItem.Header", source));
        Assert.False(BindingPathResolves(tabs!, "SelectedItem.HeaderTypo", source));
        Assert.False(BindingPathResolves(tabs!, "SelectedItem.Header"));
        Assert.Null(BindingSourceType("{Binding SelectedIndex, ElementName=MissingControl}", document));
        Assert.Null(BindingSourceType("{Binding SelectedIndex, ElementName=RootTabs}"));
    }

    [Fact]
    public void TypedDataTemplateBindingContractValidatesTheDeclaredDataProperties()
    {
        var document = XDocument.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                          xmlns:local="clr-namespace:Wisp.App"
                          DataType="{x:Type local:AppColorTheme}">
                <TextBlock Text="{Binding Name}" />
            </DataTemplate>
            """);
        var target = document.Descendants(Presentation + "TextBlock").Single();
        var sourceType = BindingSourceType("{Binding Name}", document, target);

        Assert.Equal(typeof(AppColorTheme), sourceType);
        foreach (var path in new[] { "Name", "Accent" })
            Assert.True(BindingPathResolves(sourceType!, path));
        Assert.False(BindingPathResolves(sourceType!, "AccentTypo"));
        document.Root!.Attribute("DataType")!.Remove();
        Assert.Null(BindingSourceType("{Binding Name}", document, target));
    }

    [Fact]
    public void ExtrasOffersOneLargeColorEditorWithSevenSelectableTargets()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var extras = document.Descendants(Presentation + "TabItem")
            .Single(element => element.Attribute("Header")?.Value == "Extras");
        var layout = extras.Descendants(Presentation + "Grid")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "ColorEditorLayout");
        var editors = layout.Elements(Local + "ColorWheelEditor").ToArray();
        var selector = layout.Descendants(Presentation + "ListBox").Single(element =>
            element.Attribute(Xaml + "Name")?.Value == "ColorTargetSelector");
        var targets = selector.Elements(Presentation + "ListBoxItem")
            .Select(element => element.Attribute("Content")?.Value)
            .ToArray();

        Assert.Equal("Stretch", layout.Attribute("HorizontalAlignment")?.Value);
        var editor = Assert.Single(editors);
        Assert.Null(editor.Attribute("Width"));
        Assert.Equal("Stretch", editor.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("ColorEditor_SelectedColorChanged", editor.Attribute("SelectedColorChanged")?.Value);
        Assert.Equal("ColorTargetSelector_SelectionChanged", selector.Attribute("SelectionChanged")?.Value);
        Assert.Equal(new[]
        {
            "App accent", "Background and surfaces", "HUD border",
            "Gauge start", "Gauge middle", "Gauge end", "Traction hook cue"
        }, targets);
        Assert.Empty(layout.Descendants(Presentation + "ComboBox"));
        Assert.DoesNotContain(extras.Descendants(Presentation + "ListBox"), element =>
            (element.Attribute("AutomationProperties.Name")?.Value ?? string.Empty).Contains("palette", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(document.Descendants(Presentation + "Button"), element =>
            element.Attribute("Content")?.Value == "Use current theme");

        var footer = document.Descendants(Presentation + "TextBlock")
            .Single(element => element.Attribute("Text")?.Value?.StartsWith(
                "WHEEL-INDICATED SPEED PANEL", StringComparison.Ordinal) == true);
        Assert.Equal("WHEEL-INDICATED SPEED PANEL 1.0.11", footer.Attribute("Text")?.Value);
    }

    [Fact]
    public void ColorWheelRemainsReadableWhenSelectedBrightnessIsLow()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "ColorWheelEditor.xaml"));
        var source = File.ReadAllText(Path.Combine(AppSourceDirectory(), "ColorWheelEditor.xaml.cs"));

        Assert.DoesNotContain(document.Descendants(), element =>
            element.Attribute(Xaml + "Name")?.Value == "WheelShade");
        Assert.Single(document.Descendants(Presentation + "Slider"), element =>
            element.Attribute(Xaml + "Name")?.Value == "BrightnessSlider");
        Assert.Contains("ColorCustomization.FromHsv(_hue, _saturation, _brightness, _opacity)",
            source, StringComparison.Ordinal);
        Assert.DoesNotContain("1 - _brightness", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfilesHaveOneDedicatedPageAndAStyledSaveConfirmation()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var appearance = document.Descendants(Presentation + "TabItem")
            .Single(element => element.Attribute("Header")?.Value == "Appearance");
        var profiles = document.Descendants(Presentation + "TabItem")
            .Single(element => element.Attribute("Header")?.Value == "Profiles");
        var extras = document.Descendants(Presentation + "TabItem")
            .Single(element => element.Attribute("Header")?.Value == "Extras");
        var saveButtons = document.Descendants(Presentation + "Button")
            .Where(element => element.Attribute("Click")?.Value == "SaveHudProfile_Click")
            .ToArray();

        Assert.Single(saveButtons);
        Assert.Contains(saveButtons, button => appearance.Descendants().Contains(button));
        Assert.DoesNotContain(saveButtons, button => profiles.Descendants().Contains(button));
        Assert.DoesNotContain(saveButtons, button => extras.Descendants().Contains(button));
        Assert.Single(profiles.Descendants(Presentation + "ListBox"), element =>
            element.Attribute(Xaml + "Name")?.Value == "HudProfileList");
        foreach (var action in new[] { "Apply", "Update", "Rename", "Delete" })
        {
            Assert.Single(profiles.Descendants(Presentation + "Button"), element =>
                element.Attribute("Content")?.Value == action);
        }

        var dialog = document.Descendants(Presentation + "Grid")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "HudProfileDialog");
        Assert.Equal("Collapsed", dialog.Attribute("Visibility")?.Value);
        Assert.Equal("Cycle", dialog.Attribute("KeyboardNavigation.TabNavigation")?.Value);
        Assert.Equal("HudProfileDialog_KeyDown", dialog.Attribute("KeyDown")?.Value);
        Assert.Equal("0", dialog.Elements(Presentation + "Border").Single().Attribute("BorderThickness")?.Value);
        Assert.Single(dialog.Descendants(Presentation + "TextBox"), element =>
            element.Attribute(Xaml + "Name")?.Value == "HudProfileNameInput" &&
            element.Attribute("MaxLength")?.Value == HudPreset.MaximumNameLength.ToString());
        Assert.Single(dialog.Descendants(Presentation + "Button"), element =>
            element.Attribute("Click")?.Value == "ConfirmHudProfileDialog_Click");
    }

    [Fact]
    public void ApplyingAProfileCarriesThePreviousLayoutIntoPlacementRestoration()
    {
        var source = File.ReadAllText(Path.Combine(AppSourceDirectory(), "AppController.cs"));
        var apply = Regex.Match(
            source,
            @"public bool TryApplyHudPreset.*?(?=\r?\n    private void SyncHudPresetToViewModel)",
            RegexOptions.Singleline).Value;

        Assert.Matches(
            @"previousLayoutMode = Settings\.LayoutMode;\s*var previousNativeGaugeMode = Settings\.NativeGaugeMode;\s*preset\.ApplyTo\(Settings\);",
            apply);
        Assert.Contains(
            "ApplyViewOptions(previousLayoutMode, previousNativeGaugeMode)",
            apply,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowKeepsAllColorCustomizationsIndependent()
    {
        var code = File.ReadAllText(Path.Combine(AppSourceDirectory(), "MainWindow.xaml.cs"));
        var constructor = Regex.Match(
            code,
            @"public MainWindow\(AppController controller\).*?(?=\r?\n    private void )",
            RegexOptions.Singleline).Value;
        var targetLoader = Handler(code, "LoadSelectedColorTarget");
        var colorHandler = Handler(code, "ColorEditor_SelectedColorChanged");

        Assert.Contains("AppColorThemes.Resolve(controller.Settings.ColorTheme)", constructor, StringComparison.Ordinal);
        Assert.Contains("AppBackgroundThemes.Resolve(controller.Settings.BackgroundTheme)", constructor, StringComparison.Ordinal);
        Assert.Contains("AppColorThemes.Resolve(controller.Settings.HudBorderTheme)", constructor, StringComparison.Ordinal);
        Assert.Contains("controller.Settings.CustomAccentColor", constructor, StringComparison.Ordinal);
        Assert.Contains("controller.Settings.CustomBackgroundColor", constructor, StringComparison.Ordinal);
        Assert.Contains("controller.Settings.CustomHudBorderColor", constructor, StringComparison.Ordinal);
        Assert.Contains("controller.Settings.CustomBoostLowColor", constructor, StringComparison.Ordinal);
        Assert.Contains("ColorCustomization.ResolveTractionCue(controller.Settings)", constructor, StringComparison.Ordinal);

        Assert.Contains("MinimumOpacity = 0.82", targetLoader, StringComparison.Ordinal);
        Assert.Contains("MaximumBrightness = 0.34", targetLoader, StringComparison.Ordinal);
        Assert.Contains("_controller.SetCustomAccentColor(value)", colorHandler, StringComparison.Ordinal);
        Assert.Contains("_controller.SetCustomBackgroundColor(value)", colorHandler, StringComparison.Ordinal);
        Assert.Contains("_controller.SetCustomHudBorderColor(value)", colorHandler, StringComparison.Ordinal);
        Assert.Contains("ApplyGaugeColors(", colorHandler, StringComparison.Ordinal);
        Assert.Contains("_controller.SetCustomTractionCueColor(value)", colorHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryNativeSpeedReadoutDisplaysTheTractionHookCue()
    {
        var app = LoadXaml(Path.Combine(AppSourceDirectory(), "App.xaml"));
        var style = app.Descendants(Presentation + "Style")
            .Single(element => element.Attribute(Xaml + "Key")?.Value == "NativeTractionCueDigitStyle");
        Assert.Single(style.Elements(Presentation + "Setter"), setter =>
            setter.Attribute("Property")?.Value == "Fill" &&
            setter.Attribute("Value")?.Value == "{DynamicResource TractionCueBrush}");
        Assert.Single(style.Descendants(Presentation + "DataTrigger"), trigger =>
            trigger.Attribute("Binding")?.Value.Contains("IsTractionCueActive", StringComparison.Ordinal) == true &&
            trigger.Attribute("Value")?.Value == "True");

        foreach (var file in new[]
                 {
                     "NativeDigitalSpeedometer.xaml", "NativeAnalogSpeedometer.xaml",
                     "NativeElectricDigitalSpeedometer.xaml", "NativeElectricAnalogSpeedometer.xaml"
                 })
        {
            var document = LoadXaml(Path.Combine(AppSourceDirectory(), file));
            var cueDigits = document.Descendants(Presentation + "Rectangle")
                .Where(element => element.Attribute("Style")?.Value.Contains(
                    "NativeTractionCueDigitStyle", StringComparison.Ordinal) == true)
                .ToArray();
            Assert.Equal(3, cueDigits.Length);
            Assert.All(cueDigits, digit =>
            {
                Assert.Contains("Opacity", digit.Attribute("Opacity")?.Value, StringComparison.Ordinal);
                Assert.Single(digit.Descendants(Presentation + "ImageBrush"), brush =>
                    brush.Attribute("ImageSource")?.Value.Contains("Image", StringComparison.Ordinal) == true);
            });
        }
    }

    [Fact]
    public void GameplayHudVisibilityHasASeparateReadOnlyDiagnosticRow()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var value = Assert.Single(document.Descendants(Presentation + "TextBlock"), element =>
            BindingPath(element.Attribute("Text")?.Value) == nameof(DiagnosticsViewModel.GameplayHudVisibility) &&
            element.Parent!.Elements(Presentation + "TextBlock")
                .Any(sibling => sibling.Attribute("Text")?.Value == "Gameplay HUD visibility"));
        var label = Assert.Single(value.Parent!.Elements(Presentation + "TextBlock"),
            element => element.Attribute("Text")?.Value == "Gameplay HUD visibility");

        Assert.Contains("Mode=OneWay", value.Attribute("Text")!.Value);
        Assert.Equal(label.Attribute("Grid.Row")?.Value, value.Attribute("Grid.Row")?.Value);
        Assert.False(typeof(DiagnosticsViewModel).GetProperty(nameof(DiagnosticsViewModel.GameplayHudVisibility))!
            .SetMethod!.IsPublic);
        Assert.DoesNotContain(document.Descendants(Presentation + "TextBlock"),
            element => element.Attribute("Text")?.Value == "Native HUD state");
    }

    [Fact]
    public void OverlayVisibilityShortcutHasAnOptInEditorAndDiagnosticStatus()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var enabled = Assert.Single(document.Descendants(Presentation + "CheckBox"), element =>
            BindingPath(element.Attribute("IsChecked")?.Value) ==
            nameof(DiagnosticsViewModel.OverlayHotkeyEnabled));
        var editor = Assert.Single(document.Descendants(Presentation + "Button"), element =>
            element.Attribute(Xaml + "Name")?.Value == "OverlayHotkeyCaptureButton");
        var statuses = document.Descendants(Presentation + "TextBlock")
            .Where(element => BindingPath(element.Attribute("Text")?.Value) ==
                              nameof(DiagnosticsViewModel.OverlayHotkeyStatus))
            .ToArray();

        Assert.Equal("Option_Changed", enabled.Attribute("Checked")?.Value);
        Assert.Equal(nameof(DiagnosticsViewModel.OverlayHotkeyText),
            BindingPath(editor.Attribute("Content")?.Value));
        Assert.Equal("OverlayHotkeyCapture_Click", editor.Attribute("Click")?.Value);
        Assert.Contains(statuses, element => element.Parent!.Elements(Presentation + "TextBlock")
            .Any(sibling => sibling.Attribute("Text")?.Value == "Visibility shortcut"));
        Assert.Contains(statuses, element =>
            element.Ancestors(Presentation + "StackPanel").Any(ancestor =>
                ancestor.Descendants(Presentation + "CheckBox").Contains(enabled)));
    }

    [Fact]
    public void EveryMainWindowControlHasAnAccessibleNameOrTextLabel()
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), "MainWindow.xaml");
        var document = LoadXaml(xamlPath);
        var failures = new List<string>();

        foreach (var element in document.Descendants()
                     .Where(element => element.Name.Namespace == Presentation &&
                                       InteractiveElements.Contains(element.Name.LocalName)))
        {
            var automationName = element.Attribute("AutomationProperties.Name")?.Value;
            var content = element.Attribute("Content")?.Value;
            var header = element.Attribute("Header")?.Value;
            var hasDescriptiveAutomationName = !string.IsNullOrWhiteSpace(automationName);
            var hasTextLabel = IsDescriptiveText(content) || IsDescriptiveText(header);

            if (!hasDescriptiveAutomationName && !hasTextLabel)
            {
                failures.Add(
                    $"{Location(xamlPath, element)}: {element.Name.LocalName} needs descriptive Content/Header " +
                    "or AutomationProperties.Name.");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void MainWindowExposesBothPersistedSpeedSources()
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), "MainWindow.xaml");
        var document = LoadXaml(xamlPath);
        var radios = document.Descendants(Presentation + "RadioButton").ToArray();

        var wheel = Assert.Single(
            radios,
            element => element.Attribute(Xaml + "Name")?.Value == "WheelSpeedSourceRadio");
        var vehicle = Assert.Single(
            radios,
            element => element.Attribute(Xaml + "Name")?.Value == "Fh6SpeedSourceRadio");

        Assert.Equal("SpeedSource", wheel.Attribute("GroupName")?.Value);
        Assert.Equal("SpeedSource", vehicle.Attribute("GroupName")?.Value);
        Assert.Equal("SpeedSource_Checked", wheel.Attribute("Checked")?.Value);
        Assert.Equal("SpeedSource_Checked", vehicle.Attribute("Checked")?.Value);
        Assert.Equal("Wheel-indicated", wheel.Attribute("Content")?.Value);
        Assert.Equal("FH6 speed", vehicle.Attribute("Content")?.Value);
    }

    [Fact]
    public void LayoutPreviewsRepresentRuntimeGForceWindowPolicy()
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), "MainWindow.xaml");
        var document = LoadXaml(xamlPath);

        var minimalPreview = FindLayoutPreview(document, 0);
        var combinedPreview = FindLayoutPreview(document, 1);
        var separatePreview = FindLayoutPreview(document, 2);
        var nativePreview = FindLayoutPreview(document, 3);

        AssertPreviewMeterCondition(xamlPath, minimalPreview, shouldBeConditional: true, "Minimal");
        AssertPreviewMeterCondition(xamlPath, combinedPreview, shouldBeConditional: false, "Combined");
        AssertPreviewMeterCondition(xamlPath, separatePreview, shouldBeConditional: true, "Two boxes");
        Assert.Empty(nativePreview.Descendants(Local + "GForceMeterView"));
        var nativeMeters = nativePreview.Descendants(Local + "NativeGForceMeterView").ToArray();
        Assert.Equal(2, nativeMeters.Length);
        Assert.All(
            nativeMeters,
            meter => Assert.True(MeterVisibilityDependsOnGForceEnabled(meter, nativePreview)));
    }

    [Fact]
    public void HudBorderThemeIsScopedToCombinedAndTwoBoxLayouts()
    {
        const string themedBrush = "{DynamicResource HudBorderBrush}";
        var mainWindow = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));

        foreach (var name in new[]
                 {
                     "CombinedPreviewBorder",
                     "SeparateSpeedPreviewBorder",
                     "SeparateGForcePreviewBorder"
                 })
        {
            var border = mainWindow.Descendants(Presentation + "Border")
                .Single(element => element.Attribute(Xaml + "Name")?.Value == name);
            Assert.Equal(themedBrush, border.Attribute("BorderBrush")?.Value);
        }

        Assert.DoesNotContain(
            FindLayoutPreview(mainWindow, 0).DescendantsAndSelf(),
            element => element.Attributes().Any(attribute => attribute.Value.Contains(
                "HudBorderBrush",
                StringComparison.Ordinal)));
        Assert.DoesNotContain(
            FindLayoutPreview(mainWindow, 3).DescendantsAndSelf(),
            element => element.Attributes().Any(attribute => attribute.Value.Contains(
                "HudBorderBrush",
                StringComparison.Ordinal)));

        var overlay = LoadXaml(Path.Combine(AppSourceDirectory(), "OverlayWindow.xaml"));
        foreach (var name in new[] { "CombinedBorder", "BoxedSpeedBorder" })
        {
            var border = overlay.Descendants(Presentation + "Border")
                .Single(element => element.Attribute(Xaml + "Name")?.Value == name);
            Assert.Equal(themedBrush, border.Attribute("BorderBrush")?.Value);
        }

        var gForce = LoadXaml(Path.Combine(AppSourceDirectory(), "GForceWindow.xaml"));
        var gForceBorder = gForce.Descendants(Presentation + "Border")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "GForcePanelBorder");
        var style = gForceBorder.Element(Presentation + "Border.Style")!
            .Element(Presentation + "Style")!;
        var defaultBorder = style.Elements(Presentation + "Setter")
            .Single(setter => setter.Attribute("Property")?.Value == "BorderBrush");
        Assert.Equal("#66394A5D", defaultBorder.Attribute("Value")?.Value);
        var twoBoxTrigger = style.Descendants(Presentation + "DataTrigger")
            .Single(trigger =>
                BindingPath(trigger.Attribute("Binding")?.Value) == "LayoutSelectionIndex" &&
                trigger.Attribute("Value")?.Value == "2");
        var themedBorder = twoBoxTrigger.Descendants(Presentation + "Setter")
            .Single(setter => setter.Attribute("Property")?.Value == "BorderBrush");
        Assert.Equal(themedBrush, themedBorder.Attribute("Value")?.Value);

        foreach (var document in new[] { overlay, gForce })
        {
            var editBorder = document.Descendants(Presentation + "Border")
                .Single(element => element.Attribute(Xaml + "Name")?.Value == "EditBorder");
            var editBorderBrush = editBorder
                .Element(Presentation + "Border.Style")!
                .Element(Presentation + "Style")!
                .Elements(Presentation + "Setter")
                .Single(setter => setter.Attribute("Property")?.Value == "BorderBrush");

            Assert.Equal("Transparent", editBorderBrush.Attribute("Value")?.Value);
        }
    }

    [Fact]
    public void NativeGForceSkinIsCompactBorderlessAndUsesNativeNeutralPalette()
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), "NativeGForceMeterView.xaml");
        var document = LoadXaml(xamlPath);
        var root = document.Root!;
        var width = double.Parse(root.Attribute("Width")!.Value, CultureInfo.InvariantCulture);
        var height = double.Parse(root.Attribute("Height")!.Value, CultureInfo.InvariantCulture);
        var xaml = File.ReadAllText(xamlPath);

        Assert.InRange(width, 140, 150);
        Assert.InRange(height, 95, 105);
        Assert.Empty(document.Descendants(Presentation + "Border"));
        Assert.DoesNotContain("AccentBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("55E6C1", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#80FFFFFF", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#66FFFFFF", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#4DFFFFFF", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#CCFFFFFF", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GForceOffsetX", xaml, StringComparison.Ordinal);
        Assert.Contains("GForceOffsetY", xaml, StringComparison.Ordinal);

        var verticalAxis = document.Descendants(Presentation + "Rectangle")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "VerticalAxis");
        Assert.Equal("68", verticalAxis.Attribute("Height")?.Value);
        Assert.Equal("Center", verticalAxis.Attribute("VerticalAlignment")?.Value);
        Assert.Null(verticalAxis.Attribute("Margin"));
    }

    [Theory]
    [InlineData("GForceMeterView.xaml", "{StaticResource AccentBrush}")]
    [InlineData("NativeGForceMeterView.xaml", "#66FFFFFF")]
    public void GForceMetersShareTheBoundedTelemetryTrail(string fileName, string expectedBrush)
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), fileName));
        var trails = document.Descendants(Local + "GForceTrailView").ToArray();

        var trail = Assert.Single(trails);
        Assert.Equal("{Binding GForceTrailPosition}", trail.Attribute("Position")?.Value);
        Assert.Equal("{Binding HasLiveTelemetry}", trail.Attribute("IsActive")?.Value);
        Assert.Equal(expectedBrush, trail.Attribute("TrailBrush")?.Value);
    }

    [Fact]
    public void GForceTrailUsesOneFixedHistoryWithoutASecondAnimationLoop()
    {
        var source = File.ReadAllText(Path.Combine(AppSourceDirectory(), "GForceTrailView.cs"));

        Assert.Contains("private readonly Point[] _samples = new Point[Capacity];", source, StringComparison.Ordinal);
        Assert.Contains("internal const int Capacity = 38;", source, StringComparison.Ordinal);
        Assert.Contains(
            "private readonly Pen?[] _segmentPens = new Pen?[GForceTrailHistory.Capacity - 1];",
            source,
            StringComparison.Ordinal);
        Assert.Contains("drawingContext.DrawLine(pen, previous, current);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CompositionTarget.Rendering", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlWindowPackagesItsOriginalLogo()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var logo = document.Descendants(Presentation + "Image")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "HeaderLogo");
        Assert.Equal("pack://application:,,,/Wisp;component/Assets/Wisp-logo.png", logo.Attribute("Source")?.Value);
        var project = XDocument.Load(Path.Combine(AppSourceDirectory(), "Wisp.App.csproj"));
        Assert.Contains(project.Descendants("Resource"), resource =>
            resource.Attribute("Include")?.Value == @"Assets\Wisp-logo.png");
    }

    [Fact]
    public void NativePreviewUsesTheRealControlsInACenteredNaturalSizeComposition()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var preview = document.Descendants(Presentation + "Viewbox")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "NativePreviewScaleView");
        Assert.Equal("Center", preview.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Center", preview.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("Uniform", preview.Attribute("Stretch")?.Value);
        Assert.Null(preview.Elements().Single().Attribute("Height"));
        Assert.Null(preview.Elements().Single().Attribute("Width"));
        var gauges = preview.Descendants().Where(element => element.Name.Namespace == Local &&
            element.Name.LocalName.EndsWith("Speedometer", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, gauges.Length);
        Assert.All(gauges, gauge => Assert.Equal("{Binding NativePreviewFrame}", gauge.Attribute("Frame")?.Value));
        Assert.Contains(document.Descendants(Presentation + "TextBlock"), element =>
            element.Attribute("Text")?.Value == "{Binding PreviewCaption}");
    }

    [Fact]
    public void NativePreviewIncludesConfiguredBoostAndTireTemperatureGauges()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var digital = document.Descendants(Local + "DigitalBoostRailView")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "NativeDigitalBoostPreview");
        var analogue = document.Descendants(Local + "AnalogBoostGaugeView")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "NativeAnalogBoostPreview");
        var digitalTire = document.Descendants(Local + "DigitalTireTemperatureGaugeView")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "NativeDigitalTireTemperaturePreview");
        var analogueTire = document.Descendants(Local + "AnalogTireTemperatureGaugeView")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "NativeAnalogTireTemperaturePreview");

        Assert.Equal("{Binding PreviewBoostDisplay}", digital.Attribute("Display")?.Value);
        Assert.Equal("{Binding SelectedBoostPressureUnit}", digital.Attribute("PressureUnit")?.Value);
        Assert.Equal("{Binding DigitalBoostGaugeColorNumber}", digital.Attribute("ColorNumber")?.Value);
        Assert.Equal("{Binding DigitalBoostGaugeStockColors}", digital.Attribute("UseStockColors")?.Value);
        Assert.Equal("{Binding PreviewBoostDisplay}", analogue.Attribute("Display")?.Value);
        Assert.Equal("{Binding SelectedBoostPressureUnit}", analogue.Attribute("PressureUnit")?.Value);
        Assert.Equal("{Binding BoostGaugeColorNumber}", analogue.Attribute("ColorNumber")?.Value);
        Assert.Contains("{Binding BoostGaugeEnabled}", digital.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding BoostGaugeEnabled}", analogue.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding BoostGaugeAttached}", analogue.ToString(), StringComparison.Ordinal);
        Assert.Equal("{Binding PreviewTireTemperatureDisplay}", digitalTire.Attribute("Display")?.Value);
        Assert.Equal("{Binding PreviewTireTemperatureDisplay}", analogueTire.Attribute("Display")?.Value);
        Assert.Equal("{Binding SelectedTireTemperatureUnit}", digitalTire.Attribute("TemperatureUnit")?.Value);
        Assert.Equal("{Binding SelectedTireTemperatureUnit}", analogueTire.Attribute("TemperatureUnit")?.Value);
        Assert.Equal("{Binding TireTemperatureReactiveColors}", digitalTire.Attribute("ReactiveColors")?.Value);
        Assert.Equal("{Binding TireTemperatureReactiveColors}", analogueTire.Attribute("ReactiveColors")?.Value);
        Assert.Contains("{Binding TireTemperatureGaugeEnabled}", digitalTire.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding TireTemperatureGaugeEnabled}", analogueTire.ToString(), StringComparison.Ordinal);

        var digitalMeter = digital.Parent!.Elements(Local + "NativeGForceMeterView").Single();
        Assert.Contains("176,0,0,0", digitalMeter.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding GForceAttached}", digitalMeter.ToString(), StringComparison.Ordinal);
        var analogueMeter = analogue.Parent!.Elements(Local + "NativeGForceMeterView").Single();
        Assert.Contains("195,0,0,0", analogueMeter.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding GForceAttached}", analogueMeter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowGridRowsDoNotCollapseUnintentionallyOntoTheLastRow()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        foreach (var grid in document.Descendants(Presentation + "Grid"))
        {
            var rowCount = grid.Element(Presentation + "Grid.RowDefinitions")?.Elements().Count();
            if (rowCount is null)
            {
                continue;
            }

            foreach (var child in grid.Elements().Where(element => element.Attribute("Grid.Row") is not null))
            {
                var row = int.Parse(child.Attribute("Grid.Row")!.Value, CultureInfo.InvariantCulture);
                Assert.InRange(row, 0, rowCount.Value - 1);
            }
        }
    }

    [Fact]
    public void NativeAssistCompositionMatchesTheAuthoritativeDigitalAndAnalogueLayouts()
    {
        var digital = LoadXaml(Path.Combine(AppSourceDirectory(), "NativeDigitalSpeedometer.xaml"));
        var digitalContent = digital.Descendants(Presentation + "StackPanel")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "SpeedometerContent");
        Assert.Equal("0,0,-7.5,0", digitalContent.Attribute("Margin")?.Value);
        var digitalStack = digital.Descendants(Presentation + "StackPanel")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "AssistStack");
        Assert.Equal("0,4,0,0", digitalStack.Attribute("Margin")?.Value);
        var digitalImages = digitalStack.Descendants(Presentation + "Image").ToArray();
        Assert.Equal(["StmImage", "AbsImage", "LcImage", "TcrImage"],
            digitalImages.Select(image => image.Attribute(Xaml + "Name")?.Value ?? string.Empty).ToArray());
        Assert.All(digitalImages, image =>
        {
            Assert.Equal("54", image.Attribute("Width")?.Value);
            Assert.Equal("32", image.Attribute("Height")?.Value);
            Assert.Equal("0,-6", image.Parent?.Attribute("Margin")?.Value);
        });

        var analogue = LoadXaml(Path.Combine(AppSourceDirectory(), "NativeAnalogSpeedometer.xaml"));
        Assert.Equal("293", analogue.Root!.Attribute("Width")?.Value);
        Assert.Equal("293.5", analogue.Root.Attribute("Height")?.Value);
        Assert.Equal("False", analogue.Root.Attribute("ClipToBounds")?.Value);
        var analogueCoordinates = analogue.Descendants(Presentation + "Grid")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "NativeCoordinateGrid");
        Assert.Equal("288", analogueCoordinates.Attribute("Width")?.Value);
        Assert.Equal("288", analogueCoordinates.Attribute("Height")?.Value);
        Assert.Equal("0,0,5,-5.5", analogueCoordinates.Attribute("Margin")?.Value);
        var analogueGauge = analogue.Descendants(Local + "NativeAnalogGaugeVisual").Single();
        Assert.Equal("293", analogueGauge.Attribute("Width")?.Value);
        Assert.Equal("293.5", analogueGauge.Attribute("Height")?.Value);
        Assert.Equal("False", analogueGauge.Attribute("ClipToBounds")?.Value);
        var analogueMaterial = analogue.Descendants(Local + "NativeAnalogMaterialVisual").Single();
        Assert.Equal("288", analogueMaterial.Attribute("Width")?.Value);
        Assert.Equal("288", analogueMaterial.Attribute("Height")?.Value);
        var analogueOverlay = analogue.Descendants(Presentation + "Grid")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "OverlayGrid");
        Assert.Equal("0,0,0,3", analogueOverlay.Attribute("Margin")?.Value);
        var needle = analogue.Descendants(Presentation + "Canvas")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "Needle");
        Assert.Equal("288", needle.Attribute("Width")?.Value);
        Assert.Equal("288", needle.Attribute("Height")?.Value);
        Assert.Equal("False", needle.Attribute("ClipToBounds")?.Value);
        Assert.Equal("0.5,0.5", needle.Attribute("RenderTransformOrigin")?.Value);
        Assert.NotNull(needle.Descendants(Presentation + "RotateTransform").SingleOrDefault());
        var needleMaterial = needle.Elements(Local + "NativeAnalogNeedleVisual").Single();
        Assert.Equal("178.5", needleMaterial.Attribute("Canvas.Left")?.Value);
        Assert.Equal("54", needleMaterial.Attribute("Canvas.Top")?.Value);
        Assert.Equal("110", needleMaterial.Attribute("Width")?.Value);
        Assert.Equal("180", needleMaterial.Attribute("Height")?.Value);
        Assert.DoesNotContain(
            analogue.Descendants(Presentation + "Path"),
            element => element.Attribute(Xaml + "Name")?.Value is "Needle" or "NeedleGlow" or "NeedleShadow" or "NeedleRim");
        var analogueGrid = analogue.Descendants(Presentation + "Grid")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "AssistGrid");
        var analogueDecorators = analogueGrid.Elements(Presentation + "Decorator").ToArray();
        Assert.Equal(4, analogueDecorators.Length);
        var analogueImages = analogueDecorators
            .Select(decorator => decorator.Elements(Presentation + "Image").Single())
            .ToArray();
        Assert.Equal(["AbsImage", "TcrImage", "LcImage", "StmImage"],
            analogueImages.Select(image => image.Attribute(Xaml + "Name")?.Value ?? string.Empty).ToArray());
        Assert.All(analogueDecorators, decorator =>
        {
            Assert.Equal("0.5,0.5", decorator.Attribute("RenderTransformOrigin")?.Value?.Replace(" ", string.Empty));
            Assert.NotNull(decorator.Descendants(Presentation + "RotateTransform").SingleOrDefault());
        });
        Assert.All(analogueImages, image =>
        {
            Assert.Equal("54", image.Attribute("Width")?.Value);
            Assert.Equal("33", image.Attribute("Height")?.Value);
            Assert.Equal("0,0,0,132", image.Attribute("Margin")?.Value);
            Assert.Null(image.Attribute("RenderTransformOrigin"));
            Assert.Empty(image.Descendants(Presentation + "RotateTransform"));
        });

        var gaugeSource = File.ReadAllText(Path.Combine(AppSourceDirectory(), "NativeAnalogGaugeVisual.cs"));
        Assert.DoesNotContain("DrawLine(", gaugeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GaugeShadeBrush", gaugeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GaugeHaloBrush", gaugeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GaugeHighlightBrush", gaugeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NumberShadeBrush", gaugeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ElectricAssistCompositionKeepsTheNativeDigitalAndAnalogueOrder()
    {
        var digital = LoadXaml(Path.Combine(AppSourceDirectory(), "NativeElectricDigitalSpeedometer.xaml"));
        var digitalStack = digital.Descendants(Presentation + "StackPanel")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "AssistStack");
        Assert.Equal("0,4,0,0", digitalStack.Attribute("Margin")?.Value);
        var digitalImages = digitalStack.Descendants(Presentation + "Image").ToArray();
        Assert.Equal(
            ["StmImage", "AbsImage", "LcImage", "TcrImage"],
            digitalImages.Select(image => image.Attribute(Xaml + "Name")?.Value ?? string.Empty).ToArray());
        Assert.All(digitalImages, image =>
        {
            Assert.Equal("54", image.Attribute("Width")?.Value);
            Assert.Equal("32", image.Attribute("Height")?.Value);
            Assert.Equal("0,-6", image.Parent?.Attribute("Margin")?.Value);
        });

        var analogue = LoadXaml(Path.Combine(AppSourceDirectory(), "NativeElectricAnalogSpeedometer.xaml"));
        var analogueGrid = analogue.Descendants(Presentation + "Grid")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "AssistGrid");
        var analogueDecorators = analogueGrid.Elements(Presentation + "Decorator").ToArray();
        Assert.Equal(4, analogueDecorators.Length);
        var analogueImages = analogueDecorators
            .Select(decorator => decorator.Elements(Presentation + "Image").Single())
            .ToArray();
        Assert.Equal(
            ["AbsImage", "TcrImage", "LcImage", "StmImage"],
            analogueImages.Select(image => image.Attribute(Xaml + "Name")?.Value ?? string.Empty).ToArray());
        Assert.All(analogueDecorators, decorator =>
        {
            Assert.Equal("0.5,0.5", decorator.Attribute("RenderTransformOrigin")?.Value?.Replace(" ", string.Empty));
            Assert.NotNull(decorator.Descendants(Presentation + "RotateTransform").SingleOrDefault());
        });
        Assert.All(analogueImages, image =>
        {
            Assert.Equal("54", image.Attribute("Width")?.Value);
            Assert.Equal("33", image.Attribute("Height")?.Value);
            Assert.Equal("0,0,0,138", image.Attribute("Margin")?.Value);
        });
    }

    [Fact]
    public void ElectricAnaloguePowerAvailabilityCannotHideTheSpeedDigits()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "NativeElectricAnalogSpeedometer.xaml"));
        var speedDigits = document.Descendants(Presentation + "StackPanel")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "SpeedDigitsPanel");
        Assert.Equal(
            ["HundredsImage", "TensImage", "OnesImage"],
            speedDigits.Descendants(Presentation + "Image")
                .Select(image => image.Attribute(Xaml + "Name")?.Value ?? string.Empty)
                .ToArray());

        var powerBar = document.Descendants(Presentation + "StackPanel")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "PowerBarPanel");
        var powerImages = powerBar.Descendants(Presentation + "Image")
            .Select(image => image.Attribute(Xaml + "Name")?.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(["RegenLabelImage", "PowerLabelImage"], powerImages);
    }

    [Fact]
    public void VisibleHudConsumesTelemetryIndependentlyFromWpfPresentation()
    {
        var source = File.ReadAllText(Path.Combine(AppSourceDirectory(), "AppController.cs"));

        Assert.Contains("CompositionTarget.Rendering += OnCompositionRendering", source, StringComparison.Ordinal);
        Assert.Contains("_displayFrameRateCounter.Observe(currentRendering.RenderingTime)", source, StringComparison.Ordinal);
        Assert.Contains("_dispatcher.BeginInvoke(DispatcherPriority.Render, ProcessPendingActivation)", source,
            StringComparison.Ordinal);
        Assert.Contains("ProcessUiUpdate(processLatestPacket: true)", source, StringComparison.Ordinal);

        var packetStart = source.IndexOf("private void OnPacketAvailable", StringComparison.Ordinal);
        var packetEnd = source.IndexOf("private void ProcessPendingActivation", packetStart, StringComparison.Ordinal);
        Assert.True(packetStart >= 0 && packetEnd > packetStart);
        var packetHandler = source[packetStart..packetEnd];
        Assert.DoesNotContain("_wasDrivingConnected", packetHandler, StringComparison.Ordinal);
        var renderStart = source.IndexOf("private void OnCompositionRendering", StringComparison.Ordinal);
        var renderEnd = source.IndexOf("private void OnUiTimer", renderStart, StringComparison.Ordinal);
        Assert.True(renderStart >= 0 && renderEnd > renderStart);
        var renderHandler = source[renderStart..renderEnd];
        Assert.DoesNotContain("ProcessUiUpdate", renderHandler, StringComparison.Ordinal);
        Assert.Contains("_nativeHudProcessService.RequestNativeGaugeSample();", renderHandler,
            StringComparison.Ordinal);
        Assert.Contains("PublishNativeHudSnapshot(latest);", renderHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugLoggingIsExplicitLocalOptInBesideCompatibilityImport()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var importButton = document.Descendants(Presentation + "Button")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "CompatibilityImportButton");
        var toggle = document.Descendants(Presentation + "CheckBox")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "DebugLoggingToggle");

        Assert.Same(importButton.Parent, toggle.Parent);
        Assert.Equal(nameof(DiagnosticsViewModel.DebugLoggingEnabled),
            BindingPath(toggle.Attribute("IsChecked")?.Value));
        Assert.Equal("DebugLoggingToggle_Click", toggle.Attribute("Click")?.Value);
        Assert.Equal("{StaticResource ToggleSwitchStyle}", toggle.Attribute("Style")?.Value);
        var status = document.Descendants(Presentation + "TextBlock")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "DebugLoggingStatusText");
        Assert.Equal(nameof(DiagnosticsViewModel.DebugLoggingStatus), BindingPath(status.Attribute("Text")?.Value));
        Assert.Equal("SemiBold", status.Attribute("FontWeight")?.Value);
        var privacyHelp = document.Descendants(Presentation + "TextBlock").Single(element =>
            element.Attribute("Text")?.Value?.Contains("No raw packets", StringComparison.Ordinal) == true);
        Assert.Equal("{DynamicResource FaintBrush}", privacyHelp.Attribute("Foreground")?.Value);
        Assert.Single(document.Descendants(Presentation + "Button"), element =>
            element.Attribute("Click")?.Value == "ExportDebugLogs_Click");
        Assert.Single(document.Descendants(Presentation + "Button"), element =>
            element.Attribute("Click")?.Value == "DeleteDebugLogs_Click");

        var controllerSource = File.ReadAllText(Path.Combine(AppSourceDirectory(), "AppController.cs"));
        var loggerSource = File.ReadAllText(Path.Combine(AppSourceDirectory(), "DebugLogging", "DebugLogService.cs"));
        Assert.Contains("CaptureDebugSample(now, latest, connectionState, age)", controllerSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", loggerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", loggerSource, StringComparison.Ordinal);
        Assert.Contains("BoundedChannelFullMode.Wait", loggerSource, StringComparison.Ordinal);
        Assert.Contains("_records.Writer.TryWrite", loggerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_records.Writer.WriteAsync", loggerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeLayoutExposesBothGaugeModesAndIndependentAxisControls()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));

        Assert.Single(
            document.Descendants(Presentation + "RadioButton"),
            element => element.Attribute(Xaml + "Name")?.Value == "NativeLayoutRadio");
        Assert.Equal(
            ["Digital", "Analogue"],
            document.Descendants(Presentation + "RadioButton")
                .Where(element => element.Attribute("GroupName")?.Value == "NativeGauge")
                .Select(element => element.Attribute("Content")?.Value ?? string.Empty)
                .ToArray());
        Assert.Single(
            document.Descendants(Presentation + "CheckBox"),
            element => BindingPath(element.Attribute("IsChecked")?.Value) == "InvertLateralG");
        Assert.Single(
            document.Descendants(Presentation + "CheckBox"),
            element => BindingPath(element.Attribute("IsChecked")?.Value) == "InvertLongitudinalG");
    }

    [Fact]
    public void StandaloneGForceControlsAreContextuallyHiddenInCombinedLayout()
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), "MainWindow.xaml");
        var document = LoadXaml(xamlPath);
        var controlNames = new[]
        {
            "Show standalone G-meter",
            "G-meter width",
            "G-meter height"
        };

        foreach (var controlName in controlNames)
        {
            var control = document.Descendants()
                .Single(element => element.Attribute("AutomationProperties.Name")?.Value == controlName);
            Assert.True(
                HasLayoutVisibilityTrigger(control, layoutIndex: 1, visibility: "Collapsed"),
                $"{Location(xamlPath, control)}: {controlName} must be hidden, not disabled, in Combined mode.");
            Assert.DoesNotContain(control.AncestorsAndSelf().Attributes("IsEnabled"), _ => true);
        }

        var combinedNotice = document.Descendants()
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "CombinedGForceNotice");
        Assert.True(HasLayoutVisibilityTrigger(combinedNotice, layoutIndex: 1, visibility: "Visible"));
    }

    [Fact]
    public void StandaloneGForceWindowActivationUsesLayoutAwareControllerPolicy()
    {
        var appSource = File.ReadAllText(Path.Combine(AppSourceDirectory(), "App.xaml.cs"));
        var controllerSource = File.ReadAllText(Path.Combine(AppSourceDirectory(), "AppController.cs"));

        Assert.Contains(
            "gForceOverlay.SetEnabled(_controller.IsStandaloneGForceWindowEnabled)",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GForceOverlay?.SetEnabled(IsStandaloneGForceWindowEnabled)",
            controllerSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CalibrationPersistenceDoesNotScheduleDeadPeriodicRefinementWrites()
    {
        var controllerSource = File.ReadAllText(Path.Combine(AppSourceDirectory(), "AppController.cs"));

        Assert.DoesNotContain("periodicRefinement", controllerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_lastSettingsSaveAtUtc", controllerSource, StringComparison.Ordinal);
        Assert.Matches(
            @"if \(profileChanged\)\s*\{\s*Settings\.Calibrations = _calibration\.ExportSnapshots\(\)\.ToList\(\);",
            controllerSource);
    }

    [Fact]
    public void MainWindowUsesRoundedChromeAndResponsiveTabScaling()
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), "MainWindow.xaml");
        var document = LoadXaml(xamlPath);
        var codeBehind = File.ReadAllText(Path.Combine(AppSourceDirectory(), "MainWindow.xaml.cs"));
        var windowChrome = document.Descendants()
            .Single(element => element.Name.LocalName == "WindowChrome");
        Assert.Equal("8", windowChrome.Attribute("CornerRadius")?.Value);
        Assert.Contains("DwmSetWindowAttribute", codeBehind, StringComparison.Ordinal);
        Assert.Contains("FitToPhysicalWorkArea", codeBehind, StringComparison.Ordinal);

        var tabs = document.Descendants(Presentation + "TabControl")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "RootTabs");
        var scrollViewers = tabs.Elements(Presentation + "TabItem")
            .Select(tab => Assert.Single(tab.Descendants(Presentation + "ScrollViewer")))
            .ToArray();
        Assert.Equal(7, scrollViewers.Length);
        Assert.All(
            scrollViewers,
            scrollViewer => Assert.Equal(
                "Disabled",
                scrollViewer.Attribute("HorizontalScrollBarVisibility")?.Value));

        var scaleViews = document.Descendants(Presentation + "Viewbox")
            .Where(element => element.Attribute(Xaml + "Name")?.Value is
                "DashboardScaleView" or
                "AppearanceScaleView" or
                "ProfilesScaleView" or
                "DiagnosticsScaleView" or
                "SetupScaleView" or
                "ReleaseNotesScaleView")
            .ToArray();
        Assert.Equal(6, scaleViews.Length);
        foreach (var viewbox in scaleViews)
        {
            // Scroll offsets must move a lightweight parent, not recreate the
            // Viewbox's scale transform for the entire page on every wheel step.
            Assert.Equal(Presentation + "Decorator", viewbox.Parent!.Name);
            Assert.Equal(Presentation + "ScrollViewer", viewbox.Parent.Parent!.Name);
            Assert.Equal("Uniform", viewbox.Attribute("Stretch")?.Value);
            Assert.Equal("DownOnly", viewbox.Attribute("StretchDirection")?.Value);
            Assert.Equal("Stretch", viewbox.Attribute("HorizontalAlignment")?.Value);
            Assert.Null(viewbox.Attribute("MaxWidth"));
            Assert.Null(viewbox.Parent.Attribute("MaxWidth"));
            var designSurface = viewbox.Elements().Single();
            Assert.Null(designSurface.Attribute("MaxWidth"));
            Assert.Equal(
                "{Binding ViewportWidth, RelativeSource={RelativeSource AncestorType={x:Type ScrollViewer}}, Converter={StaticResource ResponsivePageWidthConverter}}",
                designSurface.Attribute("Width")?.Value);
        }
    }

    [Fact]
    public void MacStyleHeaderKeepsTheBrandCenteredBetweenEqualSideColumns()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var header = document.Descendants(Presentation + "Grid")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "TitleBar");
        Assert.Equal("56", header.Parent!.Element(Presentation + "Grid.RowDefinitions")!
            .Elements().First().Attribute("Height")?.Value);
        Assert.Equal("55", document.Descendants().Single(element => element.Name.LocalName == "WindowChrome")
            .Attribute("CaptionHeight")?.Value);
        Assert.Equal("TitleBar_MouseLeftButtonDown", header.Attribute("MouseLeftButtonDown")?.Value);
        Assert.Equal(new[] { "*", "Auto", "*" }, header.Element(Presentation + "Grid.ColumnDefinitions")!
            .Elements().Select(column => column.Attribute("Width")?.Value));

        var controls = header.Elements().Single(element => element.Attribute(Xaml + "Name")?.Value == "WindowControls");
        var brand = header.Elements().Single(element => element.Attribute(Xaml + "Name")?.Value == "HeaderBrand");
        var status = header.Elements().Single(element => element.Attribute(Xaml + "Name")?.Value == "HeaderStatus");
        Assert.Equal("0", controls.Attribute("Grid.Column")?.Value);
        Assert.Equal("Left", controls.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("14,0,12,0", controls.Attribute("Margin")?.Value);
        Assert.Equal("1", brand.Attribute("Grid.Column")?.Value);
        Assert.Equal("Center", brand.Attribute("HorizontalAlignment")?.Value);
        Assert.Null(brand.Attribute("Margin"));
        var logo = brand.Element(Presentation + "Image")!;
        Assert.Equal("20", logo.Attribute("Width")?.Value);
        Assert.Equal("20", logo.Attribute("Height")?.Value);
        var title = Assert.Single(brand.Descendants(Presentation + "TextBlock"));
        Assert.Equal("Wisp", title.Attribute("Text")?.Value);
        Assert.Equal("14", title.Attribute("FontSize")?.Value);
        Assert.Equal("2", status.Attribute("Grid.Column")?.Value);
        Assert.Equal("Right", status.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("24", status.Attribute("Height")?.Value);
        Assert.Equal("WindowControls", header.Elements()
            .First(element => element.Attribute(Xaml + "Name") is not null).Attribute(Xaml + "Name")?.Value);
    }

    [Fact]
    public void TrafficLightWindowControlsPreserveNativeWindowActionsAndAccessibleTargets()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var group = document.Descendants(Presentation + "StackPanel")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "WindowControls");
        Assert.Equal("True", group.Attributes()
            .Single(attribute => attribute.Name.LocalName == "WindowChrome.IsHitTestVisibleInChrome").Value);
        var buttons = group.Elements(Presentation + "Button").ToArray();
        Assert.Equal(new[] { "Close_Click", "Minimize_Click", "Maximize_Click" },
            buttons.Select(button => button.Attribute("Click")?.Value));
        Assert.Equal(new[] { "#FF5F57", "#FEBC2E", "#28C840" },
            buttons.Select(button => button.Attribute("Background")?.Value));
        Assert.All(buttons, button =>
        {
            Assert.False(string.IsNullOrWhiteSpace(button.Attribute("ToolTip")?.Value));
            Assert.False(string.IsNullOrWhiteSpace(button.Attribute("AutomationProperties.Name")?.Value));
        });

        var style = LoadXaml(Path.Combine(AppSourceDirectory(), "App.xaml"))
            .Descendants(Presentation + "Style")
            .Single(element => element.Attribute(Xaml + "Key")?.Value == "WindowButtonStyle");
        Assert.Equal("24", style.Elements(Presentation + "Setter")
            .Single(element => element.Attribute("Property")?.Value == "Width").Attribute("Value")?.Value);
        Assert.Equal("32", style.Elements(Presentation + "Setter")
            .Single(element => element.Attribute("Property")?.Value == "Height").Attribute("Value")?.Value);
        Assert.Contains(style.Descendants(Presentation + "Trigger"),
            trigger => trigger.Attribute("Property")?.Value == "IsKeyboardFocused");
        Assert.Contains(style.Descendants(Presentation + "Ellipse"),
            ellipse => ellipse.Attribute(Xaml + "Name")?.Value == "FocusRing");
    }

    [Fact]
    public void SharedScrollBarStyleSupportsHorizontalOverflow()
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), "App.xaml");
        var document = LoadXaml(xamlPath);
        var style = document.Descendants(Presentation + "Style")
            .Single(element =>
                element.Attribute("TargetType")?.Value == "ScrollBar" &&
                element.Attribute(Xaml + "Key") is null);
        var template = style.Descendants(Presentation + "ControlTemplate")
            .Single(element => element.Attribute("TargetType")?.Value == "ScrollBar");
        var track = template.Descendants(Presentation + "Track")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "PART_Track");
        var thumb = template.Descendants(Presentation + "Thumb")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "ScrollThumb");

        Assert.Equal("{TemplateBinding Orientation}", track.Attribute("Orientation")?.Value);
        Assert.Equal("34", thumb.Attribute("MinHeight")?.Value);

        var horizontalSizeTrigger = style.Elements(Presentation + "Style.Triggers")
            .Descendants(Presentation + "Trigger")
            .Single(element =>
                element.Attribute("Property")?.Value == "Orientation" &&
                element.Attribute("Value")?.Value == "Horizontal");
        Assert.Contains(
            horizontalSizeTrigger.Elements(Presentation + "Setter"),
            setter => setter.Attribute("Property")?.Value == "Width" &&
                      setter.Attribute("Value")?.Value == "Auto");
        Assert.Contains(
            horizontalSizeTrigger.Elements(Presentation + "Setter"),
            setter => setter.Attribute("Property")?.Value == "Height" &&
                      setter.Attribute("Value")?.Value == "9");

        var horizontalTemplateTrigger = template.Elements(Presentation + "ControlTemplate.Triggers")
            .Descendants(Presentation + "Trigger")
            .Single(element =>
                element.Attribute("Property")?.Value == "Orientation" &&
                element.Attribute("Value")?.Value == "Horizontal");
        Assert.Contains(
            horizontalTemplateTrigger.Elements(Presentation + "Setter"),
            setter => setter.Attribute("TargetName")?.Value == "PART_Track" &&
                      setter.Attribute("Property")?.Value == "IsDirectionReversed" &&
                      setter.Attribute("Value")?.Value == "False");
        Assert.Contains(
            horizontalTemplateTrigger.Elements(Presentation + "Setter"),
            setter => setter.Attribute("TargetName")?.Value == "ScrollThumb" &&
                      setter.Attribute("Property")?.Value == "MinWidth" &&
                      setter.Attribute("Value")?.Value == "34");
    }

    [Fact]
    public void ScrollViewerCornerUsesTheWispWindowColor()
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), "App.xaml");
        var document = LoadXaml(xamlPath);
        var style = document.Descendants(Presentation + "Style")
            .Single(element =>
                element.Attribute("TargetType")?.Value == "ScrollViewer" &&
                element.Attribute(Xaml + "Key") is null);
        var cornerBrush = style.Elements(Presentation + "Style.Resources")
            .Elements(Presentation + "SolidColorBrush")
            .Single();

        Assert.Equal("{x:Static SystemColors.ControlBrushKey}", cornerBrush.Attribute(Xaml + "Key")?.Value);
        Assert.Equal("#090C11", cornerBrush.Attribute("Color")?.Value);
    }

    [Fact]
    public void AdjustmentSlidersHaveNoOuterFocusBorder()
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), "App.xaml");
        var document = LoadXaml(xamlPath);
        var style = document.Descendants(Presentation + "Style")
            .Single(element =>
                element.Attribute("TargetType")?.Value == "Slider" &&
                element.Attribute(Xaml + "Key") is null);
        var template = style.Descendants(Presentation + "ControlTemplate")
            .Single(element => element.Attribute("TargetType")?.Value == "Slider");

        Assert.DoesNotContain(
            template.Descendants(Presentation + "Border"),
            border => border.Attribute(Xaml + "Name")?.Value == "SliderFocusRing");
        Assert.DoesNotContain(
            template.Descendants(Presentation + "Trigger"),
            trigger => trigger.Attribute("Property")?.Value == "IsKeyboardFocusWithin");
    }

    [Fact]
    public void TabsDoNotDrawAnAccentFocusOutline()
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), "App.xaml");
        var document = LoadXaml(xamlPath);
        var style = document.Descendants(Presentation + "Style")
            .Single(element =>
                element.Attribute("TargetType")?.Value == "TabItem" &&
                element.Attribute(Xaml + "Key") is null);
        var tabShell = style.Descendants(Presentation + "Border")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "TabShell");

        Assert.Equal("Transparent", tabShell.Attribute("BorderBrush")?.Value);
        Assert.Equal("0,0,1,0", tabShell.Attribute("Margin")?.Value);
        Assert.DoesNotContain(
            style.Descendants(Presentation + "Trigger"),
            trigger => trigger.Attribute("Property")?.Value == "IsKeyboardFocused");
    }

    [Fact]
    public void HudWindowsUseSafeTopRightAndClampedRestoreGeometry()
    {
        var overlaySource = File.ReadAllText(Path.Combine(AppSourceDirectory(), "OverlayWindow.xaml.cs"));
        var gForceSource = File.ReadAllText(Path.Combine(AppSourceDirectory(), "GForceWindow.xaml.cs"));
        var controllerSource = File.ReadAllText(Path.Combine(AppSourceDirectory(), "AppController.cs"));

        Assert.Contains("OverlayPlacementGeometry.PlaceTopRight", overlaySource, StringComparison.Ordinal);
        Assert.Contains("OverlayPlacementGeometry.PlaceNativeBottomRight", overlaySource, StringComparison.Ordinal);
        Assert.Contains("CurrentMonitorPlacementArea", overlaySource, StringComparison.Ordinal);
        Assert.Contains("info.Monitor.Left", overlaySource, StringComparison.Ordinal);
        Assert.Contains("OverlayPlacementGeometry.ClampInside", overlaySource, StringComparison.Ordinal);
        Assert.Contains("EnsureHandle", overlaySource, StringComparison.Ordinal);
        Assert.Contains("OverlayPlacementGeometry.PlaceTopRight", gForceSource, StringComparison.Ordinal);
        Assert.Contains("OverlayPlacementGeometry.ClampInside", gForceSource, StringComparison.Ordinal);
        Assert.Contains("EnsureHandle", gForceSource, StringComparison.Ordinal);
        Assert.Contains("GForceOverlay.ResetPositionBelow", controllerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GForceOverlay.ResetPositionAbove", controllerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeGForceVisibilityIsControlledDeterministicallyByAppearancePolicy()
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), "GForceWindow.xaml");
        var codePath = Path.Combine(AppSourceDirectory(), "GForceWindow.xaml.cs");
        var document = LoadXaml(xamlPath);
        var nativeMeter = document.Descendants()
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "NativeGForceMeter");
        var code = File.ReadAllText(codePath);

        Assert.Null(nativeMeter.Attribute("Visibility"));
        Assert.DoesNotContain(nativeMeter.Descendants(Presentation + "Style"), _ => true);
        Assert.Contains(
            "GForcePanelBorder.Visibility = native ? Visibility.Collapsed : Visibility.Visible;",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeGForceMeter.Visibility = native ? Visibility.Visible : Visibility.Collapsed;",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LongMainWindowExplanatoryTextWrapsAtNarrowWidths()
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), "MainWindow.xaml");
        var document = LoadXaml(xamlPath);
        var failures = document.Descendants(Presentation + "TextBlock")
            .Where(element =>
            {
                var text = element.Attribute("Text")?.Value;
                return text is { Length: >= 60 } &&
                       !text.StartsWith('{') &&
                       element.Attribute("TextWrapping")?.Value != "Wrap";
            })
            .Select(element => $"{Location(xamlPath, element)}: long explanatory text must wrap.")
            .ToArray();

        Assert.Empty(failures);
    }

    [Fact]
    public void UpdateConfirmationShowsReleaseDetailsAsPlainWrappedText()
    {
        var document = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml"));
        var details = document.Descendants(Presentation + "Border")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "ApplicationUpdateConfirmationDetails");
        var summary = details.Descendants(Presentation + "TextBlock")
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "ApplicationUpdateConfirmationSummary");
        var summaryViewport = details.Descendants(Presentation + "ScrollViewer").Single();

        Assert.Equal("Collapsed", details.Attribute("Visibility")?.Value);
        Assert.Equal("Wrap", summary.Attribute("TextWrapping")?.Value);
        Assert.Equal("76", summaryViewport.Attribute("MaxHeight")?.Value);
        Assert.Equal("Auto", summaryViewport.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Empty(summary.Descendants(Presentation + "Hyperlink"));

        var code = File.ReadAllText(Path.Combine(AppSourceDirectory(), "MainWindow.xaml.cs"));
        Assert.Contains("ApplicationUpdateConfirmationSummary.Text = details.ReleaseSummary;", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OverlayWindow.xaml", "BoxedSpeedBorder")]
    [InlineData("OverlayWindow.xaml", "CombinedBorder")]
    [InlineData("GForceWindow.xaml", "GForcePanelBorder")]
    public void EditOutlineIsFlushWithBoxedHudSurface(string fileName, string panelName)
    {
        var xamlPath = Path.Combine(AppSourceDirectory(), fileName);
        var document = LoadXaml(xamlPath);
        var panel = document.Descendants()
            .Single(element => element.Attribute(Xaml + "Name")?.Value == panelName);
        var editBorder = document.Descendants()
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "EditBorder");

        Assert.Equal(panel.Attribute("Margin")?.Value, editBorder.Attribute("Margin")?.Value);
        Assert.Equal(panel.Attribute("CornerRadius")?.Value, editBorder.Attribute("CornerRadius")?.Value);
    }

    [Fact]
    public void TwoBoxDefaultPlacementUsesHorizontalAdjacencyWithoutReplacingSavedPlacements()
    {
        var controllerSource = File.ReadAllText(Path.Combine(AppSourceDirectory(), "AppController.cs"));

        Assert.Contains(
            "OverlayPlacementResolver.FindGForcePlacementForSpeedDisplay",
            controllerSource,
            StringComparison.Ordinal);
        Assert.Contains("RestoreGForcePlacement();", controllerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.GForcePlacements.Count == 0", controllerSource, StringComparison.Ordinal);
        Assert.Contains(
            "GForceOverlay.ResetPositionAdjacentTo",
            controllerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Settings.LayoutMode == HudLayoutMode.SeparateBoxes",
            controllerSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppDoesNotForceLinkCursorOnStandardControls()
    {
        var failures = AppXamlFiles()
            .SelectMany(path => LoadXaml(path).Root!.DescendantsAndSelf()
                .Attributes("Cursor")
                .Where(attribute => string.Equals(attribute.Value, "Hand", StringComparison.OrdinalIgnoreCase))
                .Select(attribute => Location(path, attribute)))
            .ToArray();

        Assert.Empty(failures);
    }

    [Fact]
    public void MainWindowAlwaysProvidesAVisibleArrowCursorFallback()
    {
        var window = LoadXaml(Path.Combine(AppSourceDirectory(), "MainWindow.xaml")).Root!;

        Assert.Equal("Arrow", window.Attribute("Cursor")?.Value);
    }

    private static void AssertThemePickerContract(
        XElement picker,
        string itemsSource,
        string handler,
        string automationName,
        string dataType,
        string itemAutomationFormat,
        IReadOnlyCollection<string> requiredBindings)
    {
        Assert.Equal(itemsSource, picker.Attribute("ItemsSource")?.Value);
        Assert.Equal("Name", picker.Attribute("SelectedValuePath")?.Value);
        Assert.Equal("{StaticResource ThemeChoiceStyle}", picker.Attribute("ItemContainerStyle")?.Value);
        Assert.Equal(handler, picker.Attribute("SelectionChanged")?.Value);
        Assert.Equal(automationName, picker.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("3", Assert.Single(picker.Descendants(Presentation + "UniformGrid"))
            .Attribute("Columns")?.Value);

        var template = Assert.Single(picker.Descendants(Presentation + "DataTemplate"));
        Assert.Equal(dataType, template.Attribute("DataType")?.Value);
        var label = template.Descendants(Presentation + "TextBlock")
            .Single(element => BindingPath(element.Attribute("Text")?.Value) == "Name");
        Assert.Contains(
            itemAutomationFormat,
            label.Attribute("AutomationProperties.Name")?.Value ?? string.Empty,
            StringComparison.Ordinal);

        var bindings = template.DescendantsAndSelf()
            .Attributes()
            .Select(attribute => BindingPath(attribute.Value))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        Assert.Subset(requiredBindings.ToHashSet(StringComparer.Ordinal), bindings);
    }

    private static string Handler(string source, string name) => Regex.Match(
        source,
        $@"private void {Regex.Escape(name)}.*?(?=\r?\n    private void )",
        RegexOptions.Singleline).Value;

    private static bool IsRoutedHandlerAttribute(XAttribute attribute)
    {
        var name = attribute.Name.LocalName;
        return RoutedHandlerAttributes.Contains(name) || name.Contains("Mouse", StringComparison.Ordinal);
    }

    private static Type? BindingSourceType(string binding, XDocument? document = null, XElement? targetElement = null)
    {
        if (binding.Contains("RelativeSource", StringComparison.Ordinal))
        {
            var ancestor = AncestorTypePattern.Match(binding);
            if (!ancestor.Success)
            {
                return null;
            }

            var ancestorName = ancestor.Groups["type"].Value;
            if (ancestorName == "UserControl" && document?.Root is { } root)
            {
                var className = root.Attribute(Xaml + "Class")?.Value;
                var ownerType = className is null
                    ? null
                    : typeof(DiagnosticsViewModel).Assembly.GetType(className);
                if (ownerType is not null)
                {
                    return ownerType;
                }
            }

            return typeof(System.Windows.Controls.Control).Assembly.GetType(
                $"System.Windows.Controls.{ancestorName}");
        }

        var elementName = Regex.Match(binding, @"\bElementName\s*=\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)");
        if (elementName.Success)
        {
            var element = BindingSourceElement(binding, document);
            return element is not null && element.Name.Namespace == Presentation
                ? typeof(System.Windows.Controls.Control).Assembly.GetType(
                    $"System.Windows.Controls.{element.Name.LocalName}")
                : null;
        }

        // Other explicit sources must gain validation rather than being treated as the view model.
        if (Regex.IsMatch(binding, @"\bSource\s*="))
            return null;

        var dataTemplate = targetElement?.AncestorsAndSelf()
            .FirstOrDefault(element => element.Name == Presentation + "DataTemplate");
        if (dataTemplate is not null)
        {
            var type = Regex.Match(dataTemplate.Attribute("DataType")?.Value ?? string.Empty,
                @"^\{x:Type\s+(?<prefix>[A-Za-z_][A-Za-z0-9_]*):(?<type>[A-Za-z_][A-Za-z0-9_]*)\s*\}$");
            return type.Success && dataTemplate.GetNamespaceOfPrefix(type.Groups["prefix"].Value)?.NamespaceName == "clr-namespace:Wisp.App"
                ? typeof(DiagnosticsViewModel).Assembly.GetType($"Wisp.App.{type.Groups["type"].Value}")
                : null;
        }

        return typeof(DiagnosticsViewModel);
    }

    private static XElement? BindingSourceElement(string binding, XDocument? document)
    {
        var elementName = Regex.Match(binding, @"\bElementName\s*=\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)");
        var matches = document?.Root?.DescendantsAndSelf()
            .Where(element => element.Attribute(Xaml + "Name")?.Value == elementName.Groups["name"].Value)
            .ToArray();
        return elementName.Success && matches is { Length: 1 } ? matches[0] : null;
    }

    private static bool BindingPathResolves(Type rootType, string path, XElement? sourceElement = null)
    {
        var currentType = rootType;
        foreach (var segment in path.Split('.'))
        {
            var property = currentType.GetProperty(segment, BindingFlags.Instance | BindingFlags.Public);
            if (property is null)
            {
                return false;
            }

            if (segment == "SelectedItem" &&
                currentType == typeof(System.Windows.Controls.TabControl) &&
                sourceElement is not null && sourceElement.Name == Presentation + "TabControl")
            {
                var items = sourceElement.Elements()
                    .Where(element => !element.Name.LocalName.Contains('.'))
                    .ToArray();
                if (items.Length == 0 || items.Any(element => element.Name != Presentation + "TabItem"))
                {
                    return false;
                }

                currentType = typeof(System.Windows.Controls.TabItem);
                sourceElement = null;
            }
            else
            {
                currentType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            }
        }

        return true;
    }

    private static bool IsDescriptiveText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('{'))
        {
            return false;
        }

        return value.Any(char.IsLetterOrDigit);
    }

    private static XElement FindLayoutPreview(XDocument document, int layoutIndex)
    {
        var previewName = layoutIndex switch
        {
            0 => "MinimalLayoutPreview",
            1 => "CombinedLayoutPreview",
            2 => "SeparateBoxesLayoutPreview",
            3 => "NativeLayoutPreview",
            _ => throw new ArgumentOutOfRangeException(nameof(layoutIndex))
        };
        var preview = document.Descendants()
            .SingleOrDefault(element => element.Attribute(Xaml + "Name")?.Value == previewName);

        Assert.NotNull(preview);
        return preview;
    }

    private static void AssertPreviewMeterCondition(
        string xamlPath,
        XElement preview,
        bool shouldBeConditional,
        string layoutName)
    {
        var meter = preview.Descendants(Local + "GForceMeterView").SingleOrDefault();
        Assert.True(
            meter is not null,
            $"{Location(xamlPath, preview)}: {layoutName} preview must contain exactly one GForceMeterView.");

        var conditional = MeterVisibilityDependsOnGForceEnabled(meter!, preview);

        Assert.True(
            conditional == shouldBeConditional,
            $"{Location(xamlPath, meter)}: {layoutName} G-force preview " +
            (shouldBeConditional
                ? "must condition its visibility on GForceEnabled."
                : "must remain visible independently of GForceEnabled."));
    }

    private static bool MeterVisibilityDependsOnGForceEnabled(XElement meter, XElement preview) =>
        meter.AncestorsAndSelf()
            .TakeWhile(element => element != preview.Parent)
            .Any(HasGForceVisibilityCondition);

    private static bool HasLayoutVisibilityTrigger(XElement element, int layoutIndex, string visibility)
    {
        var expectedIndex = layoutIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return element.AncestorsAndSelf()
            .SelectMany(ancestor => ancestor.Elements()
                .Where(child => child.Name.LocalName.EndsWith(".Style", StringComparison.Ordinal))
                .Descendants(Presentation + "DataTrigger"))
            .Any(trigger =>
                BindingPath(trigger.Attribute("Binding")?.Value) == "LayoutSelectionIndex" &&
                trigger.Attribute("Value")?.Value == expectedIndex &&
                trigger.Descendants(Presentation + "Setter").Any(setter =>
                    setter.Attribute("Property")?.Value == "Visibility" &&
                    setter.Attribute("Value")?.Value == visibility));
    }

    private static bool HasGForceVisibilityCondition(XElement element)
    {
        var visibilityBinding = element.Attribute("Visibility")?.Value;
        if (BindingPath(visibilityBinding) == "GForceEnabled")
        {
            return true;
        }

        return element.Elements()
            .Where(child => child.Name.LocalName.EndsWith(".Style", StringComparison.Ordinal))
            .Descendants()
            .Where(trigger => trigger.Name.LocalName is "DataTrigger" or "Condition")
            .Any(trigger =>
                BindingPath(trigger.Attribute("Binding")?.Value) == "GForceEnabled" &&
                trigger.Descendants()
                    .Any(setter => setter.Name.LocalName == "Setter" &&
                                   setter.Attribute("Property")?.Value == "Visibility"));
    }

    private static string? BindingPath(string? markupExtension)
    {
        if (markupExtension is null)
        {
            return null;
        }

        var match = BindingPathPattern.Match(markupExtension);
        return match.Success ? match.Groups["path"].Value : null;
    }

    private static IEnumerable<string> AppXamlFiles() =>
        Directory.EnumerateFiles(AppSourceDirectory(), "*.xaml", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal);

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

    private static XDocument LoadXaml(string path) =>
        XDocument.Load(path, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);

    private static string RelativePath(string path) =>
        Path.GetRelativePath(RepositoryRoot(), path);

    private static string Location(string path, XObject node)
    {
        var lineInfo = (IXmlLineInfo)node;
        return lineInfo.HasLineInfo()
            ? $"{RelativePath(path)}:{lineInfo.LineNumber}"
            : RelativePath(path);
    }
}
