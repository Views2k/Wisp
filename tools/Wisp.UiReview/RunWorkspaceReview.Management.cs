using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;
using Wisp.App.Runs;
using Wisp.Telemetry;

namespace Wisp.UiReview;

internal static partial class RunWorkspaceReview
{
    private static void ReviewRunManagementControls(string output, DiagnosticsViewModel fixtureViewModel, BindingTrace bindings,
        List<CaptureReport> captures, List<string> failures)
    {
        foreach (var legacy in new[] { false, true })
        {
            var mode = legacy ? "legacy" : "modern";
            bindings.Phase = "management/" + mode;
            // These runs belong only to this review output. The receiver is never started.
            var directory = Path.Combine(output, "management-fixtures", mode, Guid.NewGuid().ToString("N"));
            var receiver = new TelemetryUdpReceiver();
            var service = new RunRecordingService(receiver, directory);
            var model = new RunsViewModel(service, new AppSettings(), Dispatcher.CurrentDispatcher);
            RunsPageBase page = legacy ? new LegacyRunsPage() : new RunsPage();
            page.DataContext = model;
            var surface = new Border
            {
                Padding = new Thickness(16),
                Background = Theme("WindowBrush"),
                DataContext = fixtureViewModel,
                Child = page
            };
            var size = new Size(1280, 900);
            try
            {
                var a = CreateRun("Mountain road · baseline", 1, false);
                var b = CreateRun("Mountain road · revised", 1.15, true);
                Await(service.Store.SaveAsync(a)); Await(service.Store.SaveAsync(b));
                Await(model.InitializeAsync());
                model.SelectedRun = model.Library.Single(item => item.Id == a.Id);
                AwaitReady(model);
                model.ComparisonChoice = model.Library.Single(item => item.Id == b.Id);
                model.CompareCommand.Execute(null); AwaitReady(model);
                VisualTreeHelper.SetRootDpi(surface, new DpiScale(1, 1));
                Arrange(surface, size);

                var searchControl = Required<RunLibrarySearch>(page, "LibrarySearchControl");
                var search = Required<TextBox>(searchControl, "SearchBox");
                var export = Required<Button>(page, "RunExportButton");
                Check(search.Style is not null && search.Template is not null && search.Focusable && search.IsTabStop,
                    "search-not-themed-or-keyboard-reachable");
                Check(SameBrush(search.Background, Theme("InputBrush")) && SameBrush(search.Foreground, Theme("TextBrush")),
                    "search-does-not-use-theme-colors");
                Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(search)), "search-accessible-name-missing");
                Check(export.Style is not null && export.Template is not null && export.Focusable && export.IsTabStop,
                    "export-button-not-themed-or-keyboard-reachable");
                Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(export)), "export-accessible-name-missing");

                var report = Required<FrameworkElement>(page, "RunReportSurface");
                var reportUnloads = 0;
                report.Unloaded += ReportUnloaded;
                search.SetCurrentValue(TextBox.TextProperty, "Shorter final drive");
                search.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Arrange(surface, size);
                Check(model.FilteredLibrary.Count == 2 && model.SelectedRun?.Id == a.Id && model.HasComparison,
                    "search-lost-current-run-or-comparison");
                Capture(surface, size, "search-match");

                search.SetCurrentValue(TextBox.TextProperty, "no matching run");
                search.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Arrange(surface, size);
                Check(model.FilteredLibrary.Count == 1 && model.FilteredLibrary[0].Id == a.Id &&
                      model.LibrarySearchSummary.Contains("0 matches", StringComparison.Ordinal),
                    "search-empty-state-not-clear-or-current-run-lost");
                Check(model.ComparisonChoice?.Id == b.Id && model.HasComparison, "search-cleared-run-b");
                Capture(surface, size, "search-no-match");
                model.ClearLibrarySearchCommand.Execute(null);
                Arrange(surface, size);
                Check(model.FilteredLibrary.Count == 2 && !model.HasLibrarySearch && search.Text.Length == 0,
                    "clear-search-did-not-restore-library");

                var details = Descendants(page).OfType<Expander>().First(expander =>
                    Equals(expander.Header, legacy ? "Name, tune and notes" : "Run details"));
                details.IsExpanded = true;
                Arrange(surface, size);
                var nameBox = Descendants(details).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Run name");
                var tuneBox = Descendants(details).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Tune label");
                var statusText = Descendants(details).OfType<TextBlock>().Single(text =>
                    BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == nameof(RunsViewModel.MetadataSaveStatus));
                var summaryScroll = Required<ScrollViewer>(page, "RunsScroll");
                ScrollIntoView(details, summaryScroll, surface, size);
                Check(nameBox.Template is not null && tuneBox.Template is not null &&
                      SameBrush(nameBox.Background, Theme("InputBrush")), "details-inputs-not-themed");
                Check(AutomationProperties.GetLiveSetting(statusText) == AutomationLiveSetting.Polite,
                    "inline-save-status-not-accessible");

                nameBox.SetCurrentValue(TextBox.TextProperty, "Mountain road · renamed");
                nameBox.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Check(model.HasPendingMetadata && model.MetadataSaveStatus == "Saving…", "typing-did-not-enter-saving-state");
                // No dispatcher pump here: this captures the real pending state before the debounce fires.
                CaptureStatus("saving", pump: false);
                Await(model.FlushMetadataAsync());
                Arrange(surface, size);
                Check(!model.HasPendingMetadata && statusText.Text == "Saved", "saved-status-not-bound");
                Check(model.SelectedRun?.Id == a.Id && model.HasComparison && reportUnloads == 0,
                    "autosave-replaced-report-or-selection");
                CaptureStatus("saved");
                Capture(surface, size, "details-saved");

                using (var locked = new FileStream(Path.Combine(directory, $"{a.Id:N}.wisprun"),
                           FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    tuneBox.SetCurrentValue(TextBox.TextProperty, "Short gearing · retained draft");
                    tuneBox.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                    Await(model.FlushMetadataAsync());
                    Check(model.HasMetadataSaveError && model.HasPendingMetadata, "failed-save-state-not-exercised");
                    CaptureStatus("retry-save");
                    summaryScroll.ScrollToHome(); Arrange(surface, size);
                    Check(Descendants(page).OfType<RunMetadataStatus>().Any(control => control.Visibility == Visibility.Visible),
                        "page-does-not-expose-save-error");
                    Capture(surface, size, "page-save-error");
                }
                model.RetryMetadataSaveCommand.Execute(null);
                Await(model.FlushMetadataAsync());
                Arrange(surface, size);
                Check(!model.HasPendingMetadata && !model.HasMetadataSaveError && model.MetadataSaveStatus == "Saved",
                    "retry-did-not-return-to-saved");
                Check(reportUnloads == 0, "metadata-status-recreated-report");
                report.Unloaded -= ReportUnloaded;

                ReviewExportMenu();
                Check(PresentationSource.FromVisual(surface) is null, "management-review-created-presentation-window");

                void ReportUnloaded(object sender, RoutedEventArgs args) => reportUnloads++;

                void CaptureStatus(string phase, bool pump = true)
                {
                    var status = new RunMetadataStatus { DataContext = model };
                    var strip = new Border
                    {
                        Padding = new Thickness(18),
                        Background = Theme("PanelBrush"),
                        DataContext = fixtureViewModel,
                        Child = status
                    };
                    var stripSize = new Size(540, 110);
                    strip.Measure(stripSize); strip.Arrange(new Rect(stripSize)); strip.UpdateLayout();
                    if (pump) Arrange(strip, stripSize);
                    var text = Descendants(status).OfType<TextBlock>().Single(item =>
                        BindingOperations.GetBinding(item, TextBlock.TextProperty)?.Path?.Path == nameof(RunsViewModel.MetadataSaveStatus));
                    var retry = Descendants(status).OfType<Button>().Single();
                    Check(text.Text == model.MetadataSaveStatus, phase + "/status-binding");
                    Check(SameBrush(text.Foreground, Theme(model.HasMetadataSaveError ? "WarningBrush" : "MutedBrush")), phase + "/status-color");
                    Check(AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite, phase + "/live-status");
                    Check(retry.Style is not null && retry.Template is not null && retry.Focusable && retry.IsTabStop &&
                          ReferenceEquals(retry.Command, model.RetryMetadataSaveCommand), phase + "/retry-style-or-command");
                    Check(retry.Visibility == (model.HasMetadataSaveError ? Visibility.Visible : Visibility.Collapsed), phase + "/retry-visibility");
                    Capture(strip, stripSize, phase, pump: false);
                    strip.Child = null; status.DataContext = null;
                }

                void ReviewExportMenu()
                {
                    // Apply the same shared styles without IsOpen: no native popup or file dialog.
                    var menuStyle = (Style)page.FindResource("RunExportMenuStyle");
                    var itemStyle = (Style)page.FindResource("RunExportItemStyle");
                    var menu = new ContextMenu { Style = menuStyle, Width = 420, DataContext = model, Visibility = Visibility.Visible };
                    foreach (var label in new[] { "Wisp run file · share or compare", "CSV · recorded telemetry" })
                    {
                        var item = new MenuItem { Header = label, Style = itemStyle };
                        item.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(RunsViewModel.CanManageRun)));
                        menu.Items.Add(item);
                    }
                    // ContextMenu must remain an independent root even when its template is reviewed offscreen.
                    menu.ApplyTemplate();
                    menu.Measure(new Size(420, 280));
                    if (!double.IsFinite(menu.DesiredSize.Height) || menu.DesiredSize.Height <= 0)
                        throw new InvalidOperationException("The offscreen export menu produced no measurable content.");
                    var menuSize = new Size(420, Math.Ceiling(menu.DesiredSize.Height));
                    Arrange(menu, menuSize);
                    Check(menu.Template is not null && SameBrush(menu.Background, Theme("PanelBrush")) &&
                          SameBrush(menu.Foreground, Theme("TextBrush")), "export-menu-not-themed");
                    var items = menu.Items.Cast<MenuItem>().ToArray();
                    foreach (var item in items)
                    {
                        item.ApplyTemplate();
                        Check(item.Template is not null && SameBrush(item.Foreground, Theme("TextBrush")) && item.Focusable,
                            "export-item-not-themed-or-focusable");
                        Check(item.ActualWidth > 0 && item.ActualHeight > 0 && Within(item, menu),
                            "export-item-not-visible-within-menu");
                        Check(ReferenceEquals(item.DataContext, model) && item.IsEnabled == model.CanManageRun &&
                              BindingOperations.GetBinding(item, UIElement.IsEnabledProperty)?.Path?.Path == nameof(RunsViewModel.CanManageRun),
                            "export-item-model-or-enabled-binding");
                        Check(item.Template!.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == UIElement.IsKeyboardFocusWithinProperty),
                            "export-item-focus-trigger-missing");
                    }
                    Capture(menu, menuSize, "export-menu-default");
                    SetOffscreenReadOnlyState(items[0], MenuItem.IsHighlightedProperty, true);
                    SetOffscreenReadOnlyState(items[0], UIElement.IsKeyboardFocusWithinProperty, true);
                    items[1].SetCurrentValue(UIElement.IsEnabledProperty, false);
                    Arrange(menu, menuSize);
                    var firstBorder = (Border)items[0].Template.FindName("ItemBorder", items[0]);
                    var secondBorder = (Border)items[1].Template.FindName("ItemBorder", items[1]);
                    Check(SameBrush(firstBorder.Background, Theme("RaisedBrush")) && SameBrush(firstBorder.BorderBrush, Theme("AccentBrush")),
                        "export-highlight-focus-colors");
                    Check(secondBorder.Opacity < 1 && secondBorder.Opacity > 0, "export-disabled-item-style");
                    Capture(menu, menuSize, "export-menu-highlight-focus-disabled");
                    SetOffscreenReadOnlyState(items[0], MenuItem.IsHighlightedProperty, false);
                    SetOffscreenReadOnlyState(items[0], UIElement.IsKeyboardFocusWithinProperty, false);
                    Check(!menu.IsOpen && PresentationSource.FromVisual(menu) is null &&
                          VisualTreeHelper.GetParent(menu) is null && LogicalTreeHelper.GetParent(menu) is null,
                        "export-review-opened-or-parented-a-popup");
                    menu.DataContext = null;
                }
            }
            finally
            {
                Await(model.FlushMetadataAsync());
                page.DataContext = null; surface.Child = null;
                model.Dispose(); Await(service.DisposeAsync().AsTask()); Await(receiver.DisposeAsync().AsTask());
            }

            void Check(bool condition, string code)
            {
                if (!condition) failures.Add("management/" + mode + "/" + code);
            }

            void Capture(FrameworkElement visual, Size dimensions, string phase, bool pump = true)
            {
                bindings.Phase = "management/" + mode + "/" + phase;
                var standaloneMenu = visual as ContextMenu;
                if (standaloneMenu is not null &&
                    (!ReferenceEquals(standaloneMenu.DataContext, model) || standaloneMenu.IsOpen ||
                     PresentationSource.FromVisual(standaloneMenu) is not null || VisualTreeHelper.GetParent(standaloneMenu) is not null ||
                     LogicalTreeHelper.GetParent(standaloneMenu) is not null))
                    throw new InvalidOperationException("The export menu review lost its dedicated model or standalone offscreen root.");
                if ((standaloneMenu is null && !ReferenceEquals(visual.DataContext, fixtureViewModel)) ||
                    Descendants(visual).OfType<FrameworkElement>().Where(element =>
                        element is RunsPageBase or RunLibrarySearch or RunMetadataStatus or ContextMenu)
                        .Any(element => !ReferenceEquals(element.DataContext, model)))
                    throw new InvalidOperationException("The Runs management review lost its diagnostics host or dedicated run model.");
                if (pump) Arrange(visual, dimensions);
                var file = $"runs-management-{mode}-{phase}.png";
                var bitmap = new RenderTargetBitmap((int)dimensions.Width, (int)dimensions.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(output, file))) encoder.Save(stream);
                var inspection = ReviewDiagnostics.Inspect(visual, file, "synthetic-runs-controls", mode, phase,
                    96, (int)dimensions.Width, (int)dimensions.Height, 0,
                    fixtureContext: standaloneMenu is null ? null : fixtureViewModel);
                captures.Add(inspection);
                Check(inspection.Labels.OverflowCount == 0, phase + "/text-overflow");
            }
        }

        static Brush Theme(string key) => (Brush)Application.Current.FindResource(key);
        static bool SameBrush(Brush? actual, Brush expected) => ReferenceEquals(actual, expected) ||
            actual is SolidColorBrush first && expected is SolidColorBrush second && first.Color == second.Color;
    }

    private static void SetOffscreenReadOnlyState(DependencyObject element, DependencyProperty property, bool value)
    {
        // Exercise the actual template triggers without moving desktop keyboard focus.
        var key = property.OwnerType.GetField(property.Name + "PropertyKey", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as DependencyPropertyKey
            ?? throw new InvalidOperationException("Cannot simulate offscreen control state: " + property.Name);
        element.SetValue(key, value);
    }
}
