using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DashboardTachometerRenderingTests
{
    [Fact]
    public void WarningMinorTicksChangeColorWithoutJumpingOffTheNormalTickBaseline() => OnSta(() =>
    {
        var tach = CreateTachometer();
        Render(tach);
        var ordinary = PathGeometry.CreateFromGeometry(Geometry(tach, "_ticks")).Figures;
        Assert.NotEmpty(ordinary);
        var point = DashboardTachometer.PointOnArc(new Size(840, 100), 40 / 48d);
        var regular = ordinary.MinBy(figure => Math.Abs(figure.StartPoint.X - point.X))!;

        tach.Frame = tach.Frame with { ExactRedline = ExactRedlineResult.Exact(7500 * 2 * Math.PI / 60) };
        Render(tach);
        var warningFigures = PathGeometry.CreateFromGeometry(Geometry(tach, "_redlineTicks")).Figures;
        Assert.NotEmpty(warningFigures);
        var warning = warningFigures.MinBy(figure => Math.Abs(figure.StartPoint.X - point.X))!;
        Assert.Equal(regular.StartPoint, warning.StartPoint);
        var regularEnd = EndPoint(regular);
        var warningEnd = EndPoint(warning);
        Assert.Equal(regularEnd, warningEnd);
        Assert.Equal(4, (regularEnd - regular.StartPoint).Length, 5);
        Assert.True(regular.StartPoint.Y > point.Y);
    });

    [Fact]
    public void ApplicationGlowDoesNotAddAHaloOrChangeTheTachometer() => OnSta(() =>
    {
        var tach = CreateTachometer();
        tach.Resources["OrbitGlowOpacity"] = 1d;
        var lit = Render(tach);
        var segments = Geometry(tach, "_segments");
        var ticks = Geometry(tach, "_ticks");
        var reading = DashboardTachometer.ResolveReading(tach.Frame, true);
        tach.Resources["OrbitGlowOpacity"] = 0d;
        var unlit = Render(tach);
        Assert.Equal(lit, unlit);
        Assert.Same(segments, Geometry(tach, "_segments"));
        Assert.Same(ticks, Geometry(tach, "_ticks"));
        Assert.True(segments.IsFrozen && ticks.IsFrozen);
        Assert.Equal(reading, DashboardTachometer.ResolveReading(tach.Frame, true));
        Assert.Equal(unlit, Render(tach));
    });

    [Fact]
    public void MajorAndMinorTicksUseOneGridWithoutAnExtraRedlineSpike() => OnSta(() =>
    {
        var tach = CreateTachometer();
        tach.Frame = tach.Frame with { ExactRedline = ExactRedlineResult.Exact(9350 * 2 * Math.PI / 60) };
        Render(tach);
        var ticks = PathGeometry.CreateFromGeometry(Geometry(tach, "_ticks")).Figures
            .Concat(PathGeometry.CreateFromGeometry(Geometry(tach, "_redlineTicks")).Figures)
            .OrderBy(figure => figure.StartPoint.X).ToArray();
        Assert.Equal(51, ticks.Length);
        Assert.Equal(50, PathGeometry.CreateFromGeometry(Geometry(tach, "_segments")).Figures.Count);
        for (var index = 0; index < ticks.Length; index++)
        {
            var tick = ticks[index];
            var length = (EndPoint(tick) - tick.StartPoint).Length;
            Assert.Equal(index % 10 == 0 ? 7 : 4, length, 5);
            var fraction = index / 50d;
            var point = DashboardTachometer.PointOnArc(new Size(840, 100), fraction);
            Assert.Equal(5, (tick.StartPoint - point).Length, 5);
            var tickNormal = (EndPoint(tick) - tick.StartPoint) / length;
            var origin = tick.StartPoint - tickNormal * 5;
            Assert.Equal(point.X, origin.X, 5);
            Assert.Equal(point.Y, origin.Y, 5);
        }
        Assert.Equal(4, PathGeometry.CreateFromGeometry(Geometry(tach, "_redlineTicks")).Figures.Count);
    });

    private static DashboardTachometer CreateTachometer() => new()
    {
        IsAvailable = true,
        Accent = Brushes.Turquoise,
        Foreground = Brushes.White,
        Redline = Brushes.Red,
        Frame = NativeGaugeFrame.Empty(SpeedUnit.MilesPerHour) with { EngineRpm = 6250, TachometerMaximumRpm = 10000 }
    };

    private static Geometry Geometry(DashboardTachometer tach, string field) => Assert.IsAssignableFrom<Geometry>(
        typeof(DashboardTachometer).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tach));

    private static Point EndPoint(PathFigure figure) => Assert.Single(figure.Segments) switch
    {
        LineSegment line => line.Point,
        PolyLineSegment line => Assert.Single(line.Points),
        _ => throw new InvalidOperationException("A tach tick must be a single straight segment.")
    };

    private static byte[] Render(DashboardTachometer tach)
    {
        tach.Measure(new Size(840, 100));
        tach.Arrange(new Rect(0, 0, 840, 100));
        tach.UpdateLayout();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            typeof(DashboardTachometer).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tach, [context]);
        var bitmap = new RenderTargetBitmap(840, 100, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[840 * 100 * 4];
        bitmap.CopyPixels(pixels, 840 * 4, 0);
        return pixels;
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Dashboard tach rendering check timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
