using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace Wisp.App.Tests;

internal static class SupportReminderUiTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        VerifyPopup();
        foreach (var legacy in new[] { false, true }) VerifyWindow(legacy);
    }

    private static void VerifyPopup()
    {
        var popup = new SupportReminderPopup();
        var root = Assert.IsType<Grid>(popup.Content);
        var card = Assert.IsType<Border>(Assert.Single(root.Children.Cast<UIElement>()));
        var cardContent = Assert.IsType<Grid>(card.Child);
        var scroll = Assert.Single(cardContent.Children.OfType<ScrollViewer>());
        var content = Assert.IsType<StackPanel>(scroll.Content);
        var message = Assert.IsType<TextBlock>(popup.FindName("Message"));
        var repository = Assert.IsType<Button>(popup.FindName("RepositoryButton"));
        var dismiss = Assert.IsType<Button>(popup.FindName("DismissButton"));
        Assert.Equal("Wisp is a project I’ve spent months and thousands of hours working on. Keeping it free and accessible to everyone has always been a priority for me, so all I ask is: if you enjoy Wisp, please leave a star on GitHub :) -Views", message.Text);
        Assert.Equal(TextWrapping.Wrap, message.TextWrapping);
        Assert.Equal("Views2k/Wisp — star on GitHub", new ButtonAutomationPeer(repository).GetName());
        Assert.Equal("Close the support reminder", new ButtonAutomationPeer(dismiss).GetName());
        Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(popup));
        Assert.All(new[] { repository, dismiss }, button => Assert.True(button.Focusable && button.IsTabStop && button.IsEnabled));
        Assert.False(scroll.Focusable);

        foreach (var size in new[] { new Size(720, 440), new Size(1280, 900), new Size(720, 280) })
        {
            scroll.ScrollToTop();
            Arrange(popup, size);
            AssertFits(card, popup);
            AssertFits(scroll, card);
            AssertFits(dismiss, card);
            Assert.True(scroll.ViewportHeight > 0);
            Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
            Assert.True(content.ActualWidth <= scroll.ViewportWidth + 1);
            if (size.Height == 280) Assert.True(scroll.ScrollableHeight > 0);
            scroll.ScrollToEnd();
            Arrange(popup, size);
            AssertFits(dismiss, card);
            AssertFits(repository, scroll);
            var repositoryContent = Assert.IsType<Grid>(repository.Content);
            Assert.All(repositoryContent.Children.OfType<FrameworkElement>(), child => AssertFits(child, repository));
            var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(popup);
            if (Environment.GetEnvironmentVariable("WISP_SUPPORT_PREVIEW_DIRECTORY") is { Length: > 0 } previewDirectory)
            {
                Directory.CreateDirectory(previewDirectory);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.Combine(previewDirectory, $"support-{(int)size.Width}x{(int)size.Height}.png"));
                encoder.Save(output);
            }
        }

        Assert.Same(popup.FindResource("PanelBrush"), card.Background);
        Assert.Same(popup.FindResource("TextBrush"), message.Foreground);
        Assert.Same(popup.FindResource("RaisedBrush"), repository.Background);
        var panel = new SolidColorBrush(Colors.DarkSlateBlue);
        var text = new SolidColorBrush(Colors.Ivory);
        var raised = new SolidColorBrush(Colors.DarkOliveGreen);
        var stroke = new SolidColorBrush(Colors.Orange);
        popup.Resources["PanelBrush"] = panel;
        popup.Resources["TextBrush"] = text;
        popup.Resources["RaisedBrush"] = raised;
        popup.Resources["StrokeBrush"] = stroke;
        Arrange(popup, new Size(720, 440));
        Assert.Same(panel, card.Background);
        Assert.Same(text, message.Foreground);
        Assert.Same(text, repository.Foreground);
        Assert.Same(raised, repository.Background);
        Assert.Same(stroke, card.BorderBrush);
        Assert.Same(stroke, repository.BorderBrush);
    }

    private static void VerifyWindow(bool legacy)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", "support-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new AppSettings
        {
            UseLegacyInterface = legacy,
            StartWithForza = false,
            StartWithWindows = false,
            AutomaticApplicationUpdateChecks = false,
            BackgroundParticlesEnabled = false,
            CompletedFeatureTourId = FeatureTourSession.CurrentTourId,
            SetupCompletion = new SetupCompletionRecord
            {
                Version = SetupCompletionRecord.CurrentVersion,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ValidatedUdpPort = 5500,
                ValidatedPackets = SetupCompletionRecord.MinimumPackets,
                MovingPackets = SetupCompletionRecord.MinimumMovingPackets,
                ValidatedElapsedMilliseconds = SetupCompletionRecord.MinimumElapsedMilliseconds,
                DataOutConfirmed = true,
                DisplayModeConfirmed = true,
                StockHudConfirmed = true
            }
        };
        var writes = 0;
        var controller = new AppController(settings, _ => writes++, new NoStartupRegistration(),
            runsDirectory: Path.Combine(directory, "Runs"),
            shiftCalibrationDirectory: Path.Combine(directory, "ShiftCalibration"),
            clipLibraryDirectory: Path.Combine(directory, "Clips"),
            tuneLibraryDirectory: Path.Combine(directory, "Tunes"));
        ControlPanelWindow? window = null;
        try
        {
            window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
            var popup = Assert.IsType<SupportReminderPopup>(window.FindName("SupportReminderPopup"));
            var body = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ControlBody"));
            var titleBar = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("TitleBar"));
            var shell = Assert.IsType<Grid>(Assert.IsType<Border>(window.Content).Child);
            Assert.Contains(popup, shell.Children.Cast<UIElement>());
            Assert.Equal(1, Grid.GetRow(popup));
            Assert.Equal(shell.RowDefinitions.Count - 1, Grid.GetRowSpan(popup));
            Assert.Equal(Visibility.Collapsed, popup.Visibility);
            Assert.False(window.IsActive);
            Assert.False(settings.RequiresSetup);
            var beforeDiscovery = writes;
            window.SetFeatureTourDiscoveryAllowed(true);
            Pump();
            Assert.False(window.IsSupportReminderOpen);
            Assert.Null(settings.LastSupportReminderShownUtc);
            Assert.Equal(beforeDiscovery, writes);
            window.SetFeatureTourDiscoveryAllowed(false);
            Assert.False(ReminderField<bool>(window, "_supportReminderRequested"));
            Assert.False(ReminderField<bool>(window, "_supportReminderDailyEnabled"));
            InvokeWindowEvent(window, "OnActivated");
            Pump();
            Assert.False(ReminderField<bool>(window, "_supportReminderRequested"));
            Assert.False(window.IsSupportReminderOpen);

            // A deliberate tray/second-instance reopen remains pending even with
            // a fresh receipt, but cannot display in a hidden or inactive window.
            settings.LastSupportReminderShownUtc = DateTimeOffset.UtcNow;
            window.SetFeatureTourDiscoveryAllowed(true);
            Pump();
            Assert.True(ReminderField<bool>(window, "_supportReminderRequested"));
            Assert.True(ReminderField<bool>(window, "_supportReminderDailyEnabled"));
            Assert.False(window.IsSupportReminderOpen);
            Assert.Equal(beforeDiscovery, writes);
            window.SetFeatureTourDiscoveryAllowed(false);
            settings.LastSupportReminderShownUtc = null;

            typeof(ControlPanelWindow).GetMethod("ScheduleDailySupportReminder",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { TimeSpan.FromHours(24) });
            var timer = ReminderField<DispatcherTimer>(window, "_supportReminderTimer");
            Assert.True(timer.IsEnabled);
            InvokeWindowEvent(window, "OnDeactivated");
            Assert.False(timer.IsEnabled);
            timer.Start();
            window.WindowState = WindowState.Minimized;
            InvokeWindowEvent(window, "OnStateChanged");
            Assert.False(timer.IsEnabled);
            window.WindowState = WindowState.Normal;
            window.SetFeatureTourDiscoveryAllowed(false);

            var dismiss = Assert.IsType<Button>(popup.FindName("DismissButton"));
            body.IsEnabled = false;
            popup.Visibility = Visibility.Visible;
            // Focus changes must preserve the same offer, including a browser
            // activation after following the repository link.
            InvokeWindowEvent(window, "OnDeactivated");
            Assert.True(window.IsSupportReminderOpen);
            Assert.False(body.IsEnabled);
            window.WindowState = WindowState.Minimized;
            Assert.True(window.IsSupportReminderOpen);
            window.WindowState = WindowState.Normal;
            window.SetFeatureTourDiscoveryAllowed(false);
            Assert.True(window.IsSupportReminderOpen);
            window.SetFeatureTourDiscoveryAllowed(true);
            InvokeWindowEvent(window, "OnActivated");
            Pump();
            Assert.True(window.IsSupportReminderOpen);
            Assert.False(body.IsEnabled);

            popup.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.MouseDownEvent });
            Assert.True(window.IsSupportReminderOpen);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, new OffscreenSource(popup), Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            popup.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.True(window.IsSupportReminderOpen);

            window.StartFeatureTour();
            Assert.False(window.FeatureTour.IsOpen);
            Assert.True(window.IsSupportReminderOpen);
            if (window is MainWindow modern)
            {
                modern.SetDashboardDisplayMode(true);
                Assert.False(modern.IsDashboardDisplayMode);
                Assert.True(window.IsSupportReminderOpen);
            }
            dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.IsSupportReminderOpen);
            Assert.True(body.IsEnabled);
            Assert.True(titleBar.IsEnabled);
            Assert.False((bool)typeof(ControlPanelWindow).GetField("_supportReminderRequested",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            Assert.True(ReminderField<bool>(window, "_supportReminderDailyEnabled"));
            Assert.NotNull(ReminderField<DateTimeOffset?>(window, "_supportReminderLastDismissedUtc"));
            InvokeWindowEvent(window, "OnActivated");
            Pump();
            Assert.False(ReminderField<bool>(window, "_supportReminderRequested"));
            Assert.False(window.IsSupportReminderOpen);
            Assert.False(timer.IsEnabled);

            foreach (var dialogName in new[] { "HudProfileDialog", "ApplicationUpdateConfirmation" })
            {
                var dialog = Assert.IsType<Grid>(window.FindName(dialogName));
                body.IsEnabled = false;
                dialog.Visibility = Visibility.Visible;
                popup.Visibility = Visibility.Visible;
                dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(window.IsSupportReminderOpen);
                Assert.False(body.IsEnabled);
                Assert.Equal(Visibility.Visible, dialog.Visibility);
                dialog.Visibility = Visibility.Collapsed;
                body.IsEnabled = true;
            }
            Assert.Null(settings.LastSupportReminderShownUtc);
            Assert.Equal(beforeDiscovery, writes);
            Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
            timer.Start();
            window.Close();
            Assert.False(timer.IsEnabled);
            Assert.False(ReminderField<bool>(window, "_supportReminderDailyEnabled"));
        }
        finally
        {
            window?.Close();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Pump();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Arrange(FrameworkElement surface, Size size)
    {
        surface.Measure(size);
        surface.Arrange(new Rect(size));
        surface.UpdateLayout();
        Pump();
        surface.Measure(size);
        surface.Arrange(new Rect(size));
        surface.UpdateLayout();
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

    private static void InvokeWindowEvent(Window window, string name) =>
        typeof(Window).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { EventArgs.Empty });

    private static T ReminderField<T>(ControlPanelWindow window, string name) =>
        (T)typeof(ControlPanelWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private sealed class OffscreenSource(Visual visual) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = visual;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static void AssertFits(FrameworkElement element, FrameworkElement container)
    {
        var bounds = element.TransformToAncestor(container).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -1 && bounds.Top >= -1 &&
            bounds.Right <= container.ActualWidth + 1 && bounds.Bottom <= container.ActualHeight + 1,
            $"{element.GetType().Name} {element.Name} must fit inside {container.GetType().Name}.");
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
