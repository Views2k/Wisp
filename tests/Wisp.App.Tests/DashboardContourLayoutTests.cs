using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DashboardContourLayoutTests
{
    [Theory]
    [InlineData(1280)]
    [InlineData(1440)]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(3840)]
    public void SpeedBlockFitsTheRealContourWithoutMovingItsRightEdge(double width) => OnSta(() =>
    {
        var surface = new Size(width, 330.94);
        var cell = new Rect(113, 107, (width - 176 - 96) / 5, 164.94);
        var inset = DashboardContourLayout.SpeedInset(surface, cell, 9);
        var fitted = new Rect(cell.Left + inset, cell.Top, cell.Width - inset, cell.Height);
        Assert.Equal(cell.Right, fitted.Right, 8);
        Assert.Equal(cell.Top, fitted.Top);
        Assert.Equal(cell.Bottom, fitted.Bottom);
        Assert.True(fitted.Width >= 120, "The repair must retain usable readout space.");
        fitted.Inflate(9, 9);
        var contour = OrbitSurface.CreateGeometry(new Rect(surface), OrbitSurfaceShape.Swept, default);
        Assert.Equal(IntersectionDetail.FullyContains, contour.FillContainsWithDetail(new RectangleGeometry(fitted)));
    });

    [Fact]
    public void WideReportedLayoutNeedsInsetAndAlreadySafeContentDoesNot() => OnSta(() =>
    {
        var surface = new Size(2492.667, 330.94);
        var cell = new Rect(113, 107, 444.1334, 164.94);
        var contour = OrbitSurface.CreateGeometry(new Rect(surface), OrbitSurfaceShape.Swept, default);
        Assert.NotEqual(IntersectionDetail.FullyContains,
            contour.FillContainsWithDetail(new RectangleGeometry(cell)));
        Assert.True(DashboardContourLayout.SpeedInset(surface, cell, 9) > 0);
        Assert.Equal(0, DashboardContourLayout.SpeedInset(surface, new Rect(600, 107, 300, 164.94), 9));
    });

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Contour check timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
