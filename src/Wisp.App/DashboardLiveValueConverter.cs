using System.Globalization;
using System.Windows.Data;

namespace Wisp.App;

public sealed class DashboardLiveValueConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[1] is true && values[0] is string text && !string.IsNullOrWhiteSpace(text)
            ? text : "—";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
