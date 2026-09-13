using System.Windows;
using System.Windows.Controls;
using Wisp.Core;

namespace Wisp.App;

public partial class DriftGaugeSettingsControl : UserControl
{
    private AppController? _controller;
    private bool _ready;
    private bool _subscribed;

    public DriftGaugeSettingsControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    internal void Initialize(AppController controller)
    {
        Unsubscribe();
        _ready = false;
        _controller = controller;
        EnabledToggle.IsChecked = controller.Settings.DriftGaugeEnabled;
        DarkModeToggle.IsChecked = controller.Settings.DriftGaugeDarkMode;
        BackgroundToggle.IsChecked = controller.Settings.DriftGaugeBackgroundEnabled;
        BackgroundOpacitySlider.Value = controller.Settings.DriftGaugeBackgroundOpacity;
        GuidanceSelector.SelectedValue = controller.Settings.DriftGaugeGuidanceMode;
        TargetSlider.Value = controller.Settings.DriftTargetDegrees;
        ToleranceSlider.Value = controller.Settings.DriftToleranceDegrees;
        ScaleSlider.Value = controller.Settings.DriftGaugeScale;
        _ready = true;
        RefreshGuidance();
        if (IsLoaded) Subscribe();
        RefreshStatus();
    }

    private void Subscribe()
    {
        if (_controller is null || _subscribed) return;
        _controller.DriftGaugeStatusChanged += Status_Changed;
        _subscribed = true;
        RefreshStatus();
    }

    private void Unsubscribe()
    {
        if (_controller is not null && _subscribed) _controller.DriftGaugeStatusChanged -= Status_Changed;
        _subscribed = false;
    }

    private void Status_Changed(object? sender, EventArgs e) => RefreshStatus();
    private void RefreshStatus() => RenderStatus.Text = _controller?.DriftGaugeStatus ?? "";
    private void Enabled_Changed(object sender, RoutedEventArgs e) => Apply();
    private void Value_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Apply();
    private DriftGaugeGuidanceMode SelectedGuidance => GuidanceSelector.SelectedValue is DriftGaugeGuidanceMode mode
        ? mode : DriftGaugeGuidanceMode.DriftZoneAngleBonus;

    private void Guidance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        RefreshGuidance();
        Apply();
    }

    private void RefreshGuidance()
    {
        var custom = SelectedGuidance == DriftGaugeGuidanceMode.CustomTarget;
        CustomTargetSettings.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        ZoneGuidanceDescription.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Apply()
    {
        if (!_ready || _controller is null) return;
        _controller.SetDriftGaugeSettings(EnabledToggle.IsChecked == true, TargetSlider.Value, ToleranceSlider.Value, ScaleSlider.Value,
            DarkModeToggle.IsChecked == true, SelectedGuidance, BackgroundToggle.IsChecked == true, BackgroundOpacitySlider.Value);
        RefreshStatus();
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e) => _controller?.ResetDriftGaugePlacement();
}
