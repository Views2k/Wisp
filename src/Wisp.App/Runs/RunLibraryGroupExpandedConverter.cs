using System.Globalization;
using System.Windows.Data;

namespace Wisp.App.Runs;

public sealed class RunLibraryGroupExpandedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not CollectionViewGroup group) return false;
        return group.Name as string == "Recorded runs" || values[2] is true ||
            values[1] is SavedRunItem selected && group.Items.OfType<SavedRunItem>().Any(item => item.Id == selected.Id);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
