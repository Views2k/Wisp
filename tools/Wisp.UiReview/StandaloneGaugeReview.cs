using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App;

namespace Wisp.UiReview;

internal static class StandaloneGaugeReview
{
    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    internal static int Run(string output, Func<ResourceDictionary> resources,
        Func<Window, object?, FrameworkElement> detach, Action<FrameworkElement, int> setDpi)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = resources() };
        using var bindings = new BindingTrace();
        var failures = new List<string>();
        var captures = new List<string>();
        try
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            foreach (var layout in new[] { HudLayoutMode.Minimal, HudLayoutMode.Combined, HudLayoutMode.SeparateBoxes })
                foreach (var mode in new[] { NativeGaugeMode.Analogue, NativeGaugeMode.Digital })
                    foreach (var scale in new[] { .5, 1, 2 })
                    {
                        var name = $"{layout}-{mode}-{scale:0.0}";
                        bindings.Phase = name;
                        var settings = Fixture.All.First(f => f.Name == "orbit-reference").CreateSettings();
                        settings.LayoutMode = layout;
                        settings.NativeGaugeMode = mode;
                        settings.BoostGaugeEnabled = settings.TireTemperatureGaugeEnabled = true;
                        settings.BoostGaugeAttached = settings.TireTemperatureGaugeAttached = true;
                        settings.BoostGaugeScale = settings.TireTemperatureGaugeScale = scale;
                        settings.StartWithWindows = settings.StartWithForza = settings.AutomaticApplicationUpdateChecks = false;
                        var controller = new AppController(settings, _ => { }, new NoStartupRegistration(),
                            runsDirectory: Path.Combine(output, name + "-runs"));
                        Window[] windows = [];
                        try
                        {
                            SupplementaryGaugeReview.ApplySample(controller.ViewModel);
                            var main = new OverlayWindow(controller);
                            var boost = new BoostGaugeWindow(controller);
                            var tire = new TireTemperatureGaugeWindow(controller);
                            windows = [main, boost, tire];
                            main.ApplyLayout(layout, mode, 1, 1, 1);
                            boost.ApplyAppearance(scale, 1);
                            tire.ApplyGaugeMode(mode);
                            tire.ApplyAppearance(scale, 1);
                            if (!controller.IsDetachedBoostGaugeEnabled || !controller.IsDetachedTireTemperatureGaugeEnabled)
                                failures.Add(name + "/standalone-not-enabled");
                            foreach (var attached in new[] { "AttachedAnalogBoost", "AttachedDigitalBoost", "AttachedAnalogTireTemperature", "AttachedDigitalTireTemperature" })
                                if (((FrameworkElement)main.FindName(attached)).Visibility != Visibility.Collapsed)
                                    failures.Add(name + "/mixed-attachment-" + attached);
                            var sheet = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(18, 21, 29)) };
                            sheet.Children.Add(new TextBlock
                            {
                                Text = $"{layout} / separate gauges / {scale * 100:0}% / remembered {mode}",
                                Foreground = Brushes.White,
                                FontSize = 15,
                                Margin = new Thickness(12)
                            });
                            foreach (var window in windows)
                            {
                                var size = new Size(window.Width, window.Height);
                                var surface = detach(window, controller.ViewModel);
                                surface.Opacity = 1;
                                surface.Width = size.Width;
                                surface.Height = size.Height;
                                setDpi(surface, 144);
                                surface.Measure(size);
                                surface.Arrange(new Rect(size));
                                surface.UpdateLayout();
                                // Shader quads carry transparent corners outside their
                                // visible needles. Measure authored dial/text drawings.
                                foreach (var gauge in Descendants(surface).OfType<FrameworkElement>().Where(gauge =>
                                    gauge.Visibility == Visibility.Visible &&
                                    gauge is AnalogBoostGaugeView or AnalogTireTemperatureGaugeView or DigitalTireTemperatureGaugeView))
                                {
                                    var drawing = VisualTreeHelper.GetDrawing(gauge);
                                    var bounds = gauge.TransformToAncestor(surface).TransformBounds(drawing?.Bounds ?? Rect.Empty);
                                    if (bounds.IsEmpty || bounds.Left < -.1 || bounds.Top < -.1 ||
                                        bounds.Right > size.Width + .1 || bounds.Bottom > size.Height + .1)
                                        failures.Add(name + "/clipped-" + window.GetType().Name);
                                }
                                surface.HorizontalAlignment = HorizontalAlignment.Left;
                                sheet.Children.Add(surface);
                            }
                            setDpi(sheet, 144);
                            sheet.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            sheet.Arrange(new Rect(sheet.DesiredSize));
                            sheet.UpdateLayout();
                            var bitmap = PowerTorqueShaderCapture.RenderSurface(sheet,
                                new Size(sheet.ActualWidth, sheet.ActualHeight), 144);
                            var filename = name + ".png";
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var stream = File.Create(Path.Combine(output, filename));
                            encoder.Save(stream);
                            captures.Add(filename);
                        }
                        finally
                        {
                            foreach (var window in windows) window.Close();
                            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                    }
        }
        catch (Exception error) { failures.Add(error.ToString()); }
        finally { app.Shutdown(); }
        if (bindings.TotalCount != 0) failures.Add("binding-diagnostics");
        File.WriteAllText(Path.Combine(output, "standalone-gauges.json"), JsonSerializer.Serialize(new
        {
            method = "Authored HUD and standalone WPF surfaces at 144 DPI, synthetic telemetry, 50/100/200% sizes. No live window, game, settings or recording is changed. These captures verify layout/artwork, not gameplay presentation timing.",
            captures,
            failures,
            bindingDiagnosticCount = bindings.TotalCount
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Standalone gauges: {captures.Count} captures; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
