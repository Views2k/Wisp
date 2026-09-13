using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;

namespace Wisp.UiReview;

internal static class CalmShellReview
{
    private const int ReviewAutoCloseSeconds = 25;
    private const int ReviewHardLimitSeconds = 30;

    internal static int Run(string output, Func<ResourceDictionary> loadResources)
    {
        var elapsed = Stopwatch.StartNew();
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine($"Calm shell review exceeded {ReviewHardLimitSeconds} seconds; terminating only this review process (124).");
            Environment.Exit(124);
        }, null, TimeSpan.FromSeconds(ReviewHardLimitSeconds), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        var report = new ShellReport();
        ResourceOnlyApplication? application = null;
        AppController? controller = null;
        Session? session = null;
        try
        {
            if (Application.Current is not null || !Directory.Exists(output))
                throw new InvalidOperationException("The review needs its own application and prepared output directory.");
            var settingsPath = Path.Combine(output, "synthetic-settings.json");
            if (File.Exists(settingsPath) || File.Exists(Path.Combine(output, "review.json")))
                throw new InvalidOperationException("Review artifacts must be new.");
            // Functional UI checks must not depend on the host GPU's occlusion
            // throttling. This setting belongs to this short-lived review process only.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application = new ResourceOnlyApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources = loadResources();
            var settingsService = new SettingsService(settingsPath);
            var fixture = Fixture.All[0];
            var settings = fixture.CreateSettings();
            settings.ColorTheme = AppColorThemes.DefaultName;
            settings.BackgroundTheme = AppBackgroundThemes.DefaultName;
            settings.CustomAccentColor = null;
            settings.CustomBackgroundColor = null;
            settings.SidebarCollapsed = false;
            // This synthetic, output-local fixture represents an already configured
            // user. It never completes setup for, or starts services in, the real app.
            settings.SetupCompletion = new SetupCompletionRecord
            {
                Version = SetupCompletionRecord.CurrentVersion,
                CompletedAtUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
                ValidatedUdpPort = 5601,
                ValidatedPackets = SetupCompletionRecord.MinimumPackets,
                MovingPackets = SetupCompletionRecord.MinimumMovingPackets,
                ValidatedElapsedMilliseconds = SetupCompletionRecord.MinimumElapsedMilliseconds,
                DataOutConfirmed = true,
                DisplayModeConfirmed = true,
                StockHudConfirmed = true
            };
            settings.HasCompletedSetup = true;
            controller = new AppController(settings, settingsService);
            fixture.Apply(controller.ViewModel, waiting: false);
            session = new Session(output, settingsPath, settingsService, application, controller, report, bindings, elapsed);
            session.Run();
        }
        catch (Exception exception)
        {
            report.Failures.Add("initialize/" + exception.GetType().Name);
            report.CloseReason = "failed";
        }
        finally
        {
            try { session?.Close("cleanup"); }
            catch (Exception exception) { report.Failures.Add("close/" + exception.GetType().Name); }
            try { controller?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception exception) { report.Failures.Add("dispose/" + exception.GetType().Name); }
            report.SuppressedStartupNotifications = application?.SuppressedStartupNotifications ?? 0;
            try { application?.Shutdown(); }
            catch (Exception exception) { report.Failures.Add("shutdown/" + exception.GetType().Name); }
        }

        report.DurationSeconds = Math.Round(elapsed.Elapsed.TotalSeconds, 3);
        report.BindingDiagnosticCount = bindings.TotalCount;
        if (bindings.TotalCount != 0) report.Failures.Add("binding-diagnostics");
        try
        {
            using var destination = new FileStream(Path.Combine(output, "review.json"), FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(destination, report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Calm shell report could not be written (" + exception.GetType().Name + ").");
            return 1;
        }
        Console.WriteLine($"Calm shell review: {(report.Completed ? "PASS" : "FAIL")}; " +
            $"{report.PagesVerified} pages; {report.ThemesVerified} themes; {report.Captures.Count} window-only PNGs; see review.json.");
        return report.Completed ? 0 : 2;
    }

    private sealed class Session
    {
        private static readonly string[] PageNames =
            ["Dashboard", "Runs", "Appearance", "Diagnostics", "Profiles", "Extras", "Release Notes"];
        private readonly string _output, _settingsPath, _initialHudSettings;
        private readonly SettingsService _settingsService;
        private readonly Application _application;
        private readonly AppController _controller;
        private readonly ShellReport _report;
        private readonly BindingTrace _bindings;
        private readonly Stopwatch _elapsed;
        private readonly MainWindow _window;
        private readonly TabControl _tabs;
        private readonly ListBox _navigation, _colorTargets;
        private readonly ColorWheelEditor _colorEditor;
        private readonly Button _toggle;
        private readonly Border _sidebar;
        private readonly Grid _content;
        private readonly RowDefinition _dockRow;
        private readonly TranslateTransform _sidebarTranslation, _contentTranslation;
        private readonly RotateTransform _chevron;
        private readonly BrushSnapshot[] _globalBrushes;
        private readonly Queue<Step> _steps = new();
        private readonly DispatcherFrame _frame = new();
        private readonly DispatcherTimer _next, _deadline;
        private string _phase = "initialize";
        private bool _closed, _started;
        private object? _selectedContent;

        internal Session(string output, string settingsPath, SettingsService settingsService, Application application,
            AppController controller, ShellReport report, BindingTrace bindings, Stopwatch elapsed)
        {
            _output = output; _settingsPath = settingsPath; _settingsService = settingsService;
            _application = application; _controller = controller; _report = report; _bindings = bindings; _elapsed = elapsed;
            _initialHudSettings = HudSettings(controller.Settings);
            _globalBrushes = application.Resources.Keys.Cast<object>()
                .Where(key => application.Resources[key] is Brush)
                .Select(key => new BrushSnapshot(key, (Brush)application.Resources[key],
                    ((Brush)application.Resources[key]).ToString(CultureInfo.InvariantCulture))).ToArray();
            _report.GlobalBrushCount = _globalBrushes.Length;
            _window = new MainWindow(controller)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                Focusable = false,
                Topmost = true,
                Width = 1440,
                Height = 1000
            };
            _tabs = Named<TabControl>(_window, "RootTabs");
            _navigation = Named<ListBox>(_window, "SidebarNavigation");
            _colorTargets = Named<ListBox>(_window, "ColorTargetSelector");
            _colorEditor = Named<ColorWheelEditor>(_window, "ColorEditor");
            _toggle = Named<Button>(_window, "SidebarToggleButton");
            _sidebar = Named<Border>(_window, "SidebarHost");
            _content = Named<Grid>(_window, "ContentPane");
            _dockRow = Named<RowDefinition>(_window, "NavigationDockRow");
            _sidebarTranslation = Named<TranslateTransform>(_window, "SidebarTranslation");
            _contentTranslation = Named<TranslateTransform>(_window, "ContentTranslation");
            _chevron = Named<RotateTransform>(_window, "SidebarChevronRotation");
            _report.ClientAreaAnimationsEnabled = SystemParameters.ClientAreaAnimation;
            _next = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher);
            _deadline = new DispatcherTimer(DispatcherPriority.Send, _window.Dispatcher);
            _next.Tick += (_, _) => Advance();
            _deadline.Tick += (_, _) => { _report.Failures.Add("auto-close-deadline:" + _phase); Close("deadline"); };
            _window.Loaded += (_, _) => _report.Loaded = true;
            _window.ContentRendered += (_, _) =>
            {
                if (_started) return;
                _started = true;
                _report.ContentRendered = true;
                ScheduleNext();
            };
            _window.Closed += (_, _) =>
            {
                _closed = true;
                _next.Stop(); _deadline.Stop();
                if (_report.CloseReason == "running") _report.CloseReason = "external-close";
                _report.ClosedCleanly = !_window.IsVisible && !ClocksActive();
                _frame.Continue = false;
            };
            _window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(_window).Handle;
                const int extendedStyle = -20, noActivate = 0x08000000;
                SetWindowLong(handle, extendedStyle, GetWindowLong(handle, extendedStyle) | noActivate);
                _report.NonactivatingStyle = (GetWindowLong(handle, extendedStyle) & noActivate) != 0;
            };
            _window.PreviewKeyDown += (_, e) => { e.Handled = true; if (e.Key == Key.Escape) Close("escape"); };
            _window.PreviewKeyUp += (_, e) => e.Handled = true;
            _window.PreviewTextInput += (_, e) => e.Handled = true;
            _window.PreviewMouseDown += (_, e) => e.Handled = true;
            _window.PreviewMouseUp += (_, e) => e.Handled = true;
            _window.PreviewMouseWheel += (_, e) => e.Handled = true;
            BuildSteps();
        }

        internal void Run()
        {
            _report.CloseReason = "running";
            _deadline.Interval = TimeSpan.FromMilliseconds(Math.Max(1,
                ReviewAutoCloseSeconds * 1_000 - _elapsed.Elapsed.TotalMilliseconds));
            _deadline.Start();
            _window.Show();
            if (!_closed) Dispatcher.PushFrame(_frame);
        }

        internal void Close(string reason)
        {
            _next.Stop(); _deadline.Stop();
            if (_closed) return;
            _report.CloseReason = reason;
            _window.Close();
        }

        private void Add(string phase, int delayMilliseconds, Action action) =>
            _steps.Enqueue(new Step(phase, delayMilliseconds, action));

        private void ScheduleNext()
        {
            if (_steps.Count == 0) { Close("completed"); return; }
            _next.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _steps.Peek().DelayMilliseconds));
            _next.Start();
        }

        private void Advance()
        {
            _next.Stop();
            var step = _steps.Dequeue();
            _bindings.Phase = _phase = step.Phase;
            var failureCount = _report.Failures.Count;
            try
            {
                Check(!_window.IsActive, "window-activated");
                Check(!_controller.SetupTelemetry.IsRunning, "setup-telemetry-running");
                step.Action();
                Check(HudSettings(_controller.Settings) == _initialHudSettings, "hud-settings-changed");
                CheckGlobalBrushes();
                ScheduleNext();
            }
            catch (Exception exception)
            {
                if (_report.Failures.Count == failureCount) _report.Failures.Add(_phase + "/" + exception.GetType().Name);
                Close("failed");
            }
        }

        private void BuildSteps()
        {
            Add("loaded", 20, () =>
            {
                Check(_window.IsLoaded && _report.ContentRendered && _report.NonactivatingStyle, "loaded-nonactivating-window");
                Check(_tabs.Items.Count == PageNames.Length && _navigation.Items.Count == PageNames.Length, "page-count");
                string[] colorTargets = ["App accent", "Background and surfaces", "HUD border", "Gauge start", "Gauge middle",
                    "Gauge end", "Traction hook cue", "App borders", "Main text", "Secondary text", "G-force dot", "G-force trail", "Background particles"];
                Check(_colorTargets.Items.Cast<ListBoxItem>().Select(item => item.Content as string).SequenceEqual(colorTargets) &&
                      AppColorThemes.All.Count == 15, "color-choice-count");
                Check(_globalBrushes.Length > 0, "global-brush-snapshot-empty");
                CheckRows(); CheckSettled(open: true);
            });
            for (var index = 0; index < PageNames.Length; index++)
            {
                var page = index;
                Add("page-select-" + page, 1, () =>
                {
                    // Selection belongs to the list's item peer, not its visual wrapper.
                    var listPeer = UIElementAutomationPeer.CreatePeerForElement(_navigation);
                    var matches = listPeer?.GetChildren()?
                        .Where(peer => peer.GetName() == PageNames[page] &&
                            peer.GetAutomationControlType() == AutomationControlType.ListItem).ToArray() ?? [];
                    Check(matches.Length == 1, "page-accessible-item");
                    var selection = matches[0].GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider;
                    Check(selection is not null, "page-selection-pattern");
                    selection!.Select();
                    Check(selection.IsSelected, "page-accessible-selection");
                });
                Add("page-verify-" + page, 45, () =>
                {
                    CheckPage(page); CheckContentBounds();
                    _report.PagesVerified++;
                });
            }
            Add("appearance-select", 1, () => _navigation.SetCurrentValue(Selector.SelectedIndexProperty, 2));
            foreach (var category in new[] { "Layout", "Gauges", "Behaviour" })
            {
                Add("appearance-" + category.ToLowerInvariant() + "-select", 40, () => SelectAppearanceCategory(category));
                Add("appearance-" + category.ToLowerInvariant() + "-verify", 45, () =>
                {
                    CheckAppearanceCategory(category);
                    Capture("appearance-" + category.ToLowerInvariant() + ".png");
                    _report.AppearanceCategoriesVerified++;
                });
            }
            Add("appearance-colors-select", 1, () => SelectAppearanceCategory("Colors"));
            Add("appearance-colors-default", 60, () =>
            {
                CheckAppearanceCategory("Colors");
                _report.AppearanceCategoriesVerified++;
                CheckTheme(AppColorThemes.Resolve(AppColorThemes.DefaultName));
                var names = UIElementAutomationPeer.CreatePeerForElement(_colorTargets)?.GetChildren()?
                    .Select(peer => peer.GetName()).ToArray() ?? [];
                foreach (var item in _colorTargets.Items.Cast<ListBoxItem>())
                    Check(names.Count(name => name == item.Content as string) == 1, "color-target-automation-name");
                _report.AccessibleColorTargetsVerified = _colorTargets.Items.Count;
                Capture("appearance-colors-aqua.png");
            });
            Add("accent-contracts", 1, () =>
            {
                foreach (var theme in AppColorThemes.All)
                {
                    _colorEditor.SelectedColor = (Color)ColorConverter.ConvertFromString(theme.Accent);
                    CheckTheme(theme);
                    _report.ThemesVerified++;
                }
            });
            Add("particle-color-controls", 1, () =>
            {
                _colorTargets.SelectedIndex = 12;
                var background = _window.Resources["WindowBrush"];
                var accent = ((SolidColorBrush)_window.Resources["AccentBrush"]).Color;
                Check(_colorEditor.SelectedColor == accent, "particles-follow-accent");
                var chosen = Color.FromArgb(180, 230, 100, 150);
                _colorEditor.SelectedColor = chosen;
                Check(_controller.Settings.CustomParticleColor == ColorCustomization.ToHex(chosen) &&
                      Equals(_window.Resources["AppParticleColor"], chosen), "particle-custom-color-applied");
                Check(ReferenceEquals(background, _window.Resources["WindowBrush"]), "particle-color-preserves-background");
                var reset = Named<Button>(_window, "UseAccentParticleColorButton");
                reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(_controller.Settings.CustomParticleColor is null && _colorEditor.SelectedColor == accent,
                    "particle-accent-link-restored");
                var show = Named<CheckBox>(_window, "ShowBackgroundParticlesToggle");
                var animate = Named<CheckBox>(_window, "AnimateParticlesToggle");
                var particles = Named<AmbientBackdrop>(_window, "AppParticles");
                var colorsScroll = Named<ScrollViewer>(_window, "AppearanceColorsScroll");
                var previousOffset = colorsScroll.VerticalOffset;
                show.BringIntoView();
                _window.UpdateLayout();
                Check(ReferenceEquals(show.Style, _window.FindResource("ToggleSwitchStyle")) &&
                      ReferenceEquals(animate.Style, _window.FindResource("ToggleSwitchStyle")), "particle-toggle-styles");
                show.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
                _window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Check(!_controller.Settings.BackgroundParticlesEnabled && particles.Visibility == Visibility.Collapsed &&
                      !particles.HasAnimationTickSubscription && !animate.IsEnabled, "particles-fully-off");
                Check(ReferenceEquals(background, _window.Resources["WindowBrush"]), "particle-off-preserves-background");
                Capture("appearance-particles-off.png");
                show.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                animate.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
                _window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Check(particles.Visibility == Visibility.Visible && !particles.IsAnimationEnabled && animate.IsEnabled &&
                      !_controller.Settings.AnimatedBackground, "particles-still-background");
                Capture("appearance-particles-still.png");
                animate.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                _colorTargets.SelectedIndex = 0;
                colorsScroll.ScrollToVerticalOffset(previousOffset);
            });
            var warm = AppColorThemes.Resolve("Orange");
            Add("warm-select", 1, () => _colorEditor.SelectedColor = (Color)ColorConverter.ConvertFromString(warm.Accent));
            Add("warm-capture", 60, () =>
            {
                CheckTheme(warm); Capture("appearance-colors-orange.png"); _selectedContent = _tabs.SelectedContent;
            });
            Add("collapse", 1, () => { ClickToggle(); CheckSettled(open: false); });
            Add("reopen", 1, () => { ClickToggle(); CheckSettled(open: true); });
            Add("rapid-close", 1, () => { ClickToggle(); CheckSettled(open: false); });
            Add("rapid-open", 1, RapidToggle);
            Add("rapid-close-again", 1, RapidToggle);
            Add("rapid-open-final", 1, RapidToggle);
            Add("compact-size", 1, () => { _window.Width = 720; _window.Height = 440; });
            Add("compact-capture", 90, () =>
            {
                Check(Math.Abs(_window.ActualWidth - 720) < 1 && Math.Abs(_window.ActualHeight - 440) < 1, "compact-size");
                CheckRows(); CheckToggle(); CheckSettled(open: true); Capture("compact-720x440.png");
            });
            Add("save-collapse", 1, () => { ClickToggle(); CheckSettled(open: false); });
            Add("restore-preferences", 650, RestorePreferences);
        }

        private void CheckPage(int index)
        {
            _window.UpdateLayout();
            Check(_tabs.SelectedIndex == index && _navigation.SelectedIndex == index, "selection-binding");
            Check(_tabs.SelectedItem is TabItem page && Equals(page.Header, PageNames[index]), "selected-header");
            Check(Named<TextBlock>(_window, "PageTitleText").Text == PageNames[index], "page-title");
            var presenter = _tabs.Template.FindName("PART_SelectedContentHost", _tabs) as ContentPresenter;
            Check(presenter is not null && ReferenceEquals(presenter.Content, _tabs.SelectedContent), "selected-content");
            if (_selectedContent is not null) Check(ReferenceEquals(_selectedContent, _tabs.SelectedContent), "page-changed-during-dock-toggle");
        }

        private void SelectAppearanceCategory(string category)
        {
            CheckPage(2);
            var button = Named<RadioButton>(_window, $"Appearance{category}Category");
            Check(button.IsVisible && button.IsEnabled, "appearance-category-unreachable");
            var peer = UIElementAutomationPeer.CreatePeerForElement(button);
            var selection = peer?.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider;
            Check(selection is not null, "appearance-category-selection-pattern");
            selection!.Select();
            Check(selection.IsSelected, "appearance-category-selection");
        }

        private void CheckAppearanceCategory(string category)
        {
            CheckPage(2);
            foreach (var name in new[] { "Layout", "Gauges", "Colors", "Behaviour" })
            {
                var selected = name == category;
                Check(Named<RadioButton>(_window, $"Appearance{name}Category").IsChecked == selected,
                    "appearance-category-state");
                var scroll = Named<ScrollViewer>(_window, $"Appearance{name}Scroll");
                Check(scroll.Visibility == (selected ? Visibility.Visible : Visibility.Collapsed) &&
                      scroll.IsVisible == selected, "appearance-category-visibility");
            }
            var preview = Named<Border>(_window, "HudPreviewSurface");
            var bounds = preview.TransformToAncestor(_tabs).TransformBounds(new Rect(preview.RenderSize));
            Check(preview.IsVisible && bounds.Width > 0 && bounds.Height > 0 &&
                  bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= _tabs.ActualWidth + 0.5 &&
                  bounds.Bottom <= _tabs.ActualHeight + 0.5, "appearance-preview-persistent");
            Check(preview.ActualHeight >= 300 && bounds.Width >= 500, "appearance-preview-uses-room");
            Check(!Ancestors(preview).OfType<Viewbox>().Any(), "appearance-preview-text-downscaled");
            if (category == "Colors")
                Check(_colorTargets.IsVisible && _colorEditor.IsVisible, "appearance-color-editor-visible");
        }

        private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
        {
            for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                yield return parent;
        }

        private void CheckTheme(AppColorTheme theme)
        {
            CheckAppearanceCategory("Colors");
            var expectedAccent = (Color)ColorConverter.ConvertFromString(theme.Accent);
            Check(_colorTargets.SelectedIndex == 0 && _colorEditor.Title == "App accent", "accent-editor-target");
            Check(_colorEditor.SelectedColor == expectedAccent &&
                  ColorCustomization.ResolveAccent(_controller.Settings) == expectedAccent, "accent-editor-value");
            foreach (var (key, expected) in new[] { ("WindowBrush", "#090C11"), ("CardBrush", "#141B25"),
                         ("SidebarBrush", "#0E131B"), ("AccentBrush", theme.Accent) })
            {
                Check(_window.Resources.Contains(key) && _window.Resources[key] is SolidColorBrush brush &&
                      brush.Color == (Color)ColorConverter.ConvertFromString(expected), "local-theme-brush");
                Check(!ReferenceEquals(_window.Resources[key], _application.Resources[key]), "theme-brush-shared-globally");
            }
            Check(_window.Background is SolidColorBrush background && background.Color == (Color)ColorConverter.ConvertFromString("#090C11"), "window-theme-update");
            Check(_window.Resources.Contains("OrbitCardBrush") &&
                  _window.Resources["OrbitCardBrush"] is GradientBrush { IsFrozen: true } dockBrush &&
                  ReferenceEquals(_sidebar.Background, dockBrush), "sidebar-theme-update");
            Check(!ReferenceEquals(_sidebar.Background, _application.TryFindResource("OrbitCardBrush")), "dock-brush-shared-globally");
        }

        private void CheckGlobalBrushes()
        {
            foreach (var snapshot in _globalBrushes)
                Check(ReferenceEquals(snapshot.Brush, _application.Resources[snapshot.Key]) &&
                      snapshot.Brush.ToString(CultureInfo.InvariantCulture) == snapshot.Value, "global-brush-mutated");
            _report.GlobalBrushChecks++;
        }

        private void CheckRows()
        {
            Check(_navigation.Focusable && _navigation.IsTabStop &&
                  KeyboardNavigation.GetDirectionalNavigation(_navigation) != KeyboardNavigationMode.None, "navigation-keyboard-capability");
            var shell = (FrameworkElement)_window.FindName("ShellRoot");
            var expectedHeight = shell.ActualHeight < 650 ? 50 : 68;
            double? previousLeft = null, previousWidth = null, top = null;
            foreach (var (item, index) in _navigation.Items.Cast<ListBoxItem>().Select((item, index) => (item, index)))
            {
                Check(Math.Abs(item.ActualHeight - expectedHeight) < 0.01 && item.Margin == new Thickness(2), "dock-item-size");
                Check(AutomationProperties.GetName(item) == PageNames[index] && item.Focusable && item.IsTabStop, "dock-item-accessibility");
                CheckBoundsWithin(item, _navigation, "dock-item-clipped");
                var point = item.TranslatePoint(new Point(), _window);
                if (previousLeft.HasValue)
                    Check(Math.Abs(point.X - previousLeft.Value - previousWidth!.Value - 4) < 1.1 &&
                          Math.Abs(item.ActualWidth - previousWidth.Value) < 1.1 && Math.Abs(point.Y - top!.Value) < 0.1, "dock-item-pitch");
                previousLeft = point.X; previousWidth = item.ActualWidth; top = point.Y;
            }
        }

        private void ClickToggle()
        {
            var wasOpen = _window.IsSidebarOpen;
            var pageWidth = _tabs.ActualWidth;
            var pageHeight = _tabs.ActualHeight;
            var dockHeight = _dockRow.ActualHeight;
            var origin = _content.TranslatePoint(new Point(), _window);
            Snapshot(_phase + "-before");
            _toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            _window.UpdateLayout();
            Check(!DependencyPropertyHelper.GetValueSource(_dockRow, RowDefinition.HeightProperty).IsAnimated, "layout-height-animated");
            Check(Math.Abs(_tabs.ActualWidth - pageWidth) < 0.1 &&
                  _content.TranslatePoint(new Point(), _window) == origin, "content-shifted-horizontally");
            var expectedHeight = pageHeight + (wasOpen ? dockHeight : -_dockRow.ActualHeight);
            Check(Math.Abs(_tabs.ActualHeight - expectedHeight) < 0.1, "dock-height-not-reclaimed");
            _report.DockHeightChecks++;
            CheckPage(2); CheckToggle(); CheckContentBounds();
            Snapshot(_phase + "-after");
        }

        private void RapidToggle()
        {
            ClickToggle();
            CheckSettled(_window.IsSidebarOpen);
            _report.RapidTogglesVerified++;
        }

        private void CheckSettled(bool open)
        {
            _window.UpdateLayout();
            Check(!ClocksActive(), "animation-clocks-not-cleared");
            Check(open ? _dockRow.Height.IsAuto && _dockRow.ActualHeight > 60 :
                _dockRow.Height == new GridLength(0) && _dockRow.ActualHeight == 0, "navigation-dock-row");
            Check(_sidebar.Visibility == (open ? Visibility.Visible : Visibility.Collapsed), "dock-visibility");
            Check(_navigation.IsEnabled == open && _navigation.IsHitTestVisible == open, "dock-interaction");
            Check(Math.Abs(_contentTranslation.X) < 0.001 && Math.Abs(_contentTranslation.Y) < 0.001 &&
                  Math.Abs(_sidebarTranslation.X) < 0.001 && Math.Abs(_sidebarTranslation.Y) < 0.001 &&
                  Math.Abs(_chevron.Angle - (open ? 0 : 180)) < 0.001, "settled-transform");
            if (_selectedContent is not null) CheckPage(2);
            if (open) CheckRows();
            CheckToggle(); CheckContentBounds(); Snapshot(_phase);
            _report.SettledStatesVerified++;
        }

        private void CheckContentBounds()
        {
            var surface = _window.Content as FrameworkElement ?? throw new InvalidOperationException("Missing window surface.");
            CheckBoundsWithin(_content, surface, "content-pane-clipped");
            CheckBoundsWithin(_tabs, _content, "page-presenter-clipped");
            if (_window.IsSidebarOpen) CheckBoundsWithin(_sidebar, surface, "navigation-dock-clipped");
        }

        private void CheckBoundsWithin(FrameworkElement element, FrameworkElement container, string code)
        {
            var point = element.TranslatePoint(new Point(), container);
            Check(point.X >= -0.5 && point.Y >= -0.5 &&
                  point.X + element.ActualWidth <= container.ActualWidth + 0.5 &&
                  point.Y + element.ActualHeight <= container.ActualHeight + 0.5, code);
        }

        private void CheckToggle()
        {
            Check(_toggle.IsVisible && _toggle.IsEnabled && _toggle.IsHitTestVisible &&
                  !string.IsNullOrWhiteSpace(AutomationProperties.GetName(_toggle)), "toggle-unreachable");
            var point = _toggle.TranslatePoint(new Point(_toggle.ActualWidth / 2, _toggle.ActualHeight / 2), _window);
            var hit = _window.InputHitTest(point) as DependencyObject;
            while (hit is not null && !ReferenceEquals(hit, _toggle)) hit = VisualTreeHelper.GetParent(hit);
            Check(ReferenceEquals(hit, _toggle), "toggle-hit-test");
        }

        private bool ClocksActive() => _sidebarTranslation.HasAnimatedProperties ||
            _contentTranslation.HasAnimatedProperties || _chevron.HasAnimatedProperties;

        private void Snapshot(string phase) => _report.DockSamples.Add(new DockSample(phase,
            Math.Round(_elapsed.Elapsed.TotalMilliseconds, 1), Math.Round(_dockRow.ActualHeight, 3),
            Math.Round(_tabs.ActualWidth, 3), Math.Round(_tabs.ActualHeight, 3),
            Math.Round(_chevron.Angle, 3), ClocksActive(), _tabs.SelectedIndex));

        private void RestorePreferences()
        {
            Check(File.Exists(_settingsPath), "synthetic-preferences-not-saved");
            var saved = _settingsService.Load();
            var warm = (Color)ColorConverter.ConvertFromString(AppColorThemes.Resolve("Orange").Accent);
            Check(saved.CustomAccentColor == ColorCustomization.ToHex(warm) && saved.SidebarCollapsed, "saved-preferences");
            AppController? restoredController = null;
            MainWindow? restored = null;
            try
            {
                restoredController = new AppController(saved, _settingsService);
                Fixture.All[0].Apply(restoredController.ViewModel, waiting: false);
                restored = new MainWindow(restoredController) { ShowActivated = false, ShowInTaskbar = false };
                Check(!restored.IsLoaded && !restored.IsVisible && new WindowInteropHelper(restored).Handle == IntPtr.Zero, "restoration-window-shown");
                Check(Named<RowDefinition>(restored, "NavigationDockRow").Height == new GridLength(0) &&
                      Named<Border>(restored, "SidebarHost").Visibility == Visibility.Collapsed, "restored-dock");
                Check(Named<ListBox>(restored, "ColorTargetSelector").SelectedIndex == 0 &&
                      Named<ColorWheelEditor>(restored, "ColorEditor").SelectedColor == warm, "restored-accent-editor");
                Check(restored.Resources["AccentBrush"] is SolidColorBrush accent &&
                      accent.Color == (Color)ColorConverter.ConvertFromString(AppColorThemes.Resolve("Orange").Accent), "restored-theme-brush");
                Check(HudSettings(saved) == _initialHudSettings && !restoredController.SetupTelemetry.IsRunning, "restored-non-ui-state");
                _report.SavedPreferencesRestored = true;
            }
            finally
            {
                restored?.Close();
                restoredController?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        private void Capture(string fileName)
        {
            var surface = _window.Content as FrameworkElement ?? throw new InvalidOperationException("Missing window surface.");
            surface.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(surface);
            var width = checked((int)Math.Ceiling(surface.ActualWidth * dpi.DpiScaleX));
            var height = checked((int)Math.Ceiling(surface.ActualHeight * dpi.DpiScaleY));
            Check(width is > 0 and <= 4096 && height is > 0 and <= 4096, "capture-size");
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var destination = new FileStream(Path.Combine(_output, fileName), FileMode.CreateNew, FileAccess.Write);
            encoder.Save(destination);
            _report.Captures.Add(new CaptureInfo(fileName, width, height,
                Math.Round(surface.ActualWidth, 2), Math.Round(surface.ActualHeight, 2),
                _window.Width, _window.ActualWidth, _window.Height, _window.ActualHeight));
        }

        private void Check(bool condition, string code)
        {
            if (condition) { _report.PassedChecks++; return; }
            _report.Failures.Add(_phase + "/" + code);
            throw new InvalidOperationException("Calm shell review assertion failed.");
        }
    }

    private static T Named<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException("The named UI contract changed.");

    private static string HudSettings(AppSettings settings) => JsonSerializer.Serialize(
        JsonSerializer.SerializeToElement(settings).EnumerateObject()
            .Where(property => property.Name is not (nameof(AppSettings.ColorTheme) or nameof(AppSettings.CustomAccentColor) or nameof(AppSettings.SidebarCollapsed)))
            .ToDictionary(property => property.Name, property => property.Value.Clone()));

    private sealed class ResourceOnlyApplication : Application
    {
        public int SuppressedStartupNotifications { get; private set; }
        protected override void OnStartup(StartupEventArgs e) => SuppressedStartupNotifications++;
        protected override void OnExit(ExitEventArgs e) { }
    }

    private sealed class ShellReport
    {
        public string Scope => "calm-shell-check";
        public string Input => "synthetic-fixture; no runtime start, provider calls, desktop capture, or real settings";
        public string RenderMode => "software-only review process; production rendering unchanged";
        public int AutoCloseSeconds => ReviewAutoCloseSeconds;
        public int HardProcessLimitSeconds => ReviewHardLimitSeconds;
        public double DurationSeconds { get; set; }
        public string CloseReason { get; set; } = "not-shown";
        public bool Loaded { get; set; }
        public bool ContentRendered { get; set; }
        public bool NonactivatingStyle { get; set; }
        public bool ClosedCleanly { get; set; }
        public bool ClientAreaAnimationsEnabled { get; set; }
        public int PagesVerified { get; set; }
        public int ThemesVerified { get; set; }
        public int AppearanceCategoriesVerified { get; set; }
        public int AccessibleColorTargetsVerified { get; set; }
        public int GlobalBrushCount { get; set; }
        public int GlobalBrushChecks { get; set; }
        public int DockHeightChecks { get; set; }
        public int SettledStatesVerified { get; set; }
        public int RapidTogglesVerified { get; set; }
        public bool SavedPreferencesRestored { get; set; }
        public int PassedChecks { get; set; }
        public int BindingDiagnosticCount { get; set; }
        public int SuppressedStartupNotifications { get; set; }
        public List<DockSample> DockSamples { get; } = [];
        public List<CaptureInfo> Captures { get; } = [];
        public List<string> Failures { get; } = [];
        public bool Completed => CloseReason == "completed" && Loaded && ContentRendered && NonactivatingStyle &&
            ClosedCleanly && PagesVerified == 7 && ThemesVerified == 15 && AppearanceCategoriesVerified == 4 && AccessibleColorTargetsVerified == 13 && GlobalBrushChecks > 0 &&
            DockHeightChecks == 7 && SettledStatesVerified == 9 &&
            RapidTogglesVerified == 3 && SavedPreferencesRestored && Captures.Count == 8 &&
            BindingDiagnosticCount == 0 && Failures.Count == 0;
    }

    private sealed record BrushSnapshot(object Key, Brush Brush, string Value);
    private sealed record Step(string Phase, int DelayMilliseconds, Action Action);
    private sealed record CaptureInfo(string File, int PixelWidth, int PixelHeight, double LogicalWidth, double LogicalHeight,
        double WindowWidth, double WindowActualWidth, double WindowHeight, double WindowActualHeight);
    private sealed record DockSample(string Phase, double ElapsedMilliseconds, double DockHeight,
        double PageWidth, double PageHeight, double ChevronAngle, bool ClocksActive, int SelectedPage);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
