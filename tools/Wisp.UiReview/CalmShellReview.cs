using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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
            ["Dashboard", "Appearance", "Diagnostics", "Profiles", "Setup", "Extras", "Release Notes"];
        private readonly string _output, _settingsPath, _initialHudSettings;
        private readonly SettingsService _settingsService;
        private readonly Application _application;
        private readonly AppController _controller;
        private readonly ShellReport _report;
        private readonly BindingTrace _bindings;
        private readonly Stopwatch _elapsed;
        private readonly MainWindow _window;
        private readonly TabControl _tabs;
        private readonly ListBox _navigation, _themes;
        private readonly Button _toggle;
        private readonly Border _sidebar;
        private readonly Grid _content;
        private readonly ColumnDefinition _column;
        private readonly TranslateTransform _sidebarTranslation, _contentTranslation;
        private readonly RotateTransform _chevron;
        private readonly BrushSnapshot[] _globalBrushes;
        private readonly Queue<Step> _steps = new();
        private readonly DispatcherFrame _frame = new();
        private readonly DispatcherTimer _next, _deadline;
        private string _phase = "initialize";
        private bool _closed, _started;
        private object? _selectedContent;
        private Step? _pending;
        private double _pendingSince, _motionOrigin;

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
                Topmost = true
            };
            _tabs = Named<TabControl>(_window, "RootTabs");
            _navigation = Named<ListBox>(_window, "SidebarNavigation");
            _themes = Named<ListBox>(_window, "ThemePicker");
            _toggle = Named<Button>(_window, "SidebarToggleButton");
            _sidebar = Named<Border>(_window, "SidebarHost");
            _content = Named<Grid>(_window, "ContentPane");
            _column = Named<ColumnDefinition>(_window, "SidebarColumn");
            _sidebarTranslation = Named<TranslateTransform>(_window, "SidebarTranslation");
            _contentTranslation = Named<TranslateTransform>(_window, "ContentTranslation");
            _chevron = Named<RotateTransform>(_window, "SidebarChevronRotation");
            _report.ClientAreaAnimationsEnabled = SystemParameters.ClientAreaAnimation;
            _next = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher);
            _deadline = new DispatcherTimer(DispatcherPriority.Send, _window.Dispatcher);
            _next.Tick += (_, _) => Advance();
            CompositionTarget.Rendering += OnRendering;
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
                CompositionTarget.Rendering -= OnRendering;
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
            CompositionTarget.Rendering -= OnRendering;
            if (_closed) return;
            _report.CloseReason = reason;
            _window.Close();
        }

        private void Add(string phase, int delayMilliseconds, Action action) =>
            _steps.Enqueue(new Step(phase, delayMilliseconds, action));

        private void AwaitRender(string phase, Func<bool> ready, Action action) =>
            _steps.Enqueue(new Step(phase, 0, action, ready));

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_closed && _pending?.Ready is not null)
            {
                if (_report.AnimationSamples.Count < 80) Snapshot(_phase + "-render");
                Advance();
            }
        }

        private void ScheduleNext()
        {
            if (_steps.Count == 0) { Close("completed"); return; }
            if (_steps.Peek().Ready is not null) { Advance(); return; }
            _next.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _steps.Peek().DelayMilliseconds));
            _next.Start();
        }

        private void Advance()
        {
            _next.Stop();
            if (_pending is null)
            {
                _pending = _steps.Dequeue();
                _pendingSince = _elapsed.Elapsed.TotalMilliseconds;
            }
            var step = _pending;
            _bindings.Phase = _phase = step.Phase;
            var failureCount = _report.Failures.Count;
            try
            {
                Check(!_window.IsActive, "window-activated");
                Check(!_controller.SetupTelemetry.IsRunning, "setup-telemetry-running");
                if (step.Ready is not null && !step.Ready())
                {
                    // Observe actual render-clock progress, not a wall-clock guess that
                    // can land before the first frame or after a busy compositor's last.
                    if (_elapsed.Elapsed.TotalMilliseconds - _pendingSince >= 1_000)
                    {
                        Snapshot(_phase);
                        Check(false, "render-state-timeout");
                    }
                    _next.Interval = TimeSpan.FromMilliseconds(16);
                    _next.Start();
                    return;
                }
                _pending = null;
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
                Check(_tabs.Items.Count == 5 && _navigation.Items.Count == 5, "page-count");
                Check(AppColorThemes.All.Count == 15 && _themes.Items.Count == 15, "theme-count");
                Check(_globalBrushes.Length > 0, "global-brush-snapshot-empty");
                CheckRows(); CheckSettled(open: true);
            });
            for (var index = 0; index < PageNames.Length; index++)
            {
                var page = index;
                Add("page-select-" + page, 1, () => _navigation.SetCurrentValue(Selector.SelectedIndexProperty, page));
                Add("page-verify-" + page, 45, () =>
                {
                    CheckPage(page);
                    _report.PagesVerified++;
                });
            }
            Add("extras-default", 60, () =>
            {
                CheckTheme(AppColorThemes.Resolve(AppColorThemes.DefaultName));
                var names = UIElementAutomationPeer.CreatePeerForElement(_themes)?.GetChildren()?
                    .Select(peer => peer.GetName()).ToArray() ?? [];
                foreach (var theme in AppColorThemes.All)
                    Check(names.Count(name => name == theme.Name) == 1, "theme-automation-name");
                _report.AccessibleThemeNamesVerified = AppColorThemes.All.Count;
                Capture("extras-aqua.png");
            });
            Add("theme-contracts", 1, () =>
            {
                // Resource and selection contracts are synchronous. Verify all
                // palettes in one dispatcher turn instead of forcing fifteen
                // expensive software-rendered frames; Aqua and Orange still
                // receive full bitmap captures below.
                foreach (var theme in AppColorThemes.All)
                {
                    _themes.SetCurrentValue(Selector.SelectedItemProperty, theme);
                    CheckTheme(theme);
                    _report.ThemesVerified++;
                }
            });
            var warm = AppColorThemes.Resolve("Orange");
            Add("warm-select", 1, () => _themes.SetCurrentValue(Selector.SelectedItemProperty, warm));
            Add("warm-capture", 60, () =>
            {
                CheckTheme(warm); Capture("extras-orange.png"); _selectedContent = _tabs.SelectedContent;
            });
            // Let the offscreen bitmap capture drain before timing the live window.
            Add("collapse-start", 700, ClickToggle);
            AwaitRender("collapse-mid", MotionReady, () => SampleMotion("collapse-mid"));
            AwaitRender("collapsed", () => !ClocksActive(), () => CheckSettled(open: false));
            Add("reopen-start", 1, ClickToggle);
            AwaitRender("reopen-mid", MotionReady, () => SampleMotion("reopen-mid"));
            AwaitRender("reopened", () => !ClocksActive(), () => CheckSettled(open: true));
            Add("reversal-close", 1, ClickToggle);
            AwaitRender("reversal-close-mid", MotionReady, () => SampleMotion("reversal-close-mid"));
            Add("reversal-open", 1, ReverseContinuously);
            AwaitRender("reversal-open-mid", MotionReady, () => SampleMotion("reversal-open-mid"));
            Add("reversal-close-again", 1, ReverseContinuously);
            AwaitRender("reversal-close-again-mid", MotionReady, () => SampleMotion("reversal-close-again-mid"));
            Add("reversal-open-final", 1, ReverseContinuously);
            AwaitRender("reversal-settled", () => !ClocksActive(), () => CheckSettled(open: true));
            Add("compact-size", 1, () => { _window.Width = 720; _window.Height = 440; });
            Add("compact-capture", 90, () =>
            {
                Check(Math.Abs(_window.ActualWidth - 720) < 1 && Math.Abs(_window.ActualHeight - 440) < 1, "compact-size");
                CheckRows(); CheckToggle(); Capture("compact-720x440.png");
            });
            Add("save-collapse", 1, ClickToggle);
            AwaitRender("save-collapsed", () => !ClocksActive(), () => CheckSettled(open: false));
            Add("restore-preferences", 350, RestorePreferences);
        }

        private void CheckPage(int index)
        {
            _window.UpdateLayout();
            Check(_tabs.SelectedIndex == index && _navigation.SelectedIndex == index, "selection-binding");
            Check(_tabs.SelectedItem is TabItem page && Equals(page.Header, PageNames[index]), "selected-header");
            Check(Named<TextBlock>(_window, "PageTitleText").Text == PageNames[index], "page-title");
            var presenter = _tabs.Template.FindName("PART_SelectedContentHost", _tabs) as ContentPresenter;
            Check(presenter is not null && ReferenceEquals(presenter.Content, _tabs.SelectedContent), "selected-content");
            if (_selectedContent is not null) Check(ReferenceEquals(_selectedContent, _tabs.SelectedContent), "page-changed-during-sidebar-motion");
        }

        private void CheckTheme(AppColorTheme theme)
        {
            CheckPage(4);
            Check(_themes.SelectedItem is AppColorTheme selected && selected.Name == theme.Name, "theme-selection");
            Check(Named<TextBlock>(_window, "ActiveThemeName").Text == theme.Name && _controller.Settings.ColorTheme == theme.Name, "theme-name");
            foreach (var (key, expected) in new[] { ("WindowBrush", "#090C11"), ("CardBrush", "#141B25"),
                         ("SidebarBrush", "#0E131B"), ("AccentBrush", theme.Accent) })
            {
                Check(_window.Resources.Contains(key) && _window.Resources[key] is SolidColorBrush brush &&
                      brush.Color == (Color)ColorConverter.ConvertFromString(expected), "local-theme-brush");
                Check(!ReferenceEquals(_window.Resources[key], _application.Resources[key]), "theme-brush-shared-globally");
            }
            Check(_window.Background is SolidColorBrush background && background.Color == (Color)ColorConverter.ConvertFromString("#090C11"), "window-theme-update");
            Check(_sidebar.Background is SolidColorBrush sidebar && sidebar.Color == (Color)ColorConverter.ConvertFromString("#0E131B"), "sidebar-theme-update");
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
            double? previous = null;
            foreach (var row in _navigation.Items.Cast<ListBoxItem>())
            {
                Check(Math.Abs(row.ActualHeight - 40) < 0.01 && row.Margin == new Thickness(0, 2, 0, 2), "row-size");
                var top = row.TranslatePoint(new Point(), _window).Y;
                if (previous.HasValue) Check(Math.Abs(top - previous.Value - 44) < 0.1, "row-pitch");
                previous = top;
            }
        }

        private void ClickToggle()
        {
            Snapshot(_phase + "-before");
            _toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            _window.UpdateLayout();
            Check(!DependencyPropertyHelper.GetValueSource(_column, ColumnDefinition.WidthProperty).IsAnimated, "layout-width-animated");
            CheckPage(4); CheckToggle();
            _motionOrigin = _sidebarTranslation.X;
            Snapshot(_phase + "-after");
        }

        private bool MotionReady() => !_report.ClientAreaAnimationsEnabled ||
            ClocksActive() && Math.Abs(_sidebarTranslation.X - _motionOrigin) > 0.01 &&
            Math.Abs(_sidebarTranslation.X) is > 0.01 and < 167.99 &&
            Math.Abs(_contentTranslation.X) is > 0.01 and < 167.99 &&
            _chevron.Angle is > 0.01 and < 179.99;

        private void ReverseContinuously()
        {
            var previousLeft = _content.TranslatePoint(new Point(), _window).X;
            var previousSidebar = _sidebarTranslation.X;
            var previousAngle = _chevron.Angle;
            ClickToggle();
            _report.ReversalPositions.Add(new ReversalPosition(previousLeft,
                _content.TranslatePoint(new Point(), _window).X, previousSidebar,
                _sidebarTranslation.X, previousAngle, _chevron.Angle));
            if (_report.ClientAreaAnimationsEnabled)
                Check(Math.Abs(_content.TranslatePoint(new Point(), _window).X - previousLeft) < 1.5 &&
                      Math.Abs(_sidebarTranslation.X - previousSidebar) < 1.5 &&
                      Math.Abs(_chevron.Angle - previousAngle) < 1.5, "reversal-position-jump");
            _report.RapidReversalsVerified++;
        }

        private void SampleMotion(string phase)
        {
            CheckPage(4); CheckToggle();
            Snapshot(phase);
            var moving = ClocksActive() && Math.Abs(_sidebarTranslation.X) is > 0.01 and < 167.99 &&
                         Math.Abs(_contentTranslation.X) is > 0.01 and < 167.99 && _chevron.Angle is > 0.01 and < 179.99;
            if (_report.ClientAreaAnimationsEnabled) { Check(moving, "intermediate-motion-not-observed"); _report.IntermediateMotionSamples++; }
            else Check(!ClocksActive(), "animation-ran-with-system-animation-disabled");
        }

        private void CheckSettled(bool open)
        {
            _window.UpdateLayout();
            Check(!ClocksActive(), "animation-clocks-not-cleared");
            Check(_column.Width.Value == (open ? 168 : 0), "sidebar-column");
            Check(_sidebar.Visibility == (open ? Visibility.Visible : Visibility.Collapsed), "sidebar-visibility");
            Check(_navigation.IsEnabled == open && _navigation.IsHitTestVisible == open, "sidebar-interaction");
            Check(Math.Abs(_contentTranslation.X) < 0.001 && Math.Abs(_sidebarTranslation.X - (open ? 0 : -168)) < 0.001 &&
                  Math.Abs(_chevron.Angle - (open ? 0 : 180)) < 0.001, "settled-transform");
            if (_selectedContent is not null) CheckPage(4);
            CheckToggle(); Snapshot(_phase);
            _report.SettledStatesVerified++;
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

        private void Snapshot(string phase) => _report.AnimationSamples.Add(new AnimationSample(phase,
            Math.Round(_elapsed.Elapsed.TotalMilliseconds, 1), _column.Width.Value,
            Math.Round(_sidebarTranslation.X, 3), Math.Round(_contentTranslation.X, 3),
            Math.Round(_chevron.Angle, 3), ClocksActive(), _tabs.SelectedIndex));

        private void RestorePreferences()
        {
            Check(File.Exists(_settingsPath), "synthetic-preferences-not-saved");
            var saved = _settingsService.Load();
            Check(saved.ColorTheme == "Orange" && saved.SidebarCollapsed, "saved-preferences");
            AppController? restoredController = null;
            MainWindow? restored = null;
            try
            {
                restoredController = new AppController(saved, _settingsService);
                Fixture.All[0].Apply(restoredController.ViewModel, waiting: false);
                restored = new MainWindow(restoredController) { ShowActivated = false, ShowInTaskbar = false };
                Check(!restored.IsLoaded && !restored.IsVisible && new WindowInteropHelper(restored).Handle == IntPtr.Zero, "restoration-window-shown");
                Check(Named<ColumnDefinition>(restored, "SidebarColumn").Width.Value == 0 &&
                      Named<Border>(restored, "SidebarHost").Visibility == Visibility.Collapsed, "restored-sidebar");
                Check(Named<ListBox>(restored, "ThemePicker").SelectedItem is AppColorTheme theme && theme.Name == "Orange" &&
                      Named<TextBlock>(restored, "ActiveThemeName").Text == "Orange", "restored-theme");
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
            .Where(property => property.Name is not (nameof(AppSettings.ColorTheme) or nameof(AppSettings.SidebarCollapsed)))
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
        public int AccessibleThemeNamesVerified { get; set; }
        public int GlobalBrushCount { get; set; }
        public int GlobalBrushChecks { get; set; }
        public int IntermediateMotionSamples { get; set; }
        public int SettledStatesVerified { get; set; }
        public int RapidReversalsVerified { get; set; }
        public bool SavedPreferencesRestored { get; set; }
        public int PassedChecks { get; set; }
        public int BindingDiagnosticCount { get; set; }
        public int SuppressedStartupNotifications { get; set; }
        public List<AnimationSample> AnimationSamples { get; } = [];
        public List<ReversalPosition> ReversalPositions { get; } = [];
        public List<CaptureInfo> Captures { get; } = [];
        public List<string> Failures { get; } = [];
        public bool Completed => CloseReason == "completed" && Loaded && ContentRendered && NonactivatingStyle &&
            ClosedCleanly && PagesVerified == 5 && ThemesVerified == 15 && AccessibleThemeNamesVerified == 15 && GlobalBrushChecks > 0 &&
            (!ClientAreaAnimationsEnabled || IntermediateMotionSamples == 5) && SettledStatesVerified == 5 &&
            RapidReversalsVerified == 3 && SavedPreferencesRestored && Captures.Count == 3 &&
            BindingDiagnosticCount == 0 && Failures.Count == 0;
    }

    private sealed record BrushSnapshot(object Key, Brush Brush, string Value);
    private sealed record Step(string Phase, int DelayMilliseconds, Action Action, Func<bool>? Ready = null);
    private sealed record CaptureInfo(string File, int PixelWidth, int PixelHeight, double LogicalWidth, double LogicalHeight,
        double WindowWidth, double WindowActualWidth, double WindowHeight, double WindowActualHeight);
    private sealed record AnimationSample(string Phase, double ElapsedMilliseconds, double ColumnWidth,
        double SidebarX, double ContentX, double ChevronAngle, bool ClocksActive, int SelectedPage);
    private sealed record ReversalPosition(double ContentBefore, double ContentAfter, double SidebarBefore,
        double SidebarAfter, double AngleBefore, double AngleAfter);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
