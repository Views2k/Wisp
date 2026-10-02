using Wisp.Core.Runs;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class LapReviewComparisonDensityTests
{
    [Theory]
    [InlineData(120)]
    [InlineData(125)]
    public void HighRateIdenticalLapsMatchWithoutDiscardingRecordedPoints(int rate)
    {
        var lap = Circuit(90, rate);
        var points = lap.Points.ToArray();
        var comparison = LapReviewAnalysis.Compare(lap, lap, TestContext.Current.CancellationToken);

        Assert.True(comparison.CanCompare, comparison.Message);
        Assert.InRange(comparison.Coverage, .999, 1);
        Assert.Equal(90 * rate, comparison.Points.Length);
        Assert.All(comparison.Points, point =>
        {
            Assert.NotNull(point.DeltaSeconds);
            Assert.InRange(Math.Abs(point.DeltaSeconds!.Value), 0, .0001);
            Assert.InRange(point.ReferencePointIndex!.Value, 0, lap.Points.Length - 2);
        });
        for (var index = 0; index < points.Length; index++) Assert.Same(points[index], lap.Points[index]);
    }

    [Fact]
    public void DifferentPacketRatesKeepTheDeltaAtTheSameWorldPosition()
    {
        var reference = Circuit(90, 60);
        var current = Circuit(94, 125);
        var comparison = LapReviewAnalysis.Compare(current, reference, TestContext.Current.CancellationToken);

        Assert.True(comparison.CanCompare, comparison.Message);
        Assert.InRange(comparison.Coverage, .999, 1);
        var halfway = comparison.Points[current.Points.Length / 2];
        Assert.InRange(halfway.ReferenceLapSeconds!.Value, 44.999, 45.001);
        Assert.InRange(halfway.DeltaSeconds!.Value, 1.999, 2.001);
    }

    private static LapReviewLap Circuit(int duration, int rate)
    {
        var run = new RecordedRun
        {
            Samples = Enumerable.Range(0, duration * rate + 1).Select(index =>
            {
                var seconds = index / (double)rate;
                var phase = seconds / duration * Math.Tau;
                var number = (ushort)(index == duration * rate ? 1 : 0);
                return new RunSample
                {
                    ElapsedSeconds = seconds,
                    IsDriving = true,
                    State = TestVehicleState.Create(groundSpeed: (float)(100 * Math.Tau / duration)) with
                    {
                        GameTimestampMilliseconds = 100_000 + (uint)Math.Round(seconds * 1000),
                        ReceivedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(seconds),
                        Lap = new(new((float)(100 * Math.Cos(phase)), 0, (float)(100 * Math.Sin(phase))),
                            (float)(seconds - number * duration), duration, (float)seconds, number, 1)
                    }
                };
            }).ToArray()
        };
        return Assert.Single(LapReviewAnalysis.Build(run, TestContext.Current.CancellationToken).Laps, lap => lap.IsComplete);
    }
}
