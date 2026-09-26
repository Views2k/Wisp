using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Wisp.App;

public abstract partial class ControlPanelWindow
{
    internal FeatureTourSession FeatureTour { get; } = new();
    private FeatureTourBanner? _featureTourBanner;
    private FeatureTourOverlay? _featureTourOverlay;
    private bool _featureTourDiscoveryAllowed;
    private bool _featureTourNavigating;
    private IInputElement? _focusBeforeFeatureTour;
    private int _featureTourNavigationVersion;

    private void InitializeFeatureTour()
    {
        _featureTourBanner = FindName("FeatureTourWelcomeBanner") as FeatureTourBanner;
        if (_featureTourBanner is null || Content is not Border { Child: Grid shell }) return;
        _featureTourOverlay = new FeatureTourOverlay { Visibility = Visibility.Collapsed, Name = "FeatureTourOverlay" };
        RegisterName(_featureTourOverlay.Name, _featureTourOverlay);
        Grid.SetRowSpan(_featureTourOverlay, 3);
        Panel.SetZIndex(_featureTourOverlay, 90);
        shell.Children.Add(_featureTourOverlay);
        _featureTourBanner.TourStartButton.Click += (_, _) => StartFeatureTour();
        _featureTourBanner.TourDismissButton.Click += (_, _) => DismissFeatureTour();
        _featureTourBanner.TourRetryButton.Click += (_, _) => DismissFeatureTour();
        _featureTourOverlay.TourSkipButton.Click += (_, _) => DismissFeatureTour();
        _featureTourOverlay.TourBackButton.Click += (_, _) => { FeatureTour.Back(); ShowFeatureTourStep(); };
        _featureTourOverlay.TourNextButton.Click += (_, _) =>
        {
            if (FeatureTour.Next()) ShowFeatureTourStep();
            else DismissFeatureTour();
        };
        if (FindName("ReplayFeatureTourButton") is Button replay)
            replay.Click += (_, _) => StartFeatureTour();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseFeatureTour(); else RefreshFeatureTour(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) CloseFeatureTour(); };
        Deactivated += (_, _) => CloseFeatureTour();
        Closed += (_, _) => CloseFeatureTour();
        HudProfileDialog.IsVisibleChanged += (_, _) => { if (HudProfileDialog.IsVisible) CloseFeatureTour(); };
        ApplicationUpdateConfirmation.IsVisibleChanged += (_, _) => { if (ApplicationUpdateConfirmation.IsVisible) CloseFeatureTour(); };
        RootTabs.SelectionChanged += (_, args) =>
        {
            if (args.Source == RootTabs && !_featureTourNavigating && FeatureTour.IsOpen) CloseFeatureTour();
        };
        PreviewKeyDown += (_, args) =>
        {
            if (FeatureTour.IsOpen && args.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                DismissFeatureTour();
                args.Handled = true;
            }
        };
        RefreshFeatureTour();
    }

    internal void SetFeatureTourDiscoveryAllowed(bool allowed)
    {
        _featureTourDiscoveryAllowed = allowed;
        if (!allowed) CloseFeatureTour();
        RefreshFeatureTour();
    }

    internal void RefreshFeatureTour()
    {
        RefreshWhatsNew();
        if (_featureTourBanner is null) return;
        var offer = FeatureTourSession.ShouldOffer(_controller.Settings.CompletedFeatureTourId,
            _featureTourDiscoveryAllowed, _controller.Settings.RequiresSetup,
            this is MainWindow { IsDashboardDisplayMode: true });
        offer |= FeatureTour.HasPendingReceipt && _featureTourDiscoveryAllowed && !_controller.Settings.RequiresSetup &&
                 this is not MainWindow { IsDashboardDisplayMode: true };
        _featureTourBanner.Visibility = offer && !FeatureTour.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        _featureTourBanner.TourSaveFeedback.Visibility = FeatureTour.HasPendingReceipt ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void StartFeatureTour()
    {
        if (_featureTourOverlay is null || _controller.Settings.RequiresSetup ||
            this is MainWindow { IsDashboardDisplayMode: true } ||
            HudProfileDialog.Visibility == Visibility.Visible || ApplicationUpdateConfirmation.Visibility == Visibility.Visible) return;
        CloseConnectionPanel();
        _featureTourDiscoveryAllowed = true;
        _focusBeforeFeatureTour = Keyboard.FocusedElement;
        FeatureTour.Start();
        _featureTourOverlay.Visibility = Visibility.Visible;
        RefreshFeatureTour();
        ShowFeatureTourStep();
    }

    internal void CloseFeatureTour()
    {
        FeatureTour.Close();
        _featureTourNavigationVersion++;
        if (_featureTourOverlay is not null)
        {
            _featureTourOverlay.SetTarget(null);
            _featureTourOverlay.Visibility = Visibility.Collapsed;
        }
        // Lifecycle interruptions must not move keyboard focus behind another dialog.
        _focusBeforeFeatureTour = null;
        RefreshFeatureTour();
    }

    internal void DismissFeatureTour()
    {
        var previous = _focusBeforeFeatureTour;
        FeatureTour.PersistReceipt(_controller.TryCompleteFeatureTour);
        CloseFeatureTour();
        if (IsActive && HudProfileDialog.Visibility != Visibility.Visible && ApplicationUpdateConfirmation.Visibility != Visibility.Visible &&
            previous is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
        if (FeatureTour.HasPendingReceipt)
        {
            RootTabs.SelectedItem = DashboardTab;
            _featureTourBanner?.BringIntoView();
            if (IsActive) _featureTourBanner?.TourRetryButton.Focus();
        }
    }

    private void ShowFeatureTourStep()
    {
        if (!FeatureTour.IsOpen || _featureTourOverlay is null) return;
        var modern = this is MainWindow;
        var step = FeatureTour.StepIndex;
        _featureTourNavigating = true;
        try
        {
            var page = step switch { 0 => "Appearance", 1 => "Dashboard", 2 => "Runs", _ => "Appearance" };
            RootTabs.SelectedItem = RootTabs.Items.OfType<TabItem>().FirstOrDefault(item => Equals(item.Header, page));
            if (modern && (step == 0 || step == 3))
            {
                ((MainWindow)this).PrepareAppearanceForFeatureTour();
                if (FindName(step == 0 ? "AppearanceGaugesCategory" : "AppearanceColorsCategory") is RadioButton category)
                    category.IsChecked = true;
            }
        }
        finally { _featureTourNavigating = false; }
        _featureTourOverlay.TourProgress.Text = $"{step + 1} of {FeatureTourSession.StepCount} · QUICK TOUR";
        _featureTourOverlay.TourBackButton.IsEnabled = step > 0;
        _featureTourOverlay.TourNextButton.Content = step == FeatureTourSession.StepCount - 1 ? "Finish" : "Next";
        (_featureTourOverlay.TourHeading.Text, _featureTourOverlay.TourDescription.Text, _featureTourOverlay.TourLocation.Text) = step switch
        {
            0 => ("Find your drift angle", "Enable the drift gauge here when you're ready. In Drift Zone mode, the percentage shows the angle bonus, not your total points. The angle stays hidden below 5 mph; speed and your line still matter.", modern ? "Appearance → Gauges → Show drift angle gauge" : "Appearance → Show drift angle gauge"),
            1 when modern => ("A dashboard for another screen", "Display mode gives your telemetry more room. Press F11 from Dashboard to enter, then Escape to return. Use Resizable in Display mode to fit part of a monitor.", "Dashboard → Display mode"),
            1 => ("Your live drive, at a glance", "Dashboard keeps speed, power and driving data together. The new interface also includes a borderless, resizable Display mode for a second monitor. You can switch interfaces in Appearance when you choose.", "Dashboard · Your current legacy interface stays selected"),
            2 => ("Explore and share your runs", "Search saved runs by name or tune label. Select a run, then use Show graphs to compare data and Export for a Wisp run file or CSV. Your names, tune labels and notes save automatically. No recording is needed for this tour.", "Runs → Search · Show graphs · Export"),
            _ => ("Make Wisp yours", modern ? "The live preview shows your HUD as you customize it. Pick colors, tune the background and turn particles on or off. Save your favorite combination in Profiles. On smaller windows, use Show HUD preview to see it." : "Use the HUD preview while choosing layouts and gauges. Your color controls are in Extras. Save your favorite combination in Profiles so you can switch back to it later.", modern ? "Appearance → Colors · Profiles" : "Appearance → HUD preview · Extras → Colors · Profiles")
        };
        var version = ++_featureTourNavigationVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!FeatureTour.IsOpen || version != _featureTourNavigationVersion) return;
            if (step == 2 && modern && RunsSurface.FindName("RunLibraryPane") is FrameworkElement { IsVisible: false } &&
                RunsSurface.FindName("CompactRunLibraryDrawer") is Expander drawer)
            {
                drawer.IsExpanded = true;
                RunsSurface.UpdateLayout();
            }
            FrameworkElement? target = step switch
            {
                0 => (FindName("DriftGaugeSettings") as FrameworkElement)?.FindName("EnabledToggle") as FrameworkElement,
                1 when modern => FindName("DashboardWindowDisplayButton") as FrameworkElement,
                1 => FindName("LegacyDashboardHero") as FrameworkElement,
                2 => RunsSurface.FindName("LibrarySearchControl") as FrameworkElement,
                _ => FindName(modern ? "ColorTargetSelector" : "HudPreviewSurface") as FrameworkElement
            };
            if (step == 2 && target is not { IsVisible: true })
                target = RunsSurface.FindName("CompactLibrarySearchControl") as FrameworkElement;
            if (step == 2 && target is not { IsVisible: true }) target = RunsSurface;
            target?.BringIntoView();
            _featureTourOverlay.SetTarget(target);
            _featureTourOverlay.TourCardScroll.ScrollToTop();
            if (IsActive) _featureTourOverlay.TourNextButton.Focus();
        }));
    }
}
