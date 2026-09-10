using System.Diagnostics;
using System.Runtime.InteropServices;
using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DirectCompositionWaitMetricsTests
{
    [Fact]
    public void LayoutMatchesNativeWaitMetrics()
    {
        Assert.Equal(48, Marshal.SizeOf<DirectCompositionWaitMetrics>());
        Assert.Equal(32, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.CpuTime100ns)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.SwapChainGeneration)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.WaitResult)).ToInt32());
    }

    [Fact]
    public void ThreadAccountingConvertsToDiagnosticClockAndPreservesUnavailable()
    {
        var metrics = new DirectCompositionWaitMetrics { CpuTime100ns = TimeSpan.TicksPerSecond };
        Assert.Equal(Stopwatch.Frequency, metrics.CpuThreadStopwatchTicks);
        metrics.CpuTime100ns = 0;
        Assert.Equal(0, metrics.CpuThreadStopwatchTicks);
        metrics.CpuTime100ns = -1;
        Assert.Null(metrics.CpuThreadStopwatchTicks);
    }
}
