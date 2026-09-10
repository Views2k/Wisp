using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Wisp.App.DebugLogging;
using Xunit;

namespace Wisp.App.Tests;

[Collection("Tach diagnostics")]
public sealed class TachRendererDiagnosticsTests : IDisposable
{
    public TachRendererDiagnosticsTests() => Reset();
    public void Dispose() => Reset();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public void ExportPreservesCpuModeAndUnknownLegacyMode(bool? cpuRendering)
    {
        TachDiagnostics.SetEnabled(true);
        var sample = Sample() with { CpuRendering = cpuRendering };
        TachDiagnostics.RecordRenderer(in sample);
        var row = Assert.Single(TachDiagnostics.Snapshot()!.RendererRecent);
        var restored = JsonSerializer.Deserialize<TachRendererDiagnostic>(JsonSerializer.Serialize(row));
        Assert.Equal(cpuRendering, restored.CpuRendering);
    }

    [Fact]
    public void DisabledRendererDoesNotAllocateOrCreateCapture()
    {
        var sample = Sample();
        for (var index = 0; index < 1_000; index++) TachDiagnostics.RecordRenderer(in sample);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++) TachDiagnostics.RecordRenderer(in sample);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Null(TachDiagnostics.Snapshot());
    }

    [Fact]
    public void StagesIncludeFailedAttemptsAndUseOnlyAvailableInputAgeOrigins()
    {
        TachDiagnostics.SetEnabled(true);
        var sample = Sample() with
        {
            StartedTimestamp = Ticks(100),
            CompletedTimestamp = Ticks(110),
            SampleTimestamp = Ticks(97),
            ReceivedTimestamp = Ticks(98),
            QueuedTimestamp = Ticks(99),
            DrawCommands = 7,
            MapCount = 2,
            MapTicks = Ticks(4),
            MaxMapTicks = Ticks(3),
            SceneTicks = Ticks(1),
            NativeSetupTicks = Ticks(2),
            NativeDrawTicks = Ticks(8),
            NativePresentTicks = Ticks(5),
            CpuThreadTicks = Ticks(2),
            QueueDropped = 3
        };
        TachDiagnostics.RecordRenderer(in sample);
        var next = sample with
        {
            Sequence = 2,
            CompletedTimestamp = Ticks(120),
            SampleTimestamp = null,
            ReceivedTimestamp = Ticks(101),
            QueuedTimestamp = 0,
            CpuThreadTicks = null
        };
        TachDiagnostics.RecordRenderer(in next);
        var busy = sample with { Sequence = 3, Stage = "present", Result = "busy", HResult = unchecked((int)0x887A000A) };
        TachDiagnostics.RecordRenderer(in busy);
        var other = sample with { ControlId = 2, HostWindowHandle = 456, Sequence = 4 };
        TachDiagnostics.RecordRenderer(in other);

        var interval = Interval();
        Assert.Equal(3, interval.Renderer.Length);
        var draw = Assert.Single(interval.Renderer, value => value.ControlId == 1 && value.Stage == "draw");
        Assert.Equal(2, draw.Count);
        Assert.Equal(15, draw.MeanMilliseconds, 6);
        Assert.Equal(20, draw.MaximumMilliseconds, 6);
        Assert.Equal(14, draw.DrawCommands);
        Assert.Equal(4, draw.MapCount);
        Assert.Equal(8, draw.TotalMapMilliseconds, 6);
        Assert.Equal(3, draw.MaximumMapMilliseconds, 6);
        Assert.Equal(2, draw.TotalSceneMilliseconds, 6);
        Assert.Equal(4, draw.TotalNativeSetupMilliseconds, 6);
        Assert.Equal(16, draw.TotalNativeDrawMilliseconds, 6);
        Assert.Equal(10, draw.TotalNativePresentMilliseconds, 6);
        Assert.Equal(6, draw.QueueDropped);
        Assert.Equal(1, draw.QueueAgeSamples);
        Assert.Equal(1, draw.MeanQueueAgeMilliseconds, 6);
        Assert.Equal(1, draw.ReceiveAgeSamples);
        Assert.Equal(2, draw.MaximumReceiveAgeMilliseconds, 6);
        Assert.Equal(1, draw.SampleAgeSamples);
        Assert.Equal(3, draw.MeanSampleAgeMilliseconds, 6);
        Assert.Equal(1, draw.CpuThreadSamples);
        Assert.Equal(2, draw.TotalCpuThreadMilliseconds, 6);
        Assert.Equal("busy", Assert.Single(interval.Renderer, value => value.Stage == "present").Result);
        Assert.Equal(busy.HResult, Assert.Single(interval.Renderer, value => value.Stage == "present").HResult);
        Assert.Equal(busy.HResult, Snapshot().RendererRecent[2].HResult);
        Assert.Empty(Interval().Renderer);
        Assert.Equal(4, Snapshot().RendererRecent.Length);
    }

    [Fact]
    public void WaitDetailsPreserveIndependentCoverageTotalsAndReplacementGenerations()
    {
        TachDiagnostics.SetEnabled(true);
        var sample = WaitSample() with
        {
            NativeWaitTicks = Ticks(36),
            WaitPrecheckTicks = Ticks(3),
            WaitCallTicks = Ticks(30),
            WaitPostcheckTicks = Ticks(2),
            CpuThreadTicks = Ticks(2)
        };
        TachDiagnostics.RecordRenderer(in sample);
        var next = sample with
        {
            Sequence = 2,
            NativeWaitTicks = Ticks(50),
            WaitPrecheckTicks = 0,
            WaitCallTicks = Ticks(48),
            WaitPostcheckTicks = Ticks(1),
            CpuThreadTicks = 0,
            SwapChainGeneration = 3
        };
        TachDiagnostics.RecordRenderer(in next);
        var partial = sample with
        {
            Sequence = 3,
            NativeWaitTicks = Ticks(10),
            WaitPrecheckTicks = Ticks(2),
            WaitCallTicks = null,
            WaitPostcheckTicks = null,
            CpuThreadTicks = null,
            SwapChainGeneration = 2
        };
        TachDiagnostics.RecordRenderer(in partial);

        Assert.Equal(new[] { sample, next, partial }, Snapshot().RendererRecent);
        var interval = Interval();
        var count = Assert.Single(interval.Renderer);
        Assert.Equal(3, count.Count);
        Assert.Equal(3, count.NativeWaitSamples);
        Assert.Equal(96, count.TotalNativeWaitMilliseconds!.Value, 6);
        Assert.Equal(3, count.WaitPrecheckSamples);
        Assert.Equal(5, count.TotalWaitPrecheckMilliseconds!.Value, 6);
        Assert.Equal(2, count.WaitCallSamples);
        Assert.Equal(78, count.TotalWaitCallMilliseconds!.Value, 6);
        Assert.Equal(48, count.MaximumWaitCallMilliseconds!.Value, 6);
        Assert.Equal(2, count.WaitPostcheckSamples);
        Assert.Equal(3, count.TotalWaitPostcheckMilliseconds!.Value, 6);
        Assert.Equal(2, count.CpuThreadSamples);
        Assert.Equal(2, count.TotalCpuThreadMilliseconds, 6);
        Assert.Equal(3, count.SwapChainGenerationSamples);
        Assert.Equal(1u, count.MinimumSwapChainGeneration);
        Assert.Equal(3u, count.MaximumSwapChainGeneration);
        Assert.Equal(1u, count.WaitReturnCode);
        Assert.NotNull(TachDiagnosticReport.SanitizeInterval(interval));
        Assert.Empty(Interval().Renderer);
    }

    [Fact]
    public void DifferentWin32WaitOutcomesRemainDistinctInPersistedCounts()
    {
        TachDiagnostics.SetEnabled(true);
        var sample = WaitSample();
        foreach (uint? code in new uint?[] { null, 0, 1, 258, uint.MaxValue })
        {
            var next = sample with { WaitReturnCode = code };
            TachDiagnostics.RecordRenderer(in next);
        }
        var counts = Interval().Renderer;
        Assert.Equal(5, counts.Length);
        Assert.Equal(new uint?[] { null, 0, 1, 258, uint.MaxValue }, counts.Select(value => value.WaitReturnCode));
        Assert.All(counts, value => Assert.Equal(1, value.Count));
    }

    [Fact]
    public void RenderThreadIdentitySurvivesExportAndSeparatesWorkerRestarts()
    {
        TachDiagnostics.SetEnabled(true);
        foreach (uint? thread in new uint?[] { null, 0, 120, 240 })
        {
            var sample = WaitSample() with { NativeThreadId = thread };
            TachDiagnostics.RecordRenderer(in sample);
        }

        var raw = Snapshot().RendererRecent;
        Assert.Equal(new uint?[] { null, null, 120, 240 }, raw.Select(value => value.NativeThreadId));
        var interval = Interval();
        Assert.Equal(new uint?[] { null, 120, 240 }, interval.Renderer.Select(value => value.NativeThreadId));
        Assert.Equal(new long[] { 2, 1, 1 }, interval.Renderer.Select(value => value.Count));
        var restored = JsonSerializer.Deserialize<TachIntervalDiagnostic>(JsonSerializer.Serialize(interval));
        Assert.NotNull(restored);
        Assert.Equal(240u, TachDiagnosticReport.SanitizeInterval(restored)!.Renderer[2].NativeThreadId);
    }

    [Fact]
    public void NegativeWaitDetailsBecomeUnavailableWhileMeasuredZeroRemainsMeasured()
    {
        TachDiagnostics.SetEnabled(true);
        var invalid = WaitSample() with
        {
            NativeWaitTicks = -1,
            WaitPrecheckTicks = -2,
            WaitCallTicks = -3,
            WaitPostcheckTicks = -4,
            CpuThreadTicks = -5,
            SwapChainGeneration = 0
        };
        TachDiagnostics.RecordRenderer(in invalid);
        var clean = Assert.Single(Snapshot().RendererRecent);
        Assert.Null(clean.NativeWaitTicks);
        Assert.Null(clean.WaitPrecheckTicks);
        Assert.Null(clean.WaitCallTicks);
        Assert.Null(clean.WaitPostcheckTicks);
        Assert.Null(clean.CpuThreadTicks);
        Assert.Null(clean.SwapChainGeneration);
        var missing = Assert.Single(Interval().Renderer);
        Assert.Equal(0, missing.NativeWaitSamples);
        Assert.Null(missing.TotalNativeWaitMilliseconds);
        Assert.Null(missing.TotalWaitPrecheckMilliseconds);
        Assert.Null(missing.TotalWaitCallMilliseconds);
        Assert.Null(missing.MaximumWaitCallMilliseconds);
        Assert.Null(missing.TotalWaitPostcheckMilliseconds);
        Assert.Equal(0, missing.SwapChainGenerationSamples);
        Assert.Null(missing.MinimumSwapChainGeneration);
        Assert.Null(missing.MaximumSwapChainGeneration);

        var zero = WaitSample();
        TachDiagnostics.RecordRenderer(in zero);
        var measured = Assert.Single(Interval().Renderer);
        Assert.Equal(1, measured.NativeWaitSamples);
        Assert.Equal(0d, measured.TotalNativeWaitMilliseconds);
        Assert.Equal(1, measured.WaitCallSamples);
        Assert.Equal(0d, measured.TotalWaitCallMilliseconds);
        Assert.Equal(0d, measured.MaximumWaitCallMilliseconds);
        Assert.Equal(1, measured.CpuThreadSamples);
        Assert.Equal(0, measured.TotalCpuThreadMilliseconds);
    }

    [Fact]
    public void DetailRingsRetainStartupAndNewestEventsWithExactOverwriteCounts()
    {
        TachDiagnostics.SetEnabled(true);
        var sample = Sample();
        var count = TachDiagnostics.RendererRecentCapacity + 17;
        for (var index = 0; index < count; index++)
        {
            var next = sample with { Sequence = index + 1, StartedTimestamp = sample.StartedTimestamp + index, CompletedTimestamp = sample.CompletedTimestamp + index };
            TachDiagnostics.RecordRenderer(in next);
        }
        var capture = Snapshot();
        Assert.Equal(TachDiagnostics.RendererStartupCapacity, capture.RendererStartup.Length);
        Assert.Equal(TachDiagnostics.RendererRecentCapacity, capture.RendererRecent.Length);
        Assert.Equal(1, capture.RendererStartup[0].Sequence);
        Assert.Equal(TachDiagnostics.RendererStartupCapacity, capture.RendererStartup[^1].Sequence);
        Assert.Equal(18, capture.RendererRecent[0].Sequence);
        Assert.Equal(count, capture.RendererRecent[^1].Sequence);
        Assert.Equal(17, capture.RendererOverwritten);
        Assert.Equal(count, Assert.Single(Interval().Renderer).Count);
    }

    [Fact]
    public void RendererStartupStopsAfterSixtySeconds()
    {
        TachDiagnostics.SetEnabled(true);
        var sample = Sample();
        TachDiagnostics.RecordRenderer(in sample);
        sample = sample with { StartedTimestamp = sample.StartedTimestamp + Ticks(60_001), CompletedTimestamp = sample.CompletedTimestamp + Ticks(60_001) };
        TachDiagnostics.RecordRenderer(in sample);
        Assert.Single(Snapshot().RendererStartup);
        Assert.Equal(2, Snapshot().RendererRecent.Length);
    }

    [Fact]
    public void AggregateCapacityDoesNotLoseRawEventsAndSlotsAreReusedNextInterval()
    {
        TachDiagnostics.SetEnabled(true);
        var sample = Sample();
        for (var index = 0; index < TachDiagnostics.RendererAggregateCapacity + 2; index++)
        {
            var next = sample with { ControlId = index + 1 };
            TachDiagnostics.RecordRenderer(in next);
        }
        var interval = Interval();
        Assert.Equal(TachDiagnostics.RendererAggregateCapacity, interval.Renderer.Length);
        Assert.Equal(2, interval.RendererAggregateOmissions);
        Assert.Equal(TachDiagnostics.RendererAggregateCapacity + 2, Snapshot().RendererRecent.Length);
        sample = sample with { ControlId = 999 };
        TachDiagnostics.RecordRenderer(in sample);
        var nextInterval = Interval();
        Assert.Equal(999, Assert.Single(nextInterval.Renderer).ControlId);
        Assert.Equal(2, nextInterval.RendererAggregateOmissions);
    }

    [Fact]
    public void ContendedProducerReturnsWithoutWaitingAndRecordsItsOmission()
    {
        TachDiagnostics.SetEnabled(true);
        var capture = typeof(TachDiagnostics).GetField("_capture", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var gate = capture.GetType().GetField("Gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
        var sample = Sample();
        var completed = false;
        var producer = new Thread(() => { TachDiagnostics.RecordRenderer(in sample); completed = true; }) { IsBackground = true };
        lock (gate)
        {
            producer.Start();
            Assert.True(producer.Join(TimeSpan.FromSeconds(2)), "Renderer recording must not wait for the collector lock.");
        }
        Assert.True(completed);
        var snapshot = Snapshot();
        Assert.Empty(snapshot.RendererRecent);
        Assert.Equal(1, snapshot.RendererContentionOmissions);
        Assert.Equal(1, snapshot.ContentionOmissions);
        Assert.Equal(1, Interval().RendererContentionOmissions);
    }

    [Fact]
    public void ReenableStartsANewHistoryAndInvalidTimingCannotCreateNegativeDurations()
    {
        TachDiagnostics.SetEnabled(true);
        var sample = Sample() with { CompletedTimestamp = 1 };
        TachDiagnostics.RecordRenderer(in sample);
        Assert.Empty(Snapshot().RendererRecent);
        Assert.Equal(1, Interval().RendererInvalidOmissions);
        sample = Sample() with { Stage = "private-sentinel", Result = "private-sentinel", MapTicks = -1, MapCount = -1, CpuThreadTicks = -1 };
        TachDiagnostics.RecordRenderer(in sample);
        var before = Snapshot();
        var clean = Assert.Single(before.RendererRecent);
        Assert.Equal("unknown", clean.Stage);
        Assert.Equal("unknown", clean.Result);
        Assert.Equal(0, clean.MapTicks);
        Assert.Equal(0, clean.MapCount);
        Assert.Null(clean.CpuThreadTicks);
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(before));
        TachDiagnostics.SetEnabled(false);
        TachDiagnostics.RecordRenderer(in sample);
        Assert.Single(Snapshot().RendererRecent);
        TachDiagnostics.SetEnabled(true);
        Assert.NotEqual(before.CaptureId, Snapshot().CaptureId);
        Assert.Empty(Snapshot().RendererRecent);
        Assert.Equal(0, Snapshot().RendererInvalidOmissions);
    }

    [Fact]
    public void EnabledUncontendedProducerHasNoPerEventAllocations()
    {
        TachDiagnostics.SetEnabled(true);
        var sample = WaitSample();
        long allocated = -1;
        Exception? failure = null;
        var producer = new Thread(() =>
        {
            try
            {
                for (var index = 0; index < 20_000; index++) TachDiagnostics.RecordRenderer(in sample);
                Interval();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < 20_000; index++) TachDiagnostics.RecordRenderer(in sample);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception exception) { failure = exception; }
        })
        { IsBackground = true };
        using (ExecutionContext.SuppressFlow()) producer.Start();
        Assert.True(producer.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
        Assert.Equal(0, allocated);
        Assert.Equal(20_000, Assert.Single(Interval().Renderer).Count);
    }

    private static TachRendererDiagnostic Sample() => new()
    {
        ControlId = 1,
        HostWindowHandle = 123,
        Sequence = 1,
        Stage = "draw",
        Result = "ready",
        StartedTimestamp = Stopwatch.GetTimestamp(),
        CompletedTimestamp = Stopwatch.GetTimestamp() + Ticks(1)
    };
    private static TachRendererDiagnostic WaitSample() => Sample() with
    {
        Stage = "frame_wait",
        NativeWaitTicks = 0,
        WaitPrecheckTicks = 0,
        WaitCallTicks = 0,
        WaitPostcheckTicks = 0,
        CpuThreadTicks = 0,
        SwapChainGeneration = 1,
        WaitReturnCode = 1
    };
    private static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000d);
    private static TachCaptureExport Snapshot() => Assert.IsType<TachCaptureExport>(TachDiagnostics.Snapshot());
    private static TachIntervalDiagnostic Interval() => Assert.IsType<TachIntervalDiagnostic>(TachDiagnostics.CollectInterval(DateTimeOffset.UtcNow));
    private static void Reset() { TachDiagnostics.SetEnabled(false); TachDiagnostics.Clear(); }
}
