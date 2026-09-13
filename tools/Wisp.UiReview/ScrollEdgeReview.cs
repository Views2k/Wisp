using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.UiReview;

internal static class ScrollEdgeReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources,
        Func<Window, object?, FrameworkElement> detachSurface)
    {
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        var failures = new List<string>();
        var captures = new List<object>();
        var activations = 0;
        var previousSynchronization = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var application = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application.Resources = loadResources();
            foreach (var legacy in new[] { false, true }) ReviewShell(legacy);
            ReviewHorizontalFixture();
        }
        catch (Exception exception)
        {
            failures.Add(bindings.Phase + "/" + exception.GetType().Name);
        }
        finally
        {
            application.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousSynchronization);
        }
        if (bindings.TotalCount != 0) failures.Add("binding-diagnostics");
        File.WriteAllText(Path.Combine(output, "scroll-edge-review.json"), JsonSerializer.Serialize(new
        {
            method = "Actual detached modern and legacy app content with synthetic run data and output-local settings. Native-resolution software PNGs. The horizontal/fitting-content cases are isolated controls using the same application resources. No shown window, live telemetry, game access or focus changes.",
            checks = "Only a content presenter's mask changes. Viewport/extent/offset/layout measurements and scrollbar opacity/masks are compared before and after. Mask alpha is sampled at each viewport edge and must fade only where more content remains.",
            captures,
            failures,
            ownWindowActivations = activations,
            bindingDiagnosticCount = bindings.TotalCount,
            bindingDiagnostics = bindings.Messages
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Scroll edge review: {(failures.Count == 0 ? "PASS" : "FAIL")}; {captures.Count} captures; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 2;

        void ReviewShell(bool legacy)
        {
            var fixture = Fixture.All.First(item => item.Name == (legacy ? "legacy-reference" : "orbit-reference"));
            var settings = fixture.CreateSettings();
            settings.AnimatedBackground = false; settings.DebugLoggingEnabled = false;
            var controller = new AppController(settings, new SettingsService(Path.Combine(output, legacy ? "legacy-settings.json" : "modern-settings.json")));
            ControlPanelWindow? window = null;
            try
            {
                fixture.Apply(controller.ViewModel, waiting: false);
                window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
                window.Activated += (_, _) => activations++;
                var tabs = Required<TabControl>(window, "RootTabs");
                var surface = detachSurface(window, controller.ViewModel);
                VisualTreeHelper.SetRootDpi(surface, new DpiScale(1, 1));
                var size = legacy ? new Size(980, 750) : new Size(1280, 800);
                tabs.SelectedIndex = 2;
                if (!legacy) Required<RadioButton>(window, "AppearanceGaugesCategory").IsChecked = true;
                Arrange(surface, size);
                var appearance = legacy
                    ? tabs.SelectedContent as ScrollViewer ?? Descendants((DependencyObject)tabs.SelectedContent).OfType<ScrollViewer>()
                        .OrderByDescending(viewer => viewer.ActualWidth * viewer.ActualHeight).First()
                    : Required<ScrollViewer>(window, "AppearanceGaugesScroll");
                CapturePositions((legacy ? "legacy" : "modern") + "-appearance-gauges", appearance, surface, size);

                tabs.SelectedIndex = 1;
                var page = Required<FrameworkElement>(window, "RunsSurface");
                var model = controller.Runs;
                Await(model.ShowReviewAsync(CreateRun("Baseline", 1), CreateRun("Comparison", 1.1)));
                model.SetPageVisible(true); AwaitReady(model);
                Arrange(surface, size);
                CapturePositions((legacy ? "legacy" : "modern") + "-runs-summary", Required<ScrollViewer>(page, "RunsScroll"), surface, size);
                if (!legacy)
                {
                    for (var index = 0; index < 24; index++)
                        model.Library.Add(new SavedRunItem(new RunSummary(Guid.NewGuid(), $"Saved drive {index + 1:00}", "Road tune", "Synthetic library row",
                            DateTimeOffset.UnixEpoch.AddMinutes(index), 12, 241, 101, false, "Stopped by you")));
                    Arrange(surface, size);
                    var list = Required<ListBox>(page, "SavedRunList");
                    var listScroll = Descendants(list).OfType<ScrollViewer>().First();
                    CapturePositions("modern-runs-library", listScroll, surface, size);
                }
                model.ShowGraphs(); AwaitReady(model);
                size = legacy ? new Size(980, 600) : new Size(720, 440);
                Arrange(surface, size);
                foreach (var chart in Descendants(page).OfType<RunChartView>()) chart.RenderOffscreen = true;
                Arrange(surface, size);
                CapturePositions((legacy ? "legacy" : "modern") + "-runs-graphs", Required<ScrollViewer>(page, "GraphScroll"), surface, size);
                Check(activations == 0 && new WindowInteropHelper(window).Handle == IntPtr.Zero, "window-was-shown-or-activated");
                foreach (var presenter in Descendants(surface).OfType<ScrollContentPresenter>()) ScrollEdgeFade.SetIsEnabled(presenter, false);
                if (window.FindName("DashboardRim") is DashboardRimEffect rim) rim.TargetElement = null;
            }
            finally
            {
                window?.Close();
                Await(controller.DisposeAsync().AsTask());
            }
        }

        void ReviewHorizontalFixture()
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            for (var index = 0; index < 12; index++)
                content.Children.Add(new Border
                {
                    Width = 120,
                    Margin = new Thickness(6),
                    Padding = new Thickness(12),
                    Background = new SolidColorBrush(Color.FromRgb((byte)(40 + index * 7), 90, 110)),
                    Child = new TextBlock { Text = $"Item {index + 1}", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }
                });
            var scroll = new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false
            };
            var surface = new Border { Background = new SolidColorBrush(Color.FromRgb(18, 24, 30)), Padding = new Thickness(24), Child = scroll };
            var size = new Size(660, 240);
            Arrange(surface, size);
            CapturePositions("isolated-horizontal", scroll, surface, size, horizontal: true);
            scroll.Content = new TextBlock { Text = "Everything fits inside this viewport.", Foreground = Brushes.White, Margin = new Thickness(12) };
            Arrange(surface, size);
            Check(scroll.ScrollableWidth == 0 && scroll.ScrollableHeight == 0, "fit-fixture-overflows");
            Capture("isolated-fitting-content", scroll, surface, size);
            foreach (var presenter in Descendants(surface).OfType<ScrollContentPresenter>()) ScrollEdgeFade.SetIsEnabled(presenter, false);
        }

        void CapturePositions(string prefix, ScrollViewer scroll, FrameworkElement surface, Size size, bool horizontal = false)
        {
            Arrange(surface, size);
            Check(horizontal ? scroll.ScrollableWidth > 0 : scroll.ScrollableHeight > 0, prefix + "/fixture-does-not-overflow");
            foreach (var (name, portion) in new[] { ("top", 0d), ("middle", .5), ("bottom", 1d) })
            {
                if (horizontal) scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth * portion);
                else scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * portion);
                Arrange(surface, size);
                Capture(prefix + "-" + (horizontal ? name == "top" ? "left" : name == "bottom" ? "right" : name : name), scroll, surface, size);
            }
        }

        void Capture(string name, ScrollViewer scroll, FrameworkElement surface, Size size)
        {
            bindings.Phase = name;
            var presenter = Descendants(scroll).OfType<ScrollContentPresenter>().First(item => ReferenceEquals(Owner(item), scroll));
            Check(ScrollEdgeFade.GetIsEnabled(presenter), name + "/implicit-fade-not-enabled");
            var bars = Descendants(scroll).OfType<ScrollBar>().Where(bar => ReferenceEquals(Owner(bar), scroll)).ToArray();
            var barStates = bars.Select(bar => (bar.Opacity, bar.OpacityMask)).ToArray();
            ScrollEdgeFade.SetIsEnabled(presenter, false); ScrollEdgeFade.Refresh(presenter);
            Arrange(surface, size);
            var before = Metrics(scroll, presenter);
            ScrollEdgeFade.SetIsEnabled(presenter, true); ScrollEdgeFade.Refresh(presenter);
            Arrange(surface, size);
            Check(before == Metrics(scroll, presenter), name + "/fade-changes-layout-or-scroll-position");
            Check(scroll.OpacityMask is null, name + "/viewer-scrollbars-are-masked");
            for (var index = 0; index < bars.Length; index++)
                Check(bars[index].Opacity == barStates[index].Opacity && ReferenceEquals(bars[index].OpacityMask, barStates[index].OpacityMask),
                    name + "/scrollbar-opacity-changed");
            var expected = new
            {
                top = scroll.VerticalOffset > .001,
                bottom = scroll.ScrollableHeight - scroll.VerticalOffset > .001,
                left = scroll.HorizontalOffset > .001,
                right = scroll.ScrollableWidth - scroll.HorizontalOffset > .001
            };
            var alpha = MaskEdges(presenter);
            Check(expected.top ? alpha.Top < 250 : alpha.Top == 255, name + "/top-fade-state");
            Check(expected.bottom ? alpha.Bottom < 250 : alpha.Bottom == 255, name + "/bottom-fade-state");
            Check(expected.left ? alpha.Left < 250 : alpha.Left == 255, name + "/left-fade-state");
            Check(expected.right ? alpha.Right < 250 : alpha.Right == 255, name + "/right-fade-state");
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(file);
            captures.Add(new
            {
                name,
                width = size.Width,
                height = size.Height,
                expected,
                maskAlpha = new { alpha.Top, alpha.Bottom, alpha.Left, alpha.Right },
                scroll.VerticalOffset,
                scroll.HorizontalOffset,
                scroll.ScrollableHeight,
                scroll.ScrollableWidth,
                viewport = new { presenter.ActualWidth, presenter.ActualHeight },
                scrollbarCount = bars.Length
            });
        }
        void Check(bool condition, string name) { if (!condition) failures.Add(name); }
    }

    private static (byte Top, byte Bottom, byte Left, byte Right) MaskEdges(ScrollContentPresenter presenter)
    {
        var width = Math.Max(1, (int)Math.Ceiling(presenter.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(presenter.ActualHeight));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(presenter.OpacityMask ?? Brushes.White, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        byte Alpha(int x, int y) => pixels[(y * width + x) * 4 + 3];
        var lastX = Math.Max(0, (int)Math.Floor(presenter.ActualWidth) - 1);
        var lastY = Math.Max(0, (int)Math.Floor(presenter.ActualHeight) - 1);
        return (Alpha(width / 2, 0), Alpha(width / 2, lastY), Alpha(0, height / 2), Alpha(lastX, height / 2));
    }
    private static (Size Viewer, Size Presenter, double X, double Y, double Width, double Height) Metrics(ScrollViewer scroll, ScrollContentPresenter presenter) =>
        (scroll.RenderSize, presenter.RenderSize, scroll.HorizontalOffset, scroll.VerticalOffset, scroll.ExtentWidth, scroll.ExtentHeight);
    private static ScrollViewer? Owner(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) return viewer;
        return null;
    }
    private static T Required<T>(FrameworkElement scope, string name) where T : class =>
        scope.FindName(name) as T ?? throw new InvalidOperationException("Missing scroll-edge review element.");
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Arrange(FrameworkElement surface, Size size)
    {
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.UpdateLayout();
        foreach (var presenter in Descendants(surface).OfType<ScrollContentPresenter>()) ScrollEdgeFade.Refresh(presenter);
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
        var deadline = Stopwatch.StartNew();
        while ((model.IsBusy || model.IsPreparingCharts) && deadline.Elapsed < TimeSpan.FromSeconds(8)) Await(Task.Delay(10));
        if (model.IsBusy || model.IsPreparingCharts || model.HasError) throw new InvalidOperationException("Synthetic report did not settle.");
    }
    private static RecordedRun CreateRun(string name, double scale) => new()
    {
        Name = name,
        Tune = "Synthetic road tune",
        FinishReason = "Stopped by you",
        StartedAtUtc = DateTimeOffset.UnixEpoch,
        Samples = Enumerable.Range(0, 241).Select(index => new RunSample
        {
            ElapsedSeconds = index / 20d,
            IsDriving = true,
            FrontRadiusMeters = .34,
            RearRadiusMeters = .35,
            WheelSpeedMetersPerSecond = index / 10d * scale,
            State = new VehicleState
            {
                TireSlipRatio = new(0, 0, 0, 0),
                TireSlipAngle = new(0, 0, 0, 0),
                NormalizedSuspensionTravel = new(.5f, .5f, .5f, .5f),
                Steering = 0,
                Brake = 0,
                IsRaceOn = true,
                CarOrdinal = 101,
                NumCylinders = 8,
                Drivetrain = DrivetrainType.RearWheelDrive,
                GameTimestampMilliseconds = (uint)(index * 50),
                ReceivedAtUtc = DateTimeOffset.UnixEpoch.AddMilliseconds(index * 50),
                GroundSpeedMetersPerSecond = (float)(index / 10d * scale),
                EngineRpm = 1800 + index * 20,
                WheelRotationRadiansPerSecond = new(index / 3.4f, index / 3.4f, index / 3.5f, index / 3.5f),
                EngineMaximumRpm = 8200,
                Gear = TransmissionGear.Third,
                Accelerator = 255,
                PowerWatts = index * 1400,
                TorqueNm = 600,
                BoostPressurePsi = 12,
                LateralAccelerationMetersPerSecondSquared = (float)Math.Sin(index / 20d) * 4,
                LongitudinalAccelerationMetersPerSecondSquared = 2,
                TireTemperatureFahrenheit = new(170 + index / 10f, 172 + index / 10f, 180 + index / 5f, 181 + index / 5f)
            }
        }).ToArray()
    };
    private sealed class ResourceApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
}
