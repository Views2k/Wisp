using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;

namespace Wisp.UiReview;

internal static class FeatureTourReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources,
        Func<Window, object?, FrameworkElement> detachSurface, Action<FrameworkElement, int> setDpi)
    {
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        var captures = new List<object>();
        var failures = new List<string>();
        var previousSynchronization = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        var app = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var activationCount = 0;
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            app.Resources = loadResources();
            foreach (var legacy in new[] { false, true })
                foreach (var size in new[] { new Size(720, 440), new Size(1464, 994) })
                    foreach (var dpi in new[] { 96, 144 })
                    {
                        var variant = $"{(legacy ? "legacy" : "modern")}-{size.Width:0}x{size.Height:0}-{dpi}dpi";
                        bindings.Phase = variant;
                        var fixture = Fixture.All.First(item => item.Name == "orbit-reference");
                        var settings = fixture.CreateSettings();
                        settings.UseLegacyInterface = legacy;
                        settings.StartWithWindows = false;
                        settings.StartWithForza = false;
                        settings.AutomaticApplicationUpdateChecks = false;
                        settings.DebugLoggingEnabled = false;
                        settings.AnimatedBackground = false;
                        settings.BackgroundParticlesEnabled = false;
                        settings.CompletedFeatureTourId = null;
                        settings.SetupCompletion = CompletedSetup();
                        if (size.Width < 800) settings.ColorTheme = "Purple";
                        var saved = new SettingsService(Path.Combine(output, variant + "-settings.json"));
                        var failSave = false;
                        var controller = new AppController(settings, value =>
                        {
                            if (failSave) throw new IOException("Synthetic unavailable tour settings.");
                            saved.Save(value);
                        }, new NoStartupRegistration(), runsDirectory: Path.Combine(output, variant + "-runs"));
                        ControlPanelWindow window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
                        window.Activated += (_, _) => activationCount++;
                        var surface = detachSurface(window, controller.ViewModel);
                        // Detached visuals report IsVisible=false even when RenderTargetBitmap draws them.
                        // An input-disabled, never-visible source exercises the real WPF visibility gates.
                        using var presentation = new HwndSource(new HwndSourceParameters("Wisp feature tour review")
                        {
                            WindowStyle = unchecked((int)0x88000000),
                            ExtendedWindowStyle = 0x08000080,
                            Width = (int)Math.Ceiling(size.Width * dpi / 96),
                            Height = (int)Math.Ceiling(size.Height * dpi / 96),
                            PositionX = -20000,
                            PositionY = -20000
                        });
                        presentation.RootVisual = surface;
                        var tabs = Required<TabControl>(window, "RootTabs");
                        var banner = Required<FeatureTourBanner>(window, "FeatureTourWelcomeBanner");
                        var overlay = Required<FeatureTourOverlay>(window, "FeatureTourOverlay");
                        var hero = Required<FrameworkElement>(window, legacy ? "LegacyDashboardHero" : "OrbitInstrument");
                        var status = Required<FrameworkElement>(window, legacy ? "HeaderStatus" : "DashboardConnectionButton");
                        try
                        {
                            fixture.Apply(controller.ViewModel, waiting: false);
                            Arrange();
                            var heroBefore = Bounds(hero);
                            var statusBefore = Bounds(status);
                            window.SetFeatureTourDiscoveryAllowed(true);
                            Arrange();
                            Check(banner.Visibility == Visibility.Visible, "banner-not-offered");
                            Check(heroBefore == Bounds(hero), "banner-shifted-dashboard-telemetry");
                            Check(statusBefore == Bounds(status), "banner-shifted-connection-status");
                            Capture("banner");
                            banner.BringIntoView(); Arrange();
                            Check(Within(banner.TourStartButton) && Within(banner.TourDismissButton), "banner-actions-not-reachable");
                            var originalPreferences = JsonSerializer.Serialize(settings);
                            Click(banner.TourStartButton);
                            var expectedPages = new[] { "Appearance", "Dashboard", "Runs", "Appearance" };
                            for (var step = 0; step < FeatureTourSession.StepCount; step++)
                            {
                                Arrange();
                                Check(window.FeatureTour.IsOpen && window.FeatureTour.StepIndex == step, "step-route-" + step);
                                Check(Equals(((TabItem)tabs.SelectedItem).Header, expectedPages[step]), "page-route-" + step);
                                Check(overlay.TourProgress.Text.StartsWith($"{step + 1} of 4", StringComparison.Ordinal), "progress-" + step);
                                Check(overlay.TourBackButton.IsEnabled == (step > 0), "back-enabled-" + step);
                                Check(Equals(overlay.TourNextButton.Content, step == 3 ? "Finish" : "Next"), "next-label-" + step);
                                Check(KeyboardNavigation.GetTabNavigation(overlay.TourCard) == KeyboardNavigationMode.Cycle, "keyboard-cycle-" + step);
                                Check(Within(overlay.TourCard), "card-outside-viewport-" + step);
                                Check(Within(overlay.TourNextButton) && Within(overlay.TourSkipButton) && Within(overlay.TourBackButton), "step-actions-clipped-" + step);
                                Check(overlay.TourHeading.FontSize >= 18 && overlay.TourDescription.FontSize >= 14, "small-tour-text-" + step);
                                Check(overlay.TourHighlight.Visibility == Visibility.Visible, "missing-target-highlight-" + step);
                                Check(overlay.TourNextButton.Style is not null && overlay.TourSkipButton.Style is not null, "unstyled-action-" + step);
                                Capture("step-" + (step + 1));
                                Check(originalPreferences == JsonSerializer.Serialize(settings), "tour-changed-preferences-" + step);
                                Check(!controller.Runs.IsRecording && !controller.Runs.IsCountingDown, "tour-started-recording-" + step);
                                if (step == 1)
                                {
                                    Click(overlay.TourBackButton); Arrange();
                                    Check(window.FeatureTour.StepIndex == 0 && Equals(((TabItem)tabs.SelectedItem).Header, "Appearance"), "back-page-route");
                                    Click(overlay.TourNextButton); Arrange();
                                }
                                Click(overlay.TourNextButton);
                            }
                            Arrange();
                            Check(!window.FeatureTour.IsOpen && overlay.Visibility == Visibility.Collapsed, "finish-kept-tour-open");
                            Check(saved.Load().CompletedFeatureTourId == FeatureTourSession.CurrentTourId, "finish-not-persisted");
                            Check(banner.Visibility == Visibility.Collapsed, "finish-kept-banner");

                            Click(Required<Button>(window, "ReplayFeatureTourButton")); Arrange();
                            Check(window.FeatureTour.IsOpen && window.FeatureTour.StepIndex == 0, "replay-route");
                            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, presentation, 0, Key.Escape)
                            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                            window.RaiseEvent(escape); Arrange();
                            Check(escape.Handled && !window.FeatureTour.IsOpen, "escape-not-dismissed");

                            window.StartFeatureTour(); Arrange();
                            Click(overlay.TourSkipButton); Arrange();
                            Check(!window.FeatureTour.IsOpen && settings.CompletedFeatureTourId == FeatureTourSession.CurrentTourId, "skip-not-dismissed");
                            window.StartFeatureTour(); Arrange();
                            tabs.SelectedItem = Required<TabItem>(window, "DiagnosticsTab"); Arrange();
                            Check(!window.FeatureTour.IsOpen && overlay.Visibility == Visibility.Collapsed, "manual-navigation-kept-tour");

                            window.StartFeatureTour(); Arrange();
                            window.SetFeatureTourDiscoveryAllowed(false); Arrange();
                            Check(!window.FeatureTour.IsOpen && banner.Visibility == Visibility.Collapsed, "background-launch-kept-discovery");
                            window.SetFeatureTourDiscoveryAllowed(true);
                            window.StartFeatureTour(); Arrange();
                            var modal = Required<Grid>(window, "HudProfileDialog");
                            modal.Visibility = Visibility.Visible; Arrange();
                            Check(!window.FeatureTour.IsOpen, "modal-kept-tour");
                            window.CloseFeatureTour();
                            window.StartFeatureTour();
                            Check(!window.FeatureTour.IsOpen, "tour-opened-over-modal");
                            modal.Visibility = Visibility.Collapsed;
                            if (window is MainWindow modern)
                            {
                                window.StartFeatureTour(); Arrange();
                                modern.SetDashboardDisplayMode(true); Arrange();
                                Check(!window.FeatureTour.IsOpen && banner.Visibility == Visibility.Collapsed, "display-mode-kept-tour");
                                window.StartFeatureTour(); Check(!window.FeatureTour.IsOpen, "display-mode-opened-tour");
                                modern.SetDashboardDisplayMode(false); Arrange();
                            }

                            settings.CompletedFeatureTourId = null;
                            window.RefreshFeatureTour();
                            failSave = true;
                            window.StartFeatureTour(); Arrange();
                            Click(overlay.TourSkipButton); Arrange();
                            Check(!window.FeatureTour.IsOpen && window.FeatureTour.HasPendingReceipt, "save-failure-not-recoverable");
                            Check(settings.CompletedFeatureTourId is null, "save-failure-false-receipt");
                            Check(banner.TourSaveFeedback.Visibility == Visibility.Visible, "save-failure-no-feedback");
                            banner.BringIntoView(); Arrange(); Capture("save-failure");
                            Check(Within(banner.TourRetryButton), "retry-not-reachable");
                            failSave = false;
                            Click(banner.TourRetryButton); Arrange();
                            Check(!window.FeatureTour.HasPendingReceipt && saved.Load().CompletedFeatureTourId == FeatureTourSession.CurrentTourId, "retry-not-persisted");
                            Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "review-created-app-window");
                            Check(ReferenceEquals(PresentationSource.FromVisual(surface), presentation) && !IsWindowVisible(presentation.Handle), "review-shown-presentation-source");
                            Check(GetForegroundWindow() != presentation.Handle, "review-activated-presentation-source");
                        }
                        finally
                        {
                            window.CloseFeatureTour();
                            presentation.RootVisual = null;
                            window.Close();
                            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }

                        void Arrange()
                        {
                            for (var pass = 0; pass < 2; pass++)
                            {
                                setDpi(surface, dpi);
                                surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
                                var frame = new DispatcherFrame();
                                surface.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                                Dispatcher.PushFrame(frame);
                            }
                            // The hidden HWND's monitor DPI is not the requested screenshot DPI.
                            // Reapply the explicit review viewport after its queued layout settles.
                            setDpi(surface, dpi);
                            surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
                            overlay.Reposition();
                            surface.UpdateLayout();
                        }
                        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
                        bool Within(FrameworkElement element)
                        {
                            var bounds = Bounds(element);
                            return element.Visibility == Visibility.Visible && bounds.Width > 0 && bounds.Height > 0 &&
                                bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= size.Width + .5 && bounds.Bottom <= size.Height + .5 &&
                                VisibleAncestorBounds(element).All(clip => clip.Contains(bounds.TopLeft) && clip.Contains(bounds.BottomRight));
                        }
                        IEnumerable<Rect> VisibleAncestorBounds(FrameworkElement element)
                        {
                            for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is FrameworkElement ancestor; parent = VisualTreeHelper.GetParent(parent))
                            {
                                if (ancestor.ClipToBounds) yield return Bounds(ancestor);
                                if (ReferenceEquals(ancestor, surface)) yield break;
                            }
                        }
                        void Capture(string phase)
                        {
                            bindings.Phase = variant + "/" + phase;
                            var fileName = variant + "-" + phase + ".png";
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi / 96), (int)Math.Ceiling(size.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                            bitmap.Render(surface);
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var file = new FileStream(Path.Combine(output, fileName), FileMode.CreateNew); encoder.Save(file);
                            captures.Add(new
                            {
                                fileName,
                                legacy,
                                dpi,
                                viewportWidth = size.Width,
                                viewportHeight = size.Height,
                                phase,
                                step = window.FeatureTour.StepIndex,
                                card = overlay.Visibility == Visibility.Visible ? Bounds(overlay.TourCard).ToString(CultureInfo.InvariantCulture) : null
                            });
                        }
                        void Check(bool condition, string failure) { if (!condition) failures.Add(variant + "/" + failure); }
                    }
        }
        catch (Exception error) { failures.Add("exception/" + error.GetType().Name + "/" + error.Message); }
        finally
        {
            app.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousSynchronization);
        }
        if (bindings.TotalCount > 0) failures.Add("binding-errors");
        if (activationCount != 0) failures.Add("window-activated");
        File.WriteAllText(Path.Combine(output, "feature-tour-review.json"), JsonSerializer.Serialize(new
        {
            method = "Actual tour handlers, authored WPF controls, modern/legacy shells and settings serializer. The app window is detached; a hidden, input-disabled, nonactivating HwndSource supplies real WPF IsVisible semantics. Software PNG captures use synthetic telemetry and output-local storage. No displayed windows, OS input, running game or live performance claims.",
            captures,
            failures,
            ownWindowActivations = activationCount,
            bindingDiagnosticCount = bindings.TotalCount,
            bindingDiagnostics = bindings.Messages
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Feature tour: {captures.Count} detached captures; {failures.Count} failures; {bindings.TotalCount} binding diagnostics.");
        return failures.Count == 0 ? 0 : 2;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Required<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T ?? throw new InvalidOperationException("Missing tour review control: " + name);
    private static SetupCompletionRecord CompletedSetup() => new()
    {
        Version = SetupCompletionRecord.CurrentVersion,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        ValidatedUdpPort = 5500,
        ValidatedPackets = SetupCompletionRecord.MinimumPackets,
        MovingPackets = SetupCompletionRecord.MinimumMovingPackets,
        ValidatedElapsedMilliseconds = SetupCompletionRecord.MinimumElapsedMilliseconds,
        DataOutConfirmed = true,
        DisplayModeConfirmed = true,
        StockHudConfirmed = true
    };
    private sealed class ResourceApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
    private sealed class NoStartupRegistration : IStartupRegistrationService { public void Apply(bool startWithWindows, bool startWithForza) { } }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
