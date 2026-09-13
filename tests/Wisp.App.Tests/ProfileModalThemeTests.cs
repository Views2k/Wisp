using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace Wisp.App.Tests;

internal static class ProfileModalThemeTests
{
    internal static void Verify(ControlPanelWindow window)
    {
        var surface = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var tabs = Assert.IsType<TabControl>(window.FindName("RootTabs"));
        var navigation = Assert.IsType<ListBox>(window.FindName("SidebarNavigation"));
        var body = Assert.IsType<Grid>(window.FindName("ControlBody"));
        var dialog = Assert.IsType<Grid>(window.FindName("HudProfileDialog"));
        var originalPage = tabs.SelectedIndex;
        var originalSize = surface.RenderSize;
        try
        {
            tabs.SelectedIndex = 2;
            Arrange();
            var binding = navigation.GetBindingExpression(Selector.SelectedIndexProperty);
            Assert.NotNull(binding);
            var selection = navigation.SelectedItem;
            var originalBounds = navigation.TransformToAncestor(surface).TransformBounds(new Rect(navigation.RenderSize));
            var save = Assert.Single(LogicalDescendants(surface).OfType<Button>(), button => Equals(button.Content, "Save combination to Profiles"));
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Arrange();
            Assert.Equal(Visibility.Visible, dialog.Visibility);
            Assert.False(body.IsEnabled);
            Assert.False(navigation.IsEnabled);
            Assert.Same(selection, navigation.SelectedItem);
            Assert.Same(binding, navigation.GetBindingExpression(Selector.SelectedIndexProperty));
            Assert.Equal(originalBounds, navigation.TransformToAncestor(surface).TransformBounds(new Rect(navigation.RenderSize)));
            var border = Assert.IsType<Border>(navigation.Template.FindName("NavigationBorder", navigation));
            Assert.Same(navigation.Background, border.Background);
            Assert.Equal((byte)0, Assert.IsType<SolidColorBrush>(border.Background).Color.A);
            var pixel = new byte[4];
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(navigation.ActualWidth), (int)Math.Ceiling(navigation.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(navigation);
            bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert.Equal((byte)0, pixel[3]);
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(dialog));
            var name = Assert.IsType<TextBox>(window.FindName("HudProfileNameInput"));
            var confirm = Assert.IsType<Button>(window.FindName("ConfirmHudProfileButton"));
            Assert.True(name.IsEnabled && name.Focusable && name.IsTabStop);
            Assert.True(confirm.IsEnabled && confirm.Focusable && confirm.IsTabStop);
            var cancel = Assert.Single(LogicalDescendants(dialog).OfType<Button>(), button => Equals(button.Content, "Cancel"));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Arrange();
            Assert.Equal(Visibility.Collapsed, dialog.Visibility);
            Assert.True(body.IsEnabled && navigation.IsEnabled);
            Assert.Same(selection, navigation.SelectedItem);
            Assert.Same(binding, navigation.GetBindingExpression(Selector.SelectedIndexProperty));
            var item = Assert.IsType<ListBoxItem>(navigation.ItemContainerGenerator.ContainerFromIndex(2));
            Assert.True(item.IsEnabled && item.Focusable && item.IsTabStop);
            Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
        }
        finally
        {
            if (dialog.Visibility == Visibility.Visible)
                LogicalDescendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            tabs.SelectedIndex = originalPage;
            if (originalSize.Width > 0 && originalSize.Height > 0)
            { surface.Measure(originalSize); surface.Arrange(new Rect(originalSize)); surface.UpdateLayout(); }
        }

        void Arrange()
        {
            surface.Measure(new Size(1280, 900)); surface.Arrange(new Rect(0, 0, 1280, 900)); surface.UpdateLayout();
            surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
            surface.UpdateLayout();
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
}
