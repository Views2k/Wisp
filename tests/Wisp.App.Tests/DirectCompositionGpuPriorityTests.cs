using System.Runtime.InteropServices;
using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DirectCompositionGpuPriorityTests
{
    [Fact]
    public void LayoutMatchesNativePriorityStatus()
    {
        Assert.Equal(36, Marshal.SizeOf<DirectCompositionGpuPriorityStatus>());
        Assert.Equal(4, Marshal.OffsetOf<DirectCompositionGpuPriorityStatus>(nameof(DirectCompositionGpuPriorityStatus.RequestedProcessClass)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<DirectCompositionGpuPriorityStatus>(nameof(DirectCompositionGpuPriorityStatus.ProcessSetStatus)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<DirectCompositionGpuPriorityStatus>(nameof(DirectCompositionGpuPriorityStatus.DeviceSetHResult)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<DirectCompositionGpuPriorityStatus>(nameof(DirectCompositionGpuPriorityStatus.EffectiveDevicePriority)).ToInt32());
    }

    [Fact]
    public void DiagnosticPreservesRejectedRequestAndDifferentReadback()
    {
        var status = new DirectCompositionGpuPriorityStatus
        {
            Attempted = 1,
            RequestedProcessClass = 4,
            ProcessSetStatus = unchecked((int)0xC0000022),
            ProcessReadStatus = 0,
            EffectiveProcessClass = 2,
            RequestedDevicePriority = 1,
            DeviceSetHResult = unchecked((int)0x80070005),
            DeviceReadHResult = 0,
            EffectiveDevicePriority = 0
        };
        var diagnostic = status.ToDiagnostic();
        Assert.True(diagnostic.Attempted);
        Assert.Equal(4, diagnostic.RequestedProcessClass);
        Assert.Equal(unchecked((int)0xC0000022), diagnostic.ProcessSetStatus);
        Assert.Equal(0, diagnostic.ProcessReadStatus);
        Assert.Equal(2, diagnostic.EffectiveProcessClass);
        Assert.Equal(1, diagnostic.RequestedDevicePriority);
        Assert.Equal(unchecked((int)0x80070005), diagnostic.DeviceSetHResult);
        Assert.Equal(0, diagnostic.DeviceReadHResult);
        Assert.Equal(0, diagnostic.EffectiveDevicePriority);
    }
}
