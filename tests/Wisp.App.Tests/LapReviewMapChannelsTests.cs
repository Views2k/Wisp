using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LapReviewMapChannelsTests
{
    [Fact]
    public void AddedChannelsReadActualPowerTorqueAndElevation()
    {
        var data = Data();
        var point = data.Lap!.Points[0];
        Assert.Equal(100, LapReviewPlot.Value(point, data with { Channel = LapReviewChannel.Power }, 0)!.Value, 4);
        Assert.Equal(250, LapReviewPlot.Value(point, data with { Channel = LapReviewChannel.Torque }, 0));
        Assert.Equal(184.390537325, LapReviewPlot.Value(point, data with { Channel = LapReviewChannel.Torque, TorqueUnit = TorqueUnit.PoundFeet }, 0)!.Value, 6);
        Assert.Equal(123.5, LapReviewPlot.Value(point, data with { Channel = LapReviewChannel.Elevation }, 0));
    }

    [Fact]
    public void MissingAndConstantChannelsHaveHonestLegendRanges()
    {
        var data = Data() with { Channel = LapReviewChannel.Delta };
        Assert.False(LapReviewColorRange.From(data).HasValues);
        var constant = LapReviewColorRange.From(data with { Channel = LapReviewChannel.Power });
        Assert.True(constant.HasValues);
        Assert.Equal(constant.Minimum, constant.Maximum);
        Assert.Equal(.5, constant.Fraction(constant.Minimum));
    }

    [Fact]
    public void InvalidEngineOutputNeverBecomesAColoredValue()
    {
        var data = Data();
        var point = data.Lap!.Points[0];
        point = point with { Sample = point.Sample with { State = point.Sample.State with { PowerWatts = float.NaN, TorqueNm = float.PositiveInfinity } } };
        Assert.Null(LapReviewPlot.Value(point, data with { Channel = LapReviewChannel.Power }, 0));
        Assert.Null(LapReviewPlot.Value(point, data with { Channel = LapReviewChannel.Torque }, 0));
    }

    private static LapReviewPlotData Data()
    {
        var state = RunTestData.State() with { PowerWatts = 74569.987f, TorqueNm = 250 };
        var point = new LapReviewPoint(0, 0, 0, 0, new(0, 123.5f, 0), false, new() { State = state });
        return new(new() { Points = [point, point with { SampleIndex = 1, RunSeconds = .1 }] }, null, null,
            LapReviewChannel.Power, SpeedUnit.MilesPerHour, 0, 0, 1);
    }
}
