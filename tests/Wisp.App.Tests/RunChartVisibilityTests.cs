using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunChartVisibilityTests
{
    [Fact]
    public void OpeningACollapsedGraphPaneDrawsWithoutChangingItsData() => OnSta(() =>
    {
        var panel = new RunPlotPanel("Recorded speed", "mph",
            [new("A · Ground", false, 0, [new(0, 20, false), new(1, 50, false)])], 0, 60);
        var chart = new RunChartView
        {
            Panel = panel,
            Width = 700,
            Height = 360,
            StartSeconds = 0,
            EndSeconds = 1,
            AccentBrush = Brushes.Red
        };
        chart.Measure(new Size(700, 360));
        chart.Arrange(new Rect(0, 0, 700, 360));
        var initial = new RenderTargetBitmap(700, 360, 96, 96, PixelFormats.Pbgra32);
        initial.Render(chart);
        Assert.False(chart.IsVisible);

        var pane = new Grid { Visibility = Visibility.Collapsed, Width = 700, Height = 360 };
        pane.Children.Add(chart);
        // A hidden, non-activating presentation source supplies WPF visibility semantics without showing a window.
        using var source = new HwndSource(new HwndSourceParameters("Wisp graph visibility test")
        {
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = 0x08000080,
            Width = 700,
            Height = 360,
            PositionX = -20000,
            PositionY = -20000
        });
        source.RootVisual = pane;
        Settle(pane);
        Assert.False(chart.IsVisible);

        pane.Visibility = Visibility.Visible;
        Settle(pane);
        Assert.True(chart.IsVisible);
        Assert.Same(panel, chart.Panel);
        AssertGraphDrawing(chart);

        pane.Visibility = Visibility.Collapsed;
        Settle(pane);
        pane.Visibility = Visibility.Visible;
        Settle(pane);
        AssertGraphDrawing(chart);
        source.RootVisual = null;
    });

    private static void AssertGraphDrawing(RunChartView chart)
    {
        var drawing = VisualTreeHelper.GetDrawing(chart);
        Assert.NotNull(drawing);
        var content = Drawings(drawing).ToArray();
        Assert.Contains(content, item => item is GlyphRunDrawing);
        Assert.Contains(content, item => item is GeometryDrawing { Pen.Brush: SolidColorBrush brush } && brush.Color == Colors.Red);
    }

    private static IEnumerable<Drawing> Drawings(Drawing drawing)
    {
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var descendant in Drawings(child)) yield return descendant;
    }

    private static void Settle(FrameworkElement pane)
    {
        pane.Measure(new Size(700, 360));
        pane.Arrange(new Rect(0, 0, 700, 360));
        pane.UpdateLayout();
        pane.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        pane.UpdateLayout();
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
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Graph visibility regression exceeded its bounded deadline.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
