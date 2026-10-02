using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LapReviewPlotValueTests
{
    [Fact]
    public void ReferenceSpeedInterpolatesAtMatchedPositionAndConvertsUnits()
    {
        var data = Data(LapReviewChannel.Speed);
        Assert.Equal(54, LapReviewPlot.ReferenceValue(data, 0)!.Value, 8);
        Assert.Equal(15 * RunPresentation.SpeedFactor(SpeedUnit.MilesPerHour),
            LapReviewPlot.ReferenceValue(data with { SpeedUnit = SpeedUnit.MilesPerHour }, 0)!.Value, 8);
    }

    [Fact]
    public void ReferenceGearHoldsTheEarlierRecordedGear()
    {
        Assert.Equal(3d, LapReviewPlot.ReferenceValue(Data(LapReviewChannel.Gear), 0)!.Value);
    }

    [Theory]
    [InlineData(.999, 3)]
    [InlineData(1, 4)]
    [InlineData(1.001, 4)]
    public void ReferenceGearChangesAtTheExactUpperSample(double seconds, double expected)
    {
        var data = Data(LapReviewChannel.Gear);
        data = data with { Comparison = data.Comparison! with { Points = [new(0, 0, seconds, 0, 0)] } };
        Assert.Equal(expected, LapReviewPlot.ReferenceValue(data, 0));
    }

    [Theory]
    [InlineData(LapReviewChannel.SlipRatio, 0, .11)]
    [InlineData(LapReviewChannel.SlipRatio, 1, .22)]
    [InlineData(LapReviewChannel.SlipRatio, 2, .33)]
    [InlineData(LapReviewChannel.SlipRatio, 3, .44)]
    [InlineData(LapReviewChannel.SlipAngle, 0, 1.1)]
    [InlineData(LapReviewChannel.SlipAngle, 1, 2.2)]
    [InlineData(LapReviewChannel.SlipAngle, 2, 3.3)]
    [InlineData(LapReviewChannel.SlipAngle, 3, 4.4)]
    [InlineData(LapReviewChannel.Suspension, 0, .1)]
    [InlineData(LapReviewChannel.Suspension, 1, .2)]
    [InlineData(LapReviewChannel.Suspension, 2, .3)]
    [InlineData(LapReviewChannel.Suspension, 3, .4)]
    public void WheelChannelsUseTheSelectedCornerForCurrentAndReference(LapReviewChannel channel, int wheel, double expected)
    {
        var data = WheelData(channel, wheel);
        Assert.Equal(expected, LapReviewPlot.Value(data.Lap!.Points[0], data, 0)!.Value, 6);
        Assert.Equal(expected, LapReviewPlot.ReferenceValue(data, 0)!.Value, 6);
    }

    [Theory]
    [InlineData(0, 32, 0)]
    [InlineData(1, 50, 10)]
    [InlineData(2, 68, 20)]
    [InlineData(3, 86, 30)]
    public void SelectedWheelTemperatureConvertsForCurrentAndReference(int wheel, double fahrenheit, double celsius)
    {
        var data = WheelData(LapReviewChannel.TireTemperature, wheel);
        Assert.Equal(fahrenheit, LapReviewPlot.Value(data.Lap!.Points[0], data, 0));
        Assert.Equal(fahrenheit, LapReviewPlot.ReferenceValue(data, 0));
        data = data with { TemperatureUnit = TireTemperatureUnit.Celsius };
        Assert.Equal(celsius, LapReviewPlot.Value(data.Lap!.Points[0], data, 0));
        Assert.Equal(celsius, LapReviewPlot.ReferenceValue(data, 0));
    }

    [Fact]
    public void ReferenceValuesDoNotInterpolateAcrossGapsOrEqualTimes()
    {
        var data = Data(LapReviewChannel.Brake);
        var points = data.Reference!.Points;
        Assert.Null(LapReviewPlot.ReferenceValue(data with
        { Reference = data.Reference with { Points = [points[0], points[1] with { BreakBefore = true }] } }, 0));
        Assert.Null(LapReviewPlot.ReferenceValue(data with
        { Reference = data.Reference with { Points = [points[0], points[1] with { LapSeconds = points[0].LapSeconds }] } }, 0));
    }

    [Fact]
    public void DeltaMissingMatchesAndRejectedComparisonsHaveNoReferenceChannelValue()
    {
        var data = Data(LapReviewChannel.Delta);
        Assert.Null(LapReviewPlot.ReferenceValue(data, 0));
        data = data with { Channel = LapReviewChannel.Speed };
        Assert.Null(LapReviewPlot.ReferenceValue(data, 5));
        Assert.Null(LapReviewPlot.ReferenceValue(data with { Comparison = data.Comparison! with { CanCompare = false } }, 0));
        Assert.Null(LapReviewPlot.ReferenceValue(data with { Reference = null }, 0));
        Assert.Null(LapReviewPlot.ReferenceValue(data with
        {
            Comparison = data.Comparison! with
            { Points = [new(0, 0, .5, 0, 9)] }
        }, 0));
    }

    [Fact]
    public void LastReferenceSampleAndClampedFractionRemainFinite()
    {
        var data = Data(LapReviewChannel.Speed);
        Assert.Equal(72, LapReviewPlot.ReferenceValue(data with
        {
            Comparison = data.Comparison! with
            { Points = [new(0, 0, 1, 0, 1)] }
        }, 0)!.Value, 8);
        Assert.Equal(36, LapReviewPlot.ReferenceValue(data with
        {
            Comparison = data.Comparison! with
            { Points = [new(0, 0, -1, 0, 0)] }
        }, 0)!.Value, 8);
        Assert.Equal(72, LapReviewPlot.ReferenceValue(data with
        {
            Comparison = data.Comparison! with
            { Points = [new(0, 0, 2, 0, 0)] }
        }, 0)!.Value, 8);
    }

    private static LapReviewPlotData Data(LapReviewChannel channel)
    {
        var a = RunTestData.State() with { GroundSpeedMetersPerSecond = 10, Gear = TransmissionGear.Third, Brake = 0 };
        var b = a with { GroundSpeedMetersPerSecond = 20, Gear = TransmissionGear.Fourth, Brake = 255 };
        var lap = new LapReviewLap
        {
            Points =
        [
            new(0, 0, 0, 0, new(0, 0, 0), true, new() { State = a }),
            new(1, 1, 1, 10, new(10, 0, 0), false, new() { State = b })
        ]
        };
        return new(lap, lap, new([new(0, 0, .5, 0, 0)], 1, true, "matched"), channel,
            SpeedUnit.KilometersPerHour, 0, 0, 1);
    }

    private static LapReviewPlotData WheelData(LapReviewChannel channel, int wheel)
    {
        var data = Data(channel);
        var points = data.Lap!.Points.Select(point => point with
        {
            Sample = point.Sample with
            {
                State = point.Sample.State with
                {
                    TireTemperatureFahrenheit = new(32, 50, 68, 86),
                    TireSlipRatio = new(.11f, .22f, .33f, .44f),
                    TireSlipAngle = new(1.1f, 2.2f, 3.3f, 4.4f),
                    NormalizedSuspensionTravel = new(.1f, .2f, .3f, .4f)
                }
            }
        }).ToArray();
        var lap = data.Lap with { Points = points };
        return data with { Lap = lap, Reference = lap, Wheel = wheel };
    }
}
