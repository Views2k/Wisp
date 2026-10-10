using System.Diagnostics;

namespace Wisp.App.DebugLogging;

internal enum ToolsPerformanceMetric
{
    FrameWait, RenderWork, SceneBuild, PresentCall, SubmissionInterval,
    QueueToSubmit, ReceiveToSubmit, CompositorUpdate, RetryWait,
    CompositorMotionWork, CompositorMotionSourceAge, TelemetryParseWork, ParseToUiAdoption,
    ReceiveToUiAdoption, UiUpdateWork, NativeUiUpdateWork, NativeObservationToUiAdoption, PublishToUiAdoption
}

internal sealed record ToolsPerformanceDistribution(
    ToolsPerformanceMetric Metric, bool CpuRendering, long Count, double SumMs,
    double MinimumMs, double MaximumMs, long[] Buckets, long Dropped);

// Timing only: no frames, car data, handles, thread IDs or captured pixels.
// Writers never wait. A contended observation is counted as dropped instead.
internal sealed class ToolsPerformanceRecorder
{
    internal static ToolsPerformanceRecorder Current { get; } = new();
    private static readonly double[] BucketBounds =
        [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 60000];
    internal static ReadOnlySpan<double> BucketUpperBoundsMs => BucketBounds;

    private const int MetricCount = (int)ToolsPerformanceMetric.PublishToUiAdoption + 1;
    private readonly Histogram[] _histograms = Enumerable.Range(0, MetricCount * 2)
        .Select(_ => new Histogram()).ToArray();
    private int _enabled;

    internal bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    internal void RecordTicks(ToolsPerformanceMetric metric, bool cpuRendering, long ticks)
    {
        if (!Enabled || (uint)metric >= MetricCount || ticks < 0) return;
        var milliseconds = ticks * (1000d / Stopwatch.Frequency);
        _histograms[(int)metric * 2 + (cpuRendering ? 1 : 0)].Record(milliseconds);
    }

    internal void RecordDropped(ToolsPerformanceMetric metric, bool cpuRendering)
    {
        if (Enabled && (uint)metric < MetricCount)
            _histograms[(int)metric * 2 + (cpuRendering ? 1 : 0)].Drop();
    }

    // Only the background reporting worker drains these fixed-size histograms.
    // An unavailable histogram remains intact for the following collection.
    internal IReadOnlyList<ToolsPerformanceDistribution> Drain()
    {
        var result = new List<ToolsPerformanceDistribution>(MetricCount * 2);
        for (var index = 0; index < _histograms.Length; index++)
            if (_histograms[index].Drain((ToolsPerformanceMetric)(index / 2), index % 2 != 0) is { } sample)
                result.Add(sample);
        return result;
    }

    private sealed class Histogram
    {
        private readonly long[] _buckets = new long[16];
        private int _gate;
        private long _count, _dropped;
        private double _sum, _maximum, _minimum = double.PositiveInfinity;

        internal void Drop() => Interlocked.Increment(ref _dropped);

        internal void Record(double milliseconds)
        {
            if (!double.IsFinite(milliseconds) || milliseconds > 60000 ||
                Interlocked.CompareExchange(ref _gate, 1, 0) != 0)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            try
            {
                var bounds = BucketUpperBoundsMs;
                var bucket = 0;
                while (bucket < bounds.Length - 1 && milliseconds > bounds[bucket]) bucket++;
                _buckets[bucket]++;
                _count++;
                _sum += milliseconds;
                _maximum = Math.Max(_maximum, milliseconds);
                _minimum = Math.Min(_minimum, milliseconds);
            }
            finally { Volatile.Write(ref _gate, 0); }
        }

        internal ToolsPerformanceDistribution? Drain(ToolsPerformanceMetric metric, bool cpuRendering)
        {
            if (Interlocked.CompareExchange(ref _gate, 1, 0) != 0) return null;
            try
            {
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (_count == 0 && dropped == 0) return null;
                var result = new ToolsPerformanceDistribution(metric, cpuRendering,
                    _count, _sum, _count == 0 ? 0 : _minimum, _maximum, (long[])_buckets.Clone(), dropped);
                Array.Clear(_buckets);
                _count = 0;
                _sum = _maximum = 0;
                _minimum = double.PositiveInfinity;
                return result;
            }
            finally { Volatile.Write(ref _gate, 0); }
        }
    }
}
