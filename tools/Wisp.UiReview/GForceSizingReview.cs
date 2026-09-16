using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;

namespace Wisp.UiReview;

internal static class GForceSizingReview
{
    internal static void Run(string output, Action<FrameworkElement, int> setDpi,
        List<string> captures, List<string> failures)
    {
        foreach (var legacy in new[] { false, true })
            foreach (var dpi in new[] { 96, 144 })
            {
                var name = $"gforce-sizing-{(legacy ? "legacy" : "modern")}-{dpi}dpi";
                var settings = Fixture.All.First(item => item.Name == "orbit-reference").CreateSettings();
                settings.UseLegacyInterface = legacy;
                settings.GForceEnabled = settings.GForceAttached = true;
                settings.GForceGaugeScale = 1;
                settings.NativeGaugeMode = NativeGaugeMode.Analogue;
                settings.BackgroundParticlesEnabled = settings.AnimatedBackground = false;
                settings.AutomaticApplicationUpdateChecks = false;
                settings.StartWithWindows = settings.StartWithForza = false;
                var controller = new AppController(settings, _ => { }, new NoStartupRegistration(),
                    runsDirectory: Path.Combine(output, name + "-runs"));
                ControlPanelWindow window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
                try
                {
                    var card = (Border)window.FindName("GForceSettingsCard");
                    var slider = (Slider)window.FindName("GForceGaugeScaleSlider");
                    ((Panel)card.Parent).Children.Remove(card);
                    card.DataContext = controller.ViewModel;
                    card.Resources.MergedDictionaries.Add(window.Resources);
                    var surface = new Border
                    {
                        Width = 540,
                        Padding = new Thickness(12),
                        Background = (Brush)window.FindResource("WindowBrush"),
                        Child = card
                    };
                    surface.SetValue(TextElement.ForegroundProperty, window.Foreground);
                    surface.SetValue(TextElement.FontFamilyProperty, window.FontFamily);
                    setDpi(surface, dpi);
                    foreach (var (state, scale, enabled) in new[]
                             {
                             ("default-100", 1d, true), ("minimum-50", .5, true),
                             ("maximum-200", 2d, true), ("disabled", 1d, false)
                         })
                    {
                        controller.ViewModel.GForceGaugeScale = scale;
                        controller.ViewModel.GForceEnabled = enabled;
                        var size = Arrange(surface);
                        if (slider.Value != scale || slider.IsEnabled != enabled)
                            failures.Add(name + "/" + state + "-binding");
                        if (slider.Template is null || slider.Template.FindName("PART_Track", slider) is not Track track ||
                            track.Thumb?.Template is null || track.DecreaseRepeatButton?.Template is null ||
                            track.IncreaseRepeatButton?.Template is null)
                            failures.Add(name + "/" + state + "-theme");
                        var sliderRoot = (FrameworkElement?)slider.Template?.FindName("SliderRoot", slider);
                        if (!enabled && (sliderRoot is null || sliderRoot.Opacity >= 1))
                            failures.Add(name + "/disabled-styling");
                        var bounds = slider.TransformToAncestor(surface).TransformBounds(new Rect(slider.RenderSize));
                        if (!new Rect(size).Contains(bounds)) failures.Add(name + "/slider-clipped");
                        Save(name + "-" + state + ".png", surface, size);
                    }
                    if (AutomationProperties.GetName(slider) != "G-force meter size")
                        failures.Add(name + "/accessible-label");
                    // An offscreen element cannot receive real pointer/keyboard focus.
                    // Check the authored hover trigger and render its actual focus style,
                    // without moving the user's mouse or activating a window.
                    if (!Styles(slider.Style).SelectMany(style => style.Triggers.OfType<Trigger>()).Any(trigger =>
                            trigger.Property == UIElement.IsMouseOverProperty && Equals(trigger.Value, true) &&
                            trigger.Setters.OfType<Setter>().Any(setter => setter.Property == UIElement.OpacityProperty &&
                                setter.Value is double opacity && opacity > 0 && opacity < 1)))
                        failures.Add(name + "/hover-style");
                    if (slider.FocusVisualStyle is null)
                        failures.Add(name + "/focus-style");
                    else
                    {
                        var focus = new Control { Style = slider.FocusVisualStyle, Width = 480, Height = 28 };
                        focus.Resources.MergedDictionaries.Add(window.Resources);
                        var focusSurface = new Border
                        {
                            Width = 500,
                            Padding = new Thickness(10),
                            Background = surface.Background,
                            Child = focus
                        };
                        setDpi(focusSurface, dpi);
                        var focusSize = Arrange(focusSurface);
                        var outline = Descendants(focus).OfType<Border>().FirstOrDefault();
                        if (outline?.BorderBrush is not SolidColorBrush brush || brush.Color.A == 0 ||
                            outline.BorderThickness.Left <= 0)
                            failures.Add(name + "/invisible-focus-outline");
                        Save(name + "-focus-template.png", focusSurface, focusSize);
                    }
                }
                finally
                {
                    window.Close();
                    controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                void Save(string filename, FrameworkElement surface, Size size)
                {
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi / 96d),
                        (int)Math.Ceiling(size.Height * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, filename));
                    encoder.Save(stream);
                    captures.Add(filename);
                }
            }
    }

    private static Size Arrange(FrameworkElement surface)
    {
        surface.Measure(new Size(surface.Width, double.PositiveInfinity));
        var size = new Size(surface.Width, surface.DesiredSize.Height);
        surface.Arrange(new Rect(size));
        surface.UpdateLayout();
        surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.UpdateLayout();
        return size;
    }

    private static IEnumerable<Style> Styles(Style? style)
    {
        for (; style is not null; style = style.BasedOn) yield return style;
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

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
