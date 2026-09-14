using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace Wisp.App;

public partial class MainWindow : ControlPanelWindow
{
    private bool _navigationOpen = true;
    private bool _updatingDashboardLayout;
    private WindowState _windowStateBeforeDisplay;
    private Rect _boundsBeforeDisplay;
    private bool _compactPreviewOpen;
    private readonly AppController _dashboardController;
    private bool _settingDisplayPreference;
    internal bool IsDashboardDisplayMode { get; private set; }

    public MainWindow(AppController controller) : base(controller)
    {
        _dashboardController = controller;
        InitializeComponent();
        PageScrollRouting.SetIsEnabled(ShellRoot, true);
        InitializeControlPanel();
        InitializeApplicationStyle();
        ResizableDisplayToggle.IsChecked = controller.Settings.ResizableDashboardDisplay;
        ShellRoot.SizeChanged += (_, _) => UpdateCompactNavigation();
    }

    private void DashboardDisplay_Click(object sender, RoutedEventArgs e) =>
        SetDashboardDisplayMode(!IsDashboardDisplayMode);

    internal void SetDashboardDisplayMode(bool enabled)
    {
        if (enabled == IsDashboardDisplayMode) return;
        if (enabled)
        {
            _windowStateBeforeDisplay = WindowState;
            _boundsBeforeDisplay = WindowState == WindowState.Normal && IsLoaded
                ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        }
        IsDashboardDisplayMode = enabled;
        CloseFeatureTour();
        RefreshFeatureTour();
        RootTabs.SelectedItem = DashboardTab;
        DashboardToolbar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        TitleBar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ShellFooter.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ShellTitleRow.Height = new GridLength(enabled ? 0 : 56);
        ShellFooterRow.Height = new GridLength(enabled ? 0 : 34);
        DockToggleHost.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        DashboardVehicleSection.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        DashboardGaugePanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        DashboardActions.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ResetPeaksButton.Visibility = enabled ? Visibility.Hidden : Visibility.Visible;
        DashboardModeHint.Text = enabled ? "DASHBOARD  ·  ESC TO RETURN" : "DASHBOARD";
        DashboardDisplayButton.Content = "Exit display mode";
        AutomationProperties.SetName(DashboardDisplayButton, enabled ? "Exit dashboard display mode" : "Enter dashboard display mode");
        ConfigureDashboardContent();
        DashboardViewport.ScrollToTop();
        ConfigureDisplayChrome();
        ApplySidebarLayout(_navigationOpen, false);
        if (IsLoaded)
        {
            if (enabled)
            {
                WindowState = _dashboardController.Settings.ResizableDashboardDisplay ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                WindowState = WindowState.Normal;
                if (!_boundsBeforeDisplay.IsEmpty)
                {
                    Left = _boundsBeforeDisplay.Left;
                    Top = _boundsBeforeDisplay.Top;
                    Width = _boundsBeforeDisplay.Width;
                    Height = _boundsBeforeDisplay.Height;
                }
                WindowState = _windowStateBeforeDisplay;
            }
        }
        FitToCurrentWorkArea();
        UpdateDashboardLayout();
    }

    protected override Size MinimumControlPanelSize => IsDashboardDisplayMode ? new(440, 280) : new(720, 440);

    private void ConfigureDisplayChrome()
    {
        FitToCurrentWorkArea();
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.CaptionHeight = IsDashboardDisplayMode ? 0 : 55;
    }

    private void ConfigureDashboardContent()
    {
        var fillScreen = IsDashboardDisplayMode && !_dashboardController.Settings.ResizableDashboardDisplay;
        DashboardContent.Width = fillScreen ? 1280 : double.NaN;
        DashboardContent.VerticalAlignment = fillScreen ? VerticalAlignment.Center : VerticalAlignment.Top;
        DashboardSizeButton.Content = fillScreen ? "Resizable" : "Fill screen";
        UpdateDashboardModeHint(DashboardViewport.ActualWidth);
    }

    private void UpdateDashboardModeHint(double width)
    {
        var resizable = _dashboardController.Settings.ResizableDashboardDisplay;
        DashboardModeHint.Text = width < 640
            ? resizable ? "Drag to move" : "Esc to return"
            : resizable ? "DASHBOARD · DRAG TO MOVE" : "DASHBOARD · ESC TO RETURN";
    }

    private void DashboardSize_Click(object sender, RoutedEventArgs e) =>
        SetResizableDisplay(!_dashboardController.Settings.ResizableDashboardDisplay);

    private void ResizableDisplay_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingDisplayPreference && ResizableDisplayToggle is not null)
            SetResizableDisplay(ResizableDisplayToggle.IsChecked == true);
    }

    internal void SetResizableDisplay(bool enabled)
    {
        _dashboardController.SetResizableDashboardDisplay(enabled);
        _settingDisplayPreference = true;
        try { ResizableDisplayToggle.IsChecked = enabled; }
        finally { _settingDisplayPreference = false; }
        ConfigureDashboardContent();
        if (IsDashboardDisplayMode && IsLoaded)
            WindowState = enabled ? WindowState.Normal : WindowState.Maximized;
        FitToCurrentWorkArea();
        UpdateDashboardLayout();
    }

    private void DisplayToolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsDashboardDisplayMode || !_dashboardController.Settings.ResizableDashboardDisplay ||
            e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        for (DependencyObject? current = e.OriginalSource as DependencyObject;
             current is not null && current != DashboardToolbar;)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase) return;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        if (IsLoaded) DragMove();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (HudProfileDialog.Visibility != Visibility.Visible && ApplicationUpdateConfirmation.Visibility != Visibility.Visible &&
            ((e.Key == Key.F11 && RootTabs.SelectedItem == DashboardTab) || (e.Key == Key.Escape && IsDashboardDisplayMode)))
        {
            SetDashboardDisplayMode(e.Key != Key.Escape && !IsDashboardDisplayMode);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private void DashboardViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDashboardLayout();
    private void DashboardContent_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDashboardLayout();

    private void AppearanceWorkspace_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAppearanceLayout();
    private void AppearancePreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        _compactPreviewOpen = !_compactPreviewOpen;
        UpdateAppearanceLayout();
    }

    private void UpdateAppearanceLayout()
    {
        if (AppearanceWorkspace is null || AppearanceEditorPane is null) return;
        var compact = AppearanceWorkspace.ActualWidth < 1100;
        AppearancePreviewToggleRow.Height = compact ? GridLength.Auto : new GridLength(0);
        AppearancePreviewToggle.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        AppearancePreviewColumn.Width = compact ? new GridLength(0) : new GridLength(0.9, GridUnitType.Star);
        AppearanceGapColumn.Width = new GridLength(compact ? 0 : 22);
        AppearanceEditorColumn.Width = new GridLength(compact ? 1 : 1.1, GridUnitType.Star);
        System.Windows.Controls.Grid.SetColumn(AppearancePreviewPane, compact ? 2 : 0);
        AppearancePreviewPane.Visibility = !compact || _compactPreviewOpen ? Visibility.Visible : Visibility.Collapsed;
        AppearanceEditorPane.Visibility = compact && _compactPreviewOpen ? Visibility.Collapsed : Visibility.Visible;
        AppearancePreviewToggle.Content = _compactPreviewOpen ? "Back to appearance controls" : "Show HUD preview";
        if (AppearancePreviewHeader is null || AppearancePreviewActions is null || HudPreviewSurface is null) return;
        var padding = AppearancePreviewCard.Padding;
        var previewWidth = AppearancePreviewPane.ActualWidth - padding.Left - padding.Right - 18;
        var availableHeight = AppearancePreviewPane.ActualHeight - AppearancePreviewHeader.ActualHeight -
            AppearancePreviewActions.ActualHeight - padding.Top - padding.Bottom - 16;
        // Keep the artwork proportional while giving it the space left above its actions.
        var previewHeight = Math.Clamp(availableHeight, 240, Math.Max(240, previewWidth * 0.94));
        if (Math.Abs(HudPreviewSurface.Height - previewHeight) > 0.5)
            HudPreviewSurface.Height = previewHeight;
    }

    internal void PrepareAppearanceForFeatureTour()
    {
        _compactPreviewOpen = false;
        UpdateAppearanceLayout();
    }

    private void UpdateDashboardLayout()
    {
        if (_updatingDashboardLayout || DashboardViewport is null || OrbitInstrument is null) return;
        _updatingDashboardLayout = true;
        try
        {
            var fillScreen = IsDashboardDisplayMode && !_dashboardController.Settings.ResizableDashboardDisplay;
            UpdateDashboardModeHint(DashboardViewport.ActualWidth);
            var width = fillScreen ? 1280 : DashboardViewport.ActualWidth;
            var wide = width >= 1100;
            OrbitInstrument.Shape = wide ? OrbitSurfaceShape.Swept : OrbitSurfaceShape.Card;
            OrbitInstrument.Padding = new Thickness(wide ? 112 : 24, 6, wide ? 62 : 24, 12);
            if (DashboardSpeedMetrics is not null)
            {
                // Inset only the speed block inside its existing column. Other
                // metrics and connection status keep their original positions.
                var previous = DashboardSpeedMetrics.Margin.Left;
                var inset = 0d;
                if (wide && DashboardInstruments.ActualWidth > 0 && DashboardSpeedMetrics.ActualHeight > 0)
                {
                    // Reconstruct the column slot from its parent. A previous
                    // wide inset can temporarily leave the child with zero width.
                    var count = DashboardInstruments.Children.Cast<UIElement>().Count(child => child.Visibility != Visibility.Collapsed);
                    var columns = DashboardColumns.ColumnCount(DashboardInstruments.ActualWidth,
                        DashboardInstruments.MinimumColumnWidth, DashboardInstruments.Gap, DashboardInstruments.MaximumColumns, count);
                    var cellWidth = Math.Max(0, (DashboardInstruments.ActualWidth - DashboardInstruments.Gap * (columns - 1)) / columns);
                    var cell = DashboardInstruments.TransformToAncestor(OrbitInstrument)
                        .TransformBounds(new Rect(0, 0, cellWidth, DashboardSpeedMetrics.ActualHeight));
                    var edge = OrbitInstrument.BorderThickness;
                    var clearance = 8 + Math.Max(Math.Max(edge.Left, edge.Right), Math.Max(edge.Top, edge.Bottom));
                    inset = DashboardContourLayout.SpeedInset(OrbitInstrument.RenderSize, cell, clearance);
                }
                if (Math.Abs(previous - inset) > 0.01)
                    DashboardSpeedMetrics.Margin = new Thickness(inset, 0, 0, 0);
            }
            var scale = fillScreen && DashboardContent.ActualHeight > 0
                ? Math.Min(Math.Max(1, DashboardViewport.ActualWidth - 20) / 1280,
                    Math.Max(1, DashboardViewport.ActualHeight - 24) / DashboardContent.ActualHeight)
                : 1;
            if (Math.Abs(DashboardDisplayScale.ScaleX - scale) > 0.001)
                DashboardDisplayScale.ScaleX = DashboardDisplayScale.ScaleY = scale;
        }
        finally { _updatingDashboardLayout = false; }
    }

    protected override void ApplySidebarLayout(bool open, bool animate)
    {
        if (!IsDashboardDisplayMode) _navigationOpen = open;
        open &= !IsDashboardDisplayMode;
        StopSidebarAnimation();
        DockToggleHost.Width = open ? 928 : double.NaN;
        DockToggleHost.HorizontalAlignment = open ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        SidebarToggleButton.Margin = open ? new Thickness(0, 0, 22, 33) : new Thickness(0, 0, 16, 8);
        if (!open && SidebarHost.IsKeyboardFocusWithin)
        {
            SidebarToggleButton.Focus();
        }

        NavigationDockRow.Height = open ? GridLength.Auto : new GridLength(0);
        SidebarHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidebarHost.IsEnabled = open;
        SidebarHost.IsHitTestVisible = open;
        SidebarChevronRotation.Angle = open ? 0 : 180;
        var label = open ? "Hide navigation dock" : "Show navigation dock";
        SidebarToggleButton.ToolTip = label;
        AutomationProperties.SetName(SidebarToggleButton, label);
        UpdateCompactNavigation();
    }

    private void UpdateCompactNavigation()
    {
        if (SidebarNavigation is null || IsDashboardDisplayMode) return;
        var compact = ShellRoot.ActualHeight > 0 && ShellRoot.ActualHeight < 650;
        SidebarHost.Padding = compact ? new Thickness(8, 5, 72, 5) : new Thickness(8, 6, 72, 6);
        SidebarHost.Margin = compact ? new Thickness(18, 6, 18, 2) : new Thickness(18, 10, 18, 2);
        SidebarToggleButton.Margin = _navigationOpen
            ? new Thickness(0, 0, 22, compact ? 12 : 23)
            : new Thickness(0, 0, 16, 8);
        ShellFooterRow.Height = new GridLength(compact ? 22 : 34);
        foreach (var item in SidebarNavigation.Items.OfType<ListBoxItem>())
        {
            item.Height = compact ? 50 : 68;
            if (item.Content is not StackPanel content) continue;
            foreach (var icon in content.Children.OfType<System.Windows.Shapes.Path>())
            {
                if (compact)
                {
                    icon.Width = icon.Height = 18;
                    icon.Margin = new Thickness(0, 0, 0, 2);
                }
                else
                {
                    icon.ClearValue(WidthProperty);
                    icon.ClearValue(HeightProperty);
                    icon.ClearValue(MarginProperty);
                }
            }
        }
    }

    protected override void StopSidebarAnimation()
    {
        ContentTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        SidebarTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        SidebarChevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        ContentTranslation.X = 0;
        SidebarTranslation.X = 0;
    }
}
