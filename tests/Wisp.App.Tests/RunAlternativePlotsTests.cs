using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunAlternativePlotsTests
{
    private static RunSample Sample(double seconds, float rpm = 4000, byte throttle = 255, TransmissionGear gear = TransmissionGear.Third) =>
        RunPresentationTests.Sample(seconds, rpm) with
        {
            State = RunPresentationTests.Sample(seconds, rpm).State with
            { PowerWatts = 74569.9872f, TorqueNm = 500, Accelerator = throttle, Gear = gear }
        };
    private static RunAlternativePlotPanel[] Plot(RecordedRun a, RecordedRun? b = null, RunPlotMode mode = RunPlotMode.PowerByRpm,
        bool fullThrottle = true, TransmissionGear? gear = null, RunInterval? intervalA = null, RunInterval? intervalB = null,
        TorqueUnit torque = TorqueUnit.NewtonMeters) => RunAlternativePlots.Build(a, b, mode, TireTemperatureUnit.Celsius, torque, intervalA, intervalB, fullThrottle, gear);

    [Fact]
    public void PowerAndTorqueUseOriginalReadingsWithSelectedUnits()
    {
        var panels = Plot(new() { Samples = [Sample(1)] }, torque: TorqueUnit.PoundFeet);
        Assert.Equal(2, panels.Length);
        var power = Assert.Single(Assert.Single(panels[0].Series).Points);
        var torque = Assert.Single(Assert.Single(panels[1].Series).Points);
        Assert.Equal(100, power.Y, 4); Assert.Equal(500 * .7375621493, torque.Y, 6);
        Assert.Equal(4000, power.X); Assert.Equal(1, power.SourceSeconds); Assert.Equal(0, power.SampleIndex);
        Assert.Equal("hp", panels[0].YUnit); Assert.Equal("lb-ft", panels[1].YUnit);
    }

    [Fact]
    public void PowerFiltersExcludeMenuReverseUnknownRpmAndOnlyTheirOwnMode()
    {
        RunSample[] samples = [Sample(0), Sample(.1, throttle: 180), Sample(.2, gear: TransmissionGear.Fourth),
            Sample(.3, gear: TransmissionGear.Reverse), Sample(.4, rpm: 0), Sample(.5) with { IsDriving = false }];
        var run = new RecordedRun { Samples = samples };
        var filtered = Plot(run, gear: TransmissionGear.Third)[0].Series[0];
        Assert.Equal(0, Assert.Single(filtered.Points).SampleIndex);
        Assert.Equal(3, Plot(run, fullThrottle: false)[0].Series[0].Points.Length);
        Assert.Equal(5, Plot(run, mode: RunPlotMode.GForce, gear: TransmissionGear.Third)[0].Series[0].Points.Length);
    }

    [Fact]
    public void EachComparisonIntervalUsesItsOwnSourceTimeWithoutInventedBoundaryDots()
    {
        var a = new RecordedRun { Samples = [Sample(0), Sample(.1), Sample(.2)] };
        var b = new RecordedRun { Samples = [Sample(10), Sample(10.1), Sample(10.2)] };
        var panel = Plot(a, b, intervalA: new(.05, .15), intervalB: new(10.05, 10.15))[0];
        Assert.Equal(.1, Assert.Single(panel.Series[0].Points).SourceSeconds);
        Assert.Equal(10.1, Assert.Single(panel.Series[1].Points).SourceSeconds);
        Assert.True(panel.Series[1].Comparison);
    }

    [Fact]
    public void ElectricAndNaturallyAspiratedSamplesDoNotInventEngineOrBoostData()
    {
        var ev = Sample(0, 0) with { State = Sample(0, 0).State with { NumCylinders = 0, BoostPressurePsi = -10 } };
        var na = Sample(.1) with { State = Sample(.1).State with { BoostPressurePsi = -12 } };
        var run = new RecordedRun { Samples = [ev, na] };
        var panel = Plot(run)[0];
        Assert.Equal(1, Assert.Single(panel.Series[0].Points).SampleIndex);
        Assert.DoesNotContain(Plot(run), p => p.YUnit.Contains("PSI"));
        var unknownPower = na with { State = na.State with { PowerWatts = float.NaN } };
        var missing = Plot(new() { Samples = [unknownPower] });
        Assert.Empty(missing[0].Series[0].Points); Assert.Single(missing[1].Series[0].Points);
    }

    [Fact]
    public void GForceSharesSymmetricAxesAcrossBothRunsAndPreservesSigns()
    {
        var a = Sample(0) with { State = Sample(0).State with { LateralAccelerationMetersPerSecondSquared = 9.80665f, LongitudinalAccelerationMetersPerSecondSquared = -19.6133f } };
        var b = Sample(0) with { State = Sample(0).State with { LateralAccelerationMetersPerSecondSquared = -29.41995f, LongitudinalAccelerationMetersPerSecondSquared = 9.80665f } };
        var panel = Assert.Single(Plot(new() { Samples = [a] }, new() { Samples = [b] }, RunPlotMode.GForce));
        Assert.True(panel.EqualAxes); Assert.Equal(-panel.XMaximum, panel.XMinimum);
        Assert.Equal(panel.XMinimum, panel.YMinimum); Assert.Equal(panel.XMaximum, panel.YMaximum);
        Assert.Equal(-2, panel.Series[0].Points[0].Y, 5); Assert.Equal(-3, panel.Series[1].Points[0].X, 5);
        Assert.Contains("+Y acceleration", panel.Description); Assert.Contains("−Y braking", panel.Description);
    }

    [Fact]
    public void BoundedReductionPreservesActualExtremaIndicesAndDuplicateTimes()
    {
        var points = Enumerable.Range(0, 180_000).Select(i => new RunAlternativePoint(i == 1001 ? 9900 : 3000,
            i == 9001 ? 900 : i == 8001 ? -50 : 100, i / 100d, i, i / 7, TransmissionGear.Third)).ToArray();
        var reduced = RunAlternativePlots.Reduce(points);
        Assert.InRange(reduced.Length, 1, RunAlternativePlots.MaximumSeriesPoints);
        Assert.Contains(points[1001], reduced); Assert.Contains(points[9001], reduced); Assert.Contains(points[8001], reduced);
        Assert.Equal(points[0], reduced[0]); Assert.Equal(points[^1], reduced[^1]);
        Assert.All(reduced, point => Assert.Equal(points[point.SampleIndex], point));
        var duplicate = Plot(new() { Samples = [Sample(0, 1000), Sample(0, 1500), Sample(.1, 2000) with { Segment = 1 }] })[0];
        Assert.Equal(3, duplicate.Series[0].Points.Length); Assert.Equal(1, duplicate.Series[0].Points[2].Segment);
    }

    [Fact]
    public void TireBarsMatchSelectedCoreStatisticsAndOmitMissingAxles()
    {
        var a = Sample(0) with { State = Sample(0).State with { TireTemperatureFahrenheit = new(32, 32, 0, 0) } };
        var b = Sample(.2) with { State = Sample(.2).State with { TireTemperatureFahrenheit = new(212, 212, 0, 0) } };
        var panel = Assert.Single(Plot(new() { Samples = [a, b] }, mode: RunPlotMode.TireChange, intervalA: new(.05, .15)));
        Assert.Equal(2, panel.Bars.Length); Assert.Equal(25, panel.Bars[0].Value, 6); Assert.Equal(75, panel.Bars[1].Value, 6);
        Assert.All(panel.Bars, bar => Assert.True(bar.Category < 2));
        Assert.Equal(.05, panel.Bars[0].SourceSeconds); Assert.Equal(.15, panel.Bars[1].SourceSeconds);
        Assert.Equal("°C", panel.YUnit);
    }

    [Fact]
    public void InvalidBoundsAndGearFailAndTimeSeriesKeepsExistingPath()
    {
        var run = new RecordedRun { Samples = [Sample(0)] };
        Assert.Throws<ArgumentOutOfRangeException>(() => Plot(run, intervalA: new(1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plot(run, gear: TransmissionGear.Neutral));
        Assert.Empty(Plot(run, mode: RunPlotMode.TimeSeries));
    }

    [Fact]
    public void TireBarsNeverRelabelALaterOrEarlierReadingAsABoundaryInsideAGap()
    {
        var run = new RecordedRun { Samples = [Sample(0), Sample(1), Sample(2)] };
        var startsInGap = Assert.Single(Plot(run, mode: RunPlotMode.TireChange, intervalA: new(.5, 1)));
        Assert.Equal(2, startsInGap.Bars.Length);
        Assert.All(startsInGap.Bars, bar => { Assert.True(bar.Category % 2 == 1); Assert.Equal(1, bar.SourceSeconds); });
        var endsInGap = Assert.Single(Plot(run, mode: RunPlotMode.TireChange, intervalA: new(1, 1.5)));
        Assert.Equal(2, endsInGap.Bars.Length);
        Assert.All(endsInGap.Bars, bar => { Assert.True(bar.Category % 2 == 0); Assert.Equal(1, bar.SourceSeconds); });
    }

    [Fact]
    public void OffscreenViewsRenderDataWithoutCreatingAWindow()
    {
        OnSta(() =>
        {
            var run = new RecordedRun { Samples = [Sample(0), Sample(.1, 4500)] };
            foreach (var panel in Plot(run).Concat(Plot(run, mode: RunPlotMode.GForce)).Concat(Plot(run, mode: RunPlotMode.TireChange)))
            {
                var view = new RunAlternativePlotView { Panel = panel, Width = 700, Height = 360, AccentBrush = Brushes.Red, TextBrush = Brushes.White, MutedBrush = Brushes.Gray };
                Assert.False(view.IsVisible);
                Assert.True(RenderContainsRed(view));
            }
            var chart = new RunChartView
            {
                Panel = RunPresentation.Charts(run, null, RunChartGroup.Engine, SpeedUnit.MilesPerHour, TireTemperatureUnit.Fahrenheit)[0],
                Width = 700,
                Height = 360,
                StartSeconds = 0,
                EndSeconds = .1,
                AccentBrush = Brushes.Red,
                RenderOffscreen = true,
                Markers = [new(.05, "Shift", false)]
            };
            Assert.True(RenderContainsRed(chart));
        });
    }

    private static bool RenderContainsRed(FrameworkElement view)
    {
        view.Measure(new Size(700, 360)); view.Arrange(new Rect(0, 0, 700, 360)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(700, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var pixels = new byte[700 * 360 * 4]; bitmap.CopyPixels(pixels, 700 * 4, 0);
        for (var y = 90; y < 290; y++) for (var x = 65; x < 680; x++)
            { var i = (y * 700 + x) * 4; if (pixels[i + 2] > 100 && pixels[i + 1] < 50 && pixels[i] < 50) return true; }
        return false;
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Offscreen plot rendering exceeded its deadline.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
