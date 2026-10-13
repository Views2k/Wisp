using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Wisp.App;

public abstract partial class ControlPanelWindow
{
    private bool _profileDialogFeaturePaused;

    private void InitializeFeatureControlPanels()
    {
        var panels = new[]
        {
            WrapFeaturePanel(AppearanceTab, "appearance", "Appearance changes are temporarily paused."),
            WrapFeaturePanel(DiagnosticsTab, "diagnostics", "Diagnostics is temporarily paused."),
            WrapFeaturePanel(FindControl<TabItem>("ProfilesTab"), "hud-profiles", "HUD profiles are temporarily paused.")
        };
        void Refresh(object? sender, EventArgs args)
        {
            foreach (var panel in panels) panel();
            if (!_controller.FeatureAllowed("appearance")) EndOverlayHotkeyCapture();
            RefreshProfileDialogFeatureControl();
        }
        _controller.FeatureControlsChanged += Refresh;
        Closed += (_, _) => _controller.FeatureControlsChanged -= Refresh;
        Refresh(null, EventArgs.Empty);
    }

    private Action WrapFeaturePanel(TabItem tab, string feature, string fallback)
    {
        var body = (FrameworkElement)tab.Content;
        var message = SupplementaryText("");
        message.Margin = new Thickness(0, 0, 0, 12);
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
        var panel = new Grid();
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        tab.Content = null;
        panel.Children.Add(message);
        Grid.SetRow(body, 1);
        panel.Children.Add(body);
        tab.Content = panel;
        return () =>
        {
            var available = _controller.FeatureAllowed(feature);
            body.IsEnabled = available;
            message.Text = available ? "" : _controller.FeatureControls.Reason(feature) ?? fallback;
            message.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        };
    }

    private void RefreshProfileDialogFeatureControl()
    {
        var available = _controller.FeatureAllowed("hud-profiles") || _hudProfileChangePending;
        ConfirmHudProfileButton.IsEnabled = available;
        if (HudProfileDialog.Visibility != Visibility.Visible) return;
        if (!available)
        {
            _profileDialogFeaturePaused = true;
            HudProfileDialogError.Text = _controller.FeatureControls.Reason("hud-profiles") ?? "HUD profiles are temporarily paused.";
            HudProfileDialogError.Visibility = Visibility.Visible;
        }
        else if (_profileDialogFeaturePaused)
        {
            _profileDialogFeaturePaused = false;
            HudProfileDialogError.Text = "";
            HudProfileDialogError.Visibility = Visibility.Collapsed;
        }
    }
}
