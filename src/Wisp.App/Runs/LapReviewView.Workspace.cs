using System.Windows;
using System.Windows.Input;

namespace Wisp.App.Runs;

public partial class LapReviewView
{
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
