using System.Windows;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DashboardTachometerTests
{
    [Theory]
    [InlineData(-100, 8000, 0, 8000, 0)]
    [InlineData(0, 8000, 0, 8000, 0)]
    [InlineData(4000, 8000, 4000, 8000, 0.5)]
    [InlineData(8000, 8000, 8000, 8000, 1)]
    [InlineData(9000, 8000, 8000, 8000, 1)]
    [InlineData(5000, 9800, 5000, 10000, 0.5)]
    [InlineData(3500, 6500, 3500, 7000, 0.5)]
    public void SweepClampsRpmAndMatchesTheExistingDisplayedTachometerScale(
        double rpm, double nativeMaximum, double expectedRpm, double expectedMaximum, double expectedProgress)
    {
        var reading = DashboardTachometer.ResolveReading(Frame(rpm, nativeMaximum), true);

        Assert.True(reading.IsAvailable);
        Assert.Equal(expectedRpm, reading.Rpm);
        Assert.Equal(expectedMaximum, reading.ScaleMaximumRpm);
        Assert.Equal(expectedProgress, reading.Progress, 6);
    }

    [Theory]
    [InlineData(false, false, false, 4000, 8000)]
    [InlineData(true, true, false, 4000, 8000)]
    [InlineData(true, false, true, 4000, 8000)]
    [InlineData(true, false, false, double.NaN, 8000)]
    [InlineData(true, false, false, double.PositiveInfinity, 8000)]
    [InlineData(true, false, false, 4000, double.NaN)]
    [InlineData(true, false, false, 4000, double.PositiveInfinity)]
    [InlineData(true, false, false, 4000, 0)]
    [InlineData(true, false, false, 4000, -1)]
    public void UnavailableElectricAndInvalidNativeFramesNeverShowRpmProgress(
        bool available, bool electric, bool invalidated, double rpm, double maximum)
    {
        var frame = Frame(rpm, maximum) with { IsElectric = electric, NativeGaugeSourceInvalidated = invalidated };

        var reading = DashboardTachometer.ResolveReading(frame, available);

        Assert.False(reading.IsAvailable);
        Assert.Equal(0, reading.Progress);
        Assert.Equal(0, reading.ScaleMaximumRpm);
    }

    [Theory]
    [InlineData(100, 32)]
    [InlineData(280, 50)]
    [InlineData(900, 50)]
    [InlineData(900, 80)]
    [InlineData(840, 100)]
    public void ArcStaysInsideItsBoundsAndRisesBetweenItsEndpoints(double width, double height)
    {
        var size = new Size(width, height);
        var start = DashboardTachometer.PointOnArc(size, 0);
        var middle = DashboardTachometer.PointOnArc(size, 0.5);
        var end = DashboardTachometer.PointOnArc(size, 1);

        Assert.True(start.Y > end.Y);
        Assert.True(middle.Y < Math.Min(start.Y, end.Y));
        Assert.Equal(width / 2, middle.X);
        for (var index = 0; index <= 20; index++)
        {
            var point = DashboardTachometer.PointOnArc(size, index / 20d);
            Assert.InRange(point.X, 0, width);
            Assert.InRange(point.Y, 0, height);
        }
        Assert.Equal(start, DashboardTachometer.PointOnArc(size, -1));
        Assert.Equal(end, DashboardTachometer.PointOnArc(size, 2));
        Assert.Equal(start, DashboardTachometer.PointOnArc(size, double.NaN));
    }

    [Fact]
    public void ReferenceSizeSweepKeepsItsCrownAndUnequalEndpointsAboveTheLabels()
    {
        var size = new Size(840, 100);
        Assert.Equal(new Point(6, 60), DashboardTachometer.PointOnArc(size, 0));
        Assert.Equal(new Point(420, 12), DashboardTachometer.PointOnArc(size, 0.5));
        Assert.Equal(new Point(834, 50), DashboardTachometer.PointOnArc(size, 1));
    }

    [Theory]
    [InlineData(0, 10, 20)]
    [InlineData(0.2, 24, 20)]
    [InlineData(0.8, 24, 20)]
    [InlineData(1, 34, 20)]
    public void TickLabelsFitInsideTheReferenceControlIncludingBothEndpoints(double fraction, double width, double height)
    {
        var size = new Size(840, 100);
        var origin = DashboardTachometer.TickLabelOrigin(size, fraction, new Size(width, height));
        var point = DashboardTachometer.PointOnArc(size, fraction);

        Assert.InRange(origin.X, 0, size.Width - width);
        Assert.InRange(origin.Y, point.Y + 12, size.Height - height);
        Assert.InRange(point.X, origin.X, origin.X + width);
    }

    [Theory]
    [InlineData(8000, new double[] { 0, 2000, 4000, 6000, 8000 })]
    [InlineData(10000, new double[] { 0, 2000, 4000, 6000, 8000, 10000 })]
    [InlineData(7000, new double[] { 0, 2000, 4000, 6000, 7000 })]
    [InlineData(0, new double[] { })]
    public void MajorTicksUseReadableIntervalsAndKeepTheActualEndpoint(double maximum, double[] expected)
    {
        Assert.Equal(expected, DashboardTachometer.MajorTickValues(maximum));
    }

    [Theory]
    [InlineData(6500, 8000, 0.8125)]
    [InlineData(7500, 9800, 0.75)]
    public void RedlineMarkerUsesTheExactNativeValueWithinTheDisplayedScale(double redlineRpm, double maximum, double expected)
    {
        var frame = Frame(4000, maximum) with { ExactRedline = ExactRedlineResult.Exact(redlineRpm * 2 * Math.PI / 60) };
        var reading = DashboardTachometer.ResolveReading(frame, true);
        Assert.NotNull(reading.RedlineProgress);
        Assert.Equal(expected, reading.RedlineProgress.Value, 6);
    }

    [Fact]
    public void MissingOrOutOfScaleRedlineDoesNotInventAWarningZone()
    {
        Assert.Null(DashboardTachometer.ResolveReading(Frame(4000, 8000), true).RedlineProgress);
        var invalid = Frame(4000, 8000) with { ExactRedline = ExactRedlineResult.Exact(9000 * 2 * Math.PI / 60) };
        Assert.Null(DashboardTachometer.ResolveReading(invalid, true).RedlineProgress);
    }

    private static NativeGaugeFrame Frame(double rpm, double maximum) =>
        NativeGaugeFrame.Empty(SpeedUnit.MilesPerHour) with { EngineRpm = rpm, TachometerMaximumRpm = maximum };
}
