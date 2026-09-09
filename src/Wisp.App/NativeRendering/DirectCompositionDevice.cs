using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wisp.App.NativeRendering;

internal enum DirectCompositionShader : uint
{
    Image = 0,
    Dial = 1,
    Needle = 2
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
    }

    public static DirectCompositionDevice Create(IntPtr hwnd, int width, int height)
    {
        ValidateSize(width, height);
        if (hwnd == IntPtr.Zero) throw new ArgumentException("A live overlay window is required.", nameof(hwnd));
        Marshal.ThrowExceptionForHR(Native.Create(hwnd, (uint)width, (uint)height, out var handle));
        return new DirectCompositionDevice(handle, width, height);
    }

    public bool LastRenderWasOccluded { get; private set; }

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

    // A busy presentation queue returns false; the caller retries with the latest state.
    public bool RenderPresent(DirectCompositionDrawCommand[] commands, int count)
    {
        ValidateCommands(commands, count);
        LastRenderWasOccluded = false;
        var result = Native.Render(_handle, commands, (uint)count, 1);
        LastRenderWasOccluded = result == 2;
        Marshal.ThrowExceptionForHR(result);
        return result == 0;
    }

    public DirectCompositionWaitResult WaitForNextFrame(int timeoutMilliseconds, SafeWaitHandle? cancellationHandle = null)
    {
        ThrowIfDisposed();
        if (timeoutMilliseconds is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        bool addRef = false;
        try
        {
            cancellationHandle?.DangerousAddRef(ref addRef);
            var cancel = cancellationHandle?.DangerousGetHandle() ?? IntPtr.Zero;
            Marshal.ThrowExceptionForHR(Native.WaitForFrame(_handle, (uint)timeoutMilliseconds, cancel, out var result));
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

    private static class Native
    {
        private const string Library = "Wisp.NativeRenderer.dll";
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
        [DllImport(Library, EntryPoint = "WispRendererResize", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Resize(RendererHandle renderer, uint width, uint height, float offsetX, float offsetY);
        [DllImport(Library, EntryPoint = "WispRendererUploadTexture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int UploadTexture(RendererHandle renderer, uint id, uint width, uint height,
            uint stride, [In] byte[] pixels, uint byteCount);
        [DllImport(Library, EntryPoint = "WispRendererRemoveTexture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RemoveTexture(RendererHandle renderer, uint id);
        [DllImport(Library, EntryPoint = "WispRendererRender", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Render(RendererHandle renderer, [In] DirectCompositionDrawCommand[] commands, uint count, int present);
        [DllImport(Library, EntryPoint = "WispRendererWaitForFrame", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WaitForFrame(RendererHandle renderer, uint timeoutMilliseconds, IntPtr cancellation,
            out DirectCompositionWaitResult result);
        [DllImport(Library, EntryPoint = "WispRendererCapture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Capture(RendererHandle renderer, [Out] byte[] pixels, uint byteCount, uint stride);
        [DllImport(Library, EntryPoint = "WispRendererDeviceRemovedReason", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DeviceRemovedReason(RendererHandle renderer);
    }
}
