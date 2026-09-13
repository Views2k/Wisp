using System.Globalization;
using System.Windows.Data;

namespace Wisp.App;

public sealed class MetricPartConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string ?? "—";
        if (parameter as string == "wheels")
            return text.EndsWith(" wheels", StringComparison.OrdinalIgnoreCase) ? text[..^7] : text;
        var split = text.LastIndexOf(' ');
        if (parameter as string == "rpm")
            return int.TryParse(split > 0 ? text[..split] : text, NumberStyles.Integer, culture, out var rpm)
                ? rpm.ToString("N0", culture) : text;
        return parameter as string == "unit"
            ? split > 0 ? " " + text[(split + 1)..] : string.Empty
            : split > 0 ? text[..split] : text;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DockLabelLayoutConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var width = value is double current && double.IsFinite(current) ? current : 100;
        return parameter as string == "font" ? width < 88 ? 11d : 14d : Math.Clamp(width - 8, 32, 100);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
