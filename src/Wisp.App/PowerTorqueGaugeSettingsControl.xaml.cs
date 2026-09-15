using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Wisp.App;

public partial class PowerTorqueGaugeSettingsControl : UserControl
{
    private AppController? _controller;

    public PowerTorqueGaugeSettingsControl() => InitializeComponent();

    internal void Initialize(AppController controller) => _controller = controller;

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_controller is null || DataContext is not DiagnosticsViewModel model || sender is not CheckBox toggle) return;
        var current = toggle.Name switch
        {
            nameof(PowerToggle) => model.PowerGaugeEnabled,
            nameof(TorqueToggle) => model.TorqueGaugeEnabled,
            nameof(PowerAttachedToggle) => model.PowerGaugeAttached,
            nameof(TorqueAttachedToggle) => model.TorqueGaugeAttached,
            nameof(NegativeToggle) => model.PowerTorqueShowNegative,
            nameof(PowerColorNumberToggle) => model.PowerGaugeColorNumber,
            nameof(TorqueColorNumberToggle) => model.TorqueGaugeColorNumber,
            _ => toggle.IsChecked == true
        };
        if (current == (toggle.IsChecked == true)) return;
        toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        _controller.ApplyViewOptions();
    }

    private void Scale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_controller is null || DataContext is not DiagnosticsViewModel model || sender is not Slider slider) return;
        var current = slider.Name switch
        {
            nameof(PowerGaugeScaleSlider) => model.PowerGaugeScale,
            nameof(TorqueGaugeScaleSlider) => model.TorqueGaugeScale,
            nameof(PowerMaximumSlider) => model.PowerGaugeMaximum,
            nameof(TorqueMaximumSlider) => model.TorqueGaugeMaximumNm,
            nameof(SmoothingSlider) => model.PowerTorqueSmoothingMilliseconds,
            _ => slider.Value
        };
        if (current.Equals(slider.Value)) return;
        slider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateSource();
        _controller.ApplyViewOptions();
    }

    private void ResetPeaks_Click(object sender, RoutedEventArgs e) => _controller?.ViewModel.ResetDashboardPeaks();

    private void SetFromRun_Click(object sender, RoutedEventArgs e) => _controller?.SetPowerTorqueScalesFromCurrentRun();
}
