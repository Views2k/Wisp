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
        var current = toggle == PowerToggle ? model.PowerGaugeEnabled : model.TorqueGaugeEnabled;
        if (current == (toggle.IsChecked == true)) return;
        toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        _controller.ApplyViewOptions();
    }

    private void Scale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_controller is null || DataContext is not DiagnosticsViewModel model || sender is not Slider slider) return;
        var current = slider == GaugeScaleSlider ? model.PowerTorqueGaugeScale
            : slider == PowerMaximumSlider ? model.PowerGaugeMaximum : model.TorqueGaugeMaximumNm;
        if (current.Equals(slider.Value)) return;
        slider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateSource();
        _controller.ApplyViewOptions();
    }

    private void ResetPeaks_Click(object sender, RoutedEventArgs e) => _controller?.ViewModel.ResetDashboardPeaks();

    private void SetFromRun_Click(object sender, RoutedEventArgs e) => _controller?.SetPowerTorqueScalesFromCurrentRun();
}
