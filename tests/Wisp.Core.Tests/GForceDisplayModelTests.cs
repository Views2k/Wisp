using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class GForceDisplayModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MapsForwardAccelerationDownWithoutChangingItsValue()
    {
        var model = new GForceDisplayModel();

        var display = model.Calculate(0, GForceDisplayModel.StandardGravity, Start);

        Assert.Equal(1, display.LongitudinalG, 6);
        Assert.True(display.NormalizedY > 0);
        Assert.Equal(0.8, display.NormalizedY, 6);
    }

    [Fact]
    public void PreservesBrakingAsNegativeAndMapsItUp()
    {
        var model = new GForceDisplayModel();

        var display = model.Calculate(0, -GForceDisplayModel.StandardGravity, Start);

        Assert.Equal(-1, display.LongitudinalG, 6);
        Assert.True(display.NormalizedY < 0);
    }

    [Fact]
    public void LowDriftInputsRemainLegibleAtTheOneGFloor()
    {
        var model = new GForceDisplayModel();

        var display = model.Calculate(0.5 * GForceDisplayModel.StandardGravity, 0, Start);

        Assert.Equal(GForceDisplayModel.MinimumFullScaleG, display.FullScaleG);
        Assert.Equal(0.5, display.NormalizedX, 6);
    }

    [Fact]
    public void CenterNoiseDoesNotMoveTheIndicatorOrHideTheMeasurement()
    {
        var model = new GForceDisplayModel();

        var display = model.Calculate(
            0.025 * GForceDisplayModel.StandardGravity,
            -0.020 * GForceDisplayModel.StandardGravity,
            Start);

        Assert.Equal(0.025, display.LateralG, 6);
        Assert.Equal(-0.020, display.LongitudinalG, 6);
        Assert.Equal(0, display.NormalizedX);
        Assert.Equal(0, display.NormalizedY);
    }

    [Fact]
    public void RealAccelerationReceivesFullResponseOnTheNextSample()
    {
        var model = new GForceDisplayModel();
        model.Calculate(
            0.025 * GForceDisplayModel.StandardGravity,
            0,
            Start);

        var display = model.Calculate(
            0.25 * GForceDisplayModel.StandardGravity,
            0,
            Start + TimeSpan.FromMilliseconds(10));

        Assert.Equal(0.25, display.NormalizedX, 6);
        Assert.Equal(0, display.NormalizedY);
    }

    [Fact]
    public void CenterResponseTransitionsWithoutAnOutputStep()
    {
        var model = new GForceDisplayModel();
        var midpointG = (GForceDisplayModel.CenterDeadbandG +
                         GForceDisplayModel.CenterFullResponseG) / 2;

        var display = model.Calculate(
            midpointG * GForceDisplayModel.StandardGravity,
            0,
            Start);

        Assert.Equal(midpointG * 0.5, display.NormalizedX, 6);
    }

    [Fact]
    public void PeakScaleIncludesHeadroomAndUsesQuarterGSteps()
    {
        var model = new GForceDisplayModel();

        var display = model.Calculate(2 * GForceDisplayModel.StandardGravity, 0, Start);

        Assert.Equal(2.5, display.FullScaleG, 6);
        Assert.Equal(0.8, display.NormalizedX, 6);
    }

    [Fact]
    public void PeakRemainsInScaleForThirtySecondWindow()
    {
        var model = new GForceDisplayModel();
        model.Calculate(2 * GForceDisplayModel.StandardGravity, 0, Start);

        var atWindowBoundary = model.Calculate(0, 0, Start + GForceDisplayModel.PeakWindow);

        Assert.Equal(2.5, atWindowBoundary.FullScaleG, 6);
    }

    [Fact]
    public void ExpiredPeakAllowsScaleToReduceAtBoundedRate()
    {
        var model = new GForceDisplayModel();
        model.Calculate(4 * GForceDisplayModel.StandardGravity, 0, Start);
        var beforeExpiry = model.Calculate(0, 0, Start + TimeSpan.FromSeconds(29));

        var afterExpiry = model.Calculate(0, 0, Start + TimeSpan.FromSeconds(31));

        Assert.Equal(4.75, beforeExpiry.FullScaleG, 6);
        Assert.Equal(3.75, afterExpiry.FullScaleG, 6);
    }

    [Fact]
    public void MoreRecentPeakBecomesScaleTargetWhenOlderPeakExpires()
    {
        var model = new GForceDisplayModel();
        model.Calculate(4 * GForceDisplayModel.StandardGravity, 0, Start);
        model.Calculate(GForceDisplayModel.StandardGravity, 0, Start + TimeSpan.FromSeconds(10));

        var display = model.Calculate(0, 0, Start + TimeSpan.FromSeconds(31));

        Assert.Equal(1.25, display.FullScaleG, 6);
    }

    [Fact]
    public void MeasurementRemainsUnclampedWhenDisplayScaleSaturates()
    {
        var model = new GForceDisplayModel();

        var display = model.Calculate(
            75 * GForceDisplayModel.StandardGravity,
            -100 * GForceDisplayModel.StandardGravity,
            Start);

        Assert.Equal(75, display.LateralG, 6);
        Assert.Equal(-100, display.LongitudinalG, 6);
        Assert.Equal(GForceDisplayModel.MaximumFullScaleG, display.FullScaleG);
        Assert.True(display.IsOverRange);
        Assert.Equal(
            1,
            Math.Sqrt((display.NormalizedX * display.NormalizedX) +
                      (display.NormalizedY * display.NormalizedY)),
            6);
    }

    [Fact]
    public void InRangeMeasurementDoesNotReportOverRange()
    {
        var model = new GForceDisplayModel();

        var display = model.Calculate(GForceDisplayModel.StandardGravity, 0, Start);

        Assert.False(display.IsOverRange);
    }

    [Fact]
    public void OverRangeStatusPersistsForThePeakWindow()
    {
        var model = new GForceDisplayModel();
        model.Calculate(6 * GForceDisplayModel.StandardGravity, 0, Start);

        var retained = model.Calculate(0, 0, Start + TimeSpan.FromSeconds(29));
        var expired = model.Calculate(0, 0, Start + TimeSpan.FromSeconds(31));

        Assert.True(retained.IsOverRange);
        Assert.False(expired.IsOverRange);
    }

    [Fact]
    public void NonFiniteInputsCannotPoisonScaleOrPosition()
    {
        var model = new GForceDisplayModel();

        var display = model.Calculate(double.NaN, double.PositiveInfinity, Start);

        Assert.Equal(0, display.LateralG);
        Assert.Equal(0, display.LongitudinalG);
        Assert.Equal(GForceDisplayModel.MinimumFullScaleG, display.FullScaleG);
        Assert.Equal(0, display.NormalizedX);
        Assert.Equal(0, display.NormalizedY);
        Assert.False(display.IsOverRange);
    }

    [Fact]
    public void ResetClearsWindowAndRestoresFloorScale()
    {
        var model = new GForceDisplayModel();
        model.Calculate(4 * GForceDisplayModel.StandardGravity, 0, Start);

        model.Reset();
        var display = model.Calculate(0, 0, Start + TimeSpan.FromSeconds(1));

        Assert.Equal(GForceDisplayModel.MinimumFullScaleG, display.FullScaleG);
    }

    [Fact]
    public void BackwardClockMovementStartsANewPeakWindow()
    {
        var model = new GForceDisplayModel();
        model.Calculate(4 * GForceDisplayModel.StandardGravity, 0, Start);

        var display = model.Calculate(0, 0, Start - TimeSpan.FromSeconds(1));

        Assert.Equal(GForceDisplayModel.MinimumFullScaleG, display.FullScaleG);
    }
}
