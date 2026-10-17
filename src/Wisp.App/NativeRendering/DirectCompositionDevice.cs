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
// Every failed native HRESULT is raised as NativeRendererException carrying that
// HRESULT, never as a runtime exception type: E_OUTOFMEMORY from the library must
// not become OutOfMemoryException, which the render workers and crash handler
// treat as fatal.
internal sealed class DirectCompositionDevice : IDisposable
{
    private readonly RendererHandle _handle;
    private readonly Func<string, int>? _nativeFailureForTest;
    private int _width;
    private int _height;
    private bool _disposed;

    private DirectCompositionDevice(RendererHandle handle, int width, int height, Func<string, int>? nativeFailureForTest)
    {
        _handle = handle;
        _width = width;
        _height = height;
        _nativeFailureForTest = nativeFailureForTest;
        ThrowIfFailed(Native.GetGpuPriorityStatus(handle, out var priority), "gpu_priority");
        GpuPriority = priority.ToDiagnostic();
    }

    // Initialization results, refreshed after each priority request, remain available if logging starts later.
    internal TachGpuPriorityDiagnostic GpuPriority { get; private set; }

    // Render-thread only, like every other call on this device. Returns false when
    // the renderer ignored the request (WARP).
    public bool SetGpuPriority(bool raise)
    {
        ThrowIfDisposed();
        var result = Native.SetGpuPriority(_handle, raise ? 1 : 0);
        ThrowIfFailed(result, "gpu_priority");
        ThrowIfFailed(Native.GetGpuPriorityStatus(_handle, out var priority), "gpu_priority");
        GpuPriority = priority.ToDiagnostic();
        return result == 0;
    }

    // nativeFailureForTest stands in for the library on the operation it names ("create",
    // "motion_channel" or "upload_texture"): a failing HRESULT it returns is raised exactly
    // as a native failure would be, and the library is not called for that operation.
    public static DirectCompositionDevice Create(IntPtr hwnd, int width, int height, bool cpuRendering = false,
        bool compositorNeedle = false, Func<string, int>? nativeFailureForTest = null)
    {
        ValidateSize(width, height);
        if (hwnd == IntPtr.Zero) throw new ArgumentException("A live overlay window is required.", nameof(hwnd));
        RendererHandle? handle = null;
        var result = InjectedFailure(nativeFailureForTest, "create");
        if (result >= 0)
            result = compositorNeedle
                ? Native.CreateWithCompositorNeedle(hwnd, (uint)width, (uint)height, cpuRendering ? 1u : 0u, out handle)
                : Native.CreateWithMode(hwnd, (uint)width, (uint)height, cpuRendering ? 1u : 0u, out handle);
        ThrowIfFailed(result, "create");
        try { return new DirectCompositionDevice(handle!, width, height, nativeFailureForTest); }
        catch
        {
            handle!.Dispose();
            throw;
        }
    }

    public bool LastRenderWasOccluded { get; private set; }

    internal MotionChannel CreateMotionChannel()
    {
        ThrowIfDisposed();
        MotionHandle? handle = null;
        var result = InjectedFailure("motion_channel");
        if (result >= 0) result = Native.CreateMotionChannel(_handle, out handle);
        ThrowIfFailed(result, "motion_channel");
        if (result != 0 || handle is null || handle.IsInvalid)
        {
            handle?.Dispose();
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
        ThrowIfFailed(result, "compositor_update");
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
            ThrowIfFailed(result, "motion_update");
            return result == 0;
        }
        internal void Clear() => ThrowIfFailed(Native.ClearMotion(_handle), "motion_clear");
        public void Dispose() => _handle.Dispose();
    }

    internal bool UpdateCompositorNeedle(in CompositorNeedleGeometry geometry, CompositorNeedleCurve curve,
        CompositorNeedlePoint[] points)
    {
        ThrowIfDisposed();
        if (curve.Count < 1 || curve.Count > points.Length) throw new ArgumentOutOfRangeException(nameof(curve));
        var result = Native.UpdateCompositorNeedle(_handle, in geometry, curve.StartTimestamp,
            curve.FreshUntilTimestamp, points, (uint)curve.Count);
        ThrowIfFailed(result, "compositor_update");
        return result == 0;
    }

    internal void ClearCompositorNeedle()
    {
        ThrowIfDisposed();
        ThrowIfFailed(Native.ClearCompositorNeedle(_handle, IntPtr.Zero, 0, 0, IntPtr.Zero, 0), "compositor_update");
    }

    // Only a replaced swapchain requires a new frame-readiness wait.
    public bool PrepareForResume()
    {
        ThrowIfDisposed();
        var result = Native.PrepareForResume(_handle);
        ThrowIfFailed(result, "resume");
        return result == 0;
    }

    public void SetOpacity(float opacity)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(opacity) || opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        ThrowIfFailed(Native.SetOpacity(_handle, opacity), "opacity");
    }

    public void SetVisible(bool visible)
    {
        ThrowIfDisposed();
        ThrowIfFailed(Native.SetVisible(_handle, visible ? 1 : 0), "visibility");
    }

    public void SetOffset(float offsetX, float offsetY)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(offsetX) || !float.IsFinite(offsetY)) throw new ArgumentOutOfRangeException(nameof(offsetX));
        ThrowIfFailed(Native.SetOffset(_handle, offsetX, offsetY), "offset");
    }

    public void Resize(int width, int height, float offsetX = 0, float offsetY = 0)
    {
        ThrowIfDisposed();
        ValidateSize(width, height);
        if (!float.IsFinite(offsetX) || !float.IsFinite(offsetY)) throw new ArgumentOutOfRangeException(nameof(offsetX));
        ThrowIfFailed(Native.Resize(_handle, (uint)width, (uint)height, offsetX, offsetY), "resize");
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
        var result = InjectedFailure("upload_texture");
        if (result >= 0)
            result = Native.UploadTexture(_handle, id, (uint)width, (uint)height,
                (uint)stride, premultipliedBgra, (uint)premultipliedBgra.Length);
        ThrowIfFailed(result, "upload_texture");
    }

    public void RemoveTexture(uint id)
    {
        ThrowIfDisposed();
        if (id == 0) throw new ArgumentOutOfRangeException(nameof(id));
        ThrowIfFailed(Native.RemoveTexture(_handle, id), "remove_texture");
    }

    // Legacy combined draw/present entry point for renderer contract checks.
    public bool RenderPresent(DirectCompositionDrawCommand[] commands, int count)
    {
        ValidateCommands(commands, count);
        LastRenderWasOccluded = false;
        var result = Native.Render(_handle, commands, (uint)count, 1);
        LastRenderWasOccluded = result == 2;
        ThrowIfFailed(result, "render");
        return result == 0;
    }

    public void DrawForPresentation(DirectCompositionDrawCommand[] commands, int count, bool measure,
        out DirectCompositionDrawMetrics metrics)
    {
        ValidateCommands(commands, count);
        ThrowIfFailed(Native.DrawForPresentation(_handle, commands, (uint)count, measure ? 1 : 0, out metrics), "draw");
    }

    // Retry a busy presentation without issuing another clear, map, or draw.
    public bool TryPresent(bool measure, out DirectCompositionPresentMetrics metrics)
    {
        ThrowIfDisposed();
        LastRenderWasOccluded = false;
        var result = Native.TryPresent(_handle, measure ? 1 : 0, out metrics);
        LastRenderWasOccluded = result == 2;
        ThrowIfFailed(result, "present");
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
            ThrowIfFailed(hresult, "frame_wait");
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
        ThrowIfFailed(Native.Render(_handle, commands, (uint)count, 0), "render");
    }

    public byte[] CaptureBgra()
    {
        ThrowIfDisposed();
        int stride = checked(_width * 4);
        var pixels = new byte[checked(stride * _height)];
        ThrowIfFailed(Native.Capture(_handle, pixels, (uint)pixels.Length, (uint)stride), "capture");
        return pixels;
    }

    public int GetDeviceRemovedReason()
    {
        ThrowIfDisposed();
        return Native.DeviceRemovedReason(_handle);
    }

    // Gives the native renderer up without destroying it. A motion thread that ignored
    // its stop signal may still be inside a driver call on this device, and destroying
    // the device under that thread faulted (Windows recorded access violations on
    // 2.6.12). The handle is marked invalid so no release ever runs for it.
    public void Abandon()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.SetHandleAsInvalid();
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

    private int InjectedFailure(string operation) => InjectedFailure(_nativeFailureForTest, operation);

    private static int InjectedFailure(Func<string, int>? nativeFailureForTest, string operation) =>
        nativeFailureForTest?.Invoke(operation) ?? 0;

    private static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0) throw new NativeRendererException(operation, hresult);
    }

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
        [DllImport(Library, EntryPoint = "WispRendererSetGpuPriority", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetGpuPriority(RendererHandle renderer, int raise);

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
