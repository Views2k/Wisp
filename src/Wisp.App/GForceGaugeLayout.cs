using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Wisp.App;

internal static class GForceGaugeLayout
{
    internal const double DefaultNativeTopPadding = 72;
    internal const double CombinedMeterWidth = 190;
    internal const double CombinedMeterHeight = 132;

    internal static double NormalizeScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, .5, 2) : 1;

    // Reserve this space even when the meter is hidden. Visibility changes must
    // never move the native surface; only an explicit size edit changes padding.
    internal static double NativeTopPadding(double scale) =>
        DefaultNativeTopPadding + 128 * Math.Max(0, NormalizeScale(scale) - 1);

    internal static Rect NativeBounds(NativeGaugeMode mode, double scale)
    {
        scale = NormalizeScale(scale);
        var left = mode == NativeGaugeMode.Analogue ? 195 : 176;
        return new Rect(left + 72 * (1 - scale), Math.Max(0, 50 * (1 - scale)), 144 * scale, 100 * scale);
    }

    internal static Size CombinedSize(double scale)
    {
        scale = NormalizeScale(scale);
        return new Size(200 + CombinedMeterWidth * scale, Math.Max(166, 34 + CombinedMeterHeight * scale));
    }
}

public sealed class GForceGaugeLayoutConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var scale = GForceGaugeLayout.NormalizeScale(value is double number ? number : 1);
        var option = parameter as string ?? string.Empty;
        if (option == "ev-analogue")
        {
            var electric = ElectricSupplementaryGaugeLayout.GForceBounds(scale);
            return new Thickness(electric.Left, electric.Top, 0, 0);
        }
        if (option == "combined-width") return GForceGaugeLayout.CombinedSize(scale).Width;
        if (option == "combined-height") return GForceGaugeLayout.CombinedSize(scale).Height;
        var margin = (Thickness)new ThicknessConverter().ConvertFromInvariantString(option)!;
        if (margin.Top == 0 && margin.Left is 176 or 195)
        {
            var bounds = GForceGaugeLayout.NativeBounds(margin.Left == 195 ? NativeGaugeMode.Analogue : NativeGaugeMode.Digital, scale);
            return new Thickness(bounds.Left, bounds.Top, 0, 0);
        }
        return new Thickness(margin.Left, margin.Top + GForceGaugeLayout.NativeTopPadding(scale) -
            GForceGaugeLayout.DefaultNativeTopPadding, margin.Right, margin.Bottom);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
