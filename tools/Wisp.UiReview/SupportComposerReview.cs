using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App;
using Wisp.App.Supplementary;

namespace Wisp.UiReview;

internal static class SupportComposerReview
{
    internal static int RunChat(string output, Func<ResourceDictionary> resources)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = resources() };
        try
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var now = DateTimeOffset.UtcNow;
            foreach (var legacy in new[] { false, true })
            {
                var settings = Fixture.All.First(f => f.Name == "orbit-reference").CreateSettings();
                settings.BackgroundParticlesEnabled = settings.AnimatedBackground = false;
                var updateChecks = 0;
                var controller = new AppController(settings, _ => { }, new NoStartupRegistration(), runsDirectory: Path.Combine(output, "runs"),
                    checkForApplicationUpdate: (_, _) => { updateChecks++; return Task.FromResult<Wisp.Update.UpdateRelease?>(null); });
                ControlPanelWindow window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
                try
                {
                    CheckUpdateBanner(window, controller, () => updateChecks);
                    foreach (var width in new[] { 360, 480, 640 })
                    {
                        var submitted = new List<Guid>(); var fail = true;
                        var view = new SupportChatView((title, text, diagnostics) => Task.FromResult<SupplementarySupportReport?>(new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now, "2.6.11", "fixture", "stable", "feedback", title, text)),
                            (report, _) => { submitted.Add(report.EventId); return Task.FromResult(new SupplementarySupportResult(fail ? SupplementaryRequestStatus.Unavailable : SupplementaryRequestStatus.Success)); },
                            (_, _) => Task.CompletedTask);
                        var surface = view.CreatePopupSurface(() => { }, () => { });
                        surface.Resources = window.Resources;
                        surface.SetValue(Control.FontFamilyProperty, window.FontFamily);
                        surface.SetValue(Control.FontSizeProperty, 14d);
                        view.Update(new(1, now, [
                            new(Guid.NewGuid(), "WSP-" + new string('A', 32), "player", "message", "Chat", "The recorder paused after I switched windows. Can you take a look?", now.AddMinutes(-6), now.AddDays(29), null, false),
                    new(Guid.NewGuid(), "WSP-" + new string('A', 32), "views", "reply", "Chat", "Thanks for letting me know. Does it happen when you switch back to Forza, or while the other window is open?", now.AddMinutes(-4), now.AddDays(29), now, false),
                    new(Guid.NewGuid(), "WSP-" + new string('A', 32), "player", "message", "Chat", "When I switch back. I've attached the recent diagnostics.", now.AddMinutes(-2), now.AddDays(29), null, true),
                    new(Guid.NewGuid(), "WSP-" + new string('A', 32), "views", "reply", "Chat", "Got it. I'll check those and reply here.", now.AddMinutes(-1), now.AddDays(29), null, false)
                        ]), "", true);
                        Capture("thread");
                        view.Composer.Text = "Thanks!";
                        view.Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Capture("retry");
                        if (submitted.Count != 1 || view.Composer.Text != "Thanks!" || !view.Composer.IsReadOnly) throw new InvalidOperationException("Uncertain delivery discarded the message.");
                        fail = false; view.Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (submitted.Count != 2 || submitted[0] != submitted[1] || view.Composer.Text.Length != 0) throw new InvalidOperationException("Retry changed message identity.");
                        view.Composer.Text = "Here are the diagnostics."; view.Diagnostics.IsChecked = true;
                        view.Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (submitted.Count != 2) throw new InvalidOperationException("Diagnostics were sent before review.");
                        Capture("review");
                        view.Update(new(1, now, []), "", false);
                        if (view.Send.IsEnabled) throw new InvalidOperationException("Paused messaging can send.");
                        void Capture(string state)
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(PowerTorqueShaderCapture.RenderSurface(surface, new Size(width, 640), 96)));
                            using var file = File.Create(Path.Combine(output, $"{(legacy ? "legacy-" : "")}chat-{width}-{state}.png")); encoder.Save(file);
                        }
                    }
                }
                finally { window.Close(); controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            return 0;
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(output, "chat-failure.txt"), e.ToString()); return 1; }
        finally { application.Shutdown(); }
    }

    private static void CheckUpdateBanner(ControlPanelWindow window, AppController controller, Func<int> updateChecks)
    {
        var now = DateTimeOffset.UtcNow;
        var audience = new SupplementaryAudienceContext("2.6.11", "stable", "steam");
        var item = new SupplementaryContentItem("banner", "dashboard-banner", "A custom title", "A custom message", null,
            "info", now.AddMinutes(-1), now.AddMinutes(10), new(null, null, ["stable"], ["steam"], []), true);
        var content = new SupplementaryContentSnapshot(1, now, now.AddMinutes(20), false, [item]);
        var surface = (FrameworkElement)window.Content;
        var banner = (FrameworkElement)window.FindName("DashboardUpdateBanner");
        var message = (TextBlock)window.FindName("DashboardUpdateMessage");
        var action = (Button)window.FindName("DashboardUpdateAction");
        var close = (Button)window.FindName("DashboardBannerClose");
        void SetUpdate(bool available, bool enabled = true)
        {
            controller.ViewModel.UpdateApplicationUpdateStatus("Available version fixture", "Update fixture", enabled, available);
            Layout();
        }
        void Layout()
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            surface.Measure(new Size(950, 700)); surface.Arrange(new Rect(0, 0, 950, 700)); surface.UpdateLayout();
        }
        void Check(bool condition, string failure)
        {
            if (!condition) throw new InvalidOperationException(window.GetType().Name + ": " + failure);
        }
        void CheckUpdate()
        {
            Layout();
            Check(banner.Visibility == Visibility.Visible && action.Visibility == Visibility.Visible && action.IsEnabled,
                "The update action is not available.");
            Check(message.Text == controller.ViewModel.ApplicationUpdateStatus && BindingOperations.IsDataBound(message, TextBlock.TextProperty),
                "Update text lost its binding.");
            Check((string)action.Content == controller.ViewModel.ApplicationUpdateAction && close.Visibility == Visibility.Collapsed,
                "The action or custom dismissal replaced the update.");
        }

        SetUpdate(false);
        window.ApplySupplementaryContentSurfaces(content, audience, now);
        Check(message.Text == item.Message && action.Visibility == Visibility.Collapsed, "Custom content was not selected.");
        SetUpdate(true); CheckUpdate();
        window.ApplySupplementaryContentSurfaces(content with { Revision = 2 }, audience, now); CheckUpdate();
        SetUpdate(true, false);
        Check(!action.IsEnabled, "Updater busy/paused safeguard was overridden.");
        SetUpdate(true); CheckUpdate();
        var previousObserver = SupplementaryObservations.Observer;
        var observations = new List<SupplementaryObservation>();
        SupplementaryObservations.Observer = observations.Add;
        try { action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        finally { SupplementaryObservations.Observer = previousObserver; }
        Check(observations.Count(value => value.Kind == "update" && value.Stage == "chosen" && value.Outcome == "attempt") == 1,
            "The Update click was not recorded separately from detection and presentation.");
        Check(updateChecks() == 1, "The visible Update action did not reach the updater.");
        SetUpdate(false);
        Check(message.Text == item.Message && close.Visibility == Visibility.Visible, "Clearing the update did not restore the custom banner.");
        SetUpdate(true);
        controller.Settings.DismissedDashboardBannerKey = DashboardBannerDismissal.Key(item);
        SetUpdate(false);
        Check(message.Text != item.Message && close.Visibility == Visibility.Collapsed, "Dismissed content reappeared.");
        controller.Settings.DismissedDashboardBannerKey = null;
        SetUpdate(true);
        window.ApplySupplementaryContentSurfaces(content with { Items = [item with { ExpiresAt = now.AddSeconds(-1) }] }, audience, now);
        CheckUpdate(); SetUpdate(false);
        Check(message.Text != item.Message, "Expired content reappeared.");
        window.ApplySupplementaryContentSurfaces(content, audience, now);
        SetUpdate(true);
        window.ApplySupplementaryContentSurfaces(content with { ExpiresAt = now.AddSeconds(-1) }, audience, now);
        CheckUpdate(); SetUpdate(false);
        Check(message.Text != item.Message, "An expired signed envelope reappeared.");
    }
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
