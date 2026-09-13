using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

internal sealed record RunWorkspaceRequest(RecordedRun RunA, RecordedRun? RunB, string[] ModuleIds,
    SpeedUnit Speed, TireTemperatureUnit Temperature, TorqueUnit Torque, BoostPressureUnit Boost,
    double OffsetA, double OffsetB, RunInterval? TimeBoundsA, RunInterval? TimeBoundsB,
    RunInterval? AlternativeBoundsA, RunInterval? AlternativeBoundsB, bool FullThrottleOnly, TransmissionGear? Gear);

internal sealed record RunWorkspacePreparedModule(string Id, RunPlotPanel[] TimePanels, RunAlternativePlotPanel[] AlternativePanels);
internal sealed record RunWorkspacePrepared(RunWorkspaceRequest Request, RunWorkspacePreparedModule[] Modules,
    IReadOnlyDictionary<RunChartGroup, RunPlotPanel[]> TimeGroups,
    IReadOnlyDictionary<RunPlotMode, RunAlternativePlotPanel[]> AlternativeModes);

public sealed class RunWorkspacePlot(string label, RunPlotPanel? timePanel, RunAlternativePlotPanel? alternativePanel) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Label { get; } = label;
    public RunPlotPanel? TimePanel { get; private set; } = timePanel;
    public RunAlternativePlotPanel? AlternativePanel { get; private set; } = alternativePanel;
    public RunChartGroup SourceGroup { get; init; }
    public bool IsTime => TimePanel is not null;
    public bool IsAlternative => AlternativePanel is not null;
    public bool IsSplit => Label is "A" or "B";
    public RunWorkspaceWidth Width => IsSplit ? RunWorkspaceWidth.Compact : RunWorkspaceWidth.Full;
    internal void UpdatePanels(RunWorkspacePlot next)
    {
        if (ReferenceEquals(TimePanel, next.TimePanel) && ReferenceEquals(AlternativePanel, next.AlternativePanel)) return;
        TimePanel = next.TimePanel; AlternativePanel = next.AlternativePanel;
        foreach (var name in new[] { nameof(TimePanel), nameof(AlternativePanel), nameof(IsTime), nameof(IsAlternative) })
            PropertyChanged?.Invoke(this, new(name));
    }
}

internal static class RunWorkspacePreparation
{
    // Called by the existing revision-guarded worker. Each chart family scans the run at most once.
    internal static RunWorkspacePrepared Prepare(RunWorkspaceRequest request, Func<bool>? isCurrent = null)
    {
        var timeGroups = new Dictionary<RunChartGroup, RunPlotPanel[]>();
        var alternativeModes = new Dictionary<RunPlotMode, RunAlternativePlotPanel[]>();
        var modules = new List<RunWorkspacePreparedModule>();
        foreach (var id in request.ModuleIds.Where(id => RunWorkspaceCatalog.Find(id) is not null)
                     .Distinct(StringComparer.Ordinal).Take(RunWorkspaceCatalog.MaximumModules))
        {
            if (isCurrent is not null && !isCurrent()) throw new OperationCanceledException();
            var (group, index, mode) = Source(id);
            if (mode == RunPlotMode.TimeSeries)
            {
                if (!timeGroups.TryGetValue(group, out var panels))
                {
                    panels = RunPresentation.Charts(request.RunA, request.RunB, group, request.Speed, request.Temperature,
                        request.OffsetA, request.OffsetB, request.TimeBoundsA, request.TimeBoundsB, request.Torque, request.Boost);
                    timeGroups.Add(group, panels);
                }
                modules.Add(new(id, [panels[index]], []));
            }
            else
            {
                if (!alternativeModes.TryGetValue(mode, out var panels))
                {
                    panels = RunAlternativePlots.Build(request.RunA, request.RunB, mode, request.Temperature, request.Torque,
                        request.AlternativeBoundsA, request.AlternativeBoundsB, request.FullThrottleOnly, request.Gear);
                    alternativeModes.Add(mode, panels);
                }
                modules.Add(new(id, [], panels));
            }
        }
        return new(request, modules.ToArray(), timeGroups, alternativeModes);
    }

    internal static string[] EvidenceModules(RunChartGroup group, RunPlotMode mode) => mode switch
    {
        RunPlotMode.PowerByRpm => ["power-rpm"],
        RunPlotMode.GForce => ["gforce"],
        RunPlotMode.TireChange => ["tire-change"],
        _ => group switch
        {
            RunChartGroup.Inputs => ["inputs", "speed"],
            RunChartGroup.Engine => ["rpm", "power", "torque", "boost"],
            RunChartGroup.Tires => ["tires"],
            RunChartGroup.Handling => ["gforce-time"],
            _ => ["speed"]
        }
    };

    private static (RunChartGroup Group, int Index, RunPlotMode Mode) Source(string id) => id switch
    {
        "inputs" => (RunChartGroup.Inputs, 0, RunPlotMode.TimeSeries),
        "rpm" => (RunChartGroup.Engine, 0, RunPlotMode.TimeSeries),
        "power" => (RunChartGroup.Engine, 1, RunPlotMode.TimeSeries),
        "torque" => (RunChartGroup.Engine, 2, RunPlotMode.TimeSeries),
        "boost" => (RunChartGroup.Engine, 3, RunPlotMode.TimeSeries),
        "gforce-time" => (RunChartGroup.Handling, 0, RunPlotMode.TimeSeries),
        "tires" => (RunChartGroup.Tires, 0, RunPlotMode.TimeSeries),
        "power-rpm" => (RunChartGroup.Engine, 0, RunPlotMode.PowerByRpm),
        "gforce" => (RunChartGroup.Handling, 0, RunPlotMode.GForce),
        "tire-change" => (RunChartGroup.Tires, 0, RunPlotMode.TireChange),
        _ => (RunChartGroup.Speed, 0, RunPlotMode.TimeSeries)
    };

    internal static RunWorkspacePlot[] CreatePlots(RunWorkspacePreparedModule module, bool hasComparison, RunWorkspaceComparisonMode mode)
    {
        var plots = new List<RunWorkspacePlot>();
        var split = hasComparison && mode == RunWorkspaceComparisonMode.SideBySide;
        foreach (var panel in module.TimePanels)
        {
            if (!split) { plots.Add(new("", panel, null)); continue; }
            // Preserve joint ranges and the original A/B series identity, gaps and cursor readers.
            plots.Add(new("A", Split(panel, false), null));
            plots.Add(new("B", Split(panel, true), null));
        }
        foreach (var panel in module.AlternativePanels)
        {
            if (!split) { plots.Add(new("", null, panel)); continue; }
            plots.Add(new("A", null, Split(panel, false)));
            plots.Add(new("B", null, Split(panel, true)));
        }
        var group = Source(module.Id).Group;
        return plots.Select(plot => new RunWorkspacePlot(plot.Label, plot.TimePanel, plot.AlternativePanel) { SourceGroup = group }).ToArray();
    }

    internal static RunPlotPanel Split(RunPlotPanel panel, bool comparison) =>
        panel with { Series = panel.Series.Where(series => series.Comparison == comparison).ToArray() };

    internal static RunAlternativePlotPanel Split(RunAlternativePlotPanel panel, bool comparison) => panel with
    {
        Series = panel.Series.Where(series => series.Comparison == comparison).ToArray(),
        Bars = panel.Bars.Where(bar => bar.Comparison == comparison).ToArray()
    };
}
