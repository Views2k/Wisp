using System.Diagnostics;
using Wisp.App.DebugLogging;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ToolsPerformanceRecorderTests
{
    [Fact]
    public void DisabledRecorderDoesNotCollectAndDrainDoesNotRepeatSamples()
    {
        var recorder = new ToolsPerformanceRecorder();
        recorder.RecordTicks(ToolsPerformanceMetric.PresentCall, false, Stopwatch.Frequency);
        Assert.Empty(recorder.Drain());
        recorder.Enabled = true;
        recorder.RecordTicks(ToolsPerformanceMetric.PresentCall, false, Stopwatch.Frequency / 100);
        recorder.RecordTicks(ToolsPerformanceMetric.PresentCall, true, Stopwatch.Frequency / 10);
        var samples = recorder.Drain();
        Assert.Equal(2, samples.Count);
        Assert.All(samples, sample => Assert.Equal(sample.Count, sample.Buckets.Sum()));
        Assert.Equal(10, samples.Single(sample => !sample.CpuRendering).SumMs, 3);
        Assert.Equal(100, samples.Single(sample => sample.CpuRendering).MaximumMs, 3);
        Assert.Empty(recorder.Drain());
    }

    [Fact]
    public void HistogramPreservesCountsAndRejectsUnboundedClockIntervals()
    {
        var recorder = new ToolsPerformanceRecorder { Enabled = true };
        foreach (var milliseconds in new[] { 1, 2, 10, 20, 40, 75, 200, 900, 3000, 50000 })
            recorder.RecordTicks(ToolsPerformanceMetric.SubmissionInterval, false,
                Stopwatch.Frequency * milliseconds / 1000);
        recorder.RecordTicks(ToolsPerformanceMetric.SubmissionInterval, false, Stopwatch.Frequency * 61);
        recorder.RecordTicks(ToolsPerformanceMetric.SubmissionInterval, false, -1);
        recorder.RecordTicks((ToolsPerformanceMetric)999, false, 1);
        var sample = Assert.Single(recorder.Drain());
        Assert.Equal(10, sample.Count);
        Assert.Equal(10, sample.Buckets.Sum());
        Assert.Equal(1, sample.Dropped);
        Assert.Equal(50000, sample.MaximumMs, 3);
    }

    [Fact]
    public async Task ConcurrentWritersAndDrainsAccountForEveryObservation()
    {
        var recorder = new ToolsPerformanceRecorder { Enabled = true };
        long collected = 0;
        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 10000; index++)
                recorder.RecordTicks(ToolsPerformanceMetric.RenderWork, false, Stopwatch.Frequency / 1000);
        }, TestContext.Current.CancellationToken)).ToArray();
        while (writers.Any(writer => !writer.IsCompleted))
        {
            foreach (var sample in recorder.Drain())
            {
                Assert.Equal(sample.Count, sample.Buckets.Sum());
                collected += sample.Count + sample.Dropped;
            }
            await Task.Yield();
        }
        await Task.WhenAll(writers);
        collected += recorder.Drain().Sum(sample => sample.Count + sample.Dropped);
        Assert.Equal(40000, collected);
    }

    [Fact]
    public void RecordingAllocatesNoManagedMemoryAfterWarmup()
    {
        var recorder = new ToolsPerformanceRecorder { Enabled = true };
        for (var index = 0; index < 100; index++)
            recorder.RecordTicks(ToolsPerformanceMetric.RenderWork, false, 1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100000; index++)
            recorder.RecordTicks(ToolsPerformanceMetric.RenderWork, false, 1);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
