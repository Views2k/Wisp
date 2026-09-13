using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;

namespace Wisp.UiReview;

internal static class ProfileModalReview
{
    private sealed class ResourceApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }

    internal static int Run(string output, Func<ResourceDictionary> loadResources,
        Func<Window, object?, FrameworkElement> detachSurface)
    {
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        var application = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var failures = new List<string>();
        var captures = new List<string>();
        var probes = new List<object>();
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application.Resources = loadResources();
            foreach (var variant in new[] { "modern", "compact-purple", "legacy" })
            {
                bindings.Phase = variant;
                var fixture = Fixture.All.First(item => item.Name == "orbit-reference");
                var settings = fixture.CreateSettings();
                settings.StartWithWindows = false;
                settings.BackgroundParticlesEnabled = false;
                settings.ColorTheme = variant == "compact-purple" ? "Purple" : AppColorThemes.DefaultName;
                var controller = new AppController(settings, new SettingsService(Path.Combine(output, variant + "-settings.json")));
                ControlPanelWindow window = variant == "legacy" ? new LegacyMainWindow(controller) : new MainWindow(controller);
                var activations = 0;
                window.Activated += (_, _) => activations++;
                var surface = detachSurface(window, controller.ViewModel);
                var tabs = Required<TabControl>(window, "RootTabs");
                var navigation = Required<ListBox>(window, "SidebarNavigation");
                var body = Required<Grid>(window, "ControlBody");
                var dialog = Required<Grid>(window, "HudProfileDialog");
                var size = variant == "compact-purple" ? new Size(720, 440) : new Size(1280, 900);
                try
                {
                    fixture.Apply(controller.ViewModel, waiting: false);
                    tabs.SelectedIndex = 2;
                    Arrange();
                    if (variant == "compact-purple")
                    {
                        Required<Button>(window, "AppearancePreviewToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Arrange();
                    }
                    Capture("before");
                    var selected = navigation.SelectedItem;
                    var selectionBinding = navigation.GetBindingExpression(Selector.SelectedIndexProperty);
                    var bounds = navigation.TransformToAncestor(surface).TransformBounds(new Rect(navigation.RenderSize));
                    var save = LogicalDescendants(surface).OfType<Button>().Single(button => Equals(button.Content, "Save combination to Profiles"));
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Arrange();
                    Check(dialog.Visibility == Visibility.Visible && !body.IsEnabled && !navigation.IsEnabled, "modal-not-blocking-background");
                    Check(KeyboardNavigation.GetTabNavigation(dialog) == KeyboardNavigationMode.Cycle, "modal-tab-cycle");
                    Check(Required<TextBox>(window, "HudProfileNameInput").IsEnabled && Required<Button>(window, "ConfirmHudProfileButton").IsEnabled, "modal-actions-disabled");
                    // Remove only our scoped style in this detached fixture to expose
                    // the same default disabled template used before the fix.
                    var authoredStyle = navigation.Style;
                    navigation.SetCurrentValue(FrameworkElement.StyleProperty, null);
                    Arrange();
                    var defaultPixel = CornerPixel(navigation);
                    var defaultBackgrounds = VisualDescendants(navigation).OfType<Border>()
                        .Select(border => border.Background).OfType<SolidColorBrush>()
                        .Select(brush => brush.Color.ToString()).Distinct().ToArray();
                    if (variant == "modern") Capture("default-disabled-reproduction");
                    navigation.SetCurrentValue(FrameworkElement.StyleProperty, authoredStyle);
                    Arrange();
                    var correctedPixel = CornerPixel(navigation);
                    probes.Add(new { variant, defaultDisabledCorner = defaultPixel, defaultBackgrounds, correctedCorner = correctedPixel });
                    Check(correctedPixel.Alpha == 0, "disabled-navigation-painted-a-background");
                    var border = navigation.Template.FindName("NavigationBorder", navigation) as Border;
                    Check(border is not null && ReferenceEquals(border.Background, navigation.Background), "navigation-background-not-template-bound");
                    Check(navigation.SelectedItem == selected && ReferenceEquals(selectionBinding, navigation.GetBindingExpression(Selector.SelectedIndexProperty)), "selection-or-binding-changed");
                    Check(bounds == navigation.TransformToAncestor(surface).TransformBounds(new Rect(navigation.RenderSize)), "modal-changed-navigation-layout");
                    Capture("fixed-dialog");
                    LogicalDescendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Arrange();
                    Check(dialog.Visibility == Visibility.Collapsed && body.IsEnabled && navigation.IsEnabled, "cancel-kept-navigation-disabled");
                    Check(navigation.SelectedItem == selected, "cancel-changed-page");
                    var selectedItem = navigation.ItemContainerGenerator.ContainerFromIndex(2) as ListBoxItem;
                    Check(selectedItem is { IsEnabled: true, Focusable: true, IsTabStop: true }, "navigation-keyboard-target-not-restored");
                    Capture("after-cancel");
                    Check(new WindowInteropHelper(window).Handle == nint.Zero && activations == 0, "created-or-activated-window");
                }
                finally
                {
                    window.Close();
                    controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                void Arrange()
                {
                    surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
                    surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
                    surface.UpdateLayout();
                }
                void Capture(string phase)
                {
                    var fileName = variant + "-" + phase + ".png";
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, fileName)); encoder.Save(file);
                    captures.Add(fileName);
                }
                void Check(bool condition, string failure) { if (!condition) failures.Add(variant + "/" + failure); }
            }
        }
        catch (Exception error) { failures.Add(error.GetType().Name + "/" + error.Message); }
        finally { application.Shutdown(); }
        if (bindings.TotalCount > 0) failures.Add("binding-errors");
        File.WriteAllText(Path.Combine(output, "profile-modal-review.json"), JsonSerializer.Serialize(new
        {
            method = "Actual profile-save routed handler and authored WPF shell, detached software render; default-template comparison changes only the isolated review navigation style. No shown windows or game access.",
            captures,
            probes,
            failures,
            bindingDiagnosticCount = bindings.TotalCount,
            bindingDiagnostics = bindings.Messages
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Profile modal review: {captures.Count} captures; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 2;
    }

    private static Pixel CornerPixel(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var bytes = new byte[4]; bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), bytes, 4, 0);
        return new(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
    private readonly record struct Pixel(byte Alpha, byte Red, byte Green, byte Blue);
    private static T Required<T>(FrameworkElement root, string name) where T : FrameworkElement => root.FindName(name) as T ?? throw new InvalidOperationException(name + " was not found.");
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in LogicalDescendants(child)) yield return descendant; }
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var descendant in VisualDescendants(child)) yield return descendant; }
    }
}
