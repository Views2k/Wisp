using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using Wisp.App;
using Wisp.Core;

namespace Wisp.UiReview;

internal static class ShiftCueReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources,
        Action<FrameworkElement, int> setDpi)
    {
        using var deadline = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var bindings = new BindingTrace();
        var failures = new List<string>();
        var captures = new List<string>();
        var geometry = new List<object>();
        var checks = new List<object>();
        var phase = "initialize";
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application.Resources = loadResources();
            var fixture = Fixture.All.First(f => f.Name == "native-analogue");
            var model = new DiagnosticsViewModel(fixture.CreateSettings());
            fixture.Apply(model, waiting: false);
            var baseline = model.NativeGaugeFrame with
            {
                Gear = TransmissionGear.Third,
                GearDisplayMode = GearDisplayMode.Manual,
                EngineRpm = 6500,
                Speed = 123,
                IsElectric = false
            };
            var states = new (string Name, ShiftCueVisualState Cue)[]
            {
                ("Off", default), ("Approach", new(true, 1, 0xFF70E7A1, true, 6700)),
                ("Prepare", new(true, 2, 0xFFFFD166, true, 6700)),
                ("Shift · flash on", new(true, 3, 0xFFFF5364, true, 6700)),
                ("Shift · flash off", new(true, 3, 0xFFFF5364, false, 6700)),
                ("Custom color", new(true, 2, 0xFFC28AFF, true, 6700))
            };
            foreach (var digital in new[] { false, true })
            {
                phase = bindings.Phase = digital ? "digital-cue-stages" : "analogue-cue-stages";
                var matrix = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var state in states)
                {
                    var frame = baseline with { ShiftCue = state.Cue };
                    UserControl gauge = digital
                        ? new NativeDigitalSpeedometer { DataContext = model, Frame = frame }
                        : new NativeAnalogSpeedometer { DataContext = model, Frame = frame };
                    var cell = new StackPanel { Width = 340, Margin = new Thickness(8) };
                    cell.Children.Add(Label(state.Name, 18));
                    cell.Children.Add(new Border { Padding = new Thickness(14, 18, 14, 14), Child = gauge });
                    matrix.Children.Add(cell);
                }
                var surface = Surface(matrix, digital ? "Digital gear cue" : "Analogue gear cue");
                var size = new Size(2180, digital ? 330 : 465);
                Arrange(surface, size);
                foreach (var gauge in Descendants(surface).OfType<UserControl>().Where(c => c is NativeAnalogSpeedometer or NativeDigitalSpeedometer))
                {
                    var ring = (FrameworkElement)gauge.FindName("ShiftCueRing");
                    var gear = (FrameworkElement)gauge.FindName("GearImage");
                    var ringRect = ring.TransformToAncestor(gauge).TransformBounds(new Rect(ring.RenderSize));
                    var gearRect = gear.TransformToAncestor(gauge).TransformBounds(new Rect(gear.RenderSize));
                    var centered = Math.Abs(ringRect.X + ringRect.Width / 2 - gearRect.X - gearRect.Width / 2) < .1 &&
                                   Math.Abs(ringRect.Y + ringRect.Height / 2 - gearRect.Y - gearRect.Height / 2) < .1;
                    if (!centered) failures.Add(phase + "/ring-not-centered-on-gear");
                    geometry.Add(new { mode = phase, ring = ringRect.ToString(CultureInfo.InvariantCulture), gear = gearRect.ToString(CultureInfo.InvariantCulture), centered });
                }
                Capture(surface, size, phase + ".png");
            }

            foreach (var variant in new[] { "off", "enabled", "expanded", "disabled", "focus-treatment" })
            {
                phase = bindings.Phase = "settings-" + variant;
                var settings = fixture.CreateSettings();
                settings.AccelerationShiftCueEnabled = variant != "off";
                var settingsModel = new DiagnosticsViewModel(settings);
                var control = new ShiftCueSettingsControl { DataContext = settingsModel, IsEnabled = variant != "disabled" };
                var content = new Border { Background = Resource("CardBrush"), Padding = new Thickness(24), Child = control };
                var surface = Surface(content, "Performance shift guidance · " + variant);
                setDpi(surface, 144);
                var expander = LogicalDescendants(control).OfType<Expander>().Single();
                expander.IsExpanded = variant is "expanded" or "disabled" or "focus-treatment";
                surface.Measure(new Size(610, double.PositiveInfinity));
                var size = new Size(610, Math.Ceiling(surface.DesiredSize.Height));
                Arrange(surface, size);
                var toggle = (CheckBox)control.FindName("EnabledToggle");
                if (!ReferenceEquals(toggle.Style, application.Resources["ToggleSwitchStyle"])) failures.Add(phase + "/unstyled-toggle");
                if (!ReferenceEquals(expander.Style, application.Resources["MoreOptionsStyle"])) failures.Add(phase + "/unstyled-expander");
                if (toggle.Template is null || expander.Template is null) failures.Add(phase + "/missing-template");
                foreach (var button in Descendants(control).OfType<Button>())
                    if (button.Style is null || button.Template is null) failures.Add(phase + "/unstyled-button");
                var hasFocusTrigger = toggle.Template!.Triggers.OfType<Trigger>()
                    .Any(t => t.Property == UIElement.IsKeyboardFocusedProperty);
                if (!hasFocusTrigger) failures.Add(phase + "/missing-authored-focus-trigger");
                if (variant == "focus-treatment")
                {
                    // Detached capture cannot acquire genuine keyboard focus
                    // without an input host. Show the authored trigger's border
                    // treatment explicitly; never send focus or create a window.
                    var track = (Border)toggle.Template.FindName("ToggleTrack", toggle);
                    track.SetResourceReference(Border.BorderBrushProperty, "TextBrush");
                    var disclosure = (System.Windows.Controls.Primitives.ToggleButton)expander.Template!.FindName("Disclosure", expander);
                    disclosure.ApplyTemplate();
                    var disclosureBorder = (Border)disclosure.Template.FindName("DisclosureSurface", disclosure);
                    disclosureBorder.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                }
                checks.Add(new
                {
                    variant,
                    toggleStyle = toggle.Style is not null,
                    disclosureStyle = expander.Style is not null,
                    hasFocusTrigger,
                    focusIsSimulated = variant == "focus-treatment",
                    width = size.Width,
                    height = size.Height
                });
                Arrange(surface, size);
                Capture(surface, size, phase + ".png");
            }
            CheckSharedPagePaths(output, failures, checks);
        }
        catch (Exception error)
        {
            failures.Add(phase + "/" + error.GetType().Name);
            if (error.InnerException is { } inner) failures.Add(phase + "/inner-" + inner.GetType().Name);
        }
        finally { application.Shutdown(); }
        if (bindings.TotalCount > 0) failures.Add("binding-diagnostics");
        File.WriteAllText(Path.Combine(output, "shift-cue-review.json"), JsonSerializer.Serialize(new
        {
            build = new
            {
                ApplicationVersionInfo.MachineVersion,
                ApplicationVersionInfo.DiagnosticBuildId,
                ApplicationVersionInfo.DiagnosticBuildLabel,
                assemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(typeof(ApplicationVersionInfo).Assembly.Location)))
            },
            method = "Detached actual WPF gauges and shared settings control at144DPI; synthetic frames only. No Window, HWND, keyboard focus, live game, controller, production settings or telemetry connection is created. Needle shader pixels and native GPU presentation are not verified by these WPF captures. Focus-treatment capture applies the existing template border resources explicitly, not real keyboard focus.",
            captures,
            checks,
            geometry,
            failures,
            bindingDiagnosticCount = bindings.TotalCount,
            bindings = bindings.Messages
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Shift cue review: {captures.Count} captures, {failures.Count} failures; synthetic offscreen artwork only.");
        return failures.Count == 0 ? 0 : 1;

        Brush Resource(string name) => (Brush)application.Resources[name];
        TextBlock Label(string text, double size) => new()
        {
            Text = text,
            Foreground = Resource("TextBrush"),
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12)
        };
        FrameworkElement Surface(UIElement child, string title)
        {
            var panel = new StackPanel();
            panel.Children.Add(Label(title, 24));
            panel.Children.Add(Label("Synthetic offscreen review · current Wisp controls", 13));
            panel.Children.Add(child);
            return new Border { Background = Resource("WindowBrush"), Padding = new Thickness(20), Child = panel };
        }
        void Capture(FrameworkElement surface, Size size, string filename)
        {
            setDpi(surface, 144);
            Arrange(surface, size);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 1.5), (int)Math.Ceiling(size.Height * 1.5), 144, 144, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(Path.Combine(output, filename), FileMode.CreateNew, FileAccess.Write);
            encoder.Save(stream);
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
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    {
        yield return parent;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
    }

    private static void CheckSharedPagePaths(string output, List<string> failures, List<object> checks)
    {
        DirectoryInfo? root = new(output);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Wisp.sln"))) root = root.Parent;
        if (root is null) throw new ArgumentException("Checkout not found");
        foreach (var file in new[] { "MainWindow.xaml", "LegacyMainWindow.xaml" })
        {
            using var reader = XmlReader.Create(Path.Combine(root.FullName, "src", "Wisp.App", file),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            var controls = document.Descendants().Where(e => e.Name.LocalName == nameof(ShiftCueSettingsControl)).ToArray();
            var colorItems = document.Descendants().Where(e => e.Name.LocalName == "ListBoxItem" &&
                ((string?)e.Attribute("Content"))?.StartsWith("Shift cue ·", StringComparison.Ordinal) == true).ToArray();
            var styledSelector = colorItems.Length == 3 && colorItems.All(e => e.Parent?.Name.LocalName == "ListBox" &&
                e.Parent.Elements().Any(child => child.Name.LocalName == "ListBox.ItemContainerStyle"));
            if (controls.Length != 1) failures.Add(file + "/shared-settings-count");
            if (!styledSelector) failures.Add(file + "/missing-styled-color-choices");
            checks.Add(new { page = file, sharedControlCount = controls.Length, colorChoiceCount = colorItems.Length, styledSelector });
        }
    }
}
