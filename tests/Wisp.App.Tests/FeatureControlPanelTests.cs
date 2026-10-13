using System.Collections.Immutable;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Xunit;

namespace Wisp.App.Tests;

internal static class FeatureControlPanelTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { AssertPanels(); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void AssertPanels()
    {
        foreach (var legacy in new[] { false, true })
        {
            var directory = Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", "control-panel-" + Guid.NewGuid().ToString("N"));
            var settings = new AppSettings { StartWithWindows = false, StartWithForza = false, AutomaticApplicationUpdateChecks = false };
            var controller = new AppController(settings, _ => { }, new NoStartup(),
                runsDirectory: Path.Combine(directory, "Runs"), shiftCalibrationDirectory: Path.Combine(directory, "Calibration"),
                clipLibraryDirectory: Path.Combine(directory, "Clips"), tuneLibraryDirectory: Path.Combine(directory, "Tunes"));
            ControlPanelWindow? window = null;
            try
            {
                window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
                var tab = Assert.IsType<TabItem>(window.FindName("AppearanceTab"));
                var panel = Assert.IsType<Grid>(tab.Content);
                var body = Assert.IsAssignableFrom<FrameworkElement>(panel.Children[1]);
                var profileName = Assert.IsType<TextBox>(window.FindName("HudProfileNameInput"));
                Invoke(window, "SaveHudProfile_Click", new Button(), new RoutedEventArgs());
                profileName.Text = "Keep my draft";
                var confirm = Assert.IsType<Button>(window.FindName("ConfirmHudProfileButton"));
                var support = Assert.IsType<ContentControl>(window.FindName("SupplementaryPanel"));
                Invoke(window, "SupplementaryPanel_Loaded", support, new RoutedEventArgs());
                var fields = Descendants(support).OfType<TextBox>().ToArray();
                var title = Assert.Single(fields, value => value.MaxLength == 100);
                var message = Assert.Single(fields, value => value.MaxLength == 4000);
                title.Text = "Keep this title"; message.Text = "Keep this message";
                var review = Assert.Single(Descendants(support).OfType<Button>(), value => Equals(value.Content, "Review before sending"));
                var disabled = ImmutableDictionary<string, string>.Empty
                    .Add("appearance", "Appearance editing is paused.")
                    .Add("hud-profiles", "Profile editing is paused.")
                    .Add("support", "Messages are paused.").Add("news", "Notices are paused.");
                controller.ApplyFeatureControlsAsync(new(1, disabled)).GetAwaiter().GetResult();
                Assert.False(body.IsEnabled);
                Assert.Equal("Appearance editing is paused.", Assert.IsType<TextBlock>(panel.Children[0]).Text);
                Assert.False(confirm.IsEnabled);
                Invoke(window, "ConfirmHudProfileDialog_Click", confirm, new RoutedEventArgs());
                Assert.Empty(settings.HudPresets);
                Assert.Equal("Keep my draft", profileName.Text);
                Assert.False(review.IsEnabled);
                Assert.Contains(Descendants(support).OfType<TextBlock>(), value => value.Text == "Messages are paused.");
                Assert.Contains(Descendants(support).OfType<TextBlock>(), value => value.Text == "Notices are paused.");
                controller.ApplyFeatureControlsAsync(new(2, ImmutableDictionary<string, string>.Empty)).GetAwaiter().GetResult();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.True(body.IsEnabled);
                Assert.True(confirm.IsEnabled);
                Assert.True(review.IsEnabled);
                Assert.Equal("Keep my draft", profileName.Text);
                Assert.Equal("Keep this title", title.Text);
                Assert.Equal("Keep this message", message.Text);
                Assert.Empty(settings.HudPresets);
                Assert.False(window.IsVisible);
                Invoke(window, "CancelHudProfileDialog_Click", confirm, new RoutedEventArgs());
                AssertLibraryControls(window, controller);
            }
            finally
            {
                window?.Close();
                controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }

    private static void AssertLibraryControls(ControlPanelWindow window, AppController controller)
    {
        var tabs = Assert.IsType<TabControl>(window.FindName("RootTabs"));
        tabs.SelectedItem = window.FindName("RunsTab");
        Layout(window);
        var runs = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("RunsSurface"));
        var library = Assert.Single(Descendants(runs).OfType<FrameworkElement>(), value =>
            BindingOperations.GetBinding(value, UIElement.VisibilityProperty)?.Path.Path == "LibraryFeatureAvailable");
        var lap = Assert.Single(Descendants(runs).OfType<Runs.LapReviewView>());
        Assert.Same(controller.Runs.LapReview, lap.DataContext);
        var lapRoot = Assert.IsType<StackPanel>(lap.Content);
        var lapStatus = Assert.IsType<TextBlock>(lapRoot.Children[0]);
        var lapBody = Assert.IsType<StackPanel>(lapRoot.Children[1]);
        var clips = Assert.IsType<Clips.ClipsPage>(window.FindName("ClipsSurface"));
        var refreshClips = Assert.Single(Descendants(clips).OfType<Button>(), value =>
            ReferenceEquals(value.Command, controller.Clips.RefreshCommand));
        var tune = Assert.IsType<Tunes.TunePage>(window.FindName("TuneSurface"));
        Await(controller.ApplyFeatureControlsAsync(new(3, ImmutableDictionary<string, string>.Empty
            .Add("lap-review", "Lap review is paused."))));
        Layout(window);
        Assert.Equal(Visibility.Visible, library.Visibility);
        Assert.Equal(Visibility.Collapsed, lapBody.Visibility);
        Assert.Equal("Lap review is paused.", lapStatus.Text);
        Await(controller.ApplyFeatureControlsAsync(new(4, ImmutableDictionary<string, string>.Empty
            .Add("run-library", "Saved runs are paused.")
            .Add("clip-library", "Saved clips are paused.")
            .Add("tune-read", "Tune reading is paused."))));
        Layout(window);
        Assert.Equal(Visibility.Collapsed, library.Visibility);
        Assert.False(refreshClips.IsEnabled);
        Assert.Contains(Descendants(clips).OfType<TextBlock>(), value => value.Text == "Saved clips are paused.");
        Assert.Contains(Descendants(tune).OfType<TextBlock>(), value => value.Text == "Tune reading is paused.");
        Assert.True(controller.FeatureAllowed("run-recording"));
        Assert.True(controller.FeatureAllowed("clip-recording"));
        Await(controller.ApplyFeatureControlsAsync(new(5, ImmutableDictionary<string, string>.Empty)));
        Layout(window);
        Assert.Equal(Visibility.Visible, library.Visibility);
        Assert.Equal(Visibility.Visible, lapBody.Visibility);
        Assert.Equal(Visibility.Collapsed, lapStatus.Visibility);
        Assert.True(refreshClips.IsEnabled);
        Assert.False(controller.Clips.HasFeatureStatus);
        Assert.False(window.IsVisible);
    }

    private static void Layout(ControlPanelWindow window)
    {
        var surface = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var size = new Size(720, 440);
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
    }

    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(10) };
            timeout.Tick += (_, _) => frame.Continue = false;
            _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
            timeout.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); }
            Assert.True(task.IsCompleted, "Feature-control fixture exceeded its completion bound.");
        }
        task.GetAwaiter().GetResult();
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
    }

    private static void Invoke(ControlPanelWindow window, string name, object sender, RoutedEventArgs args) =>
        typeof(ControlPanelWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [sender, args]);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class NoStartup : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
