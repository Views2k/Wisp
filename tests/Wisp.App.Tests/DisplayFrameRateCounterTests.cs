using Xunit;

namespace Wisp.App.Tests;

public sealed class DisplayFrameRateCounterTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(240)]
    public void MeasuresTheCompositorRateWithoutASixtyHertzCap(int refreshRate)
    {
        var counter = new DisplayFrameRateCounter();

        for (var frame = 0; frame <= refreshRate; frame++)
        {
            counter.Observe(FrameTime(frame, refreshRate));
        }

        Assert.InRange(counter.Rate, refreshRate - 0.1, refreshRate + 0.1);
    }

    [Fact]
    public void DuplicateRenderingCallbacksDoNotInflateTheRate()
    {
        var counter = new DisplayFrameRateCounter();

        for (var frame = 0; frame <= 144; frame++)
        {
            var timestamp = FrameTime(frame, 144);
            counter.Observe(timestamp);
            counter.Observe(timestamp);
        }

        Assert.InRange(counter.Rate, 143.9, 144.1);
    }

    [Fact]
    public void ResetRequiresANewCompleteMeasurementWindow()
    {
        var counter = new DisplayFrameRateCounter();
        for (var frame = 0; frame <= 120; frame++)
        {
            counter.Observe(FrameTime(frame, 120));
        }

        counter.Reset();
        counter.Observe(TimeSpan.Zero);

        Assert.Equal(0, counter.Rate);
    }

    private static TimeSpan FrameTime(int frame, int refreshRate) =>
        TimeSpan.FromTicks((long)Math.Round(
            frame * TimeSpan.TicksPerSecond / (double)refreshRate));
}
