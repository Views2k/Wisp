using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.UiReview;

internal static partial class RunWorkspaceReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources,
        Func<Window, object?, FrameworkElement> detachSurface)
    {
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        var captures = new List<CaptureReport>();
        var failures = new List<string>();
        var previousSynchronization = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        AppController? controller = null;
        MainWindow? window = null;
        var activations = 0;
        var application = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application.Resources = loadResources();
            var fixture = Fixture.All.First(item => item.Name == "orbit-reference");
            var settings = fixture.CreateSettings();
            settings.DebugLoggingEnabled = false;
            var settingsPath = Path.Combine(output, "synthetic-settings.json");
            controller = new AppController(settings, new SettingsService(settingsPath));
            fixture.Apply(controller.ViewModel, waiting: false);
            window = new MainWindow(controller);
            window.Activated += (_, _) => activations++;
            var tabs = Required<TabControl>(window, "RootTabs");
            var page = Required<RunsPage>(window, "RunsSurface");
            var surface = detachSurface(window, controller.ViewModel);
            VisualTreeHelper.SetRootDpi(surface, new DpiScale(1, 1));
            tabs.SelectedItem = window.FindName("RunsTab");
            var model = controller.Runs;
            Check(model.WorkspacePresets.Select(preset => preset.Title).SequenceEqual(new[] { "Overview", "Engine", "Tires & handling" }),
                "graph-categories-not-consolidated");
            var scroll = Required<ScrollViewer>(page, "RunsScroll");
            var graphScroll = Required<ScrollViewer>(page, "GraphScroll");
            var header = Required<FrameworkElement>(page, "RunRecordingHeader");
            var record = Required<Button>(page, "RunRecordButton");
            var statisticCards = Required<ItemsControl>(page, "StatisticsCards");
            var statisticTable = Required<ItemsControl>(page, "StatisticsTable");
            var statisticsGroup = Required<ComboBox>(page, "StatisticGroupSelector");
            var statisticsView = Required<ComboBox>(page, "StatisticsViewSelector");
            var a = CreateRun("Mountain road · baseline", 1, false);
            var b = CreateRun("Mountain road · revised", 1.15, true);
            var size = new Size(1440, 900);
            foreach (var viewport in new[] { new Size(1440, 900), new Size(720, 440) })
            {
                size = viewport;
                bindings.Phase = $"{size.Width:0}/single";
                Await(model.ShowReviewAsync(a));
                model.SetPageVisible(true);
                AwaitReady(model);
                model.SelectedStatisticGroup = "Overview";
                model.StatisticsView = RunStatisticsView.Cards;
                Arrange(surface, size);
                Check(model.Statistics.Count == 6, "overview-does-not-have-six-key-statistics");
                Check(ReferenceEquals(statisticCards.ItemsSource, model.Statistics), "cards-use-wrong-statistic-source");
                Check(ReferenceEquals(statisticsGroup.SelectedItem, model.SelectedStatisticGroup) || Equals(statisticsGroup.SelectedItem, model.SelectedStatisticGroup), "statistics-group-binding");
                Capture("single-overview");

                bindings.Phase = $"{size.Width:0}/comparison";
                Await(model.ShowReviewAsync(a, b, RunPurpose.Acceleration));
                AwaitReady(model);
                model.StatisticsView = RunStatisticsView.Table;
                model.SelectedStatisticGroup = "All statistics";
                Arrange(surface, size);
                Check(model.HasComparison && model.Statistics.Count == 24, "comparison-statistics-missing");
                Check(Equals(statisticsView.SelectedItem, RunStatisticsView.Table), "statistics-view-binding");
                Check(ReferenceEquals(statisticTable.ItemsSource, model.Statistics), "table-use-wrong-statistic-source");
                Check(model.Statistics.Any(row => row.Difference?.StartsWith('+') == true), "comparison-differences-missing");
                ScrollIntoView(statisticTable, scroll, surface, size);
                Capture("comparison-table");
                scroll.ScrollToBottom(); Arrange(surface, size); CheckHeader("summary-bottom");

                bindings.Phase = $"{size.Width:0}/selected-section";
                model.SelectInterval(3, 9);
                AwaitReady(model);
                model.SelectedStatisticGroup = "Tire temperatures";
                Arrange(surface, size);
                Check(model.HasSelection && model.SelectionStart == 3 && model.SelectionEnd == 9, "section-selection-lost");
                Check(model.Statistics.Count == 6, "temperature-statistics-group");
                Check(model.Statistics.All(row => row.ValueB is not null), "section-comparison-values-missing");
                ScrollIntoView(statisticTable, scroll, surface, size);
                Capture("selected-temperature-table");

                bindings.Phase = $"{size.Width:0}/workspace";
                model.ShowGraphs();
                model.SelectedWorkspacePreset = model.WorkspacePresets.First(preset => preset.Id == RunWorkspacePreset.Engine);
                AwaitReady(model);
                Arrange(surface, size);
                Check(model.WorkspacePanels.Any(module => module.Id == "power-rpm"), "engine-preset-missing-rpm-plot");
                Check(model.WorkspacePanels.All(module => module.Plots.Count > 0), "enabled-module-not-prepared");
                graphScroll.ScrollToHome(); Arrange(surface, size);
                Capture("engine-workspace");
                CheckRenderedPlots();

                model.WorkspaceComparisonMode = model.WorkspaceComparisonModes.First(mode => mode.Id == RunWorkspaceComparisonMode.SideBySide);
                Arrange(surface, size);
                foreach (var module in model.WorkspacePanels)
                {
                    var timePlots = module.Plots.Where(plot => plot.TimePanel is not null).ToArray();
                    if (timePlots.Length == 0) continue;
                    Check(timePlots.Length == 2, "split-time-plot-count");
                    var left = timePlots.First(plot => plot.Label == "A").TimePanel!;
                    var right = timePlots.First(plot => plot.Label == "B").TimePanel!;
                    Check(left.Minimum == right.Minimum && left.Maximum == right.Maximum, "split-axes-not-shared");
                    Check(left.Series.All(series => !series.Comparison) && right.Series.All(series => series.Comparison), "split-series-identity");
                }
                Check(!Equals(((SolidColorBrush)RunComparisonColors.RunA).Color, ((SolidColorBrush)RunComparisonColors.RunB).Color), "run-colors-not-distinct");
                Capture("engine-side-by-side");

                bindings.Phase = $"{size.Width:0}/custom-workspace";
                var speed = model.AvailableWorkspaceModules.First(module => module.Id == "speed");
                speed.IsVisible = true;
                speed.Width = RunWorkspaceWidth.Full;
                AwaitReady(model);
                while (speed.MoveUpCommand.CanExecute(null)) speed.MoveUpCommand.Execute(null);
                var torque = model.AvailableWorkspaceModules.First(module => module.Id == "torque");
                torque.IsVisible = false;
                Check(model.WorkspacePanels.First() == speed && !model.WorkspacePanels.Contains(torque), "custom-order-or-visibility");
                Check(settings.RunWorkspace.Panels.First(panel => panel.IsVisible).Id == "speed" &&
                      settings.RunWorkspace.Panels.First(panel => panel.Id == "speed").Width == RunWorkspaceWidth.Full,
                    "custom-workspace-not-saved");
                graphScroll.ScrollToHome(); Arrange(surface, size);
                Capture("custom-workspace");
                graphScroll.ScrollToBottom(); Arrange(surface, size); CheckHeader("graphs-bottom");
                Check(model.HasSelection && model.HasComparison, "workspace-edits-lost-report-context");
                model.ResetWorkspaceCommand.Execute(null);
                AwaitReady(model);
                Check(!model.HasCustomWorkspaceLayout && model.WorkspacePanels.Select(module => module.Id)
                    .SequenceEqual(RunWorkspaceCatalog.PresetModules(RunWorkspacePreset.Engine)), "preset-reset-did-not-restore-modules");
                model.ShowSummary();
            }

            ReviewDesktopDrawersAndRows();
            ReviewSummarySwitch();
            ReviewSavedRunSwitchAndBulkActions();
            ReviewRunManagementControls(output, controller.ViewModel, bindings, captures, failures);
            Check(new WindowInteropHelper(window).Handle == IntPtr.Zero && activations == 0,
                "offscreen-review-created-or-activated-a-window");

            void ReviewDesktopDrawersAndRows()
            {
                var customize = Required<Expander>(page, "CustomizeWorkspaceExpander");
                var customizeButton = Required<Button>(page, "CustomizeWorkspaceButton");
                var picker = Required<ItemsControl>(page, "WorkspaceModulePicker");
                var graphs = Required<FrameworkElement>(page, "GraphsSurface");
                var gearFilter = Required<ComboBox>(page, "WorkspaceGearFilter");
                size = new Size(1920, 1080);
                model.ShowGraphs();
                model.WorkspaceComparisonMode = model.WorkspaceComparisonModes.First(mode => mode.Id == RunWorkspaceComparisonMode.Overlay);
                foreach (var preset in model.WorkspacePresets)
                {
                    bindings.Phase = "desktop/" + preset.Id;
                    model.SelectedWorkspacePreset = preset;
                    model.ResetWorkspaceCommand.Execute(null);
                    AwaitReady(model);
                    graphScroll.ScrollToHome(); Arrange(surface, size);
                    if (preset.Id == RunWorkspacePreset.Overview)
                        Check(model.WorkspacePanels.Select(module => module.Id).SequenceEqual(new[]
                        { "speed", "inputs", "rpm", "gforce-time", "power", "torque", "boost", "gforce", "tires" }),
                            "overview-missing-former-driving-graphs");
                    CheckFilledRows();
                    Capture("desktop-" + preset.Id.ToString().ToLowerInvariant());
                    CheckRenderedPlots();
                }

                size = new Size(2560, 1440);
                bindings.Phase = "desktop/mixed-widths";
                model.SelectedWorkspacePreset = model.WorkspacePresets.First(preset => preset.Id == RunWorkspacePreset.Overview);
                AwaitReady(model);
                graphScroll.ScrollToHome(); Arrange(surface, size);
                Check(model.WorkspacePanels.Take(3).Select(module => module.Width)
                    .SequenceEqual(new[] { RunWorkspaceWidth.Wide, RunWorkspaceWidth.Wide, RunWorkspaceWidth.Compact }),
                    "desktop-mixed-width-fixture-missing");
                CheckFilledRows(); Capture("desktop-mixed-widths");

                size = new Size(1920, 1080);
                bindings.Phase = "desktop/customize-open";
                model.SelectedWorkspacePreset = model.WorkspacePresets.First(preset => preset.Id == RunWorkspacePreset.Engine);
                AwaitReady(model); Arrange(surface, size);
                var savedWorkspace = JsonSerializer.Serialize(settings.RunWorkspace);
                graphScroll.ScrollToBottom(); Arrange(surface, size);
                var drawerReturnOffset = graphScroll.VerticalOffset;
                customizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Arrange(surface, size);
                Check(customize.IsExpanded, "customize-button-did-not-open");
                Check(Ancestors(customize).Contains(graphScroll) && !Ancestors(customize).Contains(graphs),
                    "customize-not-in-shared-scroll-or-in-image-export");
                Check(!Descendants(customize).OfType<ScrollViewer>().Any(viewer => Ancestors(picker).Contains(viewer)),
                    "module-picker-has-nested-scroll");
                var reset = Descendants(customize).OfType<Button>().First(button => ReferenceEquals(button.Command, model.ResetWorkspaceCommand));
                CheckReachableInGraphScroll(reset);
                graphScroll.ScrollToHome(); Arrange(surface, size);
                CheckHeader("customize-open");
                Capture("customize-top");

                var lastModule = picker.ItemContainerGenerator.ContainerFromIndex(picker.Items.Count - 1) as FrameworkElement
                    ?? throw new InvalidOperationException("Missing last graph module editor.");
                foreach (var control in Descendants(lastModule).OfType<Control>()
                             .Where(control => control is CheckBox or ComboBox or Button))
                    CheckReachableInGraphScroll(control);
                CheckReachableInGraphScroll(gearFilter);
                Check(gearFilter.TranslatePoint(new Point(0, gearFilter.ActualHeight), surface).Y <=
                      graphs.TranslatePoint(new Point(), surface).Y + 1.5, "customize-controls-overlap-graphs");
                CheckScrollClipping(customize);
                Capture("customize-bottom");

                size = new Size(720, 440);
                bindings.Phase = "compact/customize-resized";
                Arrange(surface, size);
                Check(customize.IsExpanded, "resize-closed-customize");
                CheckReachableInGraphScroll(gearFilter);
                CheckScrollClipping(customize);
                Capture("customize-resized");
                size = new Size(1920, 1080);
                bindings.Phase = "desktop/customize-return";
                Arrange(surface, size);
                Check(customize.IsExpanded && savedWorkspace == JsonSerializer.Serialize(settings.RunWorkspace),
                    "resize-changed-workspace-or-drawer-state");
                customizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Arrange(surface, size);
                Check(!customize.IsExpanded, "customize-button-did-not-close");
                Check(Math.Abs(graphScroll.VerticalOffset - Math.Min(drawerReturnOffset, graphScroll.ScrollableHeight)) < 1.5,
                    "closing-customize-lost-plot-scroll-context");
                CheckFilledRows(); Capture("customize-return"); CheckRenderedPlots();
                bindings.Phase = "desktop/side-by-side";
                model.WorkspaceComparisonMode = model.WorkspaceComparisonModes.First(mode => mode.Id == RunWorkspaceComparisonMode.SideBySide);
                AwaitReady(model); Arrange(surface, size);
                CheckFilledRows(); Capture("desktop-side-by-side");
                CheckRenderedPlots();
            }

            void ReviewSummarySwitch()
            {
                bindings.Phase = "desktop/summary-switch";
                size = new Size(1440, 900);
                Await(model.ShowReviewAsync(a, b));
                model.RemoveComparisonCommand.Execute(null); AwaitReady(model);
                model.ShowSummary(); model.SelectedStatisticGroup = "Overview"; model.StatisticsView = RunStatisticsView.Cards;
                Arrange(surface, size);
                var library = Required<ListBox>(page, "SavedRunList");
                var report = Required<FrameworkElement>(page, "RunReportSurface");
                var findings = Required<ItemsControl>(page, "FindingsList");
                var stats = Enumerable.Range(0, statisticCards.Items.Count).Select(index =>
                    statisticCards.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement
                    ?? throw new InvalidOperationException("A summary statistic has no visual container.")).ToArray();
                var findingViews = Enumerable.Range(0, findings.Items.Count).Select(index =>
                    findings.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement
                    ?? throw new InvalidOperationException("A summary finding has no visual container.")).ToArray();
                var oldValues = model.Statistics.Select(row => row.ValueA).ToArray();
                var unloaded = 0;
                RoutedEventHandler onUnloaded = (_, _) => unloaded++;
                foreach (var visual in stats.Append(report)) visual.Unloaded += onUnloaded;
                scroll.ScrollToVerticalOffset(Math.Min(180, scroll.ScrollableHeight)); Arrange(surface, size);
                var offset = scroll.VerticalOffset;
                Capture("summary-before-switch");
                var frames = 0;
                var monitor = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1) };
                monitor.Tick += (_, _) => CheckFrame();
                try
                {
                    monitor.Start();
                    library.SetCurrentValue(Selector.SelectedItemProperty, model.Library.Single(item => item.Id == b.Id));
                    Check(model.IsBusy && model.RunALabel.Contains(a.Name, StringComparison.Ordinal), "summary-not-retained-while-opening");
                    Check(oldValues.SequenceEqual(model.Statistics.Select(row => row.ValueA)), "summary-values-cleared-before-replacement");
                    CheckFrame();
                    // Capture without pumping the pending analysis continuation into this opening state.
                    surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(output, "runs-summary-opening-1440x900.png"))) encoder.Save(stream);
                    AwaitReady(model); Arrange(surface, size); CheckFrame();
                }
                finally
                {
                    monitor.Stop();
                    foreach (var visual in stats.Append(report)) visual.Unloaded -= onUnloaded;
                }
                Check(frames >= 2 && unloaded == 0, "summary-visuals-unloaded-during-selection");
                Check(!oldValues.SequenceEqual(model.Statistics.Select(row => row.ValueA)), "summary-kept-old-values");
                Check(model.SelectedRun?.Id == b.Id && model.RunALabel.Contains(b.Name, StringComparison.Ordinal), "summary-selection-not-committed");
                for (var index = 0; index < Math.Min(findingViews.Length, findings.Items.Count); index++)
                    Check(ReferenceEquals(findingViews[index], findings.ItemContainerGenerator.ContainerFromIndex(index)), "summary-recreated-finding-template");
                Check(Math.Abs(scroll.VerticalOffset - Math.Min(offset, scroll.ScrollableHeight)) < 1.5, "summary-switch-reset-scroll");
                Capture("summary-after-switch");

                void CheckFrame()
                {
                    frames++;
                    Check(model.HasRun && model.IsSummaryVisible && report.Visibility == Visibility.Visible && report.ActualHeight > 0,
                        "summary-report-disappeared-during-selection");
                    Check(statisticCards.Items.Count == stats.Length, "summary-statistics-became-empty");
                    for (var index = 0; index < stats.Length; index++)
                        Check(ReferenceEquals(stats[index], statisticCards.ItemContainerGenerator.ContainerFromIndex(index)),
                            "summary-recreated-statistic-template");
                    CheckHeader("summary-transition");
                }
            }

            void ReviewSavedRunSwitchAndBulkActions()
            {
                bindings.Phase = "desktop/saved-run-switch";
                size = new Size(1440, 900);
                var shorter = b with { Markers = [] };
                Await(model.ShowReviewAsync(a, shorter));
                model.RemoveComparisonCommand.Execute(null);
                AwaitReady(model);
                model.ShowGraphs();
                model.SelectedWorkspacePreset = model.WorkspacePresets.First(preset => preset.Id == RunWorkspacePreset.Engine);
                model.WorkspaceComparisonMode = model.WorkspaceComparisonModes.First(mode => mode.Id == RunWorkspaceComparisonMode.Overlay);
                AwaitReady(model); Arrange(surface, size);
                var library = Required<ListBox>(page, "SavedRunList");
                var libraryPane = Required<FrameworkElement>(page, "RunLibraryPane");
                var replacement = model.Library.Single(item => item.Id == shorter.Id);
                Check(model.HasRun && !model.HasComparison && library.IsEnabled, "single-run-selection-fixture");
                var containers = model.Library.ToDictionary(item => item.Id, item =>
                    library.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem
                    ?? throw new InvalidOperationException("Missing visible saved run card."));
                var heights = containers.ToDictionary(pair => pair.Key, pair => pair.Value.ActualHeight);
                Check(containers.Values.All(item => item.ActualHeight > 0), "run-card-height-not-measured");
                foreach (var pair in containers)
                {
                    var badge = Descendants(pair.Value).OfType<TextBlock>().Single(text => text.Text == "RUN A");
                    Check(badge.Visibility == (pair.Key == a.Id ? Visibility.Visible : Visibility.Hidden), "run-a-badge-space-not-reserved");
                }
                var cardsBefore = ModuleCards();
                var plotsBefore = model.WorkspacePanels.SelectMany(module => module.Plots).ToArray();
                var controlsBefore = cardsBefore.SelectMany(card => Descendants(card).OfType<RunChartView>()).ToArray();
                var dataBefore = plotsBefore.Select(plot => (plot.TimePanel, plot.AlternativePanel)).ToArray();
                graphScroll.ScrollToBottom(); Arrange(surface, size);
                var previousOffset = graphScroll.VerticalOffset;
                Check(previousOffset > 100, "run-switch-fixture-did-not-scroll");
                CheckBulkActions(libraryPane);
                Capture("saved-run-before-switch");
                var disabledDuringLoad = false;
                var observedBusy = false;
                DependencyPropertyChangedEventHandler enabledChanged = (_, change) => disabledDuringLoad |= !(bool)change.NewValue;
                System.ComponentModel.PropertyChangedEventHandler modelChanged = (_, change) =>
                {
                    if (change.PropertyName == nameof(RunsViewModel.IsBusy)) observedBusy |= model.IsBusy;
                };
                library.IsEnabledChanged += enabledChanged;
                model.PropertyChanged += modelChanged;
                try
                {
                    // Exercise the actual two-way selector binding rather than opening through a fixture API.
                    library.SetCurrentValue(Selector.SelectedItemProperty, replacement);
                    AwaitReady(model); Arrange(surface, size);
                }
                finally
                {
                    library.IsEnabledChanged -= enabledChanged;
                    model.PropertyChanged -= modelChanged;
                }
                Check(observedBusy && !disabledDuringLoad && library.IsEnabled, "saved-list-disabled-or-load-not-exercised");
                Check(model.SelectedRun?.Id == shorter.Id && model.RunALabel.Contains(shorter.Name, StringComparison.Ordinal) &&
                      model.IsGraphWorkspaceOpen && !model.HasComparison, "saved-selection-did-not-update-open-graphs");
                Check(Math.Abs(graphScroll.VerticalOffset - Math.Min(previousOffset, graphScroll.ScrollableHeight)) <= 1.5,
                    "saved-selection-reset-graph-scroll");
                Check(cardsBefore.SequenceEqual(ModuleCards()), "saved-selection-recreated-module-cards");
                var plotsAfter = model.WorkspacePanels.SelectMany(module => module.Plots).ToArray();
                Check(plotsBefore.Length == plotsAfter.Length && plotsBefore.Zip(plotsAfter).All(pair => ReferenceEquals(pair.First, pair.Second)),
                    "saved-selection-recreated-plot-models");
                Check(controlsBefore.SequenceEqual(ModuleCards().SelectMany(card => Descendants(card).OfType<RunChartView>())),
                    "saved-selection-recreated-time-chart-controls");
                Check(plotsAfter.Length == dataBefore.Length && plotsAfter.Zip(dataBefore).All(pair =>
                    !ReferenceEquals(pair.First.TimePanel, pair.Second.TimePanel) || !ReferenceEquals(pair.First.AlternativePanel, pair.Second.AlternativePanel)),
                    "saved-selection-kept-stale-plot-data");
                foreach (var pair in containers)
                {
                    var current = library.ItemContainerGenerator.ContainerFromItem(model.Library.Single(item => item.Id == pair.Key)) as ListBoxItem;
                    Check(ReferenceEquals(current, pair.Value) && Math.Abs(pair.Value.ActualHeight - heights[pair.Key]) <= 1.5,
                        "selection-changed-saved-card-height-or-container");
                    var badge = Descendants(pair.Value).OfType<TextBlock>().Single(text => text.Text == "RUN A");
                    Check(badge.Visibility == (pair.Key == shorter.Id ? Visibility.Visible : Visibility.Hidden), "run-a-badge-did-not-follow-selection");
                }
                Capture("saved-run-after-switch");

                size = new Size(720, 440);
                bindings.Phase = "compact/bulk-library-actions";
                Arrange(surface, size);
                Required<Button>(page, "CompactLibraryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Arrange(surface, size);
                Check(Required<Expander>(page, "CompactRunLibraryDrawer").IsExpanded, "compact-library-did-not-open");
                CheckBulkActions(Required<FrameworkElement>(page, "CompactRunLibrary"));
                var compactSearch = Required<RunLibrarySearch>(page, "CompactLibrarySearchControl");
                var compactSearchBox = Required<TextBox>(compactSearch, "SearchBox");
                var compactSelector = Required<ComboBox>(page, "SavedRunSelector");
                Check(Within(compactSearch, surface), "compact-run-search-clipped");
                Check(Within(compactSelector, surface), "compact-run-selector-clipped");
                var compactSearchBounds = compactSearch.TransformToAncestor(surface).TransformBounds(new Rect(compactSearch.RenderSize));
                var compactSelectorBounds = compactSelector.TransformToAncestor(surface).TransformBounds(new Rect(compactSelector.RenderSize));
                Check(compactSearchBounds.Bottom <= compactSelectorBounds.Top + .5,
                    "compact-search-overlaps-run-selector");
                foreach (var button in Descendants(Required<FrameworkElement>(page, "CompactRunLibrary")).OfType<Button>()
                             .Where(button => button.Visibility == Visibility.Visible &&
                                 button.Content is string label && new[] { "Import", "Refresh", "Export all", "Delete all", "Undo" }.Contains(label)))
                {
                    var buttonBounds = button.TransformToAncestor(surface).TransformBounds(new Rect(button.RenderSize));
                    var overlap = Rect.Intersect(compactSelectorBounds, buttonBounds);
                    Check(overlap.IsEmpty || overlap.Width <= .5 || overlap.Height <= .5,
                        "compact-selector-overlaps-library-action/" + button.Content);
                }
                foreach (var target in new Control[] { compactSearchBox, compactSelector })
                {
                    Check(target.Style is not null && target.Template is not null && target.IsEnabled && target.Focusable && target.IsTabStop,
                        "compact-library-input-not-themed-or-keyboard-reachable/" + target.Name);
                    var center = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), surface);
                    var hit = VisualTreeHelper.HitTest(surface, center)?.VisualHit;
                    Check(hit is not null && (ReferenceEquals(hit, target) || Ancestors(hit).Contains(target)),
                        "compact-library-input-obstructed/" + target.Name);
                }
                Capture("compact-bulk-library");

                OrbitSurface[] ModuleCards()
                {
                    var host = Required<ItemsControl>(page, "ChartPanels");
                    var layout = Descendants(host).OfType<RunWorkspacePanelLayout>().First();
                    return layout.Children.OfType<ContentPresenter>()
                        .Select(container => Descendants(container).OfType<OrbitSurface>().First()).ToArray();
                }

                void CheckBulkActions(FrameworkElement pane)
                {
                    foreach (var label in new[] { "Export all", "Delete all" })
                    {
                        var button = Descendants(pane).OfType<Button>().Single(button => Equals(button.Content, label));
                        Check(Within(button, surface) && Within(button, pane) && button.IsEnabled && button.Focusable && button.IsTabStop,
                            "bulk-action-clipped-or-not-reachable/" + label);
                    }
                }
            }

            void CheckFilledRows()
            {
                var host = Required<ItemsControl>(page, "ChartPanels");
                var layout = Descendants(host).OfType<RunWorkspacePanelLayout>().First();
                Check(layout.FillRows, "graph-cards-do-not-fill-rows");
                var cards = layout.Children.OfType<ContentPresenter>()
                    .Where(container => container.Visibility == Visibility.Visible)
                    .Select(container => Descendants(container).OfType<OrbitSurface>().First())
                    .Select(card => card.TransformToAncestor(layout).TransformBounds(new Rect(card.RenderSize))).ToArray();
                Check(cards.Length == model.WorkspacePanels.Count, "module-card-count");
                foreach (var row in cards.GroupBy(bounds => Math.Round(bounds.Top, 3)))
                {
                    var items = row.ToArray();
                    Check(Math.Abs(items[0].Left) <= 1.5 && Math.Abs(items[^1].Right - layout.ActualWidth) <= 1.5,
                        "card-row-leaves-unused-width");
                    Check(items.All(item => Math.Abs(item.Height - items[0].Height) <= 1.5), "card-row-heights-differ");
                    for (var index = 1; index < items.Length; index++)
                        Check(Math.Abs(items[index].Left - items[index - 1].Right - layout.Gap) <= 1.5,
                            "cards-overlap-or-have-extra-gap");
                }
                for (var index = 1; index < cards.Length; index++)
                    Check(cards[index].Top >= cards[index - 1].Bottom + layout.Gap - 1.5 ||
                          Math.Abs(cards[index].Top - cards[index - 1].Top) <= 1.5 && cards[index].Left > cards[index - 1].Left,
                        "card-reading-order-or-row-overlap");
            }

            void CheckReachableInGraphScroll(Control control)
            {
                ScrollIntoView(control, graphScroll, surface, size);
                var viewport = Descendants(graphScroll).OfType<ScrollContentPresenter>().First();
                Check(Within(control, viewport) && control.Focusable && control.IsTabStop,
                    "customize-control-clipped-or-not-keyboard-reachable/" + control.GetType().Name);
                CheckHeader("customize-scroll");
            }

            void CheckScrollClipping(FrameworkElement drawer)
            {
                var viewport = Descendants(graphScroll).OfType<ScrollContentPresenter>().First();
                var bounds = viewport.TransformToAncestor(surface).TransformBounds(new Rect(viewport.RenderSize));
                foreach (var point in new[] { new Point(bounds.Left + bounds.Width / 2, bounds.Top - 2),
                             new Point(bounds.Left + bounds.Width / 2, bounds.Bottom + 2) })
                {
                    var hit = VisualTreeHelper.HitTest(surface, point)?.VisualHit;
                    Check(hit is null || !ReferenceEquals(hit, drawer) && !Ancestors(hit).Contains(drawer),
                        "customize-content-hits-outside-graph-viewport");
                }
            }

            void Capture(string phase)
            {
                bindings.Phase = $"{size.Width:0}/{phase}";
                Arrange(surface, size);
                foreach (var chart in Descendants(graphScroll).OfType<RunChartView>())
                {
                    chart.RenderOffscreen = true;
                    chart.InvalidateVisual();
                }
                Arrange(surface, size);
                CheckHeader(phase);
                var file = $"runs-{phase}-{size.Width:0}x{size.Height:0}.png";
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(output, file))) encoder.Save(stream);
                var inspection = ReviewDiagnostics.Inspect(surface, file, "synthetic-run-workspace", "runs", phase,
                    96, (int)size.Width, (int)size.Height, 0);
                captures.Add(inspection);
                Check(inspection.Labels.OverflowCount == 0, "text-overflow");
            }

            void CheckHeader(string phase)
            {
                Check(Within(header, surface) && Within(record, surface), phase + "/record-stop-control-outside-viewport");
                Check(ReferenceEquals(record.Command, model.ToggleRecordingCommand), phase + "/record-stop-command-detached");
                Check(!Ancestors(header).Contains(scroll) && !Ancestors(header).Contains(graphScroll), phase + "/record-stop-header-scrolls-away");
            }

            void CheckRenderedPlots()
            {
                var charts = Descendants(graphScroll).OfType<RunChartView>().Where(chart => chart.ActualWidth > 0 && chart.ActualHeight > 0).ToArray();
                Check(charts.Length > 0, "no-time-chart-controls");
                Check(charts.Any(chart => VisualTreeHelper.GetDrawing(chart) is { } drawing && drawing.Children.Count > 0), "time-charts-have-no-drawing");
                var viewport = new Rect(graphScroll.TranslatePoint(new Point(), surface), graphScroll.RenderSize);
                var plots = charts.Cast<FrameworkElement>().Concat(Descendants(graphScroll).OfType<RunAlternativePlotView>());
                Check(plots.Any(chart =>
                {
                    if (chart.Visibility != Visibility.Visible || VisualTreeHelper.GetDrawing(chart) is not { Children.Count: > 0 }) return false;
                    var bounds = new Rect(chart.TranslatePoint(new Point(), surface), chart.RenderSize);
                    bounds.Intersect(viewport);
                    return !bounds.IsEmpty && bounds.Width >= 180 && bounds.Height >= 80;
                }), "no-readable-graph-in-viewport");
            }

            void Check(bool condition, string code)
            {
                if (!condition) failures.Add(bindings.Phase + "/" + code);
            }
        }
        catch (Exception exception)
        {
            failures.Add(bindings.Phase + "/" + exception.GetType().Name);
            for (Exception? detail = exception; detail is not null; detail = detail.InnerException)
                failures.Add(detail.GetType().Name + ": " + detail.Message);
            failures.AddRange(new StackTrace(exception, false).GetFrames().Select(frame => frame.GetMethod())
                .Where(method => method is not null).Take(8).Select(method => method!.DeclaringType?.Name + "." + method.Name));
        }
        finally
        {
            window?.Close();
            if (controller is not null) Await(controller.DisposeAsync().AsTask());
            application.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousSynchronization);
        }
        if (bindings.TotalCount != 0) failures.Add("binding-diagnostics");
        File.WriteAllText(Path.Combine(output, "runs-workspace-review.json"), JsonSerializer.Serialize(new
        {
            input = "Synthetic runs and isolated output-local settings. Detached app content and isolated modern/legacy Runs controls with the original diagnostics fixture at their host and a dedicated Runs model on the controls. No shown window, live service, game access, or desktop capture.",
            captures,
            failures,
            ownWindowActivations = activations,
            bindingDiagnosticCount = bindings.TotalCount,
            bindingDiagnostics = bindings.Messages
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Runs workspace review: {(failures.Count == 0 ? "PASS" : "FAIL")}; {captures.Count} offscreen images; {failures.Count} failures; {bindings.TotalCount} binding diagnostics.");
        return failures.Count == 0 ? 0 : 2;
    }

    private static T Required<T>(FrameworkElement scope, string name) where T : class =>
        scope.FindName(name) as T ?? throw new InvalidOperationException("Missing review control: " + name);

    private static void ScrollIntoView(FrameworkElement target, ScrollViewer scroll, FrameworkElement surface, Size size)
    {
        Arrange(surface, size);
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + target.TranslatePoint(new Point(), scroll).Y);
        Arrange(surface, size);
    }

    private static void Arrange(FrameworkElement surface, Size size)
    {
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
        surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
    }

    private static bool Within(FrameworkElement element, FrameworkElement surface)
    {
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        var bounds = element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
        return bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= surface.ActualWidth + .5 && bounds.Bottom <= surface.ActualHeight + .5;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent)) yield return parent;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static void AwaitReady(RunsViewModel model)
    {
        var deadline = Stopwatch.StartNew();
        do { Await(Task.Delay(20)); } while ((model.IsBusy || model.IsPreparingCharts) && deadline.Elapsed < TimeSpan.FromSeconds(8));
        Await(Task.Delay(40));
        if (model.IsBusy || model.IsPreparingCharts || model.HasError) throw new InvalidOperationException("Synthetic run analysis did not settle.");
    }

    private static RecordedRun CreateRun(string name, double acceleration, bool gap)
    {
        var samples = Enumerable.Range(0, 1201).Select(index =>
        {
            var time = index / 50d;
            var speed = (float)(time < 12 ? time * 3 * acceleration : Math.Max(10, 36 * acceleration - (time - 12) * 1.4));
            var slip = time is > 2 and < 5 ? (float)(4 + Math.Sin(time * 7)) : 0;
            return new RunSample
            {
                ElapsedSeconds = time,
                IsDriving = true,
                Segment = 0,
                FrontRadiusMeters = .34,
                RearRadiusMeters = .35,
                WheelSpeedMetersPerSecond = speed + slip,
                State = new VehicleState
                {
                    IsRaceOn = true,
                    GameTimestampMilliseconds = (uint)(index * 20),
                    ReceivedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(time),
                    CarOrdinal = 101,
                    Drivetrain = DrivetrainType.RearWheelDrive,
                    NumCylinders = 8,
                    GroundSpeedMetersPerSecond = speed,
                    WheelRotationRadiansPerSecond = new(speed / .34f, speed / .34f, (speed + slip) / .35f, (speed + slip) / .35f),
                    TireSlipRatio = new(0, 0, slip / 10, slip / 10),
                    TireSlipAngle = default,
                    NormalizedSuspensionTravel = new(.5f, .5f, .5f, .5f),
                    LateralAccelerationMetersPerSecondSquared = (float)Math.Sin(time / 2) * 4,
                    LongitudinalAccelerationMetersPerSecondSquared = time < 12 ? 3 : -1.4f,
                    EngineRpm = (float)(1800 + speed * 120 + Math.Sin(time * 4) * 180),
                    EngineMaximumRpm = 8200,
                    Gear = time < 5 ? TransmissionGear.Second : TransmissionGear.Third,
                    Steering = (sbyte)(Math.Sin(time) * (time < 12 ? 4 : 48)),
                    Accelerator = time < 12 ? (byte)255 : (byte)100,
                    Brake = time is > 18 and < 20 ? (byte)120 : (byte)0,
                    BoostPressurePsi = time < 12 ? 14 : -8,
                    PowerWatts = speed * 8500,
                    TorqueNm = 650,
                    TireTemperatureFahrenheit = new(170 + (float)time, 172 + (float)time, 180 + (float)time * 2, 181 + (float)time * 2)
                }
            };
        }).Where(sample => !gap || sample.ElapsedSeconds is < 16 or > 16.6).ToArray();
        return new RecordedRun
        {
            Name = name,
            Tune = acceleration > 1 ? "Shorter final drive" : "Road tune",
            Notes = "Synthetic comparison fixture.",
            StartedAtUtc = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
            FinishReason = "Stopped by you",
            Markers = [new(4.5, "Wheelspin"), new(9, "Shift")],
            Samples = samples
        };
    }

    private sealed class ResourceApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
}
