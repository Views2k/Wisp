using Wisp.App.ShiftCapture;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCaptureCanaryPhasesTests
{
    [Fact]
    public void RecordedTest8ReadCadenceCompletesAllPhasesWithoutReusingRecordedColors()
    {
        var phases = new ShiftCaptureCanaryPhases(Test8ReadTiming.Frequency);
        phases.Start(Test8ReadTiming.StartedQpc, 1, 8);
        var tick = Test8ReadTiming.StartedQpc;
        var accepted = new List<(ShiftCaptureCanaryPhaseToken Phase, long Start, long End)>();
        foreach (var read in Test8ReadTiming.Reads.Where(read => read.Started >= Test8ReadTiming.StartedQpc))
        {
            while (tick < read.Started && phases.Status == ShiftCaptureCanaryPhaseStatus.Running)
            {
                phases.Advance(tick);
                tick += 160_000; // The existing observation worker's 16 ms cadence.
            }
            if (phases.Status != ShiftCaptureCanaryPhaseStatus.Running) break;
            phases.Advance(read.Started);
            if (phases.Current is { } token && phases.TryObserve(token, read.Started, read.Finished, read.Finished))
                accepted.Add((token, read.Started, read.Finished));
            tick = read.Finished + 160_000;
        }
        Assert.Equal(ShiftCaptureCanaryPhaseStatus.Completed, phases.Status);
        Assert.Equal(5, phases.Windows.Length);
        Assert.All(phases.Windows, window =>
        {
            Assert.True(window.AcceptedReads >= 5);
            Assert.InRange((window.EndedQpc - window.StartedQpc) / (double)Test8ReadTiming.Frequency, 2, 8);
            Assert.True(window.EndedQpc - window.SamplesClosedQpc >= 1_500_000);
            Assert.All(accepted.Where(sample => sample.Phase.Phase == window.Phase), sample =>
            {
                Assert.True(sample.Start >= window.StartedQpc + 2_500_000);
                Assert.True(sample.End <= window.SamplesClosedQpc);
            });
        });
        Assert.Contains(phases.Windows, window => window.EndedQpc - window.StartedQpc > 20_000_000);
        Assert.False(phases.FlashOn);
        // This is only a scheduling regression. No old pixel colors or successful
        // presentation claim is assigned to these different phase windows.
    }

    [Fact]
    public void KnownPixelPatternQualifiesAtCostLimitedCadenceWithoutIncreasingReadRate()
    {
        var phases = new ShiftCaptureCanaryPhases(1_000);
        var source = new SlowPixelSource();
        using var probe = new ShiftCapturePresentationProbe(source, () => source.Now, 1_000);
        phases.Start(source.Now, 1, 1);
        var samples = new List<(int Phase, int Matches)>();
        var starts = new List<long>();
        while (source.Now < 45_000 && phases.Status == ShiftCaptureCanaryPhaseStatus.Running)
        {
            phases.Advance(source.Now);
            source.Red = phases.FlashOn;
            var token = phases.Current;
            if (token is null) break;
            var sample = probe.TrySample(new(10, 10, 4, 4), 0xFFFF2030, 1);
            if (sample is not null)
            {
                Assert.Equal(ShiftCapturePresentationStatus.Observed, sample.Status);
                Assert.False(sample.Qualified);
                Assert.True(sample.RecommendedIntervalMilliseconds >= 480);
                starts.Add(sample.ReadStartedQpc);
                if (phases.TryObserve(token.Value, sample.ReadStartedQpc, sample.ReadFinishedQpc, source.Now))
                    samples.Add((token.Value.Phase, sample.Pixels.CompatiblePixelCount));
            }
            source.Now += 16;
        }
        Assert.Equal(ShiftCaptureCanaryPhaseStatus.Completed, phases.Status);
        Assert.All(starts.Zip(starts.Skip(1)), pair => Assert.True(pair.Second - pair.First >= 480));
        Assert.True(ShiftCaptureSession.CanaryPasses(samples));
        Assert.True(probe.SetQualified(true));
        Assert.True(source.Now > 11_000);
    }

    [Fact]
    public void WholeReadMustBePastSettlingAndCannotBeCountedTwice()
    {
        var phases = Started();
        var token = phases.Current!.Value;
        Assert.False(phases.TryObserve(token, 1_240, 1_260, 1_260));
        Assert.True(phases.TryObserve(token, 1_250, 1_260, 1_260));
        Assert.False(phases.TryObserve(token, 1_250, 1_260, 1_270));
        Assert.False(phases.TryObserve(token, 1_270, 1_300, 1_280));
        Assert.Equal(1, phases.AcceptedReads);
    }

    [Fact]
    public void FiveFastReadsCannotShortenMinimumPhaseOrMoveFrozenTail()
    {
        var phases = Started();
        FeedFive(phases);
        phases.Advance(2_849);
        Assert.Equal(0, phases.Current!.Value.Phase);
        phases.Advance(2_850);
        var old = phases.Current!.Value;
        Assert.False(phases.TryObserve(old, 2_860, 2_870, 2_870));
        phases.Advance(2_999);
        Assert.Equal(0, phases.Current!.Value.Phase);
        phases.Advance(3_000);
        Assert.Equal(1, phases.Current!.Value.Phase);
        var completed = Assert.Single(phases.Windows);
        Assert.Equal(5, completed.AcceptedReads);
        Assert.Equal(2_850, completed.SamplesClosedQpc);
        Assert.Equal(3_000, completed.EndedQpc);
        Assert.True(phases.FlashOn);
    }

    [Fact]
    public void FourReadsCannotAdvanceEvenAfterTwoSeconds()
    {
        var phases = Started();
        var token = phases.Current!.Value;
        for (var i = 0; i < 4; i++) Assert.True(phases.TryObserve(token, 1_300 + i * 100, 1_310 + i * 100, 1_310 + i * 100));
        phases.Advance(3_000);
        Assert.Equal(0, phases.Current!.Value.Phase);
        Assert.True(phases.TryObserve(token, 3_100, 3_110, 3_110));
        phases.Advance(3_259);
        Assert.Equal(0, phases.Current!.Value.Phase);
        phases.Advance(3_260);
        Assert.Equal(1, phases.Current!.Value.Phase);
    }

    [Fact]
    public void ReadCrossingPhaseBoundaryCannotEnterEitherPhase()
    {
        var phases = Started();
        var previous = phases.Current!.Value;
        FeedFive(phases);
        phases.Advance(2_850);
        phases.Advance(3_000);
        Assert.False(phases.TryObserve(previous, 2_990, 3_010, 3_010));
        Assert.False(phases.TryObserve(phases.Current!.Value, 2_990, 3_010, 3_010));
        Assert.Equal(0, phases.AcceptedReads);
    }

    [Fact]
    public void MissingReadsTimeOutAndCannotAutomaticallyRearm()
    {
        var phases = Started();
        var old = phases.Current!.Value;
        phases.Advance(9_000);
        Assert.Equal(ShiftCaptureCanaryPhaseStatus.TimedOut, phases.Status);
        Assert.Null(phases.Current);
        Assert.False(phases.FlashOn);
        Assert.Equal(0, Assert.Single(phases.Windows).AcceptedReads);
        Assert.False(phases.TryObserve(old, 9_100, 9_110, 9_110));
        phases.Advance(10_000);
        Assert.Equal(ShiftCaptureCanaryPhaseStatus.TimedOut, phases.Status);
    }

    [Fact]
    public void DelayedWorkerCannotLeaveAnOnPhaseRunningPastItsBound()
    {
        var phases = Started();
        FeedFive(phases);
        phases.Advance(2_850);
        phases.Advance(3_000);
        Assert.True(phases.FlashOn);
        phases.Advance(11_000);
        Assert.Equal(ShiftCaptureCanaryPhaseStatus.TimedOut, phases.Status);
        Assert.False(phases.FlashOn);
        Assert.Equal("TimedOut", phases.Windows[^1].EndReason);
    }

    [Fact]
    public void RetryOrTargetChangeCannotInheritPreviousPhaseTokenOrSamples()
    {
        var phases = Started();
        var stale = phases.Current!.Value;
        phases.TryObserve(stale, 1_300, 1_310, 1_310);
        phases.Cancel(1_400);
        Assert.Equal(ShiftCaptureCanaryPhaseStatus.Cancelled, phases.Status);
        Assert.False(phases.FlashOn);
        phases.Start(2_000, 2, 3);
        Assert.Empty(phases.Windows);
        Assert.False(phases.TryObserve(stale, 2_300, 2_310, 2_310));
        Assert.False(phases.TryObserve(phases.Current!.Value with { TargetGeneration = 2 }, 2_300, 2_310, 2_310));
        Assert.True(phases.TryObserve(phases.Current!.Value, 2_300, 2_310, 2_310));
        Assert.Equal(1, phases.AcceptedReads);
        phases.Reset();
        Assert.Equal(ShiftCaptureCanaryPhaseStatus.Inactive, phases.Status);
        Assert.Null(phases.Current);
    }

    [Fact]
    public void BackwardClockStopsPhaseWithoutInventingDuration()
    {
        var phases = Started();
        phases.Advance(999);
        Assert.Equal(ShiftCaptureCanaryPhaseStatus.TimedOut, phases.Status);
        Assert.False(phases.FlashOn);
        Assert.Equal(1_000, Assert.Single(phases.Windows).EndedQpc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidFrequencyIsRejected(long frequency) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShiftCaptureCanaryPhases(frequency));

    private static ShiftCaptureCanaryPhases Started()
    {
        var phases = new ShiftCaptureCanaryPhases(1_000);
        phases.Start(1_000, 1, 1);
        return phases;
    }

    private static void FeedFive(ShiftCaptureCanaryPhases phases)
    {
        var token = phases.Current!.Value;
        for (var i = 0; i < 5; i++)
            Assert.True(phases.TryObserve(token, token.StartedQpc + 300 + i * 100, token.StartedQpc + 310 + i * 100,
                token.StartedQpc + 310 + i * 100));
    }

    private sealed class SlowPixelSource : IShiftCapturePixelSource
    {
        internal long Now = 1_000;
        internal bool Red;
        public ShiftCapturePresentationStatus OverlayStatus() => ShiftCapturePresentationStatus.Observed;
        public bool TryCopy(ShiftCaptureRect region, byte[] bgra, out int errorCode)
        {
            for (var index = 0; index < bgra.Length; index += 4)
            {
                bgra[index] = Red ? (byte)48 : (byte)0;
                bgra[index + 1] = Red ? (byte)32 : (byte)0;
                bgra[index + 2] = Red ? (byte)255 : (byte)0;
                bgra[index + 3] = 0;
            }
            Now += 24;
            errorCode = 0;
            return true;
        }
        public void Dispose() { }
    }
}
