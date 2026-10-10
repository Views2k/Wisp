using System.Diagnostics;
using Wisp.App.DebugLogging;
using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

public sealed class CompositorNeedlePerformanceTests
{
    [Fact]
    public void AcceptedOperationMeasuresWorkAndSourceAgeWithoutRawDiagnostics()
    {
        var recorder = new ToolsPerformanceRecorder { Enabled = true };
        var tick = Stopwatch.Frequency / 1000;
        CompositorNeedleMotionWorker.RecordToolsObservation(recorder, 20 * tick, 25 * tick, 10 * tick, true);
        var samples = recorder.Drain();
        Assert.Equal(2, samples.Count);
        Assert.All(samples, sample => Assert.False(sample.CpuRendering));
        Assert.Equal(5, samples.Single(s => s.Metric == ToolsPerformanceMetric.CompositorMotionWork).SumMs, 2);
        Assert.Equal(15, samples.Single(s => s.Metric == ToolsPerformanceMetric.CompositorMotionSourceAge).SumMs, 2);
    }

    [Fact]
    public void FailedOrClearOperationKeepsWorkWithoutInventingSuccessfulSourceAge()
    {
        var recorder = new ToolsPerformanceRecorder { Enabled = true };
        CompositorNeedleMotionWorker.RecordToolsObservation(recorder, 10, 20, 5, false);
        CompositorNeedleMotionWorker.RecordToolsObservation(recorder, 20, 30, 0, false);
        var sample = Assert.Single(recorder.Drain());
        Assert.Equal(ToolsPerformanceMetric.CompositorMotionWork, sample.Metric);
        Assert.Equal(2, sample.Count);
    }

    [Fact]
    public void DisabledOrInvalidClocksCannotCreateAValidObservation()
    {
        var recorder = new ToolsPerformanceRecorder();
        CompositorNeedleMotionWorker.RecordToolsObservation(recorder, 10, 20, 5, true);
        Assert.Empty(recorder.Drain());
        recorder.Enabled = true;
        CompositorNeedleMotionWorker.RecordToolsObservation(recorder, 0, 20, 5, true);
        CompositorNeedleMotionWorker.RecordToolsObservation(recorder, 20, 10, 5, true);
        Assert.Empty(recorder.Drain());
        CompositorNeedleMotionWorker.RecordToolsObservation(recorder, 10, 20, 21, true);
        Assert.Equal(ToolsPerformanceMetric.CompositorMotionWork, Assert.Single(recorder.Drain()).Metric);
    }

    [Fact]
    public void OutOfRangeIntervalsCountAsDroppedAndHotRecordingDoesNotAllocate()
    {
        var recorder = new ToolsPerformanceRecorder { Enabled = true };
        CompositorNeedleMotionWorker.RecordToolsObservation(recorder, 1, Stopwatch.Frequency * 61 + 1, 1, true);
        var dropped = recorder.Drain();
        Assert.Equal(2, dropped.Count);
        Assert.All(dropped, sample =>
        {
            Assert.Equal(0, sample.Count);
            Assert.Equal(1, sample.Dropped);
        });
        for (var i = 0; i < 100; i++)
            CompositorNeedleMotionWorker.RecordToolsObservation(recorder, 10, 20, 5, true);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100000; i++)
            CompositorNeedleMotionWorker.RecordToolsObservation(recorder, 10, 20, 5, true);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
