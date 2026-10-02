using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunPlotCursorCachingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LapCursorMovesWithoutRecreatingTheRecordedLine(bool map) => OnSta(() =>
    {
        var lap = Lap();
        var data = new LapReviewPlotData(lap, null, null, LapReviewChannel.Speed,
            SpeedUnit.KilometersPerHour, 0, 0, lap.Points.Length - 1);
        var plot = new LapReviewPlot { IsMap = map, Data = data };
        Arrange(plot);
        var initial = Render(plot);
        var prepared = Prepared(plot, "_preparedDrawing");
        var firstCursor = initial.Children.OfType<GeometryDrawing>().Last().Bounds;
        for (var cursor = 1; cursor <= 100; cursor++)
        {
            plot.Data = data with { Cursor = cursor };
            var drawing = Render(plot);
            Assert.Same(prepared, Prepared(plot, "_preparedDrawing"));
            Assert.NotEqual(firstCursor, drawing.Children.OfType<GeometryDrawing>().Last().Bounds);
        }
        Assert.Null(PresentationSource.FromVisual(plot));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LapDrawingRefreshesForChannelUnitsSelectionReferenceAndSize(bool map) => OnSta(() =>
    {
        var lap = Lap();
        var plot = new LapReviewPlot
        {
            IsMap = map,
            Data = new(lap, null, null, LapReviewChannel.Speed,
            SpeedUnit.KilometersPerHour, 0, 0, lap.Points.Length - 1)
        };
        Arrange(plot); Render(plot);
        var prepared = Prepared(plot, "_preparedDrawing");
        Refresh(() => plot.Data = plot.Data! with { Channel = LapReviewChannel.Brake });
        Refresh(() => plot.Data = plot.Data! with { SpeedUnit = SpeedUnit.MilesPerHour });
        Refresh(() => plot.Data = plot.Data! with { SectionStart = 40, SectionEnd = 120 });
        Refresh(() => plot.Data = plot.Data! with { Reference = Lap() });
        Refresh(() => plot.Data = plot.Data! with { Wheel = 1 });
        Refresh(() => plot.Data = plot.Data! with { TemperatureUnit = TireTemperatureUnit.Celsius });
        Refresh(() => Arrange(plot, 640));
        plot.Data = null; Render(plot);
        Assert.Null(Prepared(plot, "_preparedDrawing"));
        plot.Data = new(lap, null, null, LapReviewChannel.Speed, SpeedUnit.KilometersPerHour, 50, 0, 100);
        Render(plot);
        Assert.NotNull(Prepared(plot, "_preparedDrawing"));

        void Refresh(Action change)
        {
            change(); Render(plot);
            var current = Prepared(plot, "_preparedDrawing");
            Assert.NotNull(current); Assert.NotSame(prepared, current);
            prepared = current;
        }
    });

    [Fact]
    public void TimeChartKeepsStaticDrawingWhileCursorValuesAndSelectionUpdate() => OnSta(() =>
    {
        var readTimes = new List<double>();
        var chart = Chart(seconds => { readTimes.Add(seconds); return seconds * 100; });
        Arrange(chart); Render(chart);
        var background = Prepared(chart, "_backgroundDrawing");
        var line = Prepared(chart, "_plotDrawing");
        var before = readTimes.Count;
        for (var step = 1; step <= 50; step++)
        {
            chart.CursorSeconds = step / 10d;
            chart.SelectionStart = 1; chart.SelectionEnd = step / 10d;
            var drawing = Render(chart);
            Assert.Same(background, Prepared(chart, "_backgroundDrawing"));
            Assert.Same(line, Prepared(chart, "_plotDrawing"));
            Assert.Equal(chart.CursorSeconds, readTimes[^1]);
            Assert.Contains(drawing.Children.OfType<GeometryDrawing>(), item => item.Geometry is LineGeometry);
        }
        Assert.Equal(before + 50, readTimes.Count);
        Assert.Null(PresentationSource.FromVisual(chart));
    });

    [Fact]
    public void TimeChartRefreshesForRangePanelMarkersThemeAndSize() => OnSta(() =>
    {
        var chart = Chart(_ => 30);
        Arrange(chart); Render(chart);
        var prepared = Prepared(chart, "_plotDrawing");
        Refresh(() => chart.StartSeconds = 1);
        Refresh(() => chart.EndSeconds = 8);
        Refresh(() => chart.Panel = chart.Panel! with { Title = "Engine output" });
        Refresh(() => chart.Markers = [new(3, "Corner", false)]);
        Refresh(() => chart.MutedBrush = Brushes.Lime);
        Refresh(() => chart.TextBrush = Brushes.Wheat);
        Refresh(() => chart.AccentBrush = Brushes.Magenta);
        Refresh(() => Arrange(chart, 640));

        void Refresh(Action change)
        {
            change(); Render(chart);
            var current = Prepared(chart, "_plotDrawing");
            Assert.NotNull(current); Assert.NotSame(prepared, current);
            prepared = current;
        }
    });

    private static RunChartView Chart(Func<double, double?> read) => new()
    {
        RenderOffscreen = true,
        StartSeconds = 0,
        EndSeconds = 10,
        Panel = new("Speed", "km/h", [new("Ground", false, 0,
            [new(0, 20, false), new(10, 60, false)]) { ReadRecordedValue = read }], 0, 100),
        Markers = [new(2, "Start", false)]
    };

    private static LapReviewLap Lap() => new()
    {
        Points = Enumerable.Range(0, 3000).Select(index => new LapReviewPoint(index, index / 60d,
            index / 60d, index, new(index, 0, index / 2f), index == 0,
            new() { State = RunTestData.State() with { GroundSpeedMetersPerSecond = 20 + index / 100f } })).ToArray()
    };

    private static object? Prepared(FrameworkElement plot, string field) =>
        plot.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plot);

    private static void Arrange(FrameworkElement plot, double width = 800)
    {
        plot.Measure(new(width, 300)); plot.Arrange(new Rect(0, 0, width, 300));
    }

    private static DrawingGroup Render(FrameworkElement plot)
    {
        var result = new DrawingGroup();
        using var drawing = result.Open();
        plot.GetType().GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plot, [drawing]);
        return result;
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Offscreen cursor cache check exceeded its bounded deadline.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
