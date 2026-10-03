using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace Wisp.App.Tests;

internal static class MainWindowRevisionAssertions
{
    internal static void Verify(MainWindow window, FrameworkElement surface, TabControl tabs, AppController controller)
    {
        var settings = controller.Settings;
        var originalStyle = settings.ApplicationStyle.Clone();
        var originalResizable = settings.ResizableDashboardDisplay;
        var originalTab = tabs.SelectedIndex;
        var originalSize = surface.RenderSize;
        var originalOpen = window.IsSidebarOpen;
        var originalAccent = settings.ColorTheme;
        var originalBackground = settings.BackgroundTheme;
        var originalCustomAccent = settings.CustomAccentColor;
        var originalCustomBackground = settings.CustomBackgroundColor;
        try
        {
            Assert.False(window.IsLoaded);
            VerifyDisplayLayouts(window, surface, tabs);
            VerifyStyleControls(window, surface, tabs, controller);
            VerifyPaletteInitialization(controller);
            VerifyShortcutCaptureGuard(window, controller);
            VerifyClipsNotice(window, surface, tabs);
        }
        finally
        {
            window.SetDashboardDisplayMode(false);
            window.SetResizableDisplay(originalResizable);
            controller.SetApplicationStyle(originalStyle);
            settings.ColorTheme = originalAccent;
            settings.BackgroundTheme = originalBackground;
            settings.CustomAccentColor = originalCustomAccent;
            settings.CustomBackgroundColor = originalCustomBackground;
            Invoke(window, "InitializeApplicationStyle");
            AppThemeResources.Apply(window.Resources, AppColorThemes.Resolve(originalAccent),
                AppBackgroundThemes.Resolve(originalBackground), originalCustomAccent, originalCustomBackground, originalStyle);
            window.SetSidebarOpen(originalOpen, animate: false);
            tabs.SelectedIndex = originalTab;
            Arrange(surface, originalSize);
        }
    }

    private static void VerifyClipsNotice(MainWindow window, FrameworkElement surface, TabControl tabs)
    {
        var notice = Element<Border>(window, "DashboardClipPanel");
        var previousContext = notice.DataContext;
        var dismiss = new NoticeCommand();
        var disable = new NoticeCommand();
        try
        {
            foreach (var compact in new[] { false, true })
                foreach (var newClips in new[] { false, true })
                {
                    notice.DataContext = new
                    {
                        HasDashboardNotice = true,
                        CanDisableDashboardNotice = newClips,
                        DashboardNoticeTitle = newClips ? "New clips" : "Clips need attention",
                        DashboardNoticeText = newClips ? "A new clip is ready to view." : "The clip could not be saved. Open Clips to review the retained files.",
                        DismissDashboardNoticeCommand = dismiss,
                        DisableDashboardNoticesCommand = disable
                    };
                    window.SetResizableDisplay(true);
                    window.SetDashboardDisplayMode(compact);
                    tabs.SelectedIndex = 0;
                    Arrange(surface, compact ? new Size(600, 420) : new Size(720, 440));
                    Assert.Equal(Visibility.Visible, notice.Visibility);
                    Assert.InRange(notice.ActualWidth, 1, 400);
                    var origin = notice.TranslatePoint(new Point(), surface);
                    Assert.InRange(origin.X, 0, surface.ActualWidth - notice.ActualWidth + 0.5);
                    Assert.InRange(origin.Y, 0, surface.ActualHeight - notice.ActualHeight + 0.5);
                    var content = Assert.IsType<StackPanel>(notice.Child);
                    var buttons = LogicalDescendants(content).OfType<Button>().ToArray();
                    Assert.Equal(3, buttons.Length);
                    var close = Assert.Single(buttons, button => ReferenceEquals(button.Command, dismiss));
                    var dontShow = Assert.Single(buttons, button => ReferenceEquals(button.Command, disable));
                    Assert.Equal(newClips ? Visibility.Visible : Visibility.Collapsed, dontShow.Visibility);
                    foreach (var button in buttons.Where(button => button.Visibility == Visibility.Visible))
                    {
                        var position = button.TranslatePoint(new Point(), notice);
                        Assert.InRange(button.ActualWidth, 1, notice.ActualWidth);
                        Assert.InRange(position.X, 0, notice.ActualWidth - button.ActualWidth + 0.5);
                        Assert.InRange(position.Y, 0, notice.ActualHeight - button.ActualHeight + 0.5);
                    }
                    var click = typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    click.Invoke(close, null);
                    if (newClips) click.Invoke(dontShow, null);
                    var action = Assert.Single(buttons, button => Equals(button.Content, "Review clips"));
                    action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(window.IsDashboardDisplayMode);
                    Assert.Equal("Clips", Assert.IsType<TabItem>(tabs.SelectedItem).Header);
                }
            Assert.Equal(4, dismiss.Executions);
            Assert.Equal(2, disable.Executions);
        }
        finally { notice.DataContext = previousContext; }
    }

    private static void VerifyShortcutCaptureGuard(MainWindow window, AppController controller)
    {
        var previousPanel = controller.ControlPanel;
        var previousRunsCapture = controller.Runs.ShortcutCaptureActive;
        var previousClipsCapture = controller.Clips.ShortcutCaptureActive;
        var button = Element<Button>(window, "OverlayHotkeyCaptureButton");
        controller.ControlPanel = window;
        controller.Runs.ShortcutCaptureActive = false;
        controller.Clips.ShortcutCaptureActive = false;
        try
        {
            Assert.False(controller.ShortcutCaptureActive);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.IsCapturingOverlayHotkey);
            Assert.True(controller.ShortcutCaptureActive);

            // Completion and Escape cancellation share this existing end path.
            Invoke(window, "EndOverlayHotkeyCapture", typeof(ControlPanelWindow));
            Assert.False(window.IsCapturingOverlayHotkey);
            Assert.False(controller.ShortcutCaptureActive);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(controller.ShortcutCaptureActive);
            button.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,
                0, button, window)
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            Assert.False(window.IsCapturingOverlayHotkey);
            Assert.False(controller.ShortcutCaptureActive);

            controller.Runs.ShortcutCaptureActive = true;
            Assert.True(controller.ShortcutCaptureActive);
            controller.Clips.ShortcutCaptureActive = true;
            controller.Runs.ShortcutCaptureActive = false;
            Assert.True(controller.ShortcutCaptureActive);
            controller.Clips.ShortcutCaptureActive = false;
            Assert.False(controller.ShortcutCaptureActive);
        }
        finally
        {
            Invoke(window, "EndOverlayHotkeyCapture", typeof(ControlPanelWindow));
            controller.ControlPanel = previousPanel;
            controller.Runs.ShortcutCaptureActive = previousRunsCapture;
            controller.Clips.ShortcutCaptureActive = previousClipsCapture;
        }
    }

    private static void VerifyDisplayLayouts(MainWindow window, FrameworkElement surface, TabControl tabs)
    {
        var toolbar = Element<Grid>(window, "DashboardToolbar");
        var content = Element<StackPanel>(window, "DashboardContent");
        var scroll = Element<ScrollViewer>(window, "DashboardViewport");
        var scale = Assert.IsType<ScaleTransform>(content.LayoutTransform);
        Assert.Same(toolbar.Parent, scroll.Parent);
        Assert.False(content.IsAncestorOf(toolbar));
        Assert.Equal(1, Grid.GetRow(toolbar));
        Assert.Equal(2, Grid.GetRow(scroll));
        tabs.SelectedIndex = 0;
        Arrange(surface, new Size(1280, 820));
        Invoke(window, "FitToCurrentWorkArea", typeof(ControlPanelWindow));
        var normalMinimum = new Size(window.MinWidth, window.MinHeight);
        var baseline = content.RenderSize;
        var nativeState = window.WindowState;

        foreach (var resizable in new[] { false, true })
        {
            window.SetResizableDisplay(resizable);
            window.SetDashboardDisplayMode(true);
            Assert.True(window.IsDashboardDisplayMode);
            Assert.Equal(Visibility.Visible, toolbar.Visibility);
            foreach (var size in new[] { new Size(520, 360), new Size(1280, 820) })
            {
                Arrange(surface, size);
                Assert.True(toolbar.LayoutTransform.Value.IsIdentity);
                var transform = toolbar.TransformToAncestor(surface).TransformBounds(new Rect(toolbar.RenderSize));
                Assert.Equal(toolbar.ActualWidth, transform.Width, 4);
                Assert.Equal(toolbar.ActualHeight, transform.Height, 4);
                Assert.Equal(resizable, double.IsNaN(content.Width));
                if (resizable)
                {
                    Assert.Equal(1, scale.ScaleX);
                    Assert.Equal(1, scale.ScaleY);
                }
                else
                {
                    Assert.Equal(1280, content.Width);
                    Assert.True(scale.ScaleX > 0 && double.IsFinite(scale.ScaleX));
                    Assert.Equal(scale.ScaleX, scale.ScaleY);
                }
                foreach (var name in new[] { "DashboardSizeButton", "DashboardDisplayButton" })
                {
                    var button = Element<Button>(window, name);
                    Assert.True(button.IsEnabled && button.IsHitTestVisible);
                    var bounds = button.TransformToAncestor(surface).TransformBounds(new Rect(button.RenderSize));
                    Assert.InRange(bounds.Left, 0, surface.ActualWidth);
                    Assert.True(bounds.Right <= surface.ActualWidth && bounds.Bottom <= surface.ActualHeight);
                    Assert.True(bounds.Height >= 30);
                }
                Assert.InRange(scroll.ScrollableWidth, 0, 0.1);
            }
            Invoke(window, "FitToCurrentWorkArea", typeof(ControlPanelWindow));
            Assert.InRange(window.MinWidth, 1, 440);
            Assert.InRange(window.MinHeight, 1, 280);
            window.SetDashboardDisplayMode(false);
            Arrange(surface, new Size(1280, 820));
            Assert.Equal(Visibility.Collapsed, toolbar.Visibility);
            Assert.True(double.IsNaN(content.Width));
            Assert.Equal(1, scale.ScaleX);
            Assert.Equal(1, scale.ScaleY);
            Assert.Equal(baseline.Width, content.ActualWidth, 3);
            Assert.Equal(baseline.Height, content.ActualHeight, 3);
            Assert.Equal(nativeState, window.WindowState);
            Invoke(window, "FitToCurrentWorkArea", typeof(ControlPanelWindow));
            Assert.Equal(normalMinimum, new Size(window.MinWidth, window.MinHeight));
        }
    }

    private static void VerifyStyleControls(MainWindow window, FrameworkElement surface, TabControl tabs, AppController controller)
    {
        var selected = new AppStyleSettings { BorderColor = "#FFAABBCC", TextColor = "#FFEEEEDD", MutedTextColor = "#FF99AABB" };
        controller.SetApplicationStyle(selected);
        Invoke(window, "InitializeApplicationStyle");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "Appearance"));
        Element<RadioButton>(window, "AppearanceColorsCategory").IsChecked = true;
        Arrange(surface, new Size(1280, 820));
        var glow = Element<Slider>(window, "StyleGlowStrength");
        var fill = Element<Slider>(window, "StyleSurfaceOpacity");
        glow.Value = 40;
        Assert.Equal(0.4, Assert.IsType<double>(window.Resources["OrbitGlowOpacity"]));
        Assert.Equal(100, controller.Settings.ApplicationStyle.GlowStrength);
        glow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        Assert.Equal(40, controller.Settings.ApplicationStyle.GlowStrength);

        fill.Value = 65;
        Assert.Equal(0.65, Assert.IsType<double>(window.Resources["OrbitSurfaceOpacity"]));
        Assert.Equal(100, controller.Settings.ApplicationStyle.SurfaceOpacity);
        fill.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new OffscreenPresentationSource(surface),
            Environment.TickCount, Key.Left)
        { RoutedEvent = Keyboard.KeyUpEvent });
        Assert.Equal(65, controller.Settings.ApplicationStyle.SurfaceOpacity);

        var moreStyling = LogicalDescendants(window).OfType<Expander>().Single(expander =>
            System.Windows.Automation.AutomationProperties.GetName(expander) == "More surface styling options");
        moreStyling.IsExpanded = true;
        Arrange(surface, new Size(1280, 820));
        moreStyling.ApplyTemplate();
        Assert.Equal(Visibility.Visible, Assert.IsType<ContentPresenter>(moreStyling.Template.FindName("Details", moreStyling)).Visibility);
        var disclosure = Assert.IsType<ToggleButton>(moreStyling.Template.FindName("Disclosure", moreStyling));
        Assert.Equal("More surface styling options", System.Windows.Automation.AutomationProperties.GetName(disclosure));
        Element<Slider>(window, "StyleBorderThickness").Value = 2.5;
        Element<Slider>(window, "StyleCornerRadius").Value = 14;
        var padding = Element<Slider>(window, "StyleCardPadding");
        padding.Value = 32;
        padding.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        Assert.Equal(2.5, controller.Settings.ApplicationStyle.BorderThickness);
        Assert.Equal(14, controller.Settings.ApplicationStyle.CornerRadius);
        Assert.Equal(32, controller.Settings.ApplicationStyle.CardPadding);
        Assert.Equal(new Thickness(2.5), Assert.IsType<Thickness>(window.Resources["OrbitStrokeThickness"]));
        Assert.Equal(new CornerRadius(14), Assert.IsType<CornerRadius>(window.Resources["OrbitCornerRadius"]));
        Assert.Equal(new Thickness(32), Assert.IsType<Thickness>(window.Resources["OrbitCardPadding"]));
        window.SetResizableDisplay(true);
        var reset = LogicalDescendants(window).OfType<Button>().Single(button => Equals(button.Content, "Reset these options"));
        reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(controller.Settings.ResizableDashboardDisplay);
        var saved = controller.Settings.ApplicationStyle;
        Assert.Equal(100, saved.GlowStrength);
        Assert.Equal(100, saved.SurfaceOpacity);
        Assert.Equal(1, saved.BorderThickness);
        Assert.Equal(28, saved.CornerRadius);
        Assert.Equal(24, saved.CardPadding);
        Assert.Equal(selected.BorderColor, saved.BorderColor);
        Assert.Equal(selected.TextColor, saved.TextColor);
        Assert.Equal(selected.MutedTextColor, saved.MutedTextColor);
        Assert.Equal(100, glow.Value);
        Assert.Equal(100, fill.Value);
        Assert.Equal(1, Assert.IsType<double>(window.Resources["OrbitGlowOpacity"]));
    }

    private static void VerifyPaletteInitialization(AppController controller)
    {
        var settings = controller.Settings;
        var fresh = new SettingsService(Path.Combine(Path.GetTempPath(), "Wisp.App.Tests",
            "unused-fresh-palette-" + Guid.NewGuid().ToString("N"), "settings.json")).Load();
        settings.ColorTheme = fresh.ColorTheme;
        settings.BackgroundTheme = fresh.BackgroundTheme;
        settings.CustomAccentColor = null;
        settings.CustomBackgroundColor = null;
        settings.ApplicationStyle = fresh.ApplicationStyle;
        var freshWindow = new MainWindow(controller);
        try
        {
            Assert.False(freshWindow.IsLoaded);
            Assert.Equal(Color("#090D12"), Brush(freshWindow, "WindowBrush").Color);
            Assert.Equal(Color("#111822"), Brush(freshWindow, "CardBrush").Color);
            Assert.Equal(Color("#35404F"), Brush(freshWindow, "StrokeBrush").Color);
            Assert.Equal(Color("#63D8D4"), Brush(freshWindow, "AccentBrush").Color);
        }
        finally { freshWindow.Close(); }

        settings.CustomBackgroundColor = "#FF201020";
        settings.CustomAccentColor = "#FFDDCCBB";
        settings.ApplicationStyle = new AppStyleSettings { GlowStrength = 25, TextColor = "#FFEEDDCC" };
        settings.ResizableDashboardDisplay = true;
        var existingWindow = new MainWindow(controller);
        try
        {
            Assert.False(existingWindow.IsLoaded);
            Assert.Equal(Color("#FF201020"), Brush(existingWindow, "WindowBrush").Color);
            Assert.Equal(Color("#FFDDCCBB"), Brush(existingWindow, "AccentBrush").Color);
            Assert.Equal(Color("#FFEEDDCC"), Brush(existingWindow, "TextBrush").Color);
            Assert.Equal(0.25, Assert.IsType<double>(existingWindow.Resources["OrbitGlowOpacity"]));
            Assert.Equal(25, Element<Slider>(existingWindow, "StyleGlowStrength").Value);
            Assert.True(Element<CheckBox>(existingWindow, "ResizableDisplayToggle").IsChecked);
            Assert.True(settings.ResizableDashboardDisplay);
            Assert.False(existingWindow.IsDashboardDisplayMode);
        }
        finally { existingWindow.Close(); }
    }

    private static T Element<T>(MainWindow window, string name) where T : class => Assert.IsType<T>(window.FindName(name));
    private sealed class NoticeCommand : ICommand
    {
        public int Executions { get; private set; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => Executions++;
    }
    private static SolidColorBrush Brush(MainWindow window, string key) => Assert.IsType<SolidColorBrush>(window.Resources[key]);
    private static Color Color(string value) => (Color)ColorConverter.ConvertFromString(value);
    private static void Invoke(MainWindow window, string name, Type? owner = null)
    {
        var method = (owner ?? typeof(MainWindow)).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, null);
    }
    private static void Arrange(FrameworkElement surface, Size size)
    {
        surface.Measure(size);
        surface.Arrange(new Rect(size));
        surface.UpdateLayout();
        surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
        }
    }
    private sealed class OffscreenPresentationSource(Visual visual) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = visual;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
