using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App;

namespace Wisp.UiReview;

internal static class SupportComposerReview
{
    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    // Uses the production composer and theme without constructing App or starting services.
    // Inbox delivery and consent-to-send transport are covered separately by contract tests.
    internal static int Run(string output, Func<ResourceDictionary> resources,
        Func<Window, object?, FrameworkElement> detach)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = resources() };
        try
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var create = typeof(ControlPanelWindow).GetMethod("CreateSupplementarySupportForm", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("Support composer was not found.");
            foreach (var legacy in new[] { false, true })
                foreach (var messaging in new[] { false, true })
                    foreach (var width in new[] { 720, 950 })
                    {
                        var settings = Fixture.All.First(f => f.Name == "orbit-reference").CreateSettings();
                        settings.BackgroundParticlesEnabled = settings.AnimatedBackground = false;
                        var controller = new AppController(settings, _ => { }, new NoStartupRegistration(), runsDirectory: Path.Combine(output, "runs"));
                        Window window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
                        try
                        {
                            _ = detach(window, controller.ViewModel);
                            var composer = (Expander)create.Invoke(window, [messaging])!;
                            composer.IsExpanded = true;
                            var surface = new Border { Padding = new Thickness(24), Child = composer, Resources = window.Resources };
                            surface.SetResourceReference(Border.BackgroundProperty, "WindowBrush");
                            surface.SetValue(Control.FontFamilyProperty, window.FontFamily);
                            surface.SetValue(Control.FontSizeProperty, window.FontSize);
                            var controls = Descendants(composer).OfType<CheckBox>().ToArray();
                            var diagnostics = controls.Single(c => c.Content is string text && text.StartsWith("Include ", StringComparison.Ordinal));
                            var confirmation = controls.Single(c => c.Content is string text && text.StartsWith("I reviewed", StringComparison.Ordinal));
                            var recentAttachment = diagnostics.Content is string label && label.Contains("20 minutes", StringComparison.Ordinal);
                            if (confirmation.IsChecked == true || recentAttachment != messaging || diagnostics.IsChecked != !messaging)
                                throw new InvalidOperationException("Recent attachments require activation and separate consent; ordinary report details remain available.");
                            var prefix = (legacy ? "legacy-" : "") + (messaging ? "enabled-" : "disabled-") + width + "-";
                            Save(surface, Path.Combine(output, prefix + "support-composer.png"), width);
                            if (diagnostics.Style is null || confirmation.Style is null)
                                throw new InvalidOperationException("Support consent controls have no theme style.");
                            diagnostics.SetCurrentValue(CheckBox.IsCheckedProperty, true);
                            if (confirmation.IsChecked == true)
                                throw new InvalidOperationException("Attachment consent must not grant sending consent.");
                            Save(surface, Path.Combine(output, prefix + "support-diagnostics-selected.png"), width);
                            diagnostics.SetCurrentValue(CheckBox.IsCheckedProperty, false);
                        }
                        finally
                        {
                            window.Close();
                            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                    }
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "support-composer-failure.txt"), error.ToString());
            return 1;
        }
        finally { application.Shutdown(); }
    }

    private static void Save(FrameworkElement surface, string path, int width)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(PowerTorqueShaderCapture.RenderSurface(surface, new Size(width, 900), 96)));
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
