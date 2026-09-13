using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunWorkspacePreparationTests
{
    [Fact]
    public void AllModulesReuseTheirChartFamilyAndKeepEveryRequestedPanel()
    {
        var request = Request(RunWorkspaceCatalog.Modules.Select(module => module.Id).ToArray());
        var prepared = RunWorkspacePreparation.Prepare(request);
        Assert.Equal(11, prepared.Modules.Length);
        Assert.Equal(5, prepared.TimeGroups.Count);
        Assert.Equal(3, prepared.AlternativeModes.Count);
        Assert.Same(prepared.TimeGroups[RunChartGroup.Engine][0], prepared.Modules.Single(module => module.Id == "rpm").TimePanels[0]);
        Assert.Same(prepared.TimeGroups[RunChartGroup.Engine][1], prepared.Modules.Single(module => module.Id == "power").TimePanels[0]);
        Assert.Same(prepared.TimeGroups[RunChartGroup.Engine][2], prepared.Modules.Single(module => module.Id == "torque").TimePanels[0]);
        Assert.Same(prepared.TimeGroups[RunChartGroup.Engine][3], prepared.Modules.Single(module => module.Id == "boost").TimePanels[0]);
        Assert.Equal(2, prepared.Modules.Single(module => module.Id == "power-rpm").AlternativePanels.Length);
    }

    [Fact]
    public void PreparingOneModuleDoesNotPrepareUnselectedFamiliesAndCancelsBetweenFamilies()
    {
        var prepared = RunWorkspacePreparation.Prepare(Request(["rpm", "rpm", "unknown", null!]));
        Assert.Single(prepared.Modules);
        Assert.Single(prepared.TimeGroups);
        Assert.Empty(prepared.AlternativeModes);
        Assert.Throws<OperationCanceledException>(() => RunWorkspacePreparation.Prepare(Request(["speed"]), () => false));
        var empty = RunWorkspacePreparation.Prepare(Request([]));
        Assert.Empty(empty.Modules); Assert.Empty(empty.TimeGroups); Assert.Empty(empty.AlternativeModes);
    }

    [Fact]
    public void SideBySideTimeChartsKeepSharedRangesComparisonColorAndRecordedCursorData()
    {
        var prepared = RunWorkspacePreparation.Prepare(Request(["rpm"]));
        var module = Assert.Single(prepared.Modules);
        var original = Assert.Single(module.TimePanels);
        var plots = RunWorkspacePreparation.CreatePlots(module, true, RunWorkspaceComparisonMode.SideBySide);
        Assert.Equal(new[] { "A", "B" }, plots.Select(plot => plot.Label));
        Assert.All(plots, plot =>
        {
            Assert.True(plot.IsTime); Assert.False(plot.IsAlternative); Assert.True(plot.IsSplit);
            Assert.Equal(original.Minimum, plot.TimePanel!.Minimum);
            Assert.Equal(original.Maximum, plot.TimePanel.Maximum);
        });
        var a = Assert.Single(plots[0].TimePanel!.Series);
        var b = Assert.Single(plots[1].TimePanel!.Series);
        Assert.False(a.Comparison); Assert.True(b.Comparison);
        Assert.Same(original.Series[0], a); Assert.Same(original.Series[1], b);
        Assert.Equal(2000, a.ReadRecordedValue!(.01));
        Assert.Equal(6500, b.ReadRecordedValue!(.01));
        Assert.Equal(2, original.Series.Length);
    }

    [Fact]
    public void SideBySideScatterRetainsJointAxesAndOriginalSourceTimes()
    {
        var module = Assert.Single(RunWorkspacePreparation.Prepare(Request(["gforce"])).Modules);
        var original = Assert.Single(module.AlternativePanels);
        var plots = RunWorkspacePreparation.CreatePlots(module, true, RunWorkspaceComparisonMode.SideBySide);
        foreach (var plot in plots)
        {
            Assert.Equal(original.XMinimum, plot.AlternativePanel!.XMinimum);
            Assert.Equal(original.XMaximum, plot.AlternativePanel.XMaximum);
            Assert.Equal(original.YMinimum, plot.AlternativePanel.YMinimum);
            Assert.Equal(original.YMaximum, plot.AlternativePanel.YMaximum);
            Assert.True(plot.AlternativePanel.EqualAxes);
        }
        Assert.False(Assert.Single(plots[0].AlternativePanel!.Series).Comparison);
        var b = Assert.Single(plots[1].AlternativePanel!.Series);
        Assert.True(b.Comparison);
        Assert.Equal(10, b.Points[0].SourceSeconds);
        Assert.Same(original.Series[1], b);
    }

    [Fact]
    public void SideBySideBarsDoNotMixRunsAndEmptyComparisonStillHasItsOwnPanel()
    {
        var original = new RunAlternativePlotPanel("Temperatures", "", RunAlternativePlotKind.Bars, "", "", "Temperature", "°F",
            -.5, 3.5, 0, 250, [new("Run A", false, [], 0), new("Run B", true, [], 0)],
            [new(0, false, 100, 0), new(0, true, 220, 10)], ["Front"]);
        var plots = RunWorkspacePreparation.CreatePlots(new("tire-change", [], [original]), true, RunWorkspaceComparisonMode.SideBySide);
        Assert.False(Assert.Single(plots[0].AlternativePanel!.Bars).Comparison);
        Assert.True(Assert.Single(plots[1].AlternativePanel!.Bars).Comparison);
        Assert.Equal(250, plots[0].AlternativePanel!.YMaximum);
        Assert.Equal(250, plots[1].AlternativePanel!.YMaximum);
        var missing = new RunPlotPanel("RPM", "RPM", [new("A", false, 0, [])], 0, 8000);
        var emptyComparison = RunWorkspacePreparation.CreatePlots(new("rpm", [missing], []), true, RunWorkspaceComparisonMode.SideBySide);
        Assert.Equal(2, emptyComparison.Length);
        Assert.Empty(emptyComparison[1].TimePanel!.Series);
    }

    [Fact]
    public void SingleRunOrOverlayUsesOriginalPanelsWithoutMutatingPreparedData()
    {
        var module = Assert.Single(RunWorkspacePreparation.Prepare(Request(["rpm"])).Modules);
        var overlay = Assert.Single(RunWorkspacePreparation.CreatePlots(module, true, RunWorkspaceComparisonMode.Overlay));
        var single = Assert.Single(RunWorkspacePreparation.CreatePlots(module, false, RunWorkspaceComparisonMode.SideBySide));
        Assert.Same(module.TimePanels[0], overlay.TimePanel);
        Assert.Same(module.TimePanels[0], single.TimePanel);
        Assert.False(overlay.IsSplit); Assert.False(single.IsSplit);
        Assert.Equal(RunWorkspaceWidth.Full, overlay.Width);
    }

    private static RunWorkspaceRequest Request(string[] ids)
    {
        var a = new RecordedRun { Samples = [RunPresentationTests.Sample(0, 1000), RunPresentationTests.Sample(.01, 2000)] };
        var b = new RecordedRun { Samples = [RunPresentationTests.Sample(10, 6000), RunPresentationTests.Sample(10.01, 6500)] };
        return new(a, b, ids, SpeedUnit.MilesPerHour, TireTemperatureUnit.Fahrenheit, TorqueUnit.NewtonMeters,
            BoostPressureUnit.Psi, 0, 10, null, null, null, null, true, null);
    }
}
