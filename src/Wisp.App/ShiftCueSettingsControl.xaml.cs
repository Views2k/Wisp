using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Wisp.App;

public partial class ShiftCueSettingsControl : UserControl
{
    private AppController? _controller;

    public ShiftCueSettingsControl() => InitializeComponent();

    internal void Initialize(AppController controller) => _controller = controller;

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_controller is null || DataContext is not DiagnosticsViewModel model ||
            model.AccelerationShiftCueEnabled == (EnabledToggle.IsChecked == true)) return;
        EnabledToggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        _controller.ApplyViewOptions();
    }

    private void Calibrate_Click(object sender, RoutedEventArgs e) => _controller?.StartShiftCalibration();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _controller?.CancelShiftCalibration();
}
