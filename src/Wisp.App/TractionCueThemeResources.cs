using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

public static class TractionCueThemeResources
{
    public const string BrushKey = "TractionCueBrush";

    public static void Apply(ResourceDictionary resources, Color color)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[BrushKey] = brush;
    }
}
