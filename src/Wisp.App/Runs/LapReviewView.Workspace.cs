using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Wisp.App.Runs;

public partial class LapReviewView
{
    public static readonly DependencyProperty RequestedMapHeightProperty = DependencyProperty.Register(
        nameof(RequestedMapHeight), typeof(double), typeof(LapReviewView), new PropertyMetadata(double.NaN));
    public double RequestedMapHeight
    {
        get => (double)GetValue(RequestedMapHeightProperty);
        set => SetValue(RequestedMapHeightProperty, value);
    }

    private void MapResizeGrip_DragDelta(object sender, DragDeltaEventArgs e) => ResizeMapBy(e.VerticalChange);
    internal void ResizeMapBy(double change)
    {
        if (double.IsFinite(change)) RequestedMapHeight = Math.Clamp(
            (double.IsFinite(RequestedMapHeight) ? RequestedMapHeight : MapViewport.ActualHeight) + change, 180, 1400);
    }
    private void MapResizeGrip_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down) ResizeMapBy(e.Key == Key.Up ? -40 : 40);
        else if (e.Key == Key.Home) RequestedMapHeight = double.NaN;
        else return;
        e.Handled = true;
    }

    private void InitializeMapWorkspace()
    {
        Track3D.ReferencePointChosen += index =>
        {
            if (DataContext is LapReviewViewModel model) model.PickReferencePoint(index);
        };
        Track3D.PreviewKeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape || !Track3D.IsComparison) return;
            Track3D.ShowAll();
            key.Handled = true;
        };
    }

    private void ShowBothMaps_Click(object sender, RoutedEventArgs e) => Track3D.ShowAll();
}
