using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LapReviewPlotRenderingTests
{
    [Fact]
    public void EstimatedContactHasAHollowCenterWhileReportedContactIsFilled() => OnSta(() =>
    {
        byte[] Draw(LapReviewContactKind kind)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 32, 32));
                LapReviewPalette.DrawContact(drawing, new Point(16, 16), Brushes.Black, kind: kind);
            }
            var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(16, 16, 1, 1), pixel, 4, 0);
            return pixel;
        }
        var estimated = Draw(LapReviewContactKind.PossibleContact);
        var recorded = Draw(LapReviewContactKind.SmashableObject);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, estimated);
        Assert.True(recorded[2] > 200 && recorded[1] > 60);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CursorMovementReusesStaticDrawingAndChangesOnlyTheMarker(bool isMap) => OnSta(() =>
    {
        var plot = Create(isMap);
        Render(plot);
        var prepared = Prepared(plot);
        for (var i = 0; i < 12; i++)
        {
            plot.Data = plot.Data! with { Cursor = i % 3 };
            Render(plot);
            Assert.Same(prepared, Prepared(plot));
        }
    });

    [Fact]
    public void SharedColorScaleChangesActualPixelsAndInvalidatesTheCachedDrawing() => OnSta(() =>
    {
        var plot = Create(true);
        var bitmap = Render(plot);
        var prepared = Prepared(plot);
        AssertPixel(bitmap, 200, 172, LapReviewPalette.GetBrush(.5));
        plot.Data = plot.Data! with { ColorRangeOverride = new(0, 100, true) };
        bitmap = Render(plot);
        Assert.NotSame(prepared, Prepared(plot));
        AssertPixel(bitmap, 200, 172, LapReviewPalette.GetBrush(.1));
        prepared = Prepared(plot);
        plot.Data = plot.Data with { ColorRangeOverride = new(0, 100, true) };
        Render(plot);
        Assert.Same(prepared, Prepared(plot));
    });

    [Fact]
    public void HidingReferencePathKeepsSharedMapBoundsAndDoesNotHideCurrentLap() => OnSta(() =>
    {
        var plot = Create(true);
        var bitmap = Render(plot);
        var prepared = Prepared(plot);
        var referencePixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(200, 28, 1, 1), referencePixel, 4, 0);
        Assert.Contains(referencePixel.Take(3), value => value > 0);
        AssertPixel(bitmap, 200, 172, LapReviewPalette.GetBrush(.5));
        plot.Data = plot.Data! with { ShowReferencePath = false };
        bitmap = Render(plot);
        Assert.NotSame(prepared, Prepared(plot));
        AssertPixel(bitmap, 200, 28, Brushes.Black);
        AssertPixel(bitmap, 200, 172, LapReviewPalette.GetBrush(.5));
    });

    private static LapReviewPlot Create(bool isMap)
    {
        var state = RunTestData.State() with { GroundSpeedMetersPerSecond = 10 / 3.6f };
        var lap = new LapReviewLap
        {
            RunId = Guid.Parse("229b456f-7216-4f83-8ad4-dbb119227db6"),
            Points = Enumerable.Range(0, 3).Select(i => new LapReviewPoint(i, i, i, i * 50,
                new(i * 50, 0, 0), i == 0, new() { State = state })).ToArray()
        };
        var reference = lap with
        {
            RunId = Guid.Parse("5f6fcd2e-b086-4be9-b171-9c26e2317b73"),
            Points = lap.Points.Select(p => p with { Position = p.Position with { Z = 100 } }).ToArray()
        };
        var comparison = new LapReviewComparison(lap.Points.Select((p, i) =>
            new LapReviewComparisonPoint(i, p.DistanceMeters, p.LapSeconds, 0, i)).ToArray(), 1, true, "matched");
        var plot = new LapReviewPlot
        {
            IsMap = isMap,
            Data = new(lap, reference, comparison, LapReviewChannel.Speed, SpeedUnit.KilometersPerHour, 0, 0, 2)
        };
        plot.Resources["InputBrush"] = Brushes.Black;
        plot.Resources["TextBrush"] = Brushes.White;
        plot.Resources["MutedBrush"] = Brushes.Gray;
        plot.Measure(new Size(400, 200)); plot.Arrange(new Rect(0, 0, 400, 200));
        return plot;
    }

    private static DrawingGroup Prepared(LapReviewPlot plot) => Assert.IsType<DrawingGroup>(
        typeof(LapReviewPlot).GetField("_preparedDrawing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plot));

    private static RenderTargetBitmap Render(LapReviewPlot plot)
    {
        plot.UpdateLayout();
        var bitmap = new RenderTargetBitmap(400, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(plot);
        Assert.Null(PresentationSource.FromVisual(plot));
        return bitmap;
    }

    private static void AssertPixel(BitmapSource bitmap, int x, int y, Brush brush)
    {
        var expected = Assert.IsType<SolidColorBrush>(brush).Color;
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        Assert.InRange(Math.Abs(pixel[0] - expected.B), 0, 2);
        Assert.InRange(Math.Abs(pixel[1] - expected.G), 0, 2);
        Assert.InRange(Math.Abs(pixel[2] - expected.R), 0, 2);
        Assert.Equal(255, pixel[3]);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Lap review plot rendering timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
