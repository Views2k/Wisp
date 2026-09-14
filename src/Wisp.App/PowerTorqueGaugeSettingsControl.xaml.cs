using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.ComponentModel;
using System.Windows.Media;

namespace Wisp.App;

public partial class PowerTorqueGaugeSettingsControl : UserControl
{
    private AppController? _controller;
    private bool _subscribed;
    private bool _refreshingColor;
    private int _colorTarget;

    public PowerTorqueGaugeSettingsControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    internal void Initialize(AppController controller)
    {
        Unsubscribe();
        _controller = controller;
        if (IsLoaded) Subscribe();
        RefreshColor();
    }

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
            nameof(GaugeScaleSlider) => model.PowerTorqueGaugeScale,
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

    private void Subscribe()
    {
        if (_controller is null || _subscribed) return;
        _controller.ViewModel.PropertyChanged += Model_Changed;
        _subscribed = true;
        RefreshColor();
    }

    private void Unsubscribe()
    {
        if (_controller is not null && _subscribed) _controller.ViewModel.PropertyChanged -= Model_Changed;
        _subscribed = false;
    }

    private void Model_Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DiagnosticsViewModel.PowerGaugeLowBrush) or nameof(DiagnosticsViewModel.PowerGaugeMidBrush) or
            nameof(DiagnosticsViewModel.PowerGaugeHighBrush) or nameof(DiagnosticsViewModel.TorqueGaugeLowBrush) or
            nameof(DiagnosticsViewModel.TorqueGaugeMidBrush) or nameof(DiagnosticsViewModel.TorqueGaugeHighBrush)) RefreshColor();
    }

    private void ColorTarget_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out _colorTarget)) return;
        RefreshColor();
    }

    private void RefreshColor()
    {
        if (_controller is null || _refreshingColor) return;
        var model = _controller.ViewModel;
        var brush = _colorTarget switch
        {
            0 => model.PowerGaugeLowBrush,
            1 => model.PowerGaugeMidBrush,
            2 => model.PowerGaugeHighBrush,
            3 => model.TorqueGaugeLowBrush,
            4 => model.TorqueGaugeMidBrush,
            _ => model.TorqueGaugeHighBrush
        };
        if (brush is not SolidColorBrush solid) return;
        _refreshingColor = true;
        try
        {
            var gauge = _colorTarget < 3 ? "Power" : "Torque";
            var position = (_colorTarget % 3) switch { 0 => "start", 1 => "middle", _ => "end" };
            GaugeColorEditor.Title = $"{gauge} gauge {position}";
            GaugeColorEditor.Description = "Color of the gauge lines, using the same gradient controls as boost.";
            GaugeColorEditor.SetCurrentValue(ColorWheelEditor.SelectedColorProperty, solid.Color);
        }
        finally { _refreshingColor = false; }
    }

    private void GaugeColor_Changed(object sender, RoutedPropertyChangedEventArgs<Color> e)
    {
        if (_controller is null || _refreshingColor) return;
        var value = ColorCustomization.ToHex(e.NewValue);
        var model = _controller.ViewModel;
        switch (_colorTarget)
        {
            case 0: model.CustomPowerLowColor = value; break;
            case 1: model.CustomPowerMidColor = value; break;
            case 2: model.CustomPowerHighColor = value; break;
            case 3: model.CustomTorqueLowColor = value; break;
            case 4: model.CustomTorqueMidColor = value; break;
            default: model.CustomTorqueHighColor = value; break;
        }
        _controller.ApplyViewOptions();
    }
}
