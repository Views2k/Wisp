using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace Wisp.App.Runs;

public partial class LapReviewView : UserControl
{
    public LapReviewView()
    {
        InitializeComponent();
        Track.PointChosen += Pick;
        Trace.PointChosen += Pick;
    }
    private void Pick(int index) { if (DataContext is LapReviewViewModel model) model.Cursor = index; }
}

public sealed class LapReviewMapHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double height && double.IsFinite(height) && height > 0 ? Math.Clamp(height - 8, 180, 360) : 360d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
