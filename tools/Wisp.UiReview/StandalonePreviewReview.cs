using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;

namespace Wisp.UiReview;

internal static class StandalonePreviewReview
{
    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    internal static int Run(string output, Func<ResourceDictionary> resources,
        Func<Window, object?, FrameworkElement> detach)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = resources() };
        using var bindings = new BindingTrace();
        var captures = new List<string>();
        var failures = new List<string>();
        var geometry = new List<object>();
        try
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            foreach (var legacy in new[] { false, true })
                foreach (var layout in new[] { HudLayoutMode.Combined, HudLayoutMode.Minimal, HudLayoutMode.SeparateBoxes })
                    foreach (var mode in new[] { NativeGaugeMode.Analogue, NativeGaugeMode.Digital })
                    {
                        var name = $"{(legacy ? "legacy" : "modern")}-{layout}-{mode}";
                        bindings.Phase = name;
                        var settings = Fixture.All.First(f => f.Name == "orbit-reference").CreateSettings();
                        settings.LayoutMode = layout;
                        settings.NativeGaugeMode = mode;
                        settings.BoostGaugeEnabled = settings.TireTemperatureGaugeEnabled = true;
                        settings.PowerGaugeEnabled = settings.TorqueGaugeEnabled = true;
                        settings.BoostGaugeScale = settings.TireTemperatureGaugeScale = 1;
                        settings.PowerGaugeScale = settings.TorqueGaugeScale = 1;
                        settings.BackgroundParticlesEnabled = settings.AnimatedBackground = false;
                        settings.StartWithWindows = settings.StartWithForza = settings.AutomaticApplicationUpdateChecks = false;
                        var controller = new AppController(settings, _ => { }, new NoStartupRegistration(),
                            runsDirectory: Path.Combine(output, name + "-runs"));
                        Window window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
                        try
                        {
                            SupplementaryGaugeReview.ApplySample(controller.ViewModel);
                            var page = detach(window, controller.ViewModel);
                            ((TabControl)window.FindName("RootTabs")).SelectedIndex = 2;
                            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                            page.Measure(new Size(1464, 994));
                            page.Arrange(new Rect(0, 0, 1464, 994));
                            page.UpdateLayout();
                            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                            var preview = (Border)window.FindName("HudPreviewSurface");
                            var originalSize = new Size(preview.ActualWidth, preview.ActualHeight);
                            ((Panel)preview.Parent).Children.Remove(preview);
                            preview.Resources.MergedDictionaries.Add(page.Resources);
                            System.Windows.Documents.TextElement.SetForeground(preview, window.Foreground);
                            preview.DataContext = controller.ViewModel;
                            var captureRoot = new Grid();
                            captureRoot.Children.Add(preview);
                            foreach (var compact in new[] { false, true })
                            {
                                var size = compact ? new Size(340, 240) : originalSize;
                                // Capture the actual authored preview, retaining its model and theme.
                                // Give it a bounded viewport; no app window or gameplay state is changed.
                                preview.Width = size.Width;
                                preview.Height = size.Height;
                                var bitmap = PowerTorqueShaderCapture.RenderSurface(captureRoot, size, 144);
                                var layoutName = layout switch { HudLayoutMode.Minimal => "MinimalLayoutPreview", HudLayoutMode.Combined => "CombinedLayoutPreview", _ => "SeparateBoxesLayoutPreview" };
                                var speed = Descendants((DependencyObject)window.FindName(layoutName)).OfType<TextBlock>()
                                    .First(t => t.Text == controller.ViewModel.PreviewSpeed);
                                var speedBounds = speed.TransformToAncestor(preview).TransformBounds(new Rect(speed.RenderSize));
                                geometry.Add(new { name, compact, speed.Text, bounds = speedBounds.ToString() });
                                if (!ContainsReadoutPixels(bitmap, speedBounds, 144)) failures.Add(name + "/speed-readout-missing");
                                var filename = name + (compact ? "-compact" : "") + ".png";
                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                using var stream = File.Create(Path.Combine(output, filename));
                                encoder.Save(stream);
                                captures.Add(filename);
                            }
                        }
                        finally
                        {
                            window.Close();
                            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                    }
        }
        catch (Exception error) { failures.Add(error.ToString()); }
        finally { app.Shutdown(); }
        if (bindings.TotalCount != 0) failures.Add("binding-diagnostics");
        File.WriteAllText(Path.Combine(output, "standalone-preview.json"), JsonSerializer.Serialize(new
        {
            method = "Actual modern/legacy Appearance preview controls with synthetic telemetry at normal and compact sizes; native needle shader readback. Layout review only, no gameplay or performance claim.",
            captures,
            failures,
            geometry,
            bindingDiagnosticCount = bindings.TotalCount
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Standalone previews: {captures.Count} captures; {failures.Count} failures.");
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

    private static bool ContainsReadoutPixels(BitmapSource bitmap, Rect bounds, int dpi)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var whitePixels = 0;
        for (var y = Math.Max(0, (int)(bounds.Top * dpi / 96)); y < Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(bounds.Bottom * dpi / 96)); y++)
            for (var x = Math.Max(0, (int)(bounds.Left * dpi / 96)); x < Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(bounds.Right * dpi / 96)); x++)
            {
                var index = (y * bitmap.PixelWidth + x) * 4;
                if (pixels[index] > 180 && pixels[index + 1] > 180 && pixels[index + 2] > 180) whitePixels++;
            }
        return whitePixels > 4;
    }
}
