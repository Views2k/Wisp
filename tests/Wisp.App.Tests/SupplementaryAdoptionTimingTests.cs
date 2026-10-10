using System.Diagnostics;
using Wisp.App.DebugLogging;
using Wisp.App.Supplementary;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryAdoptionTimingTests
{
    [Fact]
    public void SamePacketBoundariesProduceSeparateMeasuredStageDurations()
    {
        var recorder = new ToolsPerformanceRecorder { Enabled = true };
        var tick = Stopwatch.Frequency / 1000;
        SupplementaryAdoptionTiming.Record(recorder, 20 * tick, 25 * tick, true, new(1 * tick, 2 * tick, 4 * tick, 5 * tick));
        var values = recorder.Drain().ToDictionary(s => s.Metric, s => s.SumMs);
        Assert.Equal(5, values[ToolsPerformanceMetric.UiUpdateWork], 2);
        Assert.Equal(2, values[ToolsPerformanceMetric.TelemetryParseWork], 2);
        Assert.Equal(21, values[ToolsPerformanceMetric.ParseToUiAdoption], 2);
        Assert.Equal(20, values[ToolsPerformanceMetric.PublishToUiAdoption], 2);
        Assert.Equal(24, values[ToolsPerformanceMetric.ReceiveToUiAdoption], 2);
    }

    [Fact]
    public void MissingOverwrittenOrFutureCorrelationIsDroppedNotFabricatedAsZero()
    {
        var recorder = new ToolsPerformanceRecorder { Enabled = true };
        SupplementaryAdoptionTiming.Record(recorder, 20, 25, true, null);
        SupplementaryAdoptionTiming.Record(recorder, 20, 25, true, new(1, 2, 4, 21));
        var values = recorder.Drain();
        Assert.Equal(5, values.Count);
        Assert.All(values.Where(s => s.Metric != ToolsPerformanceMetric.UiUpdateWork), s =>
        { Assert.Equal(0, s.Count); Assert.Equal(2, s.Dropped); });
    }

    [Fact]
    public void FailureOnlyMeasuresCallWorkAndNativeAgeRequiresSuccessfulAdoption()
    {
        var recorder = new ToolsPerformanceRecorder { Enabled = true };
        SupplementaryAdoptionTiming.Record(recorder, 20, 25, false, new(1, 2, 4, 5));
        Assert.Equal(ToolsPerformanceMetric.UiUpdateWork, Assert.Single(recorder.Drain()).Metric);
        SupplementaryAdoptionTiming.RecordNative(recorder, 20, 25, false, 10);
        Assert.Equal(ToolsPerformanceMetric.NativeUiUpdateWork, Assert.Single(recorder.Drain()).Metric);
        SupplementaryAdoptionTiming.RecordNative(recorder, 20, 25, true, 10);
        Assert.Equal(2, recorder.Drain().Count);
        SupplementaryAdoptionTiming.RecordNative(recorder, 20, 25, true, 30);
        Assert.Equal(1, recorder.Drain().Single(s => s.Metric == ToolsPerformanceMetric.NativeObservationToUiAdoption).Dropped);
    }

    [Fact]
    public void DisabledAndInvalidCallClocksDoNotGenerateMeasurements()
    {
        var recorder = new ToolsPerformanceRecorder();
        SupplementaryAdoptionTiming.Record(recorder, 20, 25, true, new(1, 2, 4, 5));
        Assert.Empty(recorder.Drain());
        recorder.Enabled = true;
        SupplementaryAdoptionTiming.Record(recorder, 0, 25, true, null);
        SupplementaryAdoptionTiming.Record(recorder, 20, 10, true, null);
        SupplementaryAdoptionTiming.RecordNative(recorder, 20, 10, true, 1);
        Assert.Empty(recorder.Drain());
        Assert.False(SupplementaryPerformanceProjection.HasRenderMode(ToolsPerformanceMetric.UiUpdateWork));
        Assert.Equal("telemetry", SupplementaryPerformanceProjection.Feature(ToolsPerformanceMetric.TelemetryParseWork));
    }
}
