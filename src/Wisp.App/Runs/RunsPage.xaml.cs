using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using System.Windows.Data;

namespace Wisp.App.Runs;

public partial class RunsPage : RunsPageBase
{
    private bool _graphDrawerSession;
    private double _graphReturnOffset;
    private long _graphDrawerRevision;
    private ScrollViewer? _comparisonReturnScroll;
    private double _comparisonReturnOffset;
    private long _comparisonDrawerRevision;
    public static readonly DependencyProperty IsWorkspaceShortProperty = DependencyProperty.Register(
        nameof(IsWorkspaceShort), typeof(bool), typeof(RunsPage), new PropertyMetadata(false));

    public bool IsWorkspaceShort => (bool)GetValue(IsWorkspaceShortProperty);

    protected override bool UsesModularWorkspace => true;
    protected override FrameworkElement ReportFocusTarget => LapReviewExpander.IsExpanded ? LapReviewExpander : base.ReportFocusTarget;

    public RunsPage()
    {
        InitializeComponent();
        InitializeRunsPage(new(ShortcutCaptureButton, MarkerShortcutCaptureButton, GraphScroll, GraphPicker,
            RunHeading, RunsScroll, GraphsSurface, ShowGraphsButton));
        ShowGraphsButton.Click += (_, _) => { _graphReturnOffset = 0; CloseDrawers(); };
    }

    private void RunComparisonButton_Click(object sender, RoutedEventArgs e) =>
        RunComparisonControls.IsExpanded = !RunComparisonControls.IsExpanded;

    private void RunComparisonControls_Changed(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, RunComparisonControls)) return;
        var open = RunComparisonControls.IsExpanded;
        if (open) CloseDrawers(RunComparisonControls);
        UpdateComparisonScroll(open);
        RunComparisonButton.Content = open ? "Done" : "Compare";
        RunComparisonButton.ToolTip = open ? "Close comparison controls" : "Choose Run B or match a speed range";
        System.Windows.Automation.AutomationProperties.SetName(RunComparisonButton,
            open ? "Close run comparison controls" : "Open run comparison controls");
    }

    private void UpdateComparisonScroll(bool open)
    {
        var revision = ++_comparisonDrawerRevision;
        var scroll = open ? (Model?.IsGraphWorkspaceOpen == true ? GraphScroll : RunsScroll) : _comparisonReturnScroll;
        if (scroll is null) return;
        if (open)
        {
            _comparisonReturnScroll = scroll;
            _comparisonReturnOffset = scroll.VerticalOffset;
        }
        var returnOffset = _comparisonReturnOffset;
        if (!open) _comparisonReturnScroll = null;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (revision != _comparisonDrawerRevision || RunComparisonControls.IsExpanded != open) return;
            scroll.UpdateLayout();
            scroll.ScrollToVerticalOffset(open ? 0 : Math.Clamp(returnOffset, 0, scroll.ScrollableHeight));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateComparisonHost()
    {
        if (RunComparisonControls is null) return;
        var host = Model?.IsGraphWorkspaceOpen == true ? RunGraphContent : RunSummaryContent;
        if (ReferenceEquals(RunComparisonControls.Parent, host)) return;
        // Keep one editor and one active scrolling surface in both workspaces.
        RunComparisonControls.IsExpanded = false;
        if (RunComparisonControls.Parent is Panel previous) previous.Children.Remove(RunComparisonControls);
        host.Children.Insert(0, RunComparisonControls);
    }

    private void RecordingOptionsButton_Click(object sender, RoutedEventArgs e) =>
        RecordingOptionsExpander.IsExpanded = !RecordingOptionsExpander.IsExpanded;

    private void CompactLibraryButton_Click(object sender, RoutedEventArgs e) =>
        CompactRunLibraryDrawer.IsExpanded = !CompactRunLibraryDrawer.IsExpanded;

    private void SavedRunSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && e.AddedItems.Count > 0 && CompactRunLibraryDrawer is not null)
            CompactRunLibraryDrawer.IsExpanded = false;
    }

    private void CustomizeWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, CompactGraphOptionsButton) &&
            (CustomizeWorkspaceExpander.IsExpanded || GraphRangeExpander.IsExpanded))
            CloseDrawers();
        else
            CustomizeWorkspaceExpander.IsExpanded = !CustomizeWorkspaceExpander.IsExpanded;
    }

    private void GraphRangeButton_Click(object sender, RoutedEventArgs e) =>
        GraphRangeExpander.IsExpanded = !GraphRangeExpander.IsExpanded;

    private void Drawer_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource) || sender is not Expander drawer) return;
        var graphDrawer = ReferenceEquals(drawer, CustomizeWorkspaceExpander) || ReferenceEquals(drawer, GraphRangeExpander);
        if (graphDrawer && !_graphDrawerSession)
        {
            _graphReturnOffset = GraphScroll.VerticalOffset;
            _graphDrawerSession = true;
        }
        CloseDrawers(drawer);
        UpdateGraphDrawerButton();
        if (graphDrawer)
        {
            var revision = ++_graphDrawerRevision;
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (revision != _graphDrawerRevision || !drawer.IsExpanded || GraphScroll.Content is not UIElement content) return;
                GraphScroll.UpdateLayout();
                GraphScroll.ScrollToVerticalOffset(Math.Max(0, drawer.TranslatePoint(new Point(), content).Y));
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void GraphDrawer_Collapsed(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource)) return;
        UpdateGraphDrawerButton();
        var revision = ++_graphDrawerRevision;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (revision != _graphDrawerRevision || CustomizeWorkspaceExpander.IsExpanded || GraphRangeExpander.IsExpanded) return;
            GraphScroll.UpdateLayout();
            GraphScroll.ScrollToVerticalOffset(Math.Clamp(_graphReturnOffset, 0, GraphScroll.ScrollableHeight));
            _graphDrawerSession = false;
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnModelPropertyChanged(System.ComponentModel.PropertyChangedEventArgs? change)
    {
        if (change is null || change.PropertyName == nameof(RunsViewModel.IsGraphWorkspaceOpen)) UpdateComparisonHost();
        if (change?.PropertyName == nameof(RunsViewModel.SelectedWorkspacePreset)) _graphReturnOffset = 0;
        if (change is null) { _graphDrawerRevision++; _graphDrawerSession = false; _graphReturnOffset = 0; }
    }

    private void UpdateGraphDrawerButton()
    {
        if (CompactGraphOptionsButton is null || CustomizeWorkspaceExpander is null || GraphRangeExpander is null) return;
        var open = CustomizeWorkspaceExpander.IsExpanded || GraphRangeExpander.IsExpanded;
        CompactGraphOptionsButton.Content = open ? "Done" : "Graphs";
        System.Windows.Automation.AutomationProperties.SetName(CompactGraphOptionsButton,
            open ? "Close graph options" : "Graph preset, layout and interval options");
        CustomizeWorkspaceButton.Content = CustomizeWorkspaceExpander.IsExpanded ? "Done" : "Customize";
        System.Windows.Automation.AutomationProperties.SetName(CustomizeWorkspaceButton,
            CustomizeWorkspaceExpander.IsExpanded ? "Close graph customization" : "Customize visible graph modules");
        GraphRangeButton.Content = GraphRangeExpander.IsExpanded ? "Done" : "Range & cursor";
        System.Windows.Automation.AutomationProperties.SetName(GraphRangeButton,
            GraphRangeExpander.IsExpanded ? "Close range and cursor controls" : "Open range and cursor controls");
    }

    private void CloseDrawers(Expander? except = null)
    {
        foreach (var drawer in new[] { RecordingOptionsExpander, RunComparisonControls, CustomizeWorkspaceExpander, GraphRangeExpander, CompactRunLibraryDrawer })
            if (drawer is not null && !ReferenceEquals(drawer, except)) drawer.IsExpanded = false;
    }

    private void RunsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 960;
        SetLibraryCompact(compact);
        RunLibraryColumn.Width = new GridLength(compact ? 0 : 228);
        RunLibraryGap.Width = new GridLength(compact ? 0 : 14);
        RunLibraryPane.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactLibraryButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        if (!compact) CompactRunLibraryDrawer.IsExpanded = false;
        var shortWorkspace = e.NewSize.Height < 350;
        SetValue(IsWorkspaceShortProperty, shortWorkspace);
        GraphPicker.Visibility = shortWorkspace ? Visibility.Collapsed : Visibility.Visible;
        GraphNavigationBar.Visibility = shortWorkspace ? Visibility.Collapsed : Visibility.Visible;
        CompactGraphOptions.Visibility = shortWorkspace ? Visibility.Visible : Visibility.Collapsed;
        CompactGraphOptionsButton.Visibility = shortWorkspace ? Visibility.Visible : Visibility.Collapsed;
        RunRecordingHeader.Padding = new Thickness(shortWorkspace ? 4 : 10);
        RunRecordingHeader.Margin = new Thickness(0, 0, 0, shortWorkspace ? 4 : 8);
        RunRecordButton.Padding = shortWorkspace ? new Thickness(10, 4, 10, 4) : new Thickness(14, 7, 14, 7);
        RunRecordButton.MinHeight = 30;
        RunWorkspaceToolbar.Margin = new Thickness(0, 0, 0, shortWorkspace ? 4 : 8);
        var drawerHeight = Math.Clamp(e.NewSize.Height * 0.36, 85, 200);
        RecordingOptionsScroll.MaxHeight = drawerHeight;
    }
}

public sealed class RunChartHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double height && double.IsFinite(height) && height > 0 ? Math.Clamp(height - 50, 180, 270) : 270d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
