using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;
using Wisp.Core;

namespace Wisp.UiReview;

internal static class DashboardContourReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources,
        Func<Window, object?, FrameworkElement> detachSurface, Action<FrameworkElement, int> setDpi)
    {
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = loadResources() };
        using var bindings = new BindingTrace();
        var failures = new List<string>();
        var measurements = new List<object>();
        AppController? controller = null;
        MainWindow? window = null;
        var phase = "initialize";
        try
        {
            var fixture = Fixture.All.Single(item => item.Name == "orbit-reference");
            var settings = fixture.CreateSettings();
            settings.AnimatedBackground = false;
            controller = new AppController(settings, new SettingsService(Path.Combine(output, "synthetic-settings.json")));
            fixture.Apply(controller.ViewModel, waiting: false);
            window = new MainWindow(controller);
            var surface = detachSurface(window, controller.ViewModel);
            var instrument = Named<OrbitSurface>("OrbitInstrument");
            var metrics = Named<Panel>("DashboardInstruments");
            var speed = Named<FrameworkElement>("DashboardSpeedMetrics");
            var readout = Named<TextBlock>("DashboardSpeedNumber");
            var status = Named<Button>("DashboardConnectionButton");
            var scale = Named<ScaleTransform>("DashboardDisplayScale");
            var originalFont = (readout.FontFamily, readout.FontSize, readout.FontWeight);
            var initialSettings = JsonSerializer.Serialize(settings);

            foreach (var dpi in new[] { 96, 144 })
                foreach (var lost in new[] { false, true })
                    foreach (var border in new[] { 0d, 3d })
                    {
                        fixture.Apply(controller.ViewModel, waiting: false);
                        if (lost) controller.ViewModel.UpdateWaiting(default, TelemetryConnectionState.Lost, TimeSpan.FromSeconds(1), 60, preserveHudVisuals: true);
                        surface.Resources["OrbitStrokeThickness"] = new Thickness(border);
                        foreach (var width in new[] { 720d, 1100d, 1200d, 1440d, 1920d, 2560d, 3840d })
                        {
                            phase = $"normal-{width}-{dpi}dpi-{(lost ? "lost" : "live")}-border{border}";
                            Arrange(width, 1440, dpi);
                            CheckContour();
                            if (width == 3840 && border == 3) Capture(phase, width, 1440, dpi);
                        }
                        foreach (var resizable in new[] { false, true })
                        {
                            window.SetResizableDisplay(resizable);
                            Arrange(3840, 1440, dpi);
                            var before = OtherMetricBounds();
                            var beforeStatus = Bounds(status, surface);
                            var beforeSpeed = readout.Text;
                            var beforePeak = Descendants(speed).OfType<TextBlock>().Last().Text;
                            window.SetDashboardDisplayMode(true);
                            phase = $"display-{resizable}-{dpi}dpi-{lost}-{border}";
                            Arrange(3840, 1440, dpi);
                            CheckContour();
                            window.SetDashboardDisplayMode(false);
                            phase = $"returned-{resizable}-{dpi}dpi-{lost}-{border}";
                            Arrange(3840, 1440, dpi);
                            CheckContour();
                            Check(scale.ScaleX == 1 && scale.ScaleY == 1, "normal-scale-not-restored");
                            Check(before.SequenceEqual(OtherMetricBounds()), "other-metrics-moved-after-return");
                            Check(beforeStatus == Bounds(status, surface), "connection-status-moved-after-return");
                            Check(beforeSpeed == readout.Text && beforePeak == Descendants(speed).OfType<TextBlock>().Last().Text, "readings-changed-after-return");
                            if (border == 3 && lost && !resizable) Capture(phase, 3840, 1440, dpi);
                        }
                        window.SetResizableDisplay(false);
                    }
            Check(JsonSerializer.Serialize(settings) == initialSettings, "settings-mutated");
            Check((readout.FontFamily, readout.FontSize, readout.FontWeight) == originalFont, "readout-font-mutated");
            Check(new WindowInteropHelper(window).Handle == IntPtr.Zero && PresentationSource.FromVisual(surface) is null, "detached-isolation-lost");

            T Named<T>(string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException(name);
            void Check(bool condition, string code) { if (!condition) failures.Add(phase + "/" + code); }
            Rect[] OtherMetricBounds() => metrics.Children.Cast<FrameworkElement>().Skip(1).Select(child => Bounds(child, surface)).ToArray();
            void Arrange(double width, double height, int dpi)
            {
                bindings.Phase = phase;
                for (var pass = 0; pass < 4; pass++)
                {
                    setDpi(surface, dpi);
                    surface.Measure(new Size(width, height));
                    surface.Arrange(new Rect(0, 0, width, height));
                    surface.UpdateLayout();
                    surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
                }
            }
            void CheckContour()
            {
                if (instrument.Shape == OrbitSurfaceShape.Swept)
                {
                    var contour = OrbitSurface.CreateGeometry(new Rect(instrument.RenderSize), instrument.Shape, instrument.CornerRadius);
                    var rectangle = Bounds(speed, instrument);
                    rectangle.Inflate(7, 7);
                    Check(contour.FillContainsWithDetail(new RectangleGeometry(rectangle)) == IntersectionDetail.FullyContains, "speed-block-crosses-contour");
                    Check(speed.ActualWidth >= 100, "speed-block-too-narrow");
                }
                else Check(speed.Margin.Left == 0, "compact-layout-keeps-inset");
                measurements.Add(new { phase, shape = instrument.Shape.ToString(), instrument = instrument.RenderSize.ToString(), speed = Bounds(speed, instrument).ToString(), inset = speed.Margin.Left, status = Bounds(status, surface).ToString() });
            }
            void Capture(string name, double width, double height, int dpi)
            {
                var image = new RenderTargetBitmap((int)(width * dpi / 96), (int)(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                image.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
            }
        }
        catch (Exception exception) { failures.Add(phase + "/" + exception); }
        finally
        {
            window?.Close();
            controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            application.Shutdown();
        }
        if (bindings.TotalCount > 0) failures.Add("binding-diagnostics");
        File.WriteAllText(Path.Combine(output, "review.json"), JsonSerializer.Serialize(new
        {
            method = "Detached actual dashboard, synthetic live/lost telemetry, 96/144 DPI, width through 3840 DIP, contour containment and real display-mode handlers. No shown window, live services or gameplay performance measurement.",
            measurements,
            failures,
            bindingDiagnostics = bindings.TotalCount
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Dashboard contour check: {(failures.Count == 0 ? "PASS" : "FAIL")}; {measurements.Count} layouts; {failures.Count} findings.");
        return failures.Count == 0 ? 0 : 2;
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
