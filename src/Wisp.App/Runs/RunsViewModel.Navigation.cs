namespace Wisp.App.Runs;

public sealed record RunGraphChoice(string Label, RunChartGroup Group, RunPlotMode Mode);

public sealed partial class RunsViewModel
{
    private bool _isGraphWorkspaceOpen;
    public bool IsGraphWorkspaceOpen => _isGraphWorkspaceOpen && HasRun;
    public bool IsSummaryVisible => !IsGraphWorkspaceOpen;
    public RunGraphChoice[] GraphChoices { get; } =
    [
        new("Speed", RunChartGroup.Speed, RunPlotMode.TimeSeries),
        new("Driver inputs", RunChartGroup.Inputs, RunPlotMode.TimeSeries),
        new("Engine", RunChartGroup.Engine, RunPlotMode.TimeSeries),
        new("Power / torque vs RPM", RunChartGroup.Engine, RunPlotMode.PowerByRpm),
        new("G-force over time", RunChartGroup.Handling, RunPlotMode.TimeSeries),
        new("G-force plot", RunChartGroup.Handling, RunPlotMode.GForce),
        new("Tire temperatures", RunChartGroup.Tires, RunPlotMode.TimeSeries),
        new("Tire temp change", RunChartGroup.Tires, RunPlotMode.TireChange)
    ];
    public RunGraphChoice SelectedGraph
    {
        get => GraphChoices.First(choice => choice.Group == ChartGroup && choice.Mode == GraphView.Mode);
        set
        {
            if (value is null || RecordingActive || !GraphChoices.Contains(value) || value == SelectedGraph) return;
            // Select the channel and presentation together so one click prepares one view.
            _chartGroup = value.Group;
            _graphView = AvailableGraphViews.First(view => view.Mode == value.Mode);
            OnChanged(nameof(ChartGroup));
            NotifyGraphMode();
            RequestCharts();
        }
    }
    public void ShowGraphs()
    {
        if (!HasRun || _disposed) return;
        _isGraphWorkspaceOpen = true;
        NotifyNavigation();
        if (Charts.Count + AlternativeCharts.Count == 0 && !_preparingCharts) RequestCharts();
    }
    public void ShowSummary()
    {
        _isGraphWorkspaceOpen = false;
        NotifyNavigation();
    }
    private void NotifyNavigation()
    {
        OnChanged(nameof(IsGraphWorkspaceOpen));
        OnChanged(nameof(IsSummaryVisible));
    }
}
