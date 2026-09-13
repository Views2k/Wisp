using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;
using Wisp.Core;

namespace Wisp.UiReview;

internal static class DashboardWindowReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources)
    {
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        var report = new Report();
        ResourceApplication? app = null;
        AppController? controller = null;
        MainWindow? window = null;
        DispatcherTimer? timer = null, deadline = null;
        var frame = new DispatcherFrame();
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            app = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = loadResources() };
            var fixture = Fixture.All.Single(item => item.Name == "orbit-reference");
            var settings = fixture.CreateSettings();
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
            controller = new AppController(settings, new SettingsService(Path.Combine(output, "synthetic-settings.json")));
            fixture.Apply(controller.ViewModel, waiting: false);
            window = new MainWindow(controller)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                Focusable = false,
                Width = 980,
                Height = 750,
                Topmost = false
            };
            var tabs = Named<TabControl>(window, "RootTabs");
            var initialSettings = JsonSerializer.Serialize(settings);
            var steps = new Queue<(string Name, Action Action)>();
            Rect originalBounds = default;
            var injectingKey = false;
            void Check(bool condition, string code)
            {
                if (!condition) report.Failures.Add(report.Stage + "/" + code);
            }
            void Capture(string name)
            {
                window.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(window);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var target = new FileStream(Path.Combine(output, name), FileMode.CreateNew);
                encoder.Save(target);
                report.Captures.Add(name);
            }
            void Key(Key key)
            {
                injectingKey = true;
                try
                {
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window)!, Environment.TickCount, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }
                finally { injectingKey = false; }
            }
            void CheckRestoredNormal()
            {
                report.Snapshots.Add(Snapshot(window, report.Stage + "-restored"));
                Check(!window.IsDashboardDisplayMode && window.WindowState == WindowState.Normal, "normal-state-not-restored");
                Check(Math.Abs(window.Left - originalBounds.Left) < 1 && Math.Abs(window.Top - originalBounds.Top) < 1 &&
                    Math.Abs(window.Width - originalBounds.Width) < 1 && Math.Abs(window.Height - originalBounds.Height) < 1,
                    "normal-bounds-not-restored");
                Check(Named<Grid>(window, "TitleBar").Visibility == Visibility.Visible &&
                    Named<Grid>(window, "ShellFooter").Visibility == Visibility.Visible, "normal-chrome-not-restored");
            }
            steps.Enqueue(("normal", () =>
            {
                Check(tabs.SelectedIndex == 0 && !window.IsDashboardDisplayMode, "dashboard-not-initial");
                originalBounds = new Rect(window.Left, window.Top, window.Width, window.Height);
                report.OriginalBounds = new WindowBounds(originalBounds.Left, originalBounds.Top,
                    originalBounds.Width, originalBounds.Height);
                Check(window.IsEnabled, "wpf-tree-disabled");
                Capture("normal.png");
                var displayButton = Named<Button>(window, "DashboardWindowDisplayButton");
                Check(displayButton.IsVisible && displayButton.IsEnabled, "display-button-unreachable");
                displayButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            ));
            steps.Enqueue(("display-button", () =>
            {
                Check(window.IsDashboardDisplayMode && window.WindowState == WindowState.Maximized && tabs.SelectedIndex == 0,
                    "display-mode-not-maximized");
                Check(Named<Grid>(window, "TitleBar").Visibility == Visibility.Collapsed &&
                    Named<Grid>(window, "ShellFooter").Visibility == Visibility.Collapsed, "display-chrome-visible");
                Check(!Named<Border>(window, "DashboardRunPanel").IsVisible, "display-actions-visible");
                Capture("display.png");
                Key(System.Windows.Input.Key.Escape);
            }
            ));
            steps.Enqueue(("escape", () => { CheckRestoredNormal(); tabs.SelectedIndex = 2; }));
            steps.Enqueue(("other-page", () =>
            {
                Key(System.Windows.Input.Key.F11);
                Check(!window.IsDashboardDisplayMode && tabs.SelectedIndex == 2, "f11-hijacked-another-page");
                tabs.SelectedIndex = 0;
            }
            ));
            steps.Enqueue(("dashboard-f11", () => { Key(System.Windows.Input.Key.F11); Check(window.IsDashboardDisplayMode, "f11-did-not-enter"); }));
            steps.Enqueue(("dashboard-f11-exit", () => { Key(System.Windows.Input.Key.F11); }));
            steps.Enqueue(("f11-restored", () => { CheckRestoredNormal(); window.WindowState = WindowState.Maximized; }));
            steps.Enqueue(("already-maximized", () => { window.SetDashboardDisplayMode(true); }));
            steps.Enqueue(("exit-maximized", () => { window.SetDashboardDisplayMode(false); }));
            steps.Enqueue(("maximized-restored", () =>
            {
                Check(!window.IsDashboardDisplayMode && window.WindowState == WindowState.Maximized, "maximized-state-not-restored");
                window.WindowState = WindowState.Normal;
                controller.ViewModel.UpdateWaiting(default, TelemetryConnectionState.Lost, TimeSpan.FromSeconds(1), 60, preserveHudVisuals: true);
                window.SetDashboardDisplayMode(true);
            }
            ));
            steps.Enqueue(("lost-telemetry", () =>
            {
                Check(!controller.ViewModel.HasLiveTelemetry && controller.ViewModel.NativeGaugeFrame.SpeedAvailable,
                    "lost-fixture-did-not-retain-prior-frame");
                Check(Named<TextBlock>(window, "DashboardSpeedNumber").Text == "—", "stale-speed-still-displayed");
                var lostReadouts = Descendants(Named<StackPanel>(window, "DashboardContent")).OfType<TextBlock>()
                    .Where(text => text.IsVisible && BindingOperations.GetMultiBindingExpression(text, TextBlock.TextProperty)
                        ?.ParentMultiBinding.Converter is DashboardLiveValueConverter).ToArray();
                Check(lostReadouts.Length >= 4 && lostReadouts.All(text => text.Text == "—"), "stale-road-readouts-still-displayed");
                Capture("display-lost.png");
                window.SetDashboardDisplayMode(false);
            }
            ));
            steps.Enqueue(("finished", () =>
            {
                Check(JsonSerializer.Serialize(settings) == initialSettings, "display-mode-mutated-settings");
                Check(!controller.SetupTelemetry.IsRunning, "setup-listener-started");
                CheckRestoredNormal();
            }
            ));
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                if (!steps.TryDequeue(out var step)) { window.Close(); return; }
                report.Stage = bindings.Phase = step.Name;
                try
                {
                    if (window.IsActive || window.Topmost)
                    {
                        report.Failures.Add(step.Name + "/review-window-activated");
                        window.Close();
                        return;
                    }
                    report.Snapshots.Add(Snapshot(window, step.Name));
                    step.Action();
                    report.CompletedStages++;
                }
                catch (Exception exception) { report.Failures.Add(step.Name + "/" + exception.GetType().Name); window.Close(); }
            };
            deadline = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(12) };
            deadline.Tick += (_, _) => { report.Failures.Add("deadline"); window.Close(); };
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                HwndSource.FromHwnd(handle)?.AddHook(PreserveInputGuard);
                const int index = -20, noActivate = 0x08000000;
                SetWindowLong(handle, index, GetWindowLong(handle, index) | noActivate);
                Check((GetWindowLong(handle, index) & noActivate) != 0, "noactivate-style-missing");
                // A disabled native window cannot activate, including through
                // ShowWindow(SW_MAXIMIZE); WS_EX_NOACTIVATE alone is insufficient.
                // This touches only the review HWND, never another application.
                EnableWindow(handle, false);
                Check(!IsWindowEnabled(handle), "native-input-guard-missing");
            };
            window.PreviewKeyDown += (_, e) => { if (!injectingKey) e.Handled = true; };
            window.PreviewTextInput += (_, e) => e.Handled = true;
            window.PreviewMouseDown += (_, e) => e.Handled = true;
            window.PreviewMouseUp += (_, e) => e.Handled = true;
            window.PreviewMouseWheel += (_, e) => e.Handled = true;
            window.ContentRendered += (_, _) => { if (!timer.IsEnabled) timer.Start(); };
            window.Closed += (_, _) => { timer.Stop(); deadline.Stop(); frame.Continue = false; };
            deadline.Start();
            window.Show();
            Dispatcher.PushFrame(frame);
            report.ClosedCleanly = !window.IsVisible;
        }
        catch (Exception exception) { report.Failures.Add("initialize/" + exception.GetType().Name); }
        finally
        {
            timer?.Stop(); deadline?.Stop();
            try { window?.Close(); } catch (Exception exception) { report.Failures.Add("close/" + exception.GetType().Name); }
            try { controller?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception exception) { report.Failures.Add("dispose/" + exception.GetType().Name); }
            app?.Shutdown();
        }
        report.BindingDiagnosticCount = bindings.TotalCount;
        if (bindings.TotalCount > 0) report.Failures.Add("binding-diagnostics");
        using var destination = new FileStream(Path.Combine(output, "review.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(destination, report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Console.WriteLine($"Dashboard window check: {(report.Completed ? "PASS" : "FAIL")}; {report.CompletedStages} stages; {report.BindingDiagnosticCount} binding findings; see review.json.");
        return report.Completed ? 0 : 2;
    }

    private static T Named<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException("Required dashboard review element is missing.");

    private static WindowSnapshot Snapshot(MainWindow window, string stage) =>
        new(stage, window.WindowState.ToString(), window.IsDashboardDisplayMode,
            new WindowBounds(window.Left, window.Top, window.Width, window.Height),
            window.ActualWidth, window.ActualHeight,
            Named<StackPanel>(window, "DashboardContent").ActualWidth,
            Named<StackPanel>(window, "DashboardContent").ActualHeight,
            Named<ScaleTransform>(window, "DashboardDisplayScale").ScaleX);

    private static IntPtr PreserveInputGuard(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WindowChrome rewrites cached native styles during maximize/restore.
        // Keep this tool's WS_DISABLED and WS_EX_NOACTIVATE bits in those writes.
        if (message == 0x007C && unchecked((int)wParam.ToInt64()) is -16 or -20 && lParam != IntPtr.Zero)
        {
            var styles = Marshal.PtrToStructure<StyleChange>(lParam);
            styles.New |= 0x08000000;
            Marshal.StructureToPtr(styles, lParam, false);
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(root);
        var visited = 0;
        while (pending.TryDequeue(out var current))
        {
            if (++visited > 4096) throw new InvalidOperationException("Dashboard review visual-tree limit exceeded.");
            yield return current;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
        }
    }

    private sealed class ResourceApplication : Application
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }

    private sealed class Report
    {
        public bool SyntheticOnly => true;
        public string Scope => "dashboard-window";
        public string Method => "Actual nonactivating MainWindow with isolated settings and dormant controller; direct button/routed-key events within this process, never OS input injection; WPF window-content PNGs, not desktop screenshots or gameplay performance";
        public bool Completed => ClosedCleanly && CompletedStages == 12 && Failures.Count == 0;
        public bool ClosedCleanly { get; set; }
        public string Stage { get; set; } = "initialize";
        public int CompletedStages { get; set; }
        public int BindingDiagnosticCount { get; set; }
        public List<string> Failures { get; } = [];
        public List<string> Captures { get; } = [];
        public WindowBounds? OriginalBounds { get; set; }
        public List<WindowSnapshot> Snapshots { get; } = [];
    }

    private sealed record WindowBounds(double Left, double Top, double Width, double Height);
    private sealed record WindowSnapshot(string Stage, string WindowState, bool DisplayMode,
        WindowBounds Bounds, double ActualWidth, double ActualHeight, double ContentWidth, double ContentHeight, double Scale);

    [StructLayout(LayoutKind.Sequential)]
    private struct StyleChange { public uint Old; public uint New; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hwnd);
}
