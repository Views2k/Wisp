using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;

namespace Wisp.App.Tests;

public sealed class CalmSidebarTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] PageNames = ["Dashboard", "Appearance", "Diagnostics", "Setup", "Extras"];

    [Fact]
    public void EverySidebarPageUsesTheSameUnbrokenRowSpacing()
    {
        var document = XDocument.Load(SourcePath("MainWindow.xaml"));
        var navigation = Named(document, "SidebarNavigation");
        var pages = Named(document, "RootTabs").Elements(Presentation + "TabItem").ToArray();
        var rows = navigation.Elements(Presentation + "ListBoxItem").ToArray();
        Assert.Equal(Presentation + "ListBox", navigation.Name);
        Assert.Equal(PageNames, pages.Select(page => page.Attribute("Header")?.Value));
        Assert.Equal(PageNames, rows.Select(row => row.Attribute("AutomationProperties.Name")?.Value));
        Assert.Equal(rows.Length, navigation.Elements().Count());
        Assert.Equal("{StaticResource SidebarItemStyle}", navigation.Attribute("ItemContainerStyle")?.Value);
        Assert.Equal("{Binding SelectedIndex, ElementName=RootTabs, Mode=TwoWay}",
            navigation.Attribute("SelectedIndex")?.Value);
        Assert.All(rows, row =>
        {
            Assert.Null(row.Attribute("Margin"));
            Assert.Null(row.Attribute("Height"));
            Assert.Null(row.Attribute("Style"));
            Assert.Empty(row.Elements(Presentation + "ListBoxItem.Style"));
        });

        var resources = XDocument.Load(SourcePath("CalmWindowResources.xaml"));
        var style = resources.Descendants(Presentation + "Style")
            .Single(element => element.Attribute(Xaml + "Key")?.Value == "SidebarItemStyle");
        Assert.Equal("40", SetterValue(style, "Height"));
        Assert.Equal("0,2,0,2", SetterValue(style, "Margin"));
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
    public void SidebarMotionUsesRenderTransformsWithoutAFrameTimer()
    {
        var document = XDocument.Load(SourcePath("MainWindow.xaml"));
        var code = File.ReadAllText(SourcePath("MainWindow.xaml.cs"));
        Assert.Equal("168", Named(document, "SidebarColumn").Attribute("Width")?.Value);
        Assert.NotNull(Named(document, "SidebarHost").Element(Presentation + "Border.RenderTransform")?
            .Element(Presentation + "TranslateTransform"));
        Assert.NotNull(Named(document, "ContentPane").Element(Presentation + "Grid.RenderTransform")?
            .Element(Presentation + "TranslateTransform"));
        Assert.Equal(Presentation + "RotateTransform", Named(document, "SidebarChevronRotation").Name);
        Assert.DoesNotContain("DispatcherTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CompositionTarget.Rendering", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GridLengthAnimation", code, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Descendants(),
            element => element.Name.LocalName.EndsWith(".LayoutTransform", StringComparison.Ordinal));

        var toggle = Named(document, "SidebarToggleButton");
        Assert.Equal(Named(document, "TitleBar"), toggle.Parent);
        Assert.Equal("0", toggle.Attribute("Grid.Column")?.Value);
        Assert.Equal("98,0,0,0", toggle.Attribute("Margin")?.Value);
        Assert.Equal("SidebarToggle_Click", toggle.Attribute("Click")?.Value);
    }

    internal static void AssertOnCurrentDispatcher(MainWindow window, FrameworkElement surface)
    {
        Assert.True(window.Dispatcher.CheckAccess());
        var tabs = Assert.IsType<TabControl>(window.FindName("RootTabs"));
        var navigation = Assert.IsType<ListBox>(window.FindName("SidebarNavigation"));
        var toggle = Assert.IsType<Button>(window.FindName("SidebarToggleButton"));
        var column = Assert.IsType<ColumnDefinition>(window.FindName("SidebarColumn"));
        var sidebar = Assert.IsType<Border>(window.FindName("SidebarHost"));
        var contentPane = Assert.IsType<Grid>(window.FindName("ContentPane"));
        var pageTitle = Assert.IsType<TextBlock>(window.FindName("PageTitleText"));
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
            foreach (var size in new[] { new Size(720, 440), new Size(1040, 760) })
            {
                window.SetSidebarOpen(true, animate: false);
                Arrange(surface, size);
                AssertUniformRows(navigation, surface);
                AssertToggleReachable(toggle, surface);
                Assert.Empty(VisualDescendants(tabs).OfType<TabPanel>());
                var presenter = Assert.IsType<ContentPresenter>(tabs.Template.FindName("PART_SelectedContentHost", tabs));

                for (var index = 0; index < PageNames.Length; index++)
                {
                    navigation.SetCurrentValue(Selector.SelectedIndexProperty, index);
                    Arrange(surface, size);
                    AssertPage(index, tabs, navigation, presenter, pageTitle);
                    var selectedContent = tabs.SelectedContent;
                    var expandedPageWidth = tabs.ActualWidth;

                    window.SetSidebarOpen(false, animate: false);
                    Arrange(surface, size);
                    Assert.False(window.IsSidebarOpen);
                    Assert.Equal(0, column.Width.Value);
                    Assert.Equal(0, column.ActualWidth);
                    Assert.False(navigation.IsEnabled);
                    Assert.False(navigation.IsHitTestVisible);
                    Assert.NotEqual(Visibility.Visible, sidebar.Visibility);
                    Assert.False(DependencyPropertyHelper.GetValueSource(column, ColumnDefinition.WidthProperty).IsAnimated);
                    Assert.Equal(expandedPageWidth + 168, tabs.ActualWidth, precision: 3);
                    Assert.Same(selectedContent, tabs.SelectedContent);
                    AssertPage(index, tabs, navigation, presenter, pageTitle);
                    AssertToggleReachable(toggle, surface);

                    window.SetSidebarOpen(true, animate: false);
                    Arrange(surface, size);
                    Assert.True(window.IsSidebarOpen);
                    Assert.Equal(new GridLength(168), column.Width);
                    Assert.Equal(168, column.ActualWidth);
                    Assert.Equal(Visibility.Visible, sidebar.Visibility);
                    Assert.True(navigation.IsEnabled);
                    Assert.True(navigation.IsHitTestVisible);
                    Assert.Equal(expandedPageWidth, tabs.ActualWidth, precision: 3);
                    Assert.Equal(0, Assert.IsType<TranslateTransform>(sidebar.RenderTransform).X);
                    Assert.Equal(0, Assert.IsType<TranslateTransform>(contentPane.RenderTransform).X);
                    Assert.Same(selectedContent, tabs.SelectedContent);
                    AssertPage(index, tabs, navigation, presenter, pageTitle);
                    AssertUniformRows(navigation, surface);

                    tabs.SelectedIndex = (index + 1) % PageNames.Length;
                    Arrange(surface, size);
                    AssertPage((index + 1) % PageNames.Length, tabs, navigation, presenter, pageTitle);
                }

                window.SetSidebarOpen(false, animate: false);
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(window.IsSidebarOpen);
                Assert.False(DependencyPropertyHelper.GetValueSource(column, ColumnDefinition.WidthProperty).IsAnimated);
                window.SetSidebarOpen(false, animate: false);
                window.SetSidebarOpen(true, animate: false);
                Arrange(surface, size);
                Assert.Equal(0, Assert.IsType<TranslateTransform>(sidebar.RenderTransform).X);
                Assert.Equal(0, Assert.IsType<TranslateTransform>(contentPane.RenderTransform).X);
                AssertUniformRows(navigation, surface);
            }
        }
        finally
        {
            window.SetSidebarOpen(originalOpen, animate: false);
            tabs.SelectedIndex = originalSelection;
            Arrange(surface, originalSize);
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

    private static void AssertUniformRows(ListBox navigation, FrameworkElement surface)
    {
        double? previousTop = null;
        foreach (var (item, index) in navigation.Items.Cast<ListBoxItem>().Select((item, index) => (item, index)))
        {
            Assert.Same(navigation.ItemContainerStyle, item.Style);
            Assert.Equal(PageNames[index], AutomationProperties.GetName(item));
            Assert.Equal(40, item.ActualHeight);
            Assert.Equal(new Thickness(0, 2, 0, 2), item.Margin);
            var top = item.TranslatePoint(new Point(), surface).Y;
            if (previousTop.HasValue)
                Assert.Equal(44, top - previousTop.Value, precision: 3);
            previousTop = top;
        }
    }

    private static void AssertToggleReachable(Button toggle, FrameworkElement surface)
    {
        Assert.Equal(Visibility.Visible, toggle.Visibility);
        Assert.True(toggle.IsEnabled && toggle.IsHitTestVisible && toggle.Focusable && toggle.IsTabStop);
        Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(toggle));
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(toggle)));
        Assert.NotNull(toggle.ToolTip);
        Assert.Equal(32, toggle.ActualWidth);
        Assert.Equal(32, toggle.ActualHeight);
        var point = toggle.TranslatePoint(new Point(), surface);
        Assert.Equal(99, point.X, precision: 3);
        Assert.InRange(point.Y, 0, 25);
        Assert.True(point.X + toggle.ActualWidth <= surface.ActualWidth);
        Assert.True(point.Y + toggle.ActualHeight <= 57);
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
