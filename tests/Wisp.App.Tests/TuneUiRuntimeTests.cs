using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App.Tunes;
using Xunit;

namespace Wisp.App.Tests;

internal static class TuneUiRuntimeTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            foreach (var legacy in new[] { false, true }) Verify(legacy);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void Verify(bool legacy)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WispTuneLayout", Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { UseLegacyInterface = legacy, StartWithForza = false, StartWithWindows = false, AutomaticApplicationUpdateChecks = false };
        var controller = new AppController(settings, _ => { }, new NoStartupRegistration(), tuneLibraryDirectory: directory);
        ControlPanelWindow? window = null;
        var snapshot = TuneUiTestData.ValidSnapshot();
        using var model = new TuneViewModel(new TuneStore(directory), _ => Task.FromResult(new TuneCaptureResult(snapshot, TuneCaptureStatus.Ready, "")), _ => true, Dispatcher.CurrentDispatcher);
        try
        {
            window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
            var page = Assert.IsType<TunePage>(window.FindName("TuneSurface"));
            page.DataContext = model;
            var tabs = Assert.IsType<TabControl>(window.FindName("RootTabs"));
            tabs.SelectedItem = window.FindName("TuneTab");
            var surface = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            Await(model.InitializeAsync()); Await(model.RefreshAsync());
            Assert.Equal(9, model.Categories.Count);
            foreach (var size in new[] { new Size(720, 440), new Size(1280, 900) })
            {
                Arrange(surface, size);
                var categories = Assert.IsType<ListBox>(page.FindName("TuneCategories"));
                Assert.Equal(9, categories.Items.Count);
                Assert.True(categories.ActualWidth > 400);
                Assert.True(categories.ActualWidth <= page.ActualWidth + 1);
                model.BeginSave(); Arrange(surface, size);
                var dialog = Assert.IsType<Grid>(page.FindName("TuneDialog"));
                Assert.Equal(Visibility.Visible, dialog.Visibility);
                Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(dialog));
                Assert.False(Assert.IsType<ListBox>(window.FindName("SidebarNavigation")).IsEnabled);
                var name = Assert.IsType<TextBox>(page.FindName("TuneNameInput"));
                var description = Assert.IsType<TextBox>(page.FindName("TuneDescriptionInput"));
                Assert.True(description.AcceptsReturn && description.IsEnabled && description.Focusable && name.IsEnabled);
                Assert.Equal(TextWrapping.Wrap, description.TextWrapping);
                Assert.True(name.ActualWidth > 250 && name.ActualWidth <= page.ActualWidth);
                AssertFits(Assert.IsType<Button>(page.FindName("TuneDialogSave")), dialog);
                AssertFits(Assert.IsType<Button>(page.FindName("TuneDialogCancel")), dialog);
                var fields = Assert.IsType<ScrollViewer>(page.FindName("TuneDialogFields"));
                Assert.True(fields.ViewportHeight > 0);
                fields.ScrollToEnd(); Arrange(surface, size);
                AssertFits(Assert.IsType<Button>(page.FindName("TuneDialogSave")), dialog);
                model.CancelDialog(); Arrange(surface, size);
                Assert.True(Assert.IsType<ListBox>(window.FindName("SidebarNavigation")).IsEnabled);
                Assert.Equal(Visibility.Collapsed, dialog.Visibility);
            }
            model.BeginSave(); model.DialogName = "Baseline"; Await(model.ConfirmDialogAsync());
            var baseline = Assert.Single(model.Library);
            model.SetWorkspace(TuneWorkspace.Compare); model.CompareA = baseline; model.CompareB = baseline;
            Arrange(surface, new Size(720, 440));
            foreach (var selector in Descendants(page).OfType<ComboBox>().Where(value => value.IsVisible))
            {
                selector.ApplyTemplate();
                var popup = Assert.IsType<Popup>(selector.Template.FindName("PART_Popup", selector));
                var popupBorder = Assert.IsType<Border>(popup.Child);
                Assert.Same(selector.FindResource("PanelBrush"), popupBorder.Background);
                Assert.True(popupBorder.CornerRadius.TopLeft > 0);
                var root = Assert.IsType<Grid>(selector.Template.FindName("ComboRoot", selector));
                selector.IsEnabled = false; Arrange(surface, new Size(720, 440)); Assert.Equal(.45, root.Opacity);
                selector.ClearValue(UIElement.IsEnabledProperty); Arrange(surface, new Size(720, 440)); Assert.Equal(1, root.Opacity);
            }
            var comparison = Assert.IsType<ItemsControl>(page.FindName("TuneComparison"));
            Assert.Equal(2, comparison.Items.Count); Assert.All(model.Rows, row => Assert.False(row.Changed));
            var bitmap = new RenderTargetBitmap(720, 440, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
            Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
        }
        finally
        {
            if (window is not null) window.Close();
            Await(controller.DisposeAsync().AsTask());
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void Arrange(FrameworkElement surface, Size size)
    {
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
    }

    private static void AssertFits(FrameworkElement element, FrameworkElement container)
    {
        var bounds = element.TransformToAncestor(container).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -.5 && bounds.Top >= -.5 &&
            bounds.Right <= container.ActualWidth + .5 && bounds.Bottom <= container.ActualHeight + .5,
            "Dialog actions must fit inside the visible page.");
    }

    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(10) };
            timeout.Tick += (_, _) => frame.Continue = false;
            _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
            timeout.Start(); try { Dispatcher.PushFrame(frame); } finally { timeout.Stop(); }
            Assert.True(task.IsCompleted, "Tune UI operation exceeded its bound.");
        }
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private sealed class NoStartupRegistration : IStartupRegistrationService { public void Apply(bool startWithWindows, bool startWithForza) { } }
}
