using System.Windows.Media;

namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    public Brush PowerGaugeLowBrush => PowerTorqueBrush(0);
    public Brush PowerGaugeMidBrush => PowerTorqueBrush(1);
    public Brush PowerGaugeHighBrush => PowerTorqueBrush(2);
    public Brush TorqueGaugeLowBrush => PowerTorqueBrush(0);
    public Brush TorqueGaugeMidBrush => PowerTorqueBrush(1);
    public Brush TorqueGaugeHighBrush => PowerTorqueBrush(2);

    internal void RefreshPowerTorquePalette()
    {
        OnPropertyChanged(nameof(PowerGaugeLowBrush));
        OnPropertyChanged(nameof(PowerGaugeMidBrush));
        OnPropertyChanged(nameof(PowerGaugeHighBrush));
        OnPropertyChanged(nameof(TorqueGaugeLowBrush));
        OnPropertyChanged(nameof(TorqueGaugeMidBrush));
        OnPropertyChanged(nameof(TorqueGaugeHighBrush));
    }

    private Brush PowerTorqueBrush(int stop)
    {
        var theme = _powerTorqueSettings is { } settings
            ? ColorCustomization.ResolveGauge(settings) : BoostGaugeThemes.Resolve(null);
        var value = stop == 0 ? theme.Low : stop == 1 ? theme.Mid : theme.High;
        var color = (Color)ColorConverter.ConvertFromString(value);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
