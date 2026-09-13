using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunListVisualTests
{
    [Fact]
    public void DisabledSavedRunListKeepsItsThemeAndSelectionThenRestoresVirtualizedScrolling() => OnSta(() =>
    {
        using var source = File.OpenRead(SourcePath("RunListResources.xaml"));
        var resources = Assert.IsType<ResourceDictionary>(XamlReader.Load(source));
        var list = new ListBox
        {
            Style = Assert.IsType<Style>(resources[typeof(ListBox)]),
            ItemContainerStyle = LibraryItemStyle(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { new RunItem("First run"), new RunItem("Second run") },
            DisplayMemberPath = nameof(RunItem.Name),
            SelectedIndex = 0
        };
        AppThemeResources.Apply(list.Resources, AppColorThemes.Resolve("Purple"));
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var gate = new CheckBox { IsChecked = true };
        list.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = gate });
        var background = Color.FromRgb(27, 24, 32);
        var host = new Border { Width = 240, Height = 320, Background = new SolidColorBrush(background), Child = list };
        var enabled = Render(host);
        var originalSelection = list.SelectedItem;
        var originalBinding = list.GetBindingExpression(UIElement.IsEnabledProperty);
        gate.IsChecked = false;
        var disabled = Render(host);

        Assert.False(list.IsEnabled);
        Assert.NotNull(originalBinding);
        Assert.Same(originalBinding, list.GetBindingExpression(UIElement.IsEnabledProperty));
        Assert.Same(originalSelection, list.SelectedItem);
        Assert.False(Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0)).IsEnabled);
        Assert.Equal(background, Pixel(enabled, 120, 290));
        Assert.Equal(background, Pixel(disabled, 120, 290));
        var border = Assert.IsType<Border>(list.Template.FindName("RunListBorder", list));
        Assert.Same(list.Background, border.Background);

        gate.IsChecked = true;
        var restored = Render(host);
        Assert.True(list.IsEnabled && list.Focusable);
        Assert.False(list.IsTabStop);
        Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(list));
        var restoredItem = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.True(restoredItem.IsEnabled && restoredItem.Focusable && restoredItem.IsTabStop);
        Assert.Same(originalSelection, list.SelectedItem);
        Assert.Equal(enabled, restored);
        list.ItemsSource = Enumerable.Range(0, 200).Select(index => new RunItem($"Saved run {index}")).ToArray();
        Render(host);
        var panel = Assert.Single(Descendants(list).OfType<VirtualizingStackPanel>());
        Assert.InRange(panel.Children.Count, 1, 199);
        var scroll = Assert.IsType<ScrollViewer>(list.Template.FindName("PART_ScrollViewer", list));
        Assert.True(scroll.CanContentScroll);
        var presenter = Assert.Single(Descendants(list).OfType<ItemsPresenter>());
        Assert.Equal(KeyboardNavigationMode.Contained, KeyboardNavigation.GetDirectionalNavigation(presenter));
        scroll.ScrollToEnd();
        Render(host);
        Assert.True(scroll.VerticalOffset > 0 && scroll.ExtentHeight > scroll.ViewportHeight);
        Assert.InRange(panel.Children.Count, 1, 199);
    });

    private static Style LibraryItemStyle()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var page = XDocument.Load(SourcePath("RunsPage.xaml"));
        var style = page.Descendants(presentation + "Style").Single(element => element.Attribute(xaml + "Key")?.Value == "RunLibraryItem");
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", xaml), new XElement(style));
        var resources = Assert.IsType<ResourceDictionary>(XamlReader.Parse(dictionary.ToString()));
        return Assert.IsType<Style>(resources["RunLibraryItem"]);
    }

    private sealed record RunItem(string Name);

    private static byte[] Render(FrameworkElement surface)
    {
        surface.Measure(new Size(240, 320));
        surface.Arrange(new Rect(0, 0, 240, 320));
        surface.UpdateLayout();
        surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(240, 320, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[240 * 320 * 4];
        bitmap.CopyPixels(pixels, 240 * 4, 0);
        return pixels;
    }

    private static Color Pixel(byte[] pixels, int x, int y)
    {
        var index = (y * 240 + x) * 4;
        return Color.FromArgb(pixels[index + 3], pixels[index + 2], pixels[index + 1], pixels[index]);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string SourcePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wisp.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "Wisp.App", "Runs", name);
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Runs list visual check timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
