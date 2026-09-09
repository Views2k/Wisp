using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.UiReview;

internal static class RunsReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources)
    {
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        var captures = new List<object>(); var failures = new List<string>();
        var foreground = GetForegroundWindow();
        using var foregroundMonitor = new ForegroundMonitor();
        var ownWindowActivations = 0;
        var synchronization = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        AppController? controller = null; MainWindow? source = null; Window? host = null;
        var application = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application.Resources = loadResources();
            var fixture = Fixture.All[0]; var settings = fixture.CreateSettings();
            settings.DebugLoggingEnabled = false; settings.HasCompletedSetup = true;
            controller = new AppController(settings, new SettingsService(Path.Combine(output, "fixture", "settings.json")));
            fixture.Apply(controller.ViewModel, waiting: false);
            source = new MainWindow(controller);
            source.Activated += (_, _) => ownWindowActivations++;
            var tabs = (TabControl)source.FindName("RootTabs");
            var page = (RunsPage)source.FindName("RunsSurface");
            var scroll = (ScrollViewer)page.FindName("RunsScroll");
            var graphScroll = (ScrollViewer)page.FindName("GraphScroll");
            var graphPicker = (ListBox)page.FindName("GraphPicker");
            var showGraphs = (Button)page.FindName("ShowGraphsButton");
            var backToSummary = (Button)page.FindName("BackToSummaryButton");
            var compareOptions = (Expander)page.FindName("CompareExpander");
            var surface = (FrameworkElement)source.Content;
            var names = NameScope.GetNameScope(source);
            var font = (FontFamily)surface.GetValue(TextElement.FontFamilyProperty);
            var foregroundBrush = (Brush)surface.GetValue(TextElement.ForegroundProperty);
            source.Content = null;
            surface.DataContext = controller.ViewModel;
            surface.SetValue(TextElement.FontFamilyProperty, font); surface.SetValue(TextElement.ForegroundProperty, foregroundBrush);
            NameScope.SetNameScope(surface, names);
            surface.Resources.MergedDictionaries.Add(source.Resources);
            tabs.SelectedItem = source.FindName("RunsTab");
            host = new Window
            {
                Content = surface,
                Left = -20000,
                Top = -20000,
                Width = 1080,
                Height = 780,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            host.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(host).Handle;
                _ = SetWindowLongPtr(hwnd, -20, new IntPtr(GetWindowLongPtr(hwnd, -20).ToInt64() | 0x08000000L | 0x00000080L));
            };
            host.Activated += (_, _) => ownWindowActivations++;
            host.Show();
            foreach (var (width, height) in new[] { (1080, 780), (720, 440) })
            {
                host.Width = width; host.Height = height; Settle(surface);
                foreach (var comparison in new[] { false, true })
                {
                    controller.Runs.ChartGroup = RunChartGroup.Speed;
                    var a = RunFixture("Hill road · baseline", "Road tune", 1, false);
                    var b = comparison ? RunFixture("Hill road · revised", "Shorter final drive", 1.15, true) : null;
                    Await(controller.Runs.ShowReviewAsync(a, b, RunPurpose.Acceleration));
                    controller.Runs.SetPageVisible(true);
                    AwaitReady(controller.Runs);
                    if (!controller.Runs.IsSummaryVisible || controller.Runs.IsGraphWorkspaceOpen || !scroll.IsVisible || graphScroll.IsVisible)
                        failures.Add("new-report-did-not-open-summary");
                    if (compareOptions.IsExpanded) failures.Add("comparison-controls-expanded-by-default");
                    if (Descendants(page).OfType<Button>().Count(button => Equals(button.Content, "Show graphs")) != 1)
                        failures.Add("graph-entry-is-not-unique");
                    Capture((comparison ? "comparison" : "report") + $"-{width}-opened");
                    scroll.ScrollToHome(); Settle(surface);
                    Capture((comparison ? "comparison" : "report") + $"-{width}-top");
                    if (comparison)
                    {
                        controller.Runs.SameSpeed = true; AwaitReady(controller.Runs);
                        if (!controller.Runs.SameSpeed) failures.Add("fixture-matched-speed-unavailable");
                    }
                    controller.Runs.ChartGroup = RunChartGroup.Speed; AwaitReady(controller.Runs);
                    Click(showGraphs); CheckGraphNavigation($"{width}-opened", requireVisiblePlot: true);
                    if (comparison && width == 1080)
                    {
                        Await(controller.Runs.ExportImageAsync(Path.Combine(output, "export-comparison-time.png")));
                        if (controller.Runs.HasError) failures.Add("time-image-export");
                    }
                    Capture((comparison ? "comparison" : "report") + $"-{width}-charts");
                    controller.Runs.SelectInterval(.5, 1.5); AwaitReady(controller.Runs);
                    controller.Runs.ZoomSelectionCommand.Execute(null); Settle(surface);
                    if (!controller.Runs.HasSelection || controller.Runs.ViewStart != .5 || controller.Runs.ViewEnd != 1.5)
                        failures.Add("selected-interval");
                    Capture((comparison ? "comparison" : "report") + $"-{width}-selection");
                    Click(backToSummary); CheckSummaryNavigation($"{width}-return");
                }
                compareOptions.IsExpanded = true; Settle(surface);
                var matchToggle = Descendants(compareOptions).OfType<CheckBox>()
                    .Single(toggle => Equals(toggle.Content, "Match an acceleration speed range"));
                foreach (var enabled in new[] { false, true })
                {
                    controller.Runs.SameSpeed = enabled; AwaitReady(controller.Runs); Settle(surface);
                    if (matchToggle.IsChecked != enabled || matchToggle.Template.FindName("ToggleTrack", matchToggle) is not Border)
                        failures.Add("comparison-toggle-does-not-use-wisp-style");
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + matchToggle.TranslatePoint(new Point(), scroll).Y - 50);
                    Settle(surface); Capture($"comparison-toggle-{width}-" + (enabled ? "checked" : "unchecked"));
                }
                matchToggle.SetCurrentValue(UIElement.IsEnabledProperty, false); Settle(surface);
                if (matchToggle.Template.FindName("ToggleContent", matchToggle) is not FrameworkElement { Opacity: < 1 })
                    failures.Add("comparison-toggle-disabled-state-not-visible");
                Capture($"comparison-toggle-{width}-disabled");
                matchToggle.GetBindingExpression(UIElement.IsEnabledProperty)?.UpdateTarget();
                compareOptions.IsExpanded = false; Settle(surface);
                Click(showGraphs); CheckGraphNavigation($"{width}-reopened", requireVisiblePlot: true);
                controller.Runs.ClearSelectionCommand.Execute(null); AwaitReady(controller.Runs);
                if (graphPicker.Items.Count != 8) failures.Add("graph-picker-does-not-list-all-eight-views");
                foreach (var choice in graphPicker.Items.Cast<RunGraphChoice>())
                {
                    graphPicker.SetCurrentValue(ListBox.SelectedItemProperty, choice);
                    AwaitReady(controller.Runs); Settle(surface);
                    if (controller.Runs.SelectedGraph != choice || controller.Runs.ChartGroup != choice.Group || controller.Runs.GraphView.Mode != choice.Mode)
                        failures.Add("graph-choice-not-applied-" + choice.Label);
                    if (choice.Mode == RunPlotMode.TimeSeries ? controller.Runs.Charts.Count == 0 || controller.Runs.AlternativeCharts.Count != 0
                        : controller.Runs.AlternativeCharts.Count == 0 || controller.Runs.Charts.Count != 0)
                        failures.Add("graph-choice-content-mismatch-" + choice.Label);
                    graphScroll.ScrollToHome(); Settle(surface);
                    CheckGraphNavigation($"{width}-{choice.Group}-{choice.Mode}", requireVisiblePlot: true);
                    Capture($"comparison-{width}-{choice.Group}-{choice.Mode}");
                    var pickerTop = graphPicker.TranslatePoint(new Point(), page).Y;
                    graphScroll.ScrollToEnd(); Settle(surface);
                    if (Math.Abs(graphPicker.TranslatePoint(new Point(), page).Y - pickerTop) > .5)
                        failures.Add("graph-picker-moved-with-content-" + choice.Label);
                    CheckGraphNavigation($"{width}-{choice.Group}-{choice.Mode}-scrolled");
                    if (choice.Mode == RunPlotMode.PowerByRpm && width == 1080)
                    {
                        Await(controller.Runs.ExportImageAsync(Path.Combine(output, "export-comparison-rpm.png")));
                        if (controller.Runs.HasError) failures.Add("rpm-image-export");
                    }
                }
                Click(backToSummary); CheckSummaryNavigation($"{width}-all-graphs-return");
                Capture($"summary-{width}-returned");
                var options = (Expander)page.FindName("RecordingOptionsExpander");
                options.IsExpanded = true; scroll.ScrollToHome(); Settle(surface); Capture($"options-{width}"); options.IsExpanded = false;
                Click(showGraphs); CheckGraphNavigation($"{width}-markers-open", requireVisiblePlot: true);
                var markers = (Expander)page.FindName("MarkersExpander");
                markers.IsExpanded = true; Settle(surface);
                graphScroll.ScrollToVerticalOffset(graphScroll.VerticalOffset + markers.TranslatePoint(new Point(), graphScroll).Y);
                Settle(surface);
                var jumps = Descendants(markers).OfType<Button>().Where(button => Equals(button.Content, "Jump")).ToArray();
                if (jumps.Length == 0 || jumps.Any(button => !button.IsEnabled)) failures.Add("marker-jump-buttons-unavailable");
                Capture($"markers-{width}"); markers.IsExpanded = false;
                if (jumps.FirstOrDefault() is { Command: { } jumpCommand } markerButton)
                {
                    jumpCommand.Execute(markerButton.CommandParameter); AwaitReady(controller.Runs);
                    CheckGraphNavigation($"{width}-marker-jump", requireVisiblePlot: true);
                    if (!controller.Runs.IsTimeGraph) failures.Add("marker-did-not-open-time-graph");
                }
                var protectedImage = Path.Combine(output, "fixture", $"existing-image-{width}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(protectedImage)!);
                File.WriteAllText(protectedImage, "Keep this existing fixture file.");
                graphScroll.ScrollToEnd(); Settle(surface);
                Await(controller.Runs.ExportImageAsync(protectedImage)); Settle(surface);
                var errorFeedback = (TextBlock)page.FindName("GraphActionError");
                if (!controller.Runs.HasError || errorFeedback.Text != controller.Runs.Error || !WithinPage(errorFeedback))
                    failures.Add("graph-export-error-not-visible");
                if (File.ReadAllText(protectedImage) != "Keep this existing fixture file.") failures.Add("graph-export-overwrote-existing-file");
                Capture($"graph-export-error-{width}");
                Await(controller.Runs.ExportImageAsync(Path.Combine(output, $"export-feedback-retry-{width}.png"))); AwaitReady(controller.Runs); Settle(surface);
                var savedFeedback = (TextBlock)page.FindName("GraphExportStatus");
                if (controller.Runs.HasError || errorFeedback.IsVisible || savedFeedback.Text != RunsViewModel.ImageExportSavedMessage || !WithinPage(savedFeedback))
                    failures.Add("graph-export-retry-feedback-not-visible");
                Capture($"graph-export-saved-{width}");
            }
            if (bindings.TotalCount != 0) failures.Add("binding-diagnostics");
            if (!foregroundMonitor.IsAvailable) failures.Add("foreground-monitor-unavailable");
            CheckOwnForeground();
            if (ownWindowActivations != 0 || foregroundMonitor.Events != 0) failures.Add("review-window-activated");

            void Capture(string name)
            {
                Settle(surface);
                CheckOwnForeground();
                var activeScroll = controller.Runs.IsGraphWorkspaceOpen ? graphScroll : scroll;
                if (activeScroll.ScrollableWidth > .5) failures.Add(name + "/horizontal-overflow");
                if (controller.Runs.Charts.SelectMany(panel => panel.Series).Any(series => series.Points.Length > 1200)) failures.Add(name + "/geometry-budget");
                if (new WindowInteropHelper(source).Handle != IntPtr.Zero || host.Left > -10000) failures.Add(name + "/isolation");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = new FileStream(Path.Combine(output, name + ".png"), FileMode.CreateNew); encoder.Save(file);
                captures.Add(new
                {
                    name,
                    width = surface.ActualWidth,
                    height = surface.ActualHeight,
                    scrollOffset = activeScroll.VerticalOffset,
                    graphsOpen = controller.Runs.IsGraphWorkspaceOpen,
                    visiblePlot = VisiblePlot(),
                    selectedGraph = controller.Runs.SelectedGraph.Label,
                    chartCount = controller.Runs.Charts.Count,
                    alternativeChartCount = controller.Runs.AlternativeCharts.Count,
                    graphMode = controller.Runs.GraphView.Label,
                    markerCount = controller.Runs.Markers.Count,
                    metrics = controller.Runs.Metrics.ToArray(),
                    findings = controller.Runs.Findings.Select(f => new { f.Title, f.Detail }),
                    interval = controller.Runs.IntervalLabel,
                    comparison = controller.Runs.ComparisonNote,
                    bindingWarnings = bindings.TotalCount
                });
            }
            void Click(Button button)
            {
                if (!button.IsVisible || !button.IsEnabled) failures.Add("navigation-button-unavailable-" + button.Name);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                AwaitReady(controller.Runs); Settle(surface);
            }
            void CheckSummaryNavigation(string phase)
            {
                if (!controller.Runs.IsSummaryVisible || controller.Runs.IsGraphWorkspaceOpen || !scroll.IsVisible || graphScroll.IsVisible)
                    failures.Add(phase + "/summary-pane-state");
                if (!showGraphs.IsVisible || !showGraphs.IsEnabled) failures.Add(phase + "/show-graphs-unavailable");
            }
            void CheckGraphNavigation(string phase, bool requireVisiblePlot = false)
            {
                Settle(surface);
                if (!controller.Runs.IsGraphWorkspaceOpen || controller.Runs.IsSummaryVisible || !graphScroll.IsVisible || scroll.IsVisible)
                    failures.Add(phase + "/graph-pane-state");
                if (!backToSummary.IsVisible || !backToSummary.IsEnabled) failures.Add(phase + "/back-to-summary-unavailable");
                if (graphScroll.ViewportHeight < 90) failures.Add(phase + "/graph-viewport-too-small");
                if (requireVisiblePlot)
                {
                    var visible = VisiblePlot();
                    if (visible.Width < 100 || visible.Height < 40) failures.Add(phase + "/plot-data-not-visible-on-entry");
                    foreach (var chart in Descendants(graphScroll).OfType<FrameworkElement>().Where(element => element.IsVisible && element is RunChartView or RunAlternativePlotView))
                    {
                        var drawing = VisualTreeHelper.GetDrawing(chart);
                        var content = drawing is null ? [] : Drawings(drawing).ToArray();
                        if (!content.OfType<GlyphRunDrawing>().Any() || !content.OfType<GeometryDrawing>().Any(item => item.Geometry is not null && item.Pen is not null))
                            failures.Add(phase + "/graph-has-no-rendered-content");
                    }
                }
                foreach (var choice in graphPicker.Items)
                {
                    if (graphPicker.ItemContainerGenerator.ContainerFromItem(choice) is not ListBoxItem item || !item.IsVisible)
                    { failures.Add(phase + "/hidden-graph-choice"); continue; }
                    var bounds = item.TransformToAncestor(page).TransformBounds(new Rect(item.RenderSize));
                    if (bounds.Left < -.5 || bounds.Top < -.5 || bounds.Right > page.ActualWidth + .5 || bounds.Bottom > page.ActualHeight + .5)
                        failures.Add(phase + "/graph-choice-outside-viewport");
                    if (choice is RunGraphChoice named && AutomationProperties.GetName(item) != named.Label)
                        failures.Add(phase + "/graph-choice-accessible-name");
                }
            }
            bool WithinPage(FrameworkElement element)
            {
                if (!element.IsVisible || element.ActualHeight <= 0 || element.ActualWidth <= 0) return false;
                var bounds = element.TransformToAncestor(page).TransformBounds(new Rect(element.RenderSize));
                return bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= page.ActualWidth + .5 && bounds.Bottom <= page.ActualHeight + .5;
            }
            PlotVisibility VisiblePlot()
            {
                if (!controller.Runs.IsGraphWorkspaceOpen || !graphScroll.IsVisible) return new(0, 0);
                var viewport = graphScroll.TransformToAncestor(page).TransformBounds(new Rect(0, 0, graphScroll.ViewportWidth, graphScroll.ViewportHeight));
                viewport.Intersect(new Rect(page.RenderSize));
                var best = new PlotVisibility(0, 0);
                foreach (var chart in Descendants(graphScroll).OfType<FrameworkElement>().Where(element => element.IsVisible && element is RunChartView or RunAlternativePlotView))
                {
                    // Inspect the view's actual data rectangle so a visible title or legend cannot pass for a visible graph.
                    if (chart.GetType().GetProperty("Plot", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(chart) is not Rect dataBounds) continue;
                    var visible = chart.TransformToAncestor(page).TransformBounds(dataBounds);
                    visible.Intersect(viewport);
                    if (!visible.IsEmpty && visible.Width * visible.Height > best.Width * best.Height)
                        best = new(visible.Width, visible.Height);
                }
                return best;
            }
            void CheckOwnForeground()
            {
                var current = GetForegroundWindow();
                if (current == IntPtr.Zero) return;
                _ = GetWindowThreadProcessId(current, out var process);
                if (process == (uint)Environment.ProcessId && !failures.Contains("review-process-is-foreground"))
                    failures.Add("review-process-is-foreground");
            }
        }
        catch (Exception error) { failures.Add("review/" + error.GetType().Name + ": " + error.Message); failures.Add(error.StackTrace ?? "No stack trace."); }
        finally
        {
            host?.Close(); source?.Close();
            if (controller is not null) Await(controller.DisposeAsync().AsTask());
            application.Shutdown();
            SynchronizationContext.SetSynchronizationContext(synchronization);
        }
        File.WriteAllText(Path.Combine(output, "runs-review.json"), JsonSerializer.Serialize(new
        {
            input = "Deterministic synthetic runs; isolated store; no service start, game access, or desktop capture.",
            captures,
            failures,
            ownWindowActivations,
            ownForegroundEvents = foregroundMonitor.Events,
            unrelatedForegroundChanged = ownWindowActivations == 0 && foregroundMonitor.Events == 0 && GetForegroundWindow() != foreground,
            bindingDiagnostics = bindings.Messages
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Runs review: {(failures.Count == 0 ? "PASS" : "FAIL")}; {captures.Count} own-surface images; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 2;
    }
    private sealed record PlotVisibility(double Width, double Height);

    private static IEnumerable<Drawing> Drawings(Drawing drawing)
    {
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var descendant in Drawings(child)) yield return descendant;
    }

    private sealed class ForegroundMonitor : IDisposable
    {
        private readonly WinEventCallback _callback;
        private readonly IntPtr _hook;
        private int _events;
        internal bool IsAvailable => _hook != IntPtr.Zero;
        internal int Events => Volatile.Read(ref _events);
        internal ForegroundMonitor()
        {
            _callback = (_, _, _, _, _, _, _) => Interlocked.Increment(ref _events);
            _hook = SetWinEventHook(3, 3, IntPtr.Zero, _callback, (uint)Environment.ProcessId, 0, 0);
        }
        public void Dispose()
        {
            if (_hook != IntPtr.Zero) _ = UnhookWinEvent(_hook);
            GC.KeepAlive(_callback);
        }
    }

    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static void AwaitReady(RunsViewModel model)
    {
        var started = Stopwatch.StartNew();
        do { Await(Task.Delay(20)); } while ((model.IsBusy || model.IsPreparingCharts) && started.Elapsed < TimeSpan.FromSeconds(5));
        Await(Task.Delay(40));
        if (model.IsBusy || model.IsPreparingCharts || model.HasError) throw new InvalidOperationException("Synthetic report did not settle.");
    }
    private static void Settle(FrameworkElement surface)
    {
        surface.UpdateLayout();
        surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static RecordedRun RunFixture(string name, string tune, double acceleration, bool gap)
    {
        var samples = Enumerable.Range(0, 2401).Select(index =>
        {
            var time = index / 100d;
            var speed = (float)(time < 12 ? time * 3 * acceleration : Math.Max(10, 36 * acceleration - (time - 12) * 1.4));
            var slip = time > 2 && time < 5 ? (float)(4 + Math.Sin(time * 7)) : 0;
            return new RunSample
            {
                ElapsedSeconds = time,
                IsDriving = true,
                Segment = 0,
                FrontRadiusMeters = .34,
                RearRadiusMeters = .35,
                WheelSpeedMetersPerSecond = speed + slip,
                State = new VehicleState
                {
                    IsRaceOn = true,
                    GameTimestampMilliseconds = (uint)(index * 10),
                    ReceivedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(time),
                    CarOrdinal = 101,
                    Drivetrain = DrivetrainType.RearWheelDrive,
                    NumCylinders = 8,
                    GroundSpeedMetersPerSecond = speed,
                    WheelRotationRadiansPerSecond = new(speed / .34f, speed / .34f, (speed + slip) / .35f, (speed + slip) / .35f),
                    TireSlipRatio = new(0, 0, slip / 10, slip / 10),
                    TireSlipAngle = default,
                    NormalizedSuspensionTravel = new(.5f, .5f, .5f, .5f),
                    LateralAccelerationMetersPerSecondSquared = (float)Math.Sin(time / 2) * 4,
                    LongitudinalAccelerationMetersPerSecondSquared = time < 12 ? 3 : -1.4f,
                    EngineRpm = (float)(1800 + speed * 120 + Math.Sin(time * 4) * 180),
                    EngineMaximumRpm = 8200,
                    Gear = time < 5 ? TransmissionGear.Second : TransmissionGear.Third,
                    Steering = (sbyte)(Math.Sin(time) * (time < 12 ? 4 : 48)),
                    Accelerator = time < 12 ? (byte)255 : (byte)100,
                    Brake = time is > 18 and < 20 ? (byte)120 : (byte)0,
                    BoostPressurePsi = time < 12 ? 14 : -8,
                    PowerWatts = speed * 8500,
                    TorqueNm = 650,
                    TireTemperatureFahrenheit = new(170 + (float)time, 172 + (float)time, 180 + (float)time * 2, 181 + (float)time * 2)
                }
            };
        }).Where(sample => !gap || sample.ElapsedSeconds is < 16 or > 16.6).ToArray();
        return new RecordedRun
        {
            Name = name,
            Tune = tune,
            Notes = "Same road, rolling start. Review the early wheelspin and tire temperatures.",
            StartedAtUtc = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
            FinishReason = "Stopped by you",
            Markers = [new(4.5, "Wheelspin"), new(9, "Shift")],
            Samples = samples
        };
    }
    private sealed class ResourceApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint eventTime);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
