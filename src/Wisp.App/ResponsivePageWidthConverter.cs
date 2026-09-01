using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Wisp.App;

public sealed class ResponsivePageWidthConverter : IValueConverter
{
    public const double MinimumWidth = 900;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && double.IsFinite(width)
            ? Math.Max(width, MinimumWidth)
            : MinimumWidth;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
