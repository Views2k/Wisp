using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using Wisp.App;

namespace Wisp.UiReview;

internal static class Program
{
    private static readonly (string Name, int Width, int Height)[] Viewports =
        [("baseline", 980, 750), ("compact", 720, 440), ("wide", 1280, 900), ("fullscreen", 2560, 1440)];
    private static readonly string[] TabNames = ["dashboard", "appearance", "diagnostics", "setup", "extras"];
    private static readonly (string Name, int Width, int Height)[] WizardViewports =
        [("baseline", 800, 730), ("compact", 540, 440), ("wide", 840, 760), ("launch", 900, 780)];
    private static readonly string[] WizardStepNames = ["welcome", "connection", "display", "appearance"];

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--calm-shell-check" && args[1] == "--output")
            {
                var output = PrepareOutput(args[2]);
                return CalmShellReview.Run(output, () => LoadApplicationResources(output, out _));
            }

            var options = Options.Parse(args);
            if (options is null)
            {
                Console.WriteLine("Wisp.UiReview --output <new workspace directory> [--fixture <name>] [--scope matrix|appearance|wizard] [--telemetry sample|waiting] [--dpi 96|144] [--present] [--step welcome|connection|display|appearance] [--scroll-check] [--native-lifetime-check]");
                Console.WriteLine("Fixtures: " + string.Join(", ", Fixture.All.Select(fixture => fixture.Name)));
                Console.WriteLine("Main-window --present requires one --fixture, omits --scope/--dpi, and shows display-only Appearance at monitor DPI with a 120-second auto-close timer.");
                Console.WriteLine("--scope wizard captures all four unconfirmed steps at four sizes and 96/144 DPI by default. --present --scope wizard shows one display-only step; --step selects it. Wizard mode never tests or completes setup.");
                Console.WriteLine("--scroll-check measures bounded offscreen scrolling on all main tabs at 720x440 and 980x750, direct Viewbox versus a temporary Decorator. --scope and --step are rejected; no PNGs are produced.");
                Console.WriteLine("--scroll-check --present automatically scrolls Appearance in an independent input-blocked host: direct then wrapped, 6 measured seconds each after warmup, 20-second auto-close and 30-second process watchdog. Uses monitor DPI; no --dpi. Compositor callback gaps are not GPU present timestamps.");
                Console.WriteLine("--native-lifetime-check requires only --output: four synthetic native controls in an independent nonactivating host, automatic minimize/restore/collapse/resume/close; 8-second close and 10-second watchdog. No controller or captures.");
                Console.WriteLine("--calm-shell-check --output <new workspace directory> validates the loaded sidebar/theme shell with isolated settings; 25-second auto-close and 30-second process watchdog. No live services.");
                return 0;
            }

            return Run(options);
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Invalid arguments or output directory. Use --help; the output must be an empty directory inside the Wisp checkout.");
            return 64;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"UI review could not initialize ({exception.GetType().Name}); no exception paths or values are logged.");
            return 1;
        }
    }

    private static int Run(Options options)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        var output = PrepareOutput(options.Output);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var bindings = new BindingTrace();
        var report = new ReviewReport
        {
            Telemetry = options.WizardOnly ? "not-started" : options.Waiting ? "waiting" : "synthetic-sample",
            PresentationMode = options.Present || options.NativeLifetimeCheck,
            Scope = options.NativeLifetimeCheck ? "native-lifetime-check" : options.ScrollCheck ? "scroll-check" : options.WizardOnly ? "wizard" : options.AppearanceOnly ? "appearance" : "matrix"
        };
        OffscreenApplication? application = null;
        try
        {
            application = new OffscreenApplication();
            application.Resources = LoadApplicationResources(output, out var resourcesHash);
            report.AppResourceSourceSha256 = resourcesHash;
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            report.LogoResource = ReviewDiagnostics.ProbeLogoResource();
            report.Renderer = ReviewDiagnostics.ProbeRenderer();
            using (var assembly = File.OpenRead(typeof(Wisp.App.App).Assembly.Location))
            {
                report.AppAssemblySha256 = Convert.ToHexString(SHA256.HashData(assembly));
            }

            if (options.NativeLifetimeCheck)
            {
                report.NativeLifetimeCheck = new NativeLifetimeCheckReport();
                NativeLifetimeReview.Run(report.NativeLifetimeCheck, bindings, cancellation.Token);
            }
            for (var index = 0; !options.NativeLifetimeCheck && index < options.Fixtures.Length; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (options.WizardOnly)
                {
                    CaptureWizard(options.Fixtures[index], options, output, report, bindings, cancellation.Token);
                }
                else
                {
                    CaptureFixture(options.Fixtures[index], options, output, report, bindings, cancellation.Token,
                        appearanceOnly: options.AppearanceOnly || index > 0, dpi: index > 0 ? 144 : options.Dpi);
                }
            }
        }
        catch (Exception exception)
        {
            report.FatalError = exception.GetType().Name;
            report.FatalInnerError = exception.InnerException?.GetType().Name;
            report.FatalPhase = bindings.Phase;
        }
        finally
        {
            report.SuppressedStartupNotifications = application?.SuppressedStartupNotifications ?? 0;
            try
            {
                application?.Shutdown();
            }
            catch (Exception exception)
            {
                report.FatalError ??= exception.GetType().Name;
                report.FatalPhase ??= "shutdown";
            }
        }

        report.BindingMessages = bindings.Messages;
        report.BindingMessageCount = bindings.TotalCount;
        report.BindingMessagesTruncated = bindings.Truncated;
        using (var destination = new FileStream(Path.Combine(output, "review.json"), FileMode.CreateNew, FileAccess.Write))
        {
            JsonSerializer.Serialize(destination, report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        }

        IEnumerable<CaptureReport> inspections = report.Captures;
        if (report.Presentation?.Surface is { } presentationSurface)
        {
            inspections = inspections.Append(presentationSurface);
        }

        var findings = !report.LogoResource.Found || report.BindingMessageCount > 0 ||
                       options.Present && report.Presentation is not { ContentRendered: true } ||
                       report.ScrollCheck?.HasFindings == true ||
                       report.NativeLifetimeCheck?.HasFindings == true ||
                       inspections.Any(capture => !capture.Logo.Visible || !capture.Logo.Decoded || capture.BindingFailures.Length > 0 ||
                           capture.Labels.OverflowCount > 0 || capture.VisualTreeTruncated || capture.Wizard?.HasFindings == true);
        if (options.NativeLifetimeCheck)
        {
            Console.WriteLine($"Native lifecycle check: {(report.NativeLifetimeCheck?.Completed == true ? "PASS" : "FAIL")}; " +
                $"{report.NativeLifetimeCheck?.Stages.Count ?? 0} stages; {report.BindingMessageCount} binding diagnostics; see review.json. Synthetic lifecycle only, not game performance.");
            return report.FatalError is not null ? 1 : findings ? 2 : 0;
        }
        if (options.ScrollCheck)
        {
            var count = options.Present
                ? report.ScrollCheck?.Presentation?.Variants.Count ?? 0
                : report.ScrollCheck?.Comparisons.Count ?? 0;
            Console.WriteLine($"{count} bounded scroll {(options.Present ? "presentation variants" : "comparisons")}; " +
                $"{report.BindingMessageCount} binding diagnostics; see review.json. " +
                (options.Present ? "Compositor callback timing is not proof of GPU present timing." : "Offscreen layout only, not GPU frame timing."));
            return report.FatalError is not null ? 1 : findings ? 2 : 0;
        }

        var result = options.Present ? "Synthetic presentation closed; no PNGs captured" : $"{report.Captures.Count} offscreen PNGs";
        Console.WriteLine($"{result}; {report.BindingMessageCount} binding diagnostics; " +
            $"{inspections.Sum(capture => capture.Labels.OverflowCount)} text-overflow findings; " +
            $"{inspections.Count(capture => capture.Wizard?.HasFindings == true)} wizard layout/contrast findings. See review.json.");
        return report.FatalError is not null ? 1 : findings ? 2 : 0;
    }

    private static void CaptureFixture(Fixture fixture, Options options, string output,
        ReviewReport report, BindingTrace bindings, CancellationToken cancellationToken, bool appearanceOnly, int dpi)
    {
        var stateDirectory = Path.Combine(output, ".fixture-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        var settingsPath = Path.Combine(stateDirectory, "settings.json");
        AppController? controller = null;
        MainWindow? window = null;
        try
        {
            bindings.Phase = fixture.Name + "/initialize";
            controller = new AppController(fixture.CreateSettings(), new SettingsService(settingsPath));
            fixture.Apply(controller.ViewModel, options.Waiting);
            window = new MainWindow(controller);
            if (!options.Waiting && window.FindName("PreviewCaptionText") is TextBlock previewCaption)
            {
                previewCaption.Text = Fixture.SyntheticPreviewCaption;
            }

            var tabs = window.FindName("RootTabs") as TabControl
                       ?? throw new InvalidOperationException("The control-panel page surface was not found.");
            if (tabs.Items.Count != TabNames.Length)
            {
                throw new InvalidOperationException("The tab count changed; review the capture matrix.");
            }

            var surface = DetachSurface(window, controller.ViewModel);
            if (options.ScrollCheck)
            {
                report.ScrollCheck = new ScrollCheckReport { Fixture = fixture.Name, Dpi = options.Present ? null : dpi };
                if (options.Present)
                    ScrollPresentation.Run(window, surface, tabs, fixture, report, bindings, cancellationToken);
                else
                    ScrollReview.Run(window, surface, tabs, bindings, cancellationToken, dpi, SetOffscreenDpi, report.ScrollCheck);
                return;
            }

            if (options.Present)
            {
                tabs.SelectedIndex = 1;
                PresentSurface(window, surface, fixture, report, bindings);
                return;
            }

            foreach (var viewport in appearanceOnly ? Viewports.Take(1) : Viewports)
                for (var index = 0; index < tabs.Items.Count; index++)
                {
                    if (appearanceOnly && index != 1)
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var fileName = $"{fixture.Name}-{viewport.Name}-{dpi}dpi-{TabNames[index]}.png";
                    bindings.Phase = fileName;
                    var bindingStart = bindings.TotalCount;
                    tabs.SelectedIndex = index;
                    var size = new Size(viewport.Width, viewport.Height);
                    for (var pass = 0; pass < 2; pass++)
                    {
                        SetOffscreenDpi(surface, dpi);
                        surface.Measure(size);
                        surface.Arrange(new Rect(size));
                        surface.UpdateLayout();
                        if (pass == 0 && !options.Waiting)
                        {
                            PopulateGForceTrails(surface);
                        }
                        surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle,
                            cancellationToken, TimeSpan.FromSeconds(2));
                    }

                    // Neither querying an existing handle nor rendering a detached Visual creates a Wisp window.
                    if (new WindowInteropHelper(window).Handle != IntPtr.Zero ||
                        PresentationSource.FromVisual(surface) is not null)
                    {
                        throw new InvalidOperationException("Offscreen isolation was lost.");
                    }

                    var pixelWidth = checked((int)Math.Ceiling(viewport.Width * dpi / 96d));
                    var pixelHeight = checked((int)Math.Ceiling(viewport.Height * dpi / 96d));
                    var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var destination = new FileStream(Path.Combine(output, fileName), FileMode.CreateNew, FileAccess.Write))
                    {
                        encoder.Save(destination);
                    }

                    report.Captures.Add(ReviewDiagnostics.Inspect(surface, fileName, fixture.Name, TabNames[index],
                        viewport.Name, dpi, pixelWidth, pixelHeight, bindings.TotalCount - bindingStart));
                }
        }
        finally
        {
            try
            {
                window?.Close();
            }
            finally
            {
                try
                {
                    controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    // Only the two filenames owned by the isolated SettingsService are eligible for cleanup.
                    foreach (var path in new[] { settingsPath, settingsPath + ".tmp" })
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }

                    Directory.Delete(stateDirectory, recursive: false);
                }
            }
        }
    }

    private static void PopulateGForceTrails(DependencyObject root)
    {
        Point[] samples =
        [
            new(-24, 10),
            new(-18, 5),
            new(-10, -2),
            new(-2, -8),
            new(8, -13),
            new(17, -8),
            new(23, 2)
        ];
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            if (current is GForceTrailView trail)
            {
                trail.SetCurrentValue(GForceTrailView.IsActiveProperty, true);
                foreach (var sample in samples)
                {
                    trail.SetCurrentValue(GForceTrailView.PositionProperty, sample);
                }
            }

            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }
    }

    private static void CaptureWizard(Fixture fixture, Options options, string output,
        ReviewReport report, BindingTrace bindings, CancellationToken cancellationToken)
    {
        var stateDirectory = Path.Combine(output, ".fixture-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        var settingsPath = Path.Combine(stateDirectory, "settings.json");
        AppController? controller = null;
        SetupWindow? window = null;
        try
        {
            bindings.Phase = "wizard/" + fixture.Name + "/initialize";
            controller = new AppController(fixture.CreateSettings(), new SettingsService(settingsPath));
            // Do not call Fixture.Apply: wizard review has no telemetry, confirmed
            // settings, or completion evidence, even in its display-only last step.
            window = new SetupWindow(controller);
            var surface = DetachSurface(window, window.DataContext);
            surface.SetCurrentValue(TextElement.FontSizeProperty, window.FontSize);
            surface.SetCurrentValue(TextElement.FontStyleProperty, window.FontStyle);
            surface.SetCurrentValue(TextElement.FontWeightProperty, window.FontWeight);
            surface.SetCurrentValue(TextElement.FontStretchProperty, window.FontStretch);
            if (options.Present)
            {
                var step = options.WizardStep ?? 0;
                SelectWizardStep(window, controller, step);
                PresentSurface(window, surface, fixture, report, bindings, "wizard-" + WizardStepNames[step],
                    new WizardReviewContext(window, controller, step));
                return;
            }

            foreach (var dpi in options.WizardDpis)
                foreach (var viewport in WizardViewports)
                    foreach (var step in options.WizardStep is { } selected ? new[] { selected } : Enumerable.Range(0, WizardStepNames.Length))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var fileName = $"wizard-{fixture.Name}-{viewport.Name}-{dpi}dpi-{WizardStepNames[step]}.png";
                        bindings.Phase = fileName;
                        var bindingStart = bindings.TotalCount;
                        SelectWizardStep(window, controller, step);
                        var size = new Size(viewport.Width, viewport.Height);
                        for (var pass = 0; pass < 2; pass++)
                        {
                            SetOffscreenDpi(surface, dpi);
                            surface.Measure(size);
                            surface.Arrange(new Rect(size));
                            surface.UpdateLayout();
                            surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle,
                                cancellationToken, TimeSpan.FromSeconds(2));
                        }

                        if (new WindowInteropHelper(window).Handle != IntPtr.Zero ||
                            PresentationSource.FromVisual(surface) is not null ||
                            !controller.Settings.RequiresSetup || controller.SetupTelemetry.IsRunning ||
                            controller.SetupTelemetry.SuccessfulEvidence is not null)
                        {
                            throw new InvalidOperationException("Wizard offscreen isolation was lost.");
                        }

                        var pixelWidth = checked((int)Math.Ceiling(viewport.Width * dpi / 96d));
                        var pixelHeight = checked((int)Math.Ceiling(viewport.Height * dpi / 96d));
                        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
                        bitmap.Render(surface);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var destination = new FileStream(Path.Combine(output, fileName), FileMode.CreateNew, FileAccess.Write))
                        {
                            encoder.Save(destination);
                        }

                        report.Captures.Add(ReviewDiagnostics.Inspect(surface, fileName, fixture.Name, "wizard-" + WizardStepNames[step],
                            viewport.Name, dpi, pixelWidth, pixelHeight, bindings.TotalCount - bindingStart,
                            new WizardReviewContext(window, controller, step)));
                    }
        }
        finally
        {
            try
            {
                window?.Close();
            }
            finally
            {
                try
                {
                    controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    foreach (var path in new[] { settingsPath, settingsPath + ".tmp" })
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }

                    Directory.Delete(stateDirectory, recursive: false);
                }
            }
        }
    }

    private static void SelectWizardStep(SetupWindow window, AppController controller, int step)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var stepField = typeof(SetupWindow).GetField("_step", flags);
        var update = typeof(SetupWindow).GetMethod("UpdateStep", flags, null, Type.EmptyTypes, null);
        if (step is < 0 or >= 4 || stepField is null || update is null ||
            stepField.FieldType != typeof(int) || update.ReturnType != typeof(void) ||
            !controller.Settings.RequiresSetup || controller.SetupTelemetry.IsRunning ||
            controller.SetupTelemetry.SuccessfulEvidence is not null)
        {
            throw new InvalidOperationException("The isolated wizard review contract changed.");
        }

        // Tool-only display selection: do not invoke Next/Test/Finish handlers,
        // write completion fields, or change any confirmation checkbox.
        stepField.SetValue(window, step);
        update.Invoke(window, null);
    }

    private static void PresentSurface(Window sourceWindow, FrameworkElement surface, Fixture fixture,
        ReviewReport report, BindingTrace bindings, string tab = "appearance", WizardReviewContext? wizard = null)
    {
        var presentation = new PresentationReport();
        report.Presentation = presentation;
        bindings.Phase = wizard is null ? fixture.Name + "/presentation" : fixture.Name + "/" + tab + "/presentation";
        var bindingStart = bindings.TotalCount;
        var frame = new DispatcherFrame();
        var host = new Window
        {
            Title = presentation.Title,
            Width = wizard is null ? 980 : sourceWindow.Width,
            Height = wizard is null ? 750 : sourceWindow.Height,
            MinWidth = wizard is null ? 720 : sourceWindow.MinWidth,
            MinHeight = wizard is null ? 440 : sourceWindow.MinHeight,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = sourceWindow.Background,
            FontFamily = sourceWindow.FontFamily,
            FontSize = sourceWindow.FontSize,
            FontStyle = sourceWindow.FontStyle,
            FontWeight = sourceWindow.FontWeight,
            FontStretch = sourceWindow.FontStretch,
            Foreground = sourceWindow.Foreground,
            FlowDirection = sourceWindow.FlowDirection,
            Content = surface
        };
        surface.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.None);
        KeyboardNavigation.SetControlTabNavigation(surface, KeyboardNavigationMode.None);
        host.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            if (e.Key == Key.Escape)
            {
                host.Close();
            }
        };
        host.PreviewKeyUp += (_, e) => e.Handled = true;
        host.PreviewTextInput += (_, e) => e.Handled = true;
        host.Closed += (_, _) => frame.Continue = false;
        var timer = new DispatcherTimer(DispatcherPriority.Send, host.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(presentation.AutoCloseSeconds)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            presentation.AutoClosed = true;
            host.Close();
        };
        host.ContentRendered += (_, _) =>
        {
            if (presentation.ContentRendered)
            {
                return;
            }

            try
            {
                if (new WindowInteropHelper(sourceWindow).Handle != IntPtr.Zero ||
                    PresentationSource.FromVisual(surface) is not HwndSource source)
                {
                    throw new InvalidOperationException("The independent presentation host was not established.");
                }

                var dpi = VisualTreeHelper.GetDpi(surface);
                presentation.HwndTargetRenderMode = source.CompositionTarget?.RenderMode.ToString();
                presentation.Surface = ReviewDiagnostics.Inspect(surface, null, fixture.Name, tab,
                    "presentation-baseline", (int)Math.Round(dpi.PixelsPerInchX),
                    checked((int)Math.Ceiling(surface.ActualWidth * dpi.DpiScaleX)),
                    checked((int)Math.Ceiling(surface.ActualHeight * dpi.DpiScaleY)), bindings.TotalCount - bindingStart, wizard);
                presentation.ContentRendered = true;
                var renderer = ReviewDiagnostics.ProbeRenderer();
                report.Renderer = renderer;
                Console.WriteLine($"Presentation ready; {presentation.AutoCloseSeconds}-second auto-close timer active. " +
                    $"WPF tier {renderer.Tier}; PS3 hardware support {renderer.PixelShader3HardwareSupported}; " +
                    $"software PS3 support {renderer.PixelShader3SoftwareSupported}; " +
                    $"process render mode {renderer.ProcessRenderMode}; target {presentation.HwndTargetRenderMode}. " +
                    "Capability is not proof of GPU parity; use an external OS capture for visual review.");
            }
            catch (Exception exception)
            {
                report.FatalError = exception.GetType().Name;
                report.FatalInnerError = exception.InnerException?.GetType().Name;
                report.FatalPhase = bindings.Phase;
                host.Close();
            }
        };
        var elapsed = Stopwatch.StartNew();
        try
        {
            timer.Start();
            host.Show();
            presentation.Shown = true;
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
            host.Close();
            presentation.DurationSeconds = Math.Round(elapsed.Elapsed.TotalSeconds, 3);
        }
    }

    private static void SetOffscreenDpi(FrameworkElement surface, int dpi)
    {
        VisualTreeHelper.SetRootDpi(surface, new DpiScale(dpi / 96d, dpi / 96d));

        // SetRootDpi changes visual flags, but unlike HwndTarget's window-DPI
        // path it does not invalidate descendant measurement. TextBlock can
        // otherwise reuse old-DPI line widths even after another Measure call.
        // Run before each bounded pass to include newly materialized templates.
        const int maximumVisualNodes = 4096;
        var pending = new Queue<DependencyObject>();
        var elements = new List<UIElement>();
        pending.Enqueue(surface);
        var visited = 0;
        while (pending.Count > 0)
        {
            if (++visited > maximumVisualNodes)
            {
                throw new InvalidOperationException("The offscreen DPI tree exceeds the review limit.");
            }

            var current = pending.Dequeue();
            if (current is UIElement element)
            {
                elements.Add(element);
            }

            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                if (visited + pending.Count >= maximumVisualNodes)
                {
                    throw new InvalidOperationException("The offscreen DPI tree exceeds the review limit.");
                }

                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }

        // Include collapsed steps and invalidate descendants before containers.
        for (var index = elements.Count - 1; index >= 0; index--)
        {
            elements[index].InvalidateMeasure();
        }
    }

    private static FrameworkElement DetachSurface(Window window, object? dataContext)
    {
        var surface = window.Content as FrameworkElement
                      ?? throw new InvalidOperationException("The source window content is not a FrameworkElement.");
        var names = NameScope.GetNameScope(window);
        var font = (FontFamily)surface.GetValue(TextElement.FontFamilyProperty);
        var foreground = (Brush)surface.GetValue(TextElement.ForegroundProperty);
        var flowDirection = surface.FlowDirection;
        window.Content = null;
        surface.DataContext = dataContext;
        surface.SetCurrentValue(TextElement.FontFamilyProperty, font);
        surface.SetCurrentValue(TextElement.ForegroundProperty, foreground);
        surface.FlowDirection = flowDirection;
        if (names is not null && NameScope.GetNameScope(surface) is null)
        {
            NameScope.SetNameScope(surface, names);
        }

        if (window.Resources.Count > 0 || window.Resources.MergedDictionaries.Count > 0)
        {
            surface.Resources.MergedDictionaries.Add(window.Resources);
        }

        return surface;
    }

    private static ResourceDictionary LoadApplicationResources(string output, out string sourceSha256)
    {
        var checkout = FindCheckout(output) ?? throw new ArgumentException("The Wisp checkout was not found.");
        var path = Path.Combine(checkout, "src", "Wisp.App", "App.xaml");
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length is <= 0 or > 1024 * 1024)
        {
            throw new InvalidOperationException("The application resource source is outside the review size limit.");
        }

        var bytes = new byte[checked((int)source.Length)];
        source.ReadExactly(bytes);
        if (source.ReadByte() != -1)
        {
            throw new InvalidOperationException("The application resource source changed while being read.");
        }

        sourceSha256 = Convert.ToHexString(SHA256.HashData(bytes));
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var root = document.Root ?? throw new InvalidOperationException("App.xaml has no root.");
        var resources = root.Element(presentation + "Application.Resources")
                        ?? throw new InvalidOperationException("App.xaml has no application resources.");
        var dictionary = new XElement(resources) { Name = presentation + "ResourceDictionary" };
        foreach (var attribute in root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            if (dictionary.Attribute(attribute.Name) is null)
            {
                dictionary.Add(new XAttribute(attribute));
            }
        }

        var context = new ParserContext
        {
            BaseUri = new Uri("pack://application:,,,/Wisp;component/App.xaml", UriKind.Absolute)
        };
        return XamlReader.Parse(dictionary.ToString(SaveOptions.DisableFormatting), context) as ResourceDictionary
               ?? throw new InvalidOperationException("The application resources did not produce a dictionary.");
    }

    private static string PrepareOutput(string requested)
    {
        var root = FindCheckout(Directory.GetCurrentDirectory()) ?? FindCheckout(AppContext.BaseDirectory)
                   ?? throw new ArgumentException("Run from the Wisp checkout.");
        var output = Path.GetFullPath(requested);
        if (!output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(output) || Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            throw new ArgumentException("The output must be a new or empty directory within the checkout.");
        }

        for (DirectoryInfo? directory = new(output); directory is not null &&
             !string.Equals(directory.FullName, root, StringComparison.OrdinalIgnoreCase); directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("Output directories cannot pass through a junction or symbolic link.");
            }
        }

        Directory.CreateDirectory(output);
        return output;
    }

    private static string? FindCheckout(string start)
    {
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln")) &&
                File.Exists(Path.Combine(directory.FullName, "src", "Wisp.App", "Wisp.App.csproj")))
            {
                return directory.FullName.TrimEnd(Path.DirectorySeparatorChar);
            }
        }

        return null;
    }

    private sealed class OffscreenApplication : Application
    {
        public int SuppressedStartupNotifications { get; private set; }

        // Application can queue startup before Run(); never dispatch the production override.
        protected override void OnStartup(StartupEventArgs e) => SuppressedStartupNotifications++;
        protected override void OnExit(ExitEventArgs e) { }
    }

    private sealed record Options(string Output, Fixture[] Fixtures, bool Waiting, int Dpi, bool AppearanceOnly, bool Present,
        bool WizardOnly, int[] WizardDpis, int? WizardStep, bool ScrollCheck, bool NativeLifetimeCheck)
    {
        public static Options? Parse(string[] args)
        {
            if (args.Length == 0 || args.SequenceEqual(new[] { "--help" }))
            {
                return null;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var present = false;
            var scrollCheck = false;
            var nativeLifetimeCheck = false;
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] == "--native-lifetime-check")
                {
                    if (nativeLifetimeCheck)
                        throw new ArgumentException("Duplicate native-lifetime-check option.");
                    nativeLifetimeCheck = true;
                    continue;
                }

                if (args[index] == "--scroll-check")
                {
                    if (scrollCheck)
                    {
                        throw new ArgumentException("Duplicate scroll-check option.");
                    }

                    scrollCheck = true;
                    continue;
                }

                if (args[index] == "--present")
                {
                    if (present)
                    {
                        throw new ArgumentException("Duplicate presentation option.");
                    }

                    present = true;
                    continue;
                }

                if (index + 1 >= args.Length || args[index] is not ("--output" or "--fixture" or "--telemetry" or "--dpi" or "--scope" or "--step") ||
                    args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                    !values.TryAdd(args[index], args[index + 1]))
                {
                    throw new ArgumentException("Unsupported or duplicate option.");
                }

                index++;
            }

            if (!values.TryGetValue("--output", out var output) || string.IsNullOrWhiteSpace(output))
            {
                throw new ArgumentException("An explicit output directory is required.");
            }

            if (nativeLifetimeCheck)
            {
                if (present || scrollCheck || values.Count != 1)
                    throw new ArgumentException("Native lifecycle checks accept only --output.");
                return new Options(output, [], false, 96, false, false, false, [], null, false, true);
            }

            var name = values.GetValueOrDefault("--fixture");
            Fixture[] fixtures = name is null
                ? [Fixture.All[0], Fixture.All[1], Fixture.All[2], Fixture.All[5]]
                : Fixture.All.Where(fixture => fixture.Name == name).ToArray();
            var telemetry = values.GetValueOrDefault("--telemetry", "sample");
            var dpi = values.GetValueOrDefault("--dpi", "96");
            var scope = values.GetValueOrDefault("--scope", "matrix");
            if (fixtures.Length == 0 || telemetry is not ("sample" or "waiting") ||
                dpi is not ("96" or "144") || scope is not ("matrix" or "appearance" or "wizard"))
            {
                throw new ArgumentException("Unsupported fixture, telemetry mode, or DPI.");
            }

            var wizard = scope == "wizard";
            if (scrollCheck && (values.ContainsKey("--scope") || values.ContainsKey("--step")))
            {
                throw new ArgumentException("Scroll checks use their own bounded offscreen matrix.");
            }

            if (scrollCheck && name is null)
            {
                fixtures = [Fixture.All[0]];
            }

            int? wizardStep = null;
            if (wizard && name is null)
            {
                fixtures = [Fixture.All[0]];
            }

            if (values.TryGetValue("--step", out var stepName))
            {
                var step = Array.IndexOf(WizardStepNames, stepName);
                if (!wizard || step < 0)
                {
                    throw new ArgumentException("A named wizard step requires wizard scope.");
                }

                wizardStep = step;
            }

            if (wizard && values.ContainsKey("--telemetry"))
            {
                throw new ArgumentException("Wizard review never supplies telemetry.");
            }

            if (present && (fixtures.Length != 1 || values.ContainsKey("--dpi") ||
                            !wizard && !scrollCheck && (name is null || values.ContainsKey("--scope"))))
            {
                throw new ArgumentException("Presentation requires one fixture and uses its own scope and monitor DPI.");
            }

            var parsedDpi = int.Parse(dpi, CultureInfo.InvariantCulture);
            return new Options(output, fixtures, telemetry == "waiting", parsedDpi, scope == "appearance", present,
                wizard, wizard && !values.ContainsKey("--dpi") ? [96, 144] : [parsedDpi], wizardStep, scrollCheck, false);
        }
    }
}
