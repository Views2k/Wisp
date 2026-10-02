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
            if (TryDismissFeatureTourForKey(args.Key, Keyboard.Modifiers)) args.Handled = true;
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
            HudProfileDialog.Visibility == Visibility.Visible || ApplicationUpdateConfirmation.Visibility == Visibility.Visible || IsTuneDialogOpen) return;
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
        if (IsActive && HudProfileDialog.Visibility != Visibility.Visible && ApplicationUpdateConfirmation.Visibility != Visibility.Visible && !IsTuneDialogOpen &&
            previous is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
        if (FeatureTour.HasPendingReceipt)
        {
            RootTabs.SelectedItem = DashboardTab;
            _featureTourBanner?.BringIntoView();
            if (IsActive) _featureTourBanner?.TourRetryButton.Focus();
        }
    }

    internal bool TryDismissFeatureTourForKey(Key key, ModifierKeys modifiers)
    {
        if (!FeatureTour.IsOpen || key != Key.Escape || modifiers != ModifierKeys.None) return false;
        DismissFeatureTour();
        return true;
    }

    private void ShowFeatureTourStep()
    {
        if (!FeatureTour.IsOpen || _featureTourOverlay is null) return;
        var modern = this is MainWindow;
        var step = FeatureTour.StepIndex;
        _featureTourNavigating = true;
        try
        {
            var page = step switch { 0 => "Clips", 1 => "Tune", 2 or 3 => "Appearance", _ => "Runs" };
            RootTabs.SelectedItem = RootTabs.Items.OfType<TabItem>().FirstOrDefault(item => Equals(item.Header, page));
            if (modern && step is 2 or 3)
            {
                ((MainWindow)this).PrepareAppearanceForFeatureTour();
                if (FindName("AppearanceGaugesCategory") is RadioButton category)
                    category.IsChecked = true;
            }
            if (step == 4) _controller.Runs.ShowSummary();
        }
        finally { _featureTourNavigating = false; }
        _featureTourOverlay.TourProgress.Text = $"{step + 1} of {FeatureTourSession.StepCount} · QUICK TOUR";
        _featureTourOverlay.TourBackButton.IsEnabled = step > 0;
        _featureTourOverlay.TourNextButton.Content = step == FeatureTourSession.StepCount - 1 ? "Finish" : "Next";
        (_featureTourOverlay.TourHeading.Text, _featureTourOverlay.TourDescription.Text, _featureTourOverlay.TourLocation.Text) = step switch
        {
            0 => ("Save a moment with Clips", "Turn on clipping while Forza is fullscreen. Save clip keeps recent footage to watch here or export. Recording includes everything visible on Forza's screen. Switching away pauses recording and keeps footage ready to save.", "Clips → Enable clipping · Save clip"),
            1 => ("View and compare tunes", "View the current car's setup, including locked tunes on Steam. Save a snapshot to open later, or compare the current car with a saved setup. Opening a saved tune only displays it in Wisp.", "Tune → Current car · Saved tunes · Compare"),
            2 => ("Live track map (beta)", "Follow your car around the circuit. Your first full lap builds the outline; later laps keep the full circuit in view. Turn on the map here and adjust its size to suit your HUD.", modern ? "Appearance → Gauges → Lap delta → More options" : "Appearance → Lap delta → More options"),
            3 => ("Lap delta (beta)", "Complete a full circuit lap to set a reference, then compare against your session best or previous lap. Negative means ahead; positive means behind. Choose Race / Rivals laps or Time Attack to match your session.", modern ? "Appearance → Gauges → Lap delta" : "Appearance → Lap delta"),
            _ => ("Review your laps", "Open a saved run, choose a lap and select a reference to compare your line, inputs and time around the circuit. You can also turn on Save completed laps automatically here to keep laps for later review.", "Runs → Lap review")
        };
        var version = ++_featureTourNavigationVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!FeatureTour.IsOpen || version != _featureTourNavigationVersion) return;
            var lapSettings = FindName("LapDeltaSettings") as LapDeltaSettingsControl;
            if (step == 2 && lapSettings?.FindName("MoreOptions") is Expander options)
            {
                options.IsExpanded = true;
                lapSettings.UpdateLayout();
            }
            var lapReview = RunsSurface.FindName("LapReviewExpander") as Expander;
            if (step == 4 && lapReview is not null)
            {
                lapReview.IsExpanded = true;
                RunsSurface.UpdateLayout();
            }
            FrameworkElement? target = step switch
            {
                0 => ClipsSurface.FindName("ClippingControls") as FrameworkElement,
                1 => TuneSurface.FindName("TuneWorkspaceControls") as FrameworkElement,
                2 => lapSettings?.FindName("MapToggle") as FrameworkElement,
                3 => lapSettings?.FindName("EnabledToggle") as FrameworkElement,
                _ => (lapReview?.Content as FrameworkElement)?.FindName("LapSelector") as FrameworkElement
            };
            if (target is not null)
                target.BringIntoView(new Rect(0, -8, target.ActualWidth, target.ActualHeight + 16));
            _featureTourOverlay.SetTarget(target);
            _featureTourOverlay.TourCardScroll.ScrollToTop();
            if (IsActive) _featureTourOverlay.TourNextButton.Focus();
        }));
    }
}
