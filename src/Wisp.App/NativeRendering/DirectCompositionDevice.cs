using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Wisp.App.DebugLogging;

namespace Wisp.App.NativeRendering;

internal enum DirectCompositionShader : uint
{
    Image = 0,
    Dial = 1,
    Needle = 2,
    ElectricNeedle = 3,
    ImageSector = 4,
    DigitalGauge = 5
}

internal enum DirectCompositionWaitResult : uint
{
    Ready = 0,
    Timeout = 1,
    Cancelled = 2
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DirectCompositionDrawCommand
{
    public uint TextureId;
    public DirectCompositionShader Shader;
    public float OriginX, OriginY;
    public float AxisXX, AxisXY, AxisYX, AxisYY;
    public float UvLeft, UvTop, UvRight, UvBottom;
    public float TintR, TintG, TintB, TintA;
    public float ParameterX, ParameterY, ParameterZ, ParameterW;
}

// CPU elapsed QPC ticks; these do not measure GPU execution or physical display.
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct DirectCompositionDrawMetrics
{
    public long TotalTicks, SetupTicks, MapTicks, MaximumMapTicks;
    public uint MapCount, DrawCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct DirectCompositionPresentMetrics
{
    public long DurationTicks;
    public int HResult;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct DirectCompositionWaitMetrics
{
    public long TotalTicks, PrecheckTicks, WaitCallTicks, PostcheckTicks, CpuTime100ns;
    public uint SwapChainGeneration, WaitResult;
    public long PacingTicks;
    public uint SyncInterval, RefreshRate;
    public int PacingHResult;
    public uint PacingWaitResult;

    // GetThreadTimes uses coarse 100 ns execution accounting, not elapsed QPC time.
    public readonly long? CpuThreadStopwatchTicks => CpuTime100ns < 0 ? null :
        (long)(CpuTime100ns * (Stopwatch.Frequency / (double)TimeSpan.TicksPerSecond));
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DirectCompositionGpuPriorityStatus
{
    public uint Attempted;
    public int RequestedProcessClass, ProcessSetStatus, ProcessReadStatus, EffectiveProcessClass;
    public int RequestedDevicePriority, DeviceSetHResult, DeviceReadHResult, EffectiveDevicePriority;

    internal readonly TachGpuPriorityDiagnostic ToDiagnostic() => new(
        Attempted != 0, RequestedProcessClass, ProcessSetStatus, ProcessReadStatus, EffectiveProcessClass,
        RequestedDevicePriority, DeviceSetHResult, DeviceReadHResult, EffectiveDevicePriority);
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct CompositorMotionStatus
{
    public long BeginTimestamp, EndTimestamp, FreshUntilTimestamp, CommitTimestamp;
    public ulong Commits, BitmapDraws;
    public uint PointCount, GeometryAccepted;
}

// The render worker owns this device. The HWND remains owned by WPF's UI thread.
internal sealed class DirectCompositionDevice : IDisposable
{
    private readonly RendererHandle _handle;
    private int _width;
    private int _height;
    private bool _disposed;

    private DirectCompositionDevice(RendererHandle handle, int width, int height)
    {
        _handle = handle;
        _width = width;
        _height = height;
        Marshal.ThrowExceptionForHR(Native.GetGpuPriorityStatus(handle, out var priority));
        GpuPriority = priority.ToDiagnostic();
    }

    // Immutable initialization results also remain available if logging starts later.
    internal TachGpuPriorityDiagnostic GpuPriority { get; }

    public static DirectCompositionDevice Create(IntPtr hwnd, int width, int height, bool cpuRendering = false,
        bool compositorNeedle = false)
    {
        ValidateSize(width, height);
        if (hwnd == IntPtr.Zero) throw new ArgumentException("A live overlay window is required.", nameof(hwnd));
        RendererHandle handle;
        var result = compositorNeedle
            ? Native.CreateWithCompositorNeedle(hwnd, (uint)width, (uint)height, cpuRendering ? 1u : 0u, out handle)
            : Native.CreateWithMode(hwnd, (uint)width, (uint)height, cpuRendering ? 1u : 0u, out handle);
        Marshal.ThrowExceptionForHR(result);
        try { return new DirectCompositionDevice(handle, width, height); }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public bool LastRenderWasOccluded { get; private set; }

    internal MotionChannel CreateMotionChannel()
    {
        ThrowIfDisposed();
        var result = Native.CreateMotionChannel(_handle, out var handle);
        Marshal.ThrowExceptionForHR(result);
        if (result != 0 || handle.IsInvalid)
        {
            handle.Dispose();
            throw new NotSupportedException("The native renderer does not support independent needle motion.");
        }
        return new(handle);
    }

    internal bool PrepareCompositorNeedleMotion(in CompositorNeedleGeometry geometry, CompositorNeedleCurve curve,
        CompositorNeedlePoint[] points, long generation)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(points);
        if (curve.Count < 1 || curve.Count > points.Length) throw new ArgumentOutOfRangeException(nameof(curve));
        var result = Native.PrepareCompositorNeedleMotion(_handle, in geometry, curve.StartTimestamp,
            curve.FreshUntilTimestamp, points, (uint)curve.Count, checked((ulong)generation));
        Marshal.ThrowExceptionForHR(result);
        return result == 0;
    }

    internal sealed class MotionChannel : IDisposable
    {
        private readonly MotionHandle _handle;
        internal MotionChannel(MotionHandle handle) => _handle = handle;
        internal bool Update(in CompositorNeedleGeometry geometry, CompositorNeedleCurve curve,
            CompositorNeedlePoint[] points, long generation, out CompositorMotionStatus status)
        {
            ArgumentNullException.ThrowIfNull(points);
            if (curve.Count < 1 || curve.Count > points.Length) throw new ArgumentOutOfRangeException(nameof(curve));
            var result = Native.UpdateMotion(_handle, in geometry, curve.StartTimestamp,
                curve.FreshUntilTimestamp, points, (uint)curve.Count, checked((ulong)generation), out status);
            Marshal.ThrowExceptionForHR(result);
            return result == 0;
        }
        internal void Clear() => Marshal.ThrowExceptionForHR(Native.ClearMotion(_handle));
        public void Dispose() => _handle.Dispose();
    }

    internal bool UpdateCompositorNeedle(in CompositorNeedleGeometry geometry, CompositorNeedleCurve curve,
        CompositorNeedlePoint[] points)
    {
        ThrowIfDisposed();
        if (curve.Count < 1 || curve.Count > points.Length) throw new ArgumentOutOfRangeException(nameof(curve));
        var result = Native.UpdateCompositorNeedle(_handle, in geometry, curve.StartTimestamp,
            curve.FreshUntilTimestamp, points, (uint)curve.Count);
        Marshal.ThrowExceptionForHR(result);
        return result == 0;
    }

    internal void ClearCompositorNeedle()
    {
        ThrowIfDisposed();
        Marshal.ThrowExceptionForHR(Native.ClearCompositorNeedle(_handle, IntPtr.Zero, 0, 0, IntPtr.Zero, 0));
    }

    // Only a replaced swapchain requires a new frame-readiness wait.
    public bool PrepareForResume()
    {
        ThrowIfDisposed();
        var result = Native.PrepareForResume(_handle);
        Marshal.ThrowExceptionForHR(result);
        return result == 0;
    }

    public void SetOpacity(float opacity)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(opacity) || opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        Marshal.ThrowExceptionForHR(Native.SetOpacity(_handle, opacity));
    }

    public void SetVisible(bool visible)
    {
        ThrowIfDisposed();
        Marshal.ThrowExceptionForHR(Native.SetVisible(_handle, visible ? 1 : 0));
    }

    public void SetOffset(float offsetX, float offsetY)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(offsetX) || !float.IsFinite(offsetY)) throw new ArgumentOutOfRangeException(nameof(offsetX));
        Marshal.ThrowExceptionForHR(Native.SetOffset(_handle, offsetX, offsetY));
    }

    public void Resize(int width, int height, float offsetX = 0, float offsetY = 0)
    {
        ThrowIfDisposed();
        ValidateSize(width, height);
        if (!float.IsFinite(offsetX) || !float.IsFinite(offsetY)) throw new ArgumentOutOfRangeException(nameof(offsetX));
        Marshal.ThrowExceptionForHR(Native.Resize(_handle, (uint)width, (uint)height, offsetX, offsetY));
        _width = width;
        _height = height;
    }

    public void UploadTexture(uint id, int width, int height, int stride, byte[] premultipliedBgra)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(premultipliedBgra);
        ValidateSize(width, height);
        if (id == 0 || stride < checked(width * 4) || premultipliedBgra.LongLength < (long)stride * height)
            throw new ArgumentException("Texture dimensions and premultiplied BGRA storage disagree.");
        Marshal.ThrowExceptionForHR(Native.UploadTexture(_handle, id, (uint)width, (uint)height,
            (uint)stride, premultipliedBgra, (uint)premultipliedBgra.Length));
    }

    public void RemoveTexture(uint id)
    {
        ThrowIfDisposed();
        if (id == 0) throw new ArgumentOutOfRangeException(nameof(id));
        Marshal.ThrowExceptionForHR(Native.RemoveTexture(_handle, id));
    }

    // Legacy combined draw/present entry point for renderer contract checks.
    public bool RenderPresent(DirectCompositionDrawCommand[] commands, int count)
    {
        ValidateCommands(commands, count);
        LastRenderWasOccluded = false;
        var result = Native.Render(_handle, commands, (uint)count, 1);
        LastRenderWasOccluded = result == 2;
        Marshal.ThrowExceptionForHR(result);
        return result == 0;
    }

    public void DrawForPresentation(DirectCompositionDrawCommand[] commands, int count, bool measure,
        out DirectCompositionDrawMetrics metrics)
    {
        ValidateCommands(commands, count);
        Marshal.ThrowExceptionForHR(Native.DrawForPresentation(_handle, commands, (uint)count, measure ? 1 : 0, out metrics));
    }

    // Retry a busy presentation without issuing another clear, map, or draw.
    public bool TryPresent(bool measure, out DirectCompositionPresentMetrics metrics)
    {
        ThrowIfDisposed();
        LastRenderWasOccluded = false;
        var result = Native.TryPresent(_handle, measure ? 1 : 0, out metrics);
        LastRenderWasOccluded = result == 2;
        Marshal.ThrowExceptionForHR(result);
        return result == 0;
    }

    public DirectCompositionWaitResult WaitForNextFrame(int timeoutMilliseconds, SafeWaitHandle? cancellationHandle = null) =>
        WaitForNextFrame(timeoutMilliseconds, cancellationHandle, false, out _);

    public DirectCompositionWaitResult WaitForNextFrame(int timeoutMilliseconds, SafeWaitHandle? cancellationHandle,
        bool measure, out DirectCompositionWaitMetrics metrics)
    {
        metrics = default;
        ThrowIfDisposed();
        if (timeoutMilliseconds is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        bool addRef = false;
        try
        {
            cancellationHandle?.DangerousAddRef(ref addRef);
            var cancel = cancellationHandle?.DangerousGetHandle() ?? IntPtr.Zero;
            DirectCompositionWaitResult result;
            var hresult = measure
                ? Native.WaitForFrameMeasured(_handle, (uint)timeoutMilliseconds, cancel, 1, out result, out metrics)
                : Native.WaitForFrame(_handle, (uint)timeoutMilliseconds, cancel, out result);
            Marshal.ThrowExceptionForHR(hresult);
            return result;
        }
        finally
        {
            if (addRef) cancellationHandle!.DangerousRelease();
        }
    }

    // Pixel QA only: draw without presenting, then explicitly read that target.
    public void RenderForCapture(DirectCompositionDrawCommand[] commands, int count)
    {
        ValidateCommands(commands, count);
        Marshal.ThrowExceptionForHR(Native.Render(_handle, commands, (uint)count, 0));
    }

    public byte[] CaptureBgra()
    {
        ThrowIfDisposed();
        int stride = checked(_width * 4);
        var pixels = new byte[checked(stride * _height)];
        Marshal.ThrowExceptionForHR(Native.Capture(_handle, pixels, (uint)pixels.Length, (uint)stride));
        return pixels;
    }

    public int GetDeviceRemovedReason()
    {
        ThrowIfDisposed();
        return Native.DeviceRemovedReason(_handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }

    private void ValidateCommands(DirectCompositionDrawCommand[] commands, int count)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(commands);
        if (count < 0 || count > commands.Length || count > 4096) throw new ArgumentOutOfRangeException(nameof(count));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ValidateSize(int width, int height)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(width));
    }

    private sealed class RendererHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public RendererHandle() : base(true) { }
        protected override bool ReleaseHandle()
        {
            Native.Destroy(handle);
            return true;
        }
    }

    internal sealed class MotionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public MotionHandle() : base(true) { }
        protected override bool ReleaseHandle()
        {
            Native.DestroyMotion(handle);
            return true;
        }
    }

    private static class Native
    {
        private const string Library = "Wisp.NativeRenderer.dll";
        [DllImport(Library, EntryPoint = "WispRendererCreateMotionChannel", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CreateMotionChannel(RendererHandle renderer, out MotionHandle channel);
        [DllImport(Library, EntryPoint = "WispRendererPrepareCompositorNeedleMotion", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int PrepareCompositorNeedleMotion(RendererHandle renderer, in CompositorNeedleGeometry geometry,
            long begin, long freshUntil, [In] CompositorNeedlePoint[] points, uint count, ulong generation);
        [DllImport(Library, EntryPoint = "WispMotionChannelUpdate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int UpdateMotion(MotionHandle channel, in CompositorNeedleGeometry geometry,
            long begin, long freshUntil, [In] CompositorNeedlePoint[] points, uint count, ulong generation,
            out CompositorMotionStatus status);
        [DllImport(Library, EntryPoint = "WispMotionChannelClear", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ClearMotion(MotionHandle channel);
        [DllImport(Library, EntryPoint = "WispMotionChannelDestroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void DestroyMotion(IntPtr channel);
        [DllImport(Library, EntryPoint = "WispRendererCreateWithMode", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CreateWithMode(IntPtr hwnd, uint width, uint height, uint cpuRendering, out RendererHandle renderer);
        [DllImport(Library, EntryPoint = "WispRendererCreateWithCompositorNeedle", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CreateWithCompositorNeedle(IntPtr hwnd, uint width, uint height, uint cpuRendering, out RendererHandle renderer);
        [DllImport(Library, EntryPoint = "WispRendererUpdateCompositorNeedle", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int UpdateCompositorNeedle(RendererHandle renderer, in CompositorNeedleGeometry geometry,
            long absoluteStartQpc, long freshUntilQpc, [In] CompositorNeedlePoint[] points, uint count);
        [DllImport(Library, EntryPoint = "WispRendererUpdateCompositorNeedle", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ClearCompositorNeedle(RendererHandle renderer, IntPtr geometry,
            long absoluteStartQpc, long freshUntilQpc, IntPtr points, uint count);

        [DllImport(Library, EntryPoint = "WispRendererGetGpuPriorityStatus", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetGpuPriorityStatus(RendererHandle renderer, out DirectCompositionGpuPriorityStatus status);

        [DllImport(Library, EntryPoint = "WispRendererCreate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Create(IntPtr hwnd, uint width, uint height, out RendererHandle renderer);
        [DllImport(Library, EntryPoint = "WispRendererDestroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(IntPtr renderer);
        [DllImport(Library, EntryPoint = "WispRendererPrepareForResume", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int PrepareForResume(RendererHandle renderer);
        [DllImport(Library, EntryPoint = "WispRendererSetOpacity", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetOpacity(RendererHandle renderer, float opacity);
        [DllImport(Library, EntryPoint = "WispRendererSetVisible", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetVisible(RendererHandle renderer, int visible);
        [DllImport(Library, EntryPoint = "WispRendererSetOffset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetOffset(RendererHandle renderer, float offsetX, float offsetY);
        [DllImport(Library, EntryPoint = "WispRendererResize", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Resize(RendererHandle renderer, uint width, uint height, float offsetX, float offsetY);
        [DllImport(Library, EntryPoint = "WispRendererUploadTexture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int UploadTexture(RendererHandle renderer, uint id, uint width, uint height,
            uint stride, [In] byte[] pixels, uint byteCount);
        [DllImport(Library, EntryPoint = "WispRendererRemoveTexture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RemoveTexture(RendererHandle renderer, uint id);
        [DllImport(Library, EntryPoint = "WispRendererRender", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Render(RendererHandle renderer, [In] DirectCompositionDrawCommand[] commands, uint count, int present);
        [DllImport(Library, EntryPoint = "WispRendererDrawForPresentation", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DrawForPresentation(RendererHandle renderer, [In] DirectCompositionDrawCommand[] commands,
            uint count, int measure, out DirectCompositionDrawMetrics metrics);
        [DllImport(Library, EntryPoint = "WispRendererTryPresent", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int TryPresent(RendererHandle renderer, int measure, out DirectCompositionPresentMetrics metrics);
        [DllImport(Library, EntryPoint = "WispRendererWaitForFrame", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WaitForFrame(RendererHandle renderer, uint timeoutMilliseconds, IntPtr cancellation,
            out DirectCompositionWaitResult result);
        [DllImport(Library, EntryPoint = "WispRendererWaitForFrameMeasuredV2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WaitForFrameMeasured(RendererHandle renderer, uint timeoutMilliseconds, IntPtr cancellation,
            int measure, out DirectCompositionWaitResult result, out DirectCompositionWaitMetrics metrics);
        [DllImport(Library, EntryPoint = "WispRendererCapture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Capture(RendererHandle renderer, [Out] byte[] pixels, uint byteCount, uint stride);
        [DllImport(Library, EntryPoint = "WispRendererDeviceRemovedReason", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DeviceRemovedReason(RendererHandle renderer);
    }
}
