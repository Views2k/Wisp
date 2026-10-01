using System.Globalization;
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
using Wisp.App.Tunes;
using Wisp.Core.Tunes;

namespace Wisp.UiReview;

internal static class TuneUiReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources,
        Func<Window, object?, FrameworkElement> detachSurface, Action<FrameworkElement, int> setDpi)
    {
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        var failures = new List<string>();
        var captures = new List<object>();
        var checks = 0;
        var phase = "initialize";
        var application = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previousContext = SynchronizationContext.Current;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application.Resources = loadResources();
            foreach (var legacy in new[] { false, true }) Verify(legacy);
        }
        catch (Exception error)
        {
            failures.Add($"{phase}:exception:{error.GetType().Name}");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            application.Shutdown();
        }
        Check(bindings.TotalCount == 0, "no-binding-diagnostics");
        File.WriteAllText(Path.Combine(output, "tune-ui-review.json"), JsonSerializer.Serialize(new
        {
            Passed = failures.Count == 0,
            Checks = checks,
            Failures = failures,
            Captures = captures,
            BindingDiagnostics = bindings.Messages,
            DetachedOnly = true,
            GameReads = 0,
            VisibleWindows = 0,
            Scope = "Offline fixture layouts and control states. No interactive focus, gameplay, or native-reader claim."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Tune detached UI: {checks} checks, {failures.Count} failures, {captures.Count} PNGs.");
        return failures.Count == 0 ? 0 : 2;

        void Check(bool condition, string name)
        {
            checks++;
            if (!condition) failures.Add($"{phase}:{name}");
        }

        void Verify(bool legacy)
        {
            var style = legacy ? "legacy" : "modern";
            phase = style + "-initialize"; bindings.Phase = phase;
            var library = Path.Combine(output, style + "-fixture-library");
            var settings = new AppSettings
            {
                UseLegacyInterface = legacy,
                StartWithWindows = false,
                StartWithForza = false,
                AutomaticApplicationUpdateChecks = false
            };
            var controller = new AppController(settings, _ => { }, new NoStartupRegistration(), tuneLibraryDirectory: library);
            ControlPanelWindow? window = null;
            var snapshot = TuneUiFixture.Read();
            using var model = new TuneViewModel(new TuneStore(library),
                _ => Task.FromResult(new TuneCaptureResult(snapshot, TuneCaptureStatus.Ready, "")),
                _ => true, Dispatcher.CurrentDispatcher);
            try
            {
                window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
                var page = (TunePage)window.FindName("TuneSurface");
                page.DataContext = model;
                var tabs = (TabControl)window.FindName("RootTabs");
                tabs.SelectedItem = window.FindName("TuneTab");
                var navigation = (ListBox)window.FindName("SidebarNavigation");
                var surface = detachSurface(window, controller.ViewModel);
                Await(model.InitializeAsync()); Await(model.RefreshAsync());
                Check(model.Categories.Count == 9 && model.CanSave, "current-snapshot-ready");
                model.BeginSave(); model.DialogName = "Road baseline";
                model.DialogDescription = "Stable road setup.\nPreserve both lines when reopening.";
                Await(model.ConfirmDialogAsync());
                Check(!model.IsDialogOpen && model.Library.Count == 1, "save-completed");
                var baseline = model.Library.Single();
                model.SetWorkspace(TuneWorkspace.Current); Await(model.RefreshAsync());

                foreach (var dpi in new[] { 96, 144 })
                    foreach (var size in new[] { new Size(720, 440), new Size(1280, 900) })
                    {
                        SetPhase("current", size, dpi);
                        model.SetWorkspace(TuneWorkspace.Current); Await(model.RefreshAsync());
                        model.SelectedCategory = model.Categories.First(value => value.Category == TuneCategory.Tires);
                        Layout(size, dpi);
                        var categories = (ListBox)page.FindName("TuneCategories");
                        Check(categories.Items.Count == 9 && categories.ActualWidth > 0 && categories.ActualWidth <= page.ActualWidth + 1,
                            "wrapped-categories-fit");
                        Capture(size, dpi);

                        SetPhase("dialog", size, dpi);
                        model.BeginSave(); Layout(size, dpi);
                        var dialog = (Grid)page.FindName("TuneDialog");
                        var name = (TextBox)page.FindName("TuneNameInput");
                        var description = (TextBox)page.FindName("TuneDescriptionInput");
                        Check(dialog.Visibility == Visibility.Visible && !navigation.IsEnabled &&
                            KeyboardNavigation.GetTabNavigation(dialog) == KeyboardNavigationMode.Cycle,
                            "modal-blocks-navigation-and-cycles-tab");
                        Check(description.AcceptsReturn && description.TextWrapping == TextWrapping.Wrap &&
                            description.IsEnabled && description.Focusable && name.IsEnabled,
                            "multiline-description-and-name-enabled");
                        Check(name.ActualWidth > 200 && name.ActualWidth <= page.ActualWidth, "dialog-input-fits");
                        CheckFits(dialog, page, "dialog-within-visible-page");
                        CheckFits((Button)page.FindName("TuneDialogSave"), dialog, "save-action-within-visible-page");
                        CheckFits((Button)page.FindName("TuneDialogCancel"), dialog, "cancel-action-within-visible-page");
                        CheckFits((Border)page.FindName("TuneDialogCard"), dialog, "dialog-card-within-visible-page");
                        var fields = (ScrollViewer)page.FindName("TuneDialogFields");
                        Check(fields.ViewportHeight > 0 && fields.ActualHeight <= dialog.ActualHeight, "dialog-fields-have-bounded-scroll-viewport");
                        fields.ScrollToEnd(); Layout(size, dpi);
                        CheckFits((Button)page.FindName("TuneDialogSave"), dialog, "save-action-stays-visible-after-scroll");
                        fields.ScrollToHome(); Layout(size, dpi);
                        Capture(size, dpi);
                        model.CancelDialog(); Layout(size, dpi);
                        Check(dialog.Visibility == Visibility.Collapsed && navigation.IsEnabled, "modal-cancel-restores-navigation");

                        SetPhase("saved", size, dpi);
                        model.SetWorkspace(TuneWorkspace.Saved); model.SelectedTune = baseline;
                        Await(model.LoadSelectedAsync()); Layout(size, dpi);
                        Check(model.Description.Contains('\n') && model.Heading == baseline.Name, "saved-description-reopened");
                        CheckSelectors(); Capture(size, dpi);

                        SetPhase("compare", size, dpi);
                        model.OpenComparisonSnapshots(snapshot, "Attached run A", "Run notes", TuneUiFixture.Read("rwd"), "Attached run B", "Other run");
                        model.SelectedSort = model.SortOptions.First(value => value.Sort == TuneSort.NameAscending);
                        model.SelectedCategory = model.Categories.First(value => value.Category == TuneCategory.Gearing);
                        Layout(size, dpi);
                        Check(model.CompareA?.Name == "Attached run A" && model.CompareB?.Name == "Attached run B" &&
                            model.Library.Count == 1 && model.ComparisonChoices.Count == 3, "attached-comparison-survives-sort-without-saving");
                        Check(((ItemsControl)page.FindName("TuneComparison")).Items.Count == model.Rows.Count && model.Rows.Count > 1,
                            "comparison-rows-aligned");
                        CheckSelectors(); Capture(size, dpi);
                    }
                Check(new WindowInteropHelper(window).Handle == nint.Zero && PresentationSource.FromVisual(surface) is null,
                    "no-native-window-or-presentation-source");

                void SetPhase(string mode, Size size, int dpi)
                {
                    phase = $"{style}-{mode}-{size.Width:0}x{size.Height:0}-{dpi}"; bindings.Phase = phase;
                }
                void Layout(Size size, int dpi)
                {
                    setDpi(surface, dpi);
                    Arrange(surface, size);
                }
                void Capture(Size size, int dpi)
                {
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi / 96),
                        (int)Math.Ceiling(size.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    var name = phase + ".png";
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, name)); encoder.Save(stream);
                    captures.Add(new { File = name, bitmap.PixelWidth, bitmap.PixelHeight, Dpi = dpi });
                }
                void CheckSelectors()
                {
                    foreach (var selector in Descendants(page).OfType<ComboBox>().Where(value => value.IsVisible))
                    {
                        selector.ApplyTemplate();
                        var popup = (Popup)selector.Template.FindName("PART_Popup", selector);
                        var popupBorder = (Border)popup.Child;
                        Check(ReferenceEquals(selector.FindResource("PanelBrush"), popupBorder.Background) &&
                            popupBorder.CornerRadius.TopLeft > 0 && !popup.IsOpen, "closed-themed-popup");
                        var root = (Grid)selector.Template.FindName("ComboRoot", selector);
                        selector.IsEnabled = false; Pump(); Check(root.Opacity == .45, "selector-disabled-style");
                        selector.ClearValue(UIElement.IsEnabledProperty); Pump(); Check(root.Opacity == 1, "selector-enabled-style");
                    }
                }
                void CheckFits(FrameworkElement element, FrameworkElement container, string check)
                {
                    var bounds = element.TransformToAncestor(container).TransformBounds(new Rect(element.RenderSize));
                    Check(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -.5 && bounds.Top >= -.5 &&
                        bounds.Right <= container.ActualWidth + .5 && bounds.Bottom <= container.ActualHeight + .5, check);
                }
            }
            finally
            {
                window?.Close();
                Await(controller.DisposeAsync().AsTask());
            }
        }
    }

    private static void Arrange(FrameworkElement surface, Size size)
    {
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout(); Pump();
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(10) };
            timeout.Tick += (_, _) => frame.Continue = false;
            _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
            timeout.Start(); try { Dispatcher.PushFrame(frame); } finally { timeout.Stop(); }
            if (!task.IsCompleted) throw new TimeoutException("Detached Tune operation exceeded its bound.");
        }
        task.GetAwaiter().GetResult();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
    private sealed class ResourceApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
}
