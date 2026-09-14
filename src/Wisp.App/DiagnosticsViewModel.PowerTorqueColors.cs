using System.Windows.Media;

namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    public Brush PowerGaugeLowBrush => PowerTorqueBrush(CustomPowerLowColor, 0);
    public Brush PowerGaugeMidBrush => PowerTorqueBrush(CustomPowerMidColor, 1);
    public Brush PowerGaugeHighBrush => PowerTorqueBrush(CustomPowerHighColor, 2);
    public Brush TorqueGaugeLowBrush => PowerTorqueBrush(CustomTorqueLowColor, 0);
    public Brush TorqueGaugeMidBrush => PowerTorqueBrush(CustomTorqueMidColor, 1);
    public Brush TorqueGaugeHighBrush => PowerTorqueBrush(CustomTorqueHighColor, 2);

    internal void RefreshPowerTorquePalette()
    {
        OnPropertyChanged(nameof(PowerGaugeLowBrush));
        OnPropertyChanged(nameof(PowerGaugeMidBrush));
        OnPropertyChanged(nameof(PowerGaugeHighBrush));
        OnPropertyChanged(nameof(TorqueGaugeLowBrush));
        OnPropertyChanged(nameof(TorqueGaugeMidBrush));
        OnPropertyChanged(nameof(TorqueGaugeHighBrush));
    }

    private Brush PowerTorqueBrush(string? custom, int stop)
    {
        var theme = BoostGaugeThemes.Resolve(_powerTorqueSettings?.BoostGaugeTheme);
        var fallback = stop == 0 ? theme.Low : stop == 1 ? theme.Mid : theme.High;
        var color = ColorCustomization.TryParse(custom, out var value)
            ? value : (Color)ColorConverter.ConvertFromString(fallback);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
