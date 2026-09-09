using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunCursorTests
{
    [Fact]
    public void CursorShowsTheRecordedGearWithoutInventingValuesAcrossAShift()
    {
        var first = RunPresentationTests.Sample(0, 6200);
        var next = RunPresentationTests.Sample(.1, 4500);
        var run = new RecordedRun
        {
            Samples = [first with { State = first.State with { Gear = TransmissionGear.Second } }, next]
        };
        Assert.Contains("gear 2", RunCursor.Describe("A", run, .04));
        Assert.Contains("gear 3", RunCursor.Describe("A", run, .06));
    }

    [Fact]
    public void GapsAndMatchedRangeBoundariesKeepContextUnavailable()
    {
        var run = new RecordedRun { Samples = [RunPresentationTests.Sample(0, 3000), RunPresentationTests.Sample(1, 6000)] };
        Assert.Equal("A · Reading unavailable", RunCursor.Describe("A", run, .5));
        Assert.Equal("B · Reading unavailable", RunCursor.Describe("B", run, 1, new(0, .8)));
    }

    [Theory]
    [InlineData(TransmissionGear.Neutral, "neutral")]
    [InlineData(TransmissionGear.Reverse, "reverse")]
    [InlineData(TransmissionGear.Unknown, "gear unavailable")]
    public void NonDrivingGearsHavePlainLabels(TransmissionGear gear, string expected)
    {
        var sample = RunPresentationTests.Sample(0, 1200);
        var run = new RecordedRun { Samples = [sample with { State = sample.State with { Gear = gear } }] };
        Assert.Contains(expected, RunCursor.Describe("A", run, 0));
    }
}
