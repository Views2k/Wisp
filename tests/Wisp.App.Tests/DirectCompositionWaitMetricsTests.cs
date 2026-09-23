using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DirectCompositionWaitMetricsTests
{
    [Fact]
    public void LayoutMatchesNativeWaitMetrics()
    {
        Assert.Equal(72, Marshal.SizeOf<DirectCompositionWaitMetrics>());
        Assert.Equal(0, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.TotalTicks)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.PrecheckTicks)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.WaitCallTicks)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.PostcheckTicks)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.CpuTime100ns)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.SwapChainGeneration)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.WaitResult)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.PacingTicks)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.SyncInterval)).ToInt32());
        Assert.Equal(60, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.RefreshRate)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.PacingHResult)).ToInt32());
        Assert.Equal(68, Marshal.OffsetOf<DirectCompositionWaitMetrics>(nameof(DirectCompositionWaitMetrics.PacingWaitResult)).ToInt32());
    }

    [Fact]
    public void ExpandedWaitBufferUsesTheVersionTwoExport()
    {
        var native = typeof(DirectCompositionDevice).GetNestedType("Native", BindingFlags.NonPublic)!;
        var wait = native.GetMethod("WaitForFrameMeasured", BindingFlags.Static | BindingFlags.NonPublic)!;
        var import = wait.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(import);
        Assert.Equal("WispRendererWaitForFrameMeasuredV2", import.EntryPoint);
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
