using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;

namespace Wisp.UiReview;

internal static class PowerTorqueReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources,
        Func<Window, object?, FrameworkElement> detachSurface, Action<FrameworkElement, int> setDpi)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var bindings = new BindingTrace();
        var failures = new List<string>();
        var captures = new List<string>();
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            app.Resources = loadResources();
            foreach (var torque in new[] { false, true })
            {
                var gauge = new PowerTorqueGaugeView
                {
                    Width = 280,
                    Height = 280,
                    IsTorque = torque,
                    Maximum = torque ? 1200 : 1000,
                    Display = new PowerTorqueDisplay(true, 427, 507, 612, 690),
                    LowBrush = Brushes.Cyan,
                    MidBrush = Brushes.LimeGreen,
                    HighBrush = Brushes.OrangeRed,
                    ColorNumber = true
                };
                var bitmap = PowerTorqueShaderCapture.RenderGauge(gauge, 144);
                var filename = torque ? "torque-native-shader.png" : "power-native-shader.png";
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, filename)); encoder.Save(stream);
                captures.Add(filename);
            }
            foreach (var variant in new[] { "analogue", "digital", "torque-only", "large-purple", "legacy" })
            {
                bindings.Phase = variant;
                var settings = Fixture.All.First(item => item.Name == "orbit-reference").CreateSettings();
                settings.LayoutMode = HudLayoutMode.Native;
                settings.NativeGaugeMode = variant == "digital" ? NativeGaugeMode.Digital : NativeGaugeMode.Analogue;
                settings.PowerGaugeEnabled = variant != "torque-only";
                settings.TorqueGaugeEnabled = true;
                settings.PowerTorqueGaugeScale = variant == "large-purple" ? 2 : 1;
                settings.TorqueUnit = variant == "torque-only" ? TorqueUnit.PoundFeet : TorqueUnit.NewtonMeters;
                settings.BackgroundParticlesEnabled = false;
                settings.AnimatedBackground = false;
                settings.AutomaticApplicationUpdateChecks = false;
                settings.StartWithWindows = false;
                settings.StartWithForza = false;
                settings.UseLegacyInterface = variant == "legacy";
                settings.SetupCompletion = new SetupCompletionRecord
                {
                    Version = SetupCompletionRecord.CurrentVersion,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    ValidatedUdpPort = settings.UdpPort,
                    ValidatedPackets = SetupCompletionRecord.MinimumPackets,
                    MovingPackets = SetupCompletionRecord.MinimumMovingPackets,
                    ValidatedElapsedMilliseconds = SetupCompletionRecord.MinimumElapsedMilliseconds,
                    DataOutConfirmed = true,
                    DisplayModeConfirmed = true,
                    StockHudConfirmed = true
                };
                if (variant == "large-purple") settings.ColorTheme = "Purple";
                var saved = new SettingsService(Path.Combine(output, variant + "-settings.json"));
                var controller = new AppController(settings, saved.Save, new NoStartupRegistration(),
                    runsDirectory: Path.Combine(output, variant + "-runs"));
                ControlPanelWindow window = settings.UseLegacyInterface ? new LegacyMainWindow(controller) : new MainWindow(controller);
                var surface = detachSurface(window, controller.ViewModel);
                var dpi = variant == "large-purple" ? 144 : 96;
                var size = variant == "large-purple" ? new Size(980, 750) : new Size(1464, 994);
                try
                {
                    ((TabControl)window.FindName("RootTabs")).SelectedIndex = 2;
                    setDpi(surface, dpi);
                    Arrange(surface, size);
                    Capture(surface, size, dpi, variant + "-appearance.png");
                    var control = (PowerTorqueGaugeSettingsControl)window.FindName("PowerTorqueGaugeSettings");
                    foreach (var options in LogicalDescendants(control).OfType<Expander>())
                        options.IsExpanded = true;
                    // Render the real shared settings control at a narrow available width.
                    var panel = (Border)control.Parent;
                    panel.Child = null;
                    control.DataContext = controller.ViewModel;
                    var settingsSurface = new Border { Background = (Brush)window.FindResource("CardBrush"), Padding = new Thickness(22), Child = control };
                    settingsSurface.Resources.MergedDictionaries.Add(window.Resources);
                    settingsSurface.Measure(new Size(540, double.PositiveInfinity));
                    var settingsSize = new Size(540, settingsSurface.DesiredSize.Height);
                    Arrange(settingsSurface, settingsSize);
                    Capture(settingsSurface, settingsSize, 96, variant + "-settings.png");
                    foreach (var slider in Descendants(control).OfType<Slider>())
                        if (slider.Template is null) failures.Add(variant + "/unthemed-slider");
                    var power = (CheckBox)control.FindName("PowerToggle");
                    power.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, !settings.PowerGaugeEnabled);
                    if (settings.PowerGaugeEnabled != controller.ViewModel.PowerGaugeEnabled)
                        failures.Add(variant + "/toggle-not-saved");
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
        if (bindings.TotalCount > 0) failures.Add("binding-errors");
        File.WriteAllText(Path.Combine(output, "power-torque-review.json"), JsonSerializer.Serialize(new
        {
            method = "Actual authored Appearance preview and shared gauge settings, detached WPF rendering; no live game or production settings access.",
            captures,
            failures,
            bindingDiagnosticCount = bindings.TotalCount,
            bindings = bindings.Messages
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Power/torque review: {captures.Count} captures, {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 1;

        void Capture(FrameworkElement surface, Size size, int dpi, string filename)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi / 96),
                (int)Math.Ceiling(size.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, filename)); encoder.Save(stream);
            captures.Add(filename);
        }
    }

    private static void Arrange(FrameworkElement surface, Size size)
    {
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
        surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    {
        yield return parent;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
