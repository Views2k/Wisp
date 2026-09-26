using System.Windows;
using System.Windows.Controls;
using Wisp.Core;

namespace Wisp.App;

public partial class LapDeltaSettingsControl : UserControl
{
    private AppController? _controller;
    private bool _ready;
    public LapDeltaSettingsControl() => InitializeComponent();
    internal void Initialize(AppController controller)
    {
        _ready = false;
        _controller = controller;
        var settings = controller.Settings;
        EnabledToggle.IsChecked = settings.LapDeltaEnabled;
        TimingSelector.SelectedValue = settings.LapTimingMode;
        ReferenceSelector.SelectedValue = settings.LapDeltaReference;
        BarToggle.IsChecked = settings.LapDeltaShowBar;
        ScaleSlider.Value = settings.LapDeltaScale;
        MapToggle.IsChecked = settings.LapMapEnabled;
        MapScaleSlider.Value = settings.LapMapScale;
        _ready = true;
    }
    private void Timing_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && TimingSelector.SelectedValue is LapTimingMode mode) _controller?.SetLapTimingMode(mode);
    }
    private void Apply()
    {
        if (!_ready || _controller is null) return;
        _controller.SetLapDeltaSettings(EnabledToggle.IsChecked == true,
            ReferenceSelector.SelectedValue is LapDeltaReference mode ? mode : LapDeltaReference.SessionBest,
            BarToggle.IsChecked == true, ScaleSlider.Value);
    }
    private void MapOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) _controller?.SetLapMapSettings(MapToggle.IsChecked == true, MapScaleSlider.Value);
    }
    private void MapScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => MapOptions_Changed(sender, e);
    private void Options_Changed(object sender, RoutedEventArgs e) => Apply();
    private void Reference_Changed(object sender, SelectionChangedEventArgs e) => Apply();
    private void Scale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Apply();
    private void ResetSession_Click(object sender, RoutedEventArgs e) => _controller?.ResetLapDeltaSession();
    private void ResetPosition_Click(object sender, RoutedEventArgs e) => _controller?.ResetLapDeltaPlacement();
    private void ResetMapPosition_Click(object sender, RoutedEventArgs e) => _controller?.ResetLapMapPlacement();
}
