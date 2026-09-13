namespace Wisp.App.Runs;

public partial class LegacyRunsPage : RunsPageBase
{
    public LegacyRunsPage()
    {
        InitializeComponent();
        InitializeRunsPage(new(ShortcutCaptureButton, MarkerShortcutCaptureButton, GraphScroll, GraphPicker,
            RunHeading, RunsScroll, GraphsSurface, ShowGraphsButton));
    }
}
