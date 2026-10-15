using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;

namespace Wisp.UiReview;

internal static class BoostScaleReview
{
    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    internal static int Run(string output, Func<ResourceDictionary> resources,
        Func<Window, object?, FrameworkElement> detach)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = resources() };
        try
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            foreach (var legacy in new[] { false, true })
            {
                var settings = Fixture.All.First(f => f.Name == "orbit-reference").CreateSettings();
                settings.BoostGaugeEnabled = true;
                settings.BackgroundParticlesEnabled = settings.AnimatedBackground = false;
                var controller = new AppController(settings, _ => { }, new NoStartupRegistration(),
                    runsDirectory: Path.Combine(output, "runs"));
                Window window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
                var page = detach(window, controller.ViewModel);
                ((TabControl)window.FindName("RootTabs")).SelectedItem = window.FindName("AppearanceTab");
                page.Measure(new Size(1464, 994));
                page.Arrange(new Rect(0, 0, 1464, 994));
                page.UpdateLayout();
                var slider = Descendants(page).OfType<Slider>().Single(c => AutomationProperties.GetName(c) == "Maximum boost in PSI");
                for (DependencyObject? ancestor = slider; ancestor is not null; ancestor = LogicalTreeHelper.GetParent(ancestor))
                {
                    if (ancestor is TabItem tab) tab.IsSelected = true;
                    if (ancestor is Expander expander) expander.IsExpanded = true;
                }
                foreach (var value in new[] { 20d, 70d, 200d })
                {
                    slider.SetCurrentValue(Slider.ValueProperty, value);
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    if (controller.ViewModel.BoostMaximumPsi != value || controller.ViewModel.PreviewBoostDisplay.ScaleMaximumPsi != value)
                        throw new InvalidOperationException("Boost slider did not update the gauge scale.");
                }
                Save((FrameworkElement)slider.Parent, new Size(390, 600), Path.Combine(output, legacy ? "legacy-boost-card.png" : "boost-card.png"));
                Save(page, new Size(1464, 994), Path.Combine(output, legacy ? "legacy-appearance.png" : "appearance.png"));
                window.Close();
                controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            var sheet = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(24, 25, 28)) };
            foreach (var unit in new[] { BoostPressureUnit.Psi, BoostPressureUnit.Bar })
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var maximum in new[] { 20d, 70d, 200d })
                {
                    var cell = new StackPanel { Width = 300, Margin = new Thickness(12) };
                    cell.Children.Add(new TextBlock { Text = $"{maximum:0} PSI maximum / {unit}", Foreground = Brushes.White, FontSize = 18, Margin = new Thickness(8) });
                    cell.Children.Add(new AnalogBoostGaugeView
                    {
                        Width = 270,
                        Height = 270,
                        PressureUnit = unit,
                        Display = new BoostDisplay(true, maximum, maximum, 1, maximum, -20)
                    });
                    row.Children.Add(cell);
                }
                sheet.Children.Add(row);
            }
            Save(sheet, new Size(972, 640), Path.Combine(output, "boost-scales.png"));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
            return 1;
        }
        finally { app.Shutdown(); }
    }

    private static void Save(FrameworkElement surface, Size size, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(PowerTorqueShaderCapture.RenderSurface(surface, size, 96)));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
