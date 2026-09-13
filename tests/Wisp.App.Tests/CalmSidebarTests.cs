using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;

namespace Wisp.App.Tests;

public sealed class CalmSidebarTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] PageNames = ["Dashboard", "Runs", "Appearance", "Diagnostics", "Profiles", "Extras", "Release Notes"];

    [Fact]
    public void EveryNavigationPageUsesTheSameHorizontalDockSpacing()
    {
        var document = XDocument.Load(SourcePath("MainWindow.xaml"));
        var navigation = Named(document, "SidebarNavigation");
        var pages = Named(document, "RootTabs").Elements(Presentation + "TabItem").ToArray();
        var rows = navigation.Elements(Presentation + "ListBoxItem").ToArray();
        Assert.Equal(Presentation + "ListBox", navigation.Name);
        Assert.Equal(PageNames, pages.Select(page => page.Attribute("Header")?.Value));
        Assert.Equal(PageNames, rows.Select(row => row.Attribute("AutomationProperties.Name")?.Value));
        Assert.Single(navigation.Elements(Presentation + "ListBox.ItemsPanel"));
        Assert.All(navigation.Elements(), element => Assert.True(
            element.Name == Presentation + "ListBoxItem" || element.Name == Presentation + "ListBox.ItemsPanel"));
        Assert.Equal("{StaticResource OrbitDockItemStyle}", navigation.Attribute("ItemContainerStyle")?.Value);
        Assert.Equal("{Binding SelectedIndex, ElementName=RootTabs, Mode=TwoWay}",
            navigation.Attribute("SelectedIndex")?.Value);
        Assert.All(rows, row =>
        {
            Assert.Null(row.Attribute("Margin"));
            Assert.Null(row.Attribute("Height"));
            Assert.Null(row.Attribute("Style"));
            Assert.Empty(row.Elements(Presentation + "ListBoxItem.Style"));
        });

        var resources = XDocument.Load(SourcePath("OrbitWindowResources.xaml"));
        var style = resources.Descendants(Presentation + "Style")
            .Single(element => element.Attribute(Xaml + "Key")?.Value == "OrbitDockItemStyle");
        Assert.Equal("68", SetterValue(style, "Height"));
        Assert.Equal("2", SetterValue(style, "Margin"));
        var panel = Assert.Single(navigation.Descendants(Presentation + "UniformGrid"));
        Assert.Equal("7", panel.Attribute("Columns")?.Value);
    }

    [Fact]
    public void SidebarPagesUseOneHeaderlessContentPresenter()
    {
        var document = XDocument.Load(SourcePath("MainWindow.xaml"));
        Assert.Equal("{StaticResource SidebarPagesStyle}", Named(document, "RootTabs").Attribute("Style")?.Value);
        Assert.Equal("{Binding SelectedItem.Header, ElementName=RootTabs}",
            Named(document, "PageTitleText").Attribute("Text")?.Value);
        Assert.Contains(document.Root!.Element(Presentation + "Window.Resources")!
                .Descendants(Presentation + "ResourceDictionary"),
            dictionary => dictionary.Attribute("Source")?.Value == "OrbitWindowResources.xaml");
        var orbitResources = XDocument.Load(SourcePath("OrbitWindowResources.xaml"));
        Assert.Contains(orbitResources.Descendants(Presentation + "ResourceDictionary"),
            dictionary => dictionary.Attribute("Source")?.Value == "CalmWindowResources.xaml");
        var resources = XDocument.Load(SourcePath("CalmWindowResources.xaml"));
        var style = resources.Descendants(Presentation + "Style")
            .Single(element => element.Attribute(Xaml + "Key")?.Value == "SidebarPagesStyle");
        var template = Assert.Single(style.Descendants(Presentation + "ControlTemplate"));
        var presenter = Assert.Single(template.Descendants(Presentation + "ContentPresenter"));
        Assert.Equal("PART_SelectedContentHost", presenter.Attribute(Xaml + "Name")?.Value);
        Assert.Equal("SelectedContent", presenter.Attribute("ContentSource")?.Value);
        Assert.Empty(template.Descendants(Presentation + "TabPanel"));
        Assert.Empty(template.Descendants(Presentation + "ItemsPresenter"));
    }

    [Fact]
    public void DockVisibilityUsesOneLayoutRowWithoutAnimationOrAFrameTimer()
    {
        var document = XDocument.Load(SourcePath("MainWindow.xaml"));
        var code = File.ReadAllText(SourcePath("MainWindow.xaml.cs")) + Environment.NewLine +
            File.ReadAllText(SourcePath("ControlPanelWindow.cs"));
        Assert.Equal("Auto", Named(document, "NavigationDockRow").Attribute("Height")?.Value);
        Assert.Equal("1", Named(document, "SidebarHost").Attribute("Grid.Row")?.Value);
        Assert.Equal("0", Named(document, "ContentPane").Attribute("Grid.Row")?.Value);
        Assert.NotNull(Named(document, "SidebarHost").Element(Presentation + "Border.RenderTransform")?
            .Element(Presentation + "TranslateTransform"));
        Assert.NotNull(Named(document, "ContentPane").Element(Presentation + "Grid.RenderTransform")?
            .Element(Presentation + "TranslateTransform"));
        Assert.Equal(Presentation + "RotateTransform", Named(document, "SidebarChevronRotation").Name);
        Assert.DoesNotContain("DispatcherTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CompositionTarget.Rendering", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GridLengthAnimation", code, StringComparison.Ordinal);
        Assert.DoesNotContain("new DoubleAnimation", code, StringComparison.Ordinal);
        Assert.Contains("SidebarHost.IsKeyboardFocusWithin", code, StringComparison.Ordinal);
        Assert.Contains("SidebarToggleButton.Focus()", code, StringComparison.Ordinal);
        var displayTransform = Assert.Single(document.Descendants(),
            element => element.Name.LocalName.EndsWith(".LayoutTransform", StringComparison.Ordinal));
        Assert.Same(Named(document, "DashboardContent"), displayTransform.Parent);
        var displayScale = Assert.Single(displayTransform.Elements(Presentation + "ScaleTransform"));
        Assert.Equal("DashboardDisplayScale", displayScale.Attribute(Xaml + "Name")?.Value);
        Assert.Equal("1", displayScale.Attribute("ScaleX")?.Value ?? "1");
        Assert.Equal("1", displayScale.Attribute("ScaleY")?.Value ?? "1");
        Assert.DoesNotContain(displayTransform.Ancestors(), element =>
            element == Named(document, "SidebarHost") || element == Named(document, "DockToggleHost"));

        var toggle = Named(document, "SidebarToggleButton");
        var toggleHost = Named(document, "DockToggleHost");
        Assert.Equal(toggleHost, toggle.Parent);
        Assert.Equal(Named(document, "ControlBody"), toggleHost.Parent);
        Assert.Equal("2", toggleHost.Attribute("Grid.RowSpan")?.Value);
        Assert.DoesNotContain(toggle.Ancestors(), element =>
            element == Named(document, "SidebarHost") || element == Named(document, "TitleBar"));
        Assert.Equal("Right", toggle.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Bottom", toggle.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("42", toggle.Attribute("Width")?.Value);
        Assert.Equal("42", toggle.Attribute("Height")?.Value);
        Assert.Equal("SidebarToggle_Click", toggle.Attribute("Click")?.Value);
    }

    internal static void AssertOnCurrentDispatcher(MainWindow window, FrameworkElement surface)
    {
        Assert.True(window.Dispatcher.CheckAccess());
        AssertModernControlFocusDoesNotMoveContent(window);
        var tabs = Assert.IsType<TabControl>(window.FindName("RootTabs"));
        var navigation = Assert.IsType<ListBox>(window.FindName("SidebarNavigation"));
        var toggle = Assert.IsType<Button>(window.FindName("SidebarToggleButton"));
        var dockRow = Assert.IsType<RowDefinition>(window.FindName("NavigationDockRow"));
        var sidebar = Assert.IsAssignableFrom<Border>(window.FindName("SidebarHost"));
        var toggleHost = Assert.IsType<Grid>(window.FindName("DockToggleHost"));
        var contentPane = Assert.IsType<Grid>(window.FindName("ContentPane"));
        var pageTitle = Assert.IsType<TextBlock>(window.FindName("PageTitleText"));
        var runs = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("RunsSurface"));
        var runLibrary = Assert.IsAssignableFrom<Border>(runs.FindName("RunLibraryPane"));
        Assert.Same(window.FindResource("RunCard"), runLibrary.Style);
        Assert.Same(window.FindResource("CardStyle"), runLibrary.Style.BasedOn);
        var runToggles = LogicalDescendants(runs).OfType<CheckBox>().ToArray();
        Assert.Equal(4, runToggles.Length);
        Assert.All(runToggles, runToggle => Assert.Same(window.FindResource("ToggleSwitchStyle"), runToggle.Style));
        foreach (var name in new[] { "ShowGraphsButton", "RunRecordButton" })
            Assert.Same(window.FindResource("PrimaryButtonStyle"), Assert.IsType<Button>(runs.FindName(name)).Style);
        var binding = Assert.IsType<Binding>(BindingOperations.GetBinding(navigation, Selector.SelectedIndexProperty));
        Assert.Equal("RootTabs", binding.ElementName);
        Assert.Equal("SelectedIndex", binding.Path.Path);
        Assert.Equal(BindingMode.TwoWay, binding.Mode);
        Assert.Equal(PageNames.Length, tabs.Items.Count);
        Assert.Equal(PageNames.Length, navigation.Items.Count);
        var originalSelection = tabs.SelectedIndex;
        var originalOpen = window.IsSidebarOpen;
        var originalSize = surface.RenderSize;

        try
        {
            foreach (var size in new[]
                     {
                         new Size(720, 440), new Size(980, 750), new Size(1040, 760),
                         new Size(1280, 900), new Size(1464, 994), new Size(2560, 1440)
                     })
            {
                window.SetSidebarOpen(true, animate: false);
                Arrange(surface, size);
                AssertUniformDockItems(navigation, surface);
                AssertToggleReachable(toggle, toggleHost, sidebar, navigation, surface, window.IsSidebarOpen);
                Assert.Empty(VisualDescendants(tabs).OfType<TabPanel>());
                var presenter = Assert.IsType<ContentPresenter>(tabs.Template.FindName("PART_SelectedContentHost", tabs));

                for (var index = 0; index < PageNames.Length; index++)
                {
                    navigation.SetCurrentValue(Selector.SelectedIndexProperty, index);
                    Arrange(surface, size);
                    AssertPage(index, tabs, navigation, presenter, pageTitle);
                    var selectedContent = tabs.SelectedContent;
                    var expandedPageWidth = tabs.ActualWidth;
                    var expandedPageHeight = tabs.ActualHeight;
                    var expandedDockHeight = dockRow.ActualHeight;
                    var contentOrigin = contentPane.TranslatePoint(new Point(), surface);
                    Assert.True(expandedDockHeight > 60);
                    AssertBoundsWithin(contentPane, surface);
                    AssertBoundsWithin(sidebar, surface);

                    window.SetSidebarOpen(false, animate: false);
                    Arrange(surface, size);
                    Assert.False(window.IsSidebarOpen);
                    Assert.Equal(new GridLength(0), dockRow.Height);
                    Assert.Equal(0, dockRow.ActualHeight);
                    Assert.False(navigation.IsEnabled);
                    Assert.False(navigation.IsHitTestVisible);
                    Assert.NotEqual(Visibility.Visible, sidebar.Visibility);
                    Assert.False(DependencyPropertyHelper.GetValueSource(dockRow, RowDefinition.HeightProperty).IsAnimated);
                    Assert.Equal(expandedPageWidth, tabs.ActualWidth, precision: 3);
                    Assert.Equal(expandedPageHeight + expandedDockHeight, tabs.ActualHeight, precision: 3);
                    Assert.Equal(contentOrigin, contentPane.TranslatePoint(new Point(), surface));
                    AssertBoundsWithin(contentPane, surface);
                    Assert.Same(selectedContent, tabs.SelectedContent);
                    AssertPage(index, tabs, navigation, presenter, pageTitle);
                    AssertToggleReachable(toggle, toggleHost, sidebar, navigation, surface, window.IsSidebarOpen);

                    window.SetSidebarOpen(true, animate: false);
                    Arrange(surface, size);
                    Assert.True(window.IsSidebarOpen);
                    Assert.True(dockRow.Height.IsAuto);
                    Assert.Equal(expandedDockHeight, dockRow.ActualHeight, precision: 3);
                    Assert.Equal(Visibility.Visible, sidebar.Visibility);
                    Assert.True(navigation.IsEnabled);
                    Assert.True(navigation.IsHitTestVisible);
                    Assert.Equal(expandedPageWidth, tabs.ActualWidth, precision: 3);
                    Assert.Equal(expandedPageHeight, tabs.ActualHeight, precision: 3);
                    Assert.Equal(contentOrigin, contentPane.TranslatePoint(new Point(), surface));
                    AssertBoundsWithin(contentPane, surface);
                    AssertBoundsWithin(sidebar, surface);
                    Assert.Equal(0, Assert.IsType<TranslateTransform>(sidebar.RenderTransform).X);
                    Assert.Equal(0, Assert.IsType<TranslateTransform>(contentPane.RenderTransform).X);
                    Assert.Same(selectedContent, tabs.SelectedContent);
                    AssertPage(index, tabs, navigation, presenter, pageTitle);
                    AssertUniformDockItems(navigation, surface);

                    tabs.SelectedIndex = (index + 1) % PageNames.Length;
                    Arrange(surface, size);
                    AssertPage((index + 1) % PageNames.Length, tabs, navigation, presenter, pageTitle);
                }

                window.SetSidebarOpen(false, animate: false);
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(window.IsSidebarOpen);
                Assert.False(DependencyPropertyHelper.GetValueSource(dockRow, RowDefinition.HeightProperty).IsAnimated);
                window.SetSidebarOpen(false, animate: false);
                window.SetSidebarOpen(true, animate: false);
                Arrange(surface, size);
                Assert.Equal(0, Assert.IsType<TranslateTransform>(sidebar.RenderTransform).X);
                Assert.Equal(0, Assert.IsType<TranslateTransform>(contentPane.RenderTransform).X);
                AssertUniformDockItems(navigation, surface);
            }

            foreach (var open in new[] { false, true })
            {
                window.SetSidebarOpen(open, animate: false);
                window.SetDashboardDisplayMode(true);
                Arrange(surface, originalSize);
                Assert.True(window.IsDashboardDisplayMode);
                Assert.Equal(open, window.IsSidebarOpen);
                Assert.Equal(0, tabs.SelectedIndex);
                Assert.Equal(Visibility.Collapsed, sidebar.Visibility);
                Assert.Equal(Visibility.Collapsed, toggleHost.Visibility);
                Assert.Equal(new GridLength(0), dockRow.Height);
                Assert.False(sidebar.IsEnabled || sidebar.IsHitTestVisible);

                window.SetDashboardDisplayMode(false);
                Arrange(surface, originalSize);
                Assert.False(window.IsDashboardDisplayMode);
                Assert.Equal(open, window.IsSidebarOpen);
                Assert.Equal(open ? Visibility.Visible : Visibility.Collapsed, sidebar.Visibility);
                Assert.Equal(Visibility.Visible, toggleHost.Visibility);
                Assert.Equal(open, dockRow.Height.IsAuto);
                Assert.Equal(1, Assert.IsType<ScaleTransform>(
                    ((FrameworkElement)window.FindName("DashboardContent")).LayoutTransform).ScaleX);
                AssertToggleReachable(toggle, toggleHost, sidebar, navigation, surface, open);
            }
        }
        finally
        {
            window.SetDashboardDisplayMode(false);
            window.SetSidebarOpen(originalOpen, animate: false);
            tabs.SelectedIndex = originalSelection;
            Arrange(surface, originalSize);
        }
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
        }
    }

    private static void AssertPage(int index, TabControl tabs, ListBox navigation, ContentPresenter presenter, TextBlock title)
    {
        Assert.Equal(index, tabs.SelectedIndex);
        Assert.Equal(index, navigation.SelectedIndex);
        var page = Assert.IsType<TabItem>(tabs.SelectedItem);
        Assert.Equal(PageNames[index], page.Header);
        Assert.Equal(PageNames[index], title.Text);
        Assert.Same(page.Content, tabs.SelectedContent);
        Assert.Same(page.Content, presenter.Content);
    }

    private static void AssertUniformDockItems(ListBox navigation, FrameworkElement surface)
    {
        Assert.True(navigation.Focusable && navigation.IsTabStop);
        Assert.NotEqual(KeyboardNavigationMode.None, KeyboardNavigation.GetDirectionalNavigation(navigation));
        var shell = new[] { surface }.Concat(VisualDescendants(surface).OfType<FrameworkElement>())
            .Single(element => element.Name == "ShellRoot");
        var expectedHeight = shell.ActualHeight < 650 ? 50 : 68;
        double? previousLeft = null, previousWidth = null, top = null;
        foreach (var (item, index) in navigation.Items.Cast<ListBoxItem>().Select((item, index) => (item, index)))
        {
            Assert.Same(navigation.ItemContainerStyle, item.Style);
            Assert.Equal(PageNames[index], AutomationProperties.GetName(item));
            Assert.True(item.Focusable && item.IsTabStop && item.IsEnabled);
            Assert.Equal(expectedHeight, item.ActualHeight);
            Assert.Equal(new Thickness(2), item.Margin);
            AssertBoundsWithin(item, navigation);
            AssertBoundsWithin(item, surface);
            var point = item.TranslatePoint(new Point(), surface);
            if (previousLeft.HasValue)
            {
                Assert.InRange(Math.Abs(point.X - previousLeft.Value - previousWidth!.Value - 4), 0, 1.1);
                Assert.InRange(Math.Abs(item.ActualWidth - previousWidth.Value), 0, 1.1);
                Assert.InRange(Math.Abs(point.Y - top!.Value), 0, 0.1);
            }
            var label = Assert.Single(VisualDescendants(item).OfType<TextBlock>(), text => text.Text == PageNames[index]);
            AssertBoundsWithin(label, item);
            foreach (var icon in VisualDescendants(item).OfType<System.Windows.Shapes.Path>())
                AssertBoundsWithin(icon, item);
            var marker = Assert.IsType<Border>(item.Template.FindName("SelectionMarker", item));
            Assert.Equal(item.IsSelected ? Visibility.Visible : Visibility.Hidden, marker.Visibility);
            Assert.False(marker.IsHitTestVisible);
            var focus = Assert.IsType<Border>(item.Template.FindName("DockFocus", item));
            Assert.Equal(new Thickness(2), focus.BorderThickness);
            Assert.False(focus.IsHitTestVisible);
            var text = new FormattedText(label.Text, CultureInfo.CurrentCulture, label.FlowDirection,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                label.FontSize, label.Foreground, VisualTreeHelper.GetDpi(label).PixelsPerDip);
            if (label.TextWrapping != TextWrapping.NoWrap)
                text.MaxTextWidth = Math.Max(0.1, label.ActualWidth);
            Assert.True(text.WidthIncludingTrailingWhitespace <= label.ActualWidth + 1.1 &&
                        text.Height <= label.ActualHeight + 1.1,
                $"Navigation label '{label.Text}' must fit without clipping: " +
                $"actual {label.ActualWidth:F3} x {label.ActualHeight:F3}, " +
                $"formatted {text.WidthIncludingTrailingWhitespace:F3} x {text.Height:F3}, " +
                $"font {label.FontSize:F1}, format {TextOptions.GetTextFormattingMode(label)}, " +
                $"DPI {VisualTreeHelper.GetDpi(label).PixelsPerDip:F2}, " +
                $"item {item.ActualWidth:F3} x {item.ActualHeight:F3}.");
            previousLeft = point.X;
            previousWidth = item.ActualWidth;
            top = point.Y;
        }
    }

    private static void AssertModernControlFocusDoesNotMoveContent(MainWindow window)
    {
        foreach (var key in new object[] { typeof(Button), "PrimaryButtonStyle" })
        {
            var button = new Button { Content = "Apply", BorderThickness = new Thickness(0), Style = (Style)window.FindResource(key) };
            button.Resources.MergedDictionaries.Add(window.Resources);
            button.Measure(new Size(160, 48));
            button.Arrange(new Rect(0, 0, 160, 48));
            button.UpdateLayout();
            var border = Assert.IsAssignableFrom<Border>(button.Template.FindName("ButtonBorder", button));
            var presenter = Assert.IsType<ContentPresenter>(border.Child);
            var originalBounds = presenter.TransformToAncestor(button).TransformBounds(new Rect(presenter.RenderSize));
            var focus = Assert.IsType<Border>(button.Template.FindName("ButtonFocus", button));
            Assert.Equal(new Thickness(0), border.BorderThickness);
            Assert.Equal(new Thickness(2), focus.BorderThickness);
            Assert.False(focus.IsHitTestVisible);
            Assert.Equal(Visibility.Hidden, focus.Visibility);
            var trigger = Assert.Single(button.Template.Triggers.OfType<Trigger>(),
                item => item.Property == UIElement.IsKeyboardFocusedProperty && Equals(item.Value, true));
            Assert.Contains(trigger.Setters.OfType<Setter>(), setter => setter.TargetName == "ButtonFocus" &&
                setter.Property == UIElement.VisibilityProperty && Equals(setter.Value, Visibility.Visible));
            focus.Visibility = Visibility.Visible;
            button.UpdateLayout();
            Assert.Equal(originalBounds, presenter.TransformToAncestor(button).TransformBounds(new Rect(presenter.RenderSize)));
            AssertBoundsWithin(focus, button);
        }
    }

    private static void AssertBoundsWithin(FrameworkElement element, FrameworkElement container)
    {
        var origin = element.TranslatePoint(new Point(), container);
        Assert.InRange(origin.X, -0.5, container.ActualWidth);
        Assert.InRange(origin.Y, -0.5, container.ActualHeight);
        Assert.True(origin.X + element.ActualWidth <= container.ActualWidth + 0.5);
        Assert.True(origin.Y + element.ActualHeight <= container.ActualHeight + 0.5);
    }

    private static void AssertToggleReachable(Button toggle, Grid toggleHost, Border sidebar,
        ListBox navigation, FrameworkElement surface, bool open)
    {
        Assert.Same(toggleHost, toggle.Parent);
        Assert.NotSame(sidebar, toggleHost.Parent);
        Assert.Equal(Visibility.Visible, toggle.Visibility);
        Assert.True(toggle.IsEnabled && toggle.IsHitTestVisible && toggle.Focusable && toggle.IsTabStop);
        var label = open ? "Hide navigation dock" : "Show navigation dock";
        Assert.Equal(label, AutomationProperties.GetName(toggle));
        Assert.Equal(label, toggle.ToolTip);
        Assert.Equal(42, toggle.ActualWidth);
        Assert.Equal(42, toggle.ActualHeight);
        AssertBoundsWithin(toggle, toggleHost);
        AssertBoundsWithin(toggle, surface);
        var point = toggle.TranslatePoint(new Point(), toggleHost);
        Assert.Equal(toggleHost.ActualWidth - toggle.Margin.Right - toggle.ActualWidth, point.X, precision: 3);
        Assert.Equal(toggleHost.ActualHeight - toggle.Margin.Bottom - toggle.ActualHeight, point.Y, precision: 3);
        Assert.True(toggle.TranslatePoint(new Point(), surface).Y > surface.ActualHeight / 2);
        if (open)
        {
            Assert.Equal(HorizontalAlignment.Center, toggleHost.HorizontalAlignment);
            AssertBoundsWithin(toggle, sidebar);
            var toggleBounds = new Rect(toggle.TranslatePoint(new Point(), surface), toggle.RenderSize);
            var navigationBounds = new Rect(navigation.TranslatePoint(new Point(), surface), navigation.RenderSize);
            Assert.False(toggleBounds.IntersectsWith(navigationBounds), "The dock toggle must not cover navigation items.");
        }
        else
        {
            Assert.True(double.IsNaN(toggleHost.Width));
            Assert.Equal(HorizontalAlignment.Stretch, toggleHost.HorizontalAlignment);
        }
    }

    private static void Arrange(FrameworkElement surface, Size size)
    {
        surface.Measure(size);
        surface.Arrange(new Rect(size));
        surface.UpdateLayout();
        surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child))
                yield return descendant;
        }
    }

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == name);

    private static string? SetterValue(XElement style, string property) =>
        style.Elements(Presentation + "Setter").Single(setter => setter.Attribute("Property")?.Value == property)
            .Attribute("Value")?.Value;

    private static string SourcePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "Wisp.App", fileName);
    }
}
