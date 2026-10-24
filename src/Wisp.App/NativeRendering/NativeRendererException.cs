using System.Runtime.InteropServices;

namespace Wisp.App.NativeRendering;

// A failed HRESULT returned by Wisp.NativeRenderer.dll. The library maps std::bad_alloc and
// failed Direct3D 11 / DirectComposition allocations to E_OUTOFMEMORY, which
// Marshal.ThrowExceptionForHR surfaces as the runtime's OutOfMemoryException. The render
// workers' catch filters and the crash handler treat that type as fatal, so a transient native
// allocation failure ended the process. HResult keeps the native code for health recording
// and status reporting.
internal sealed class NativeRendererException : COMException
{
    internal const int OutOfMemory = unchecked((int)0x8007000E); // E_OUTOFMEMORY
    // DXGI device loss: the graphics device was removed, reset or hung, or its driver failed. The
    // device is gone for every user; the renderer is built again on a fresh one.
    internal const int DeviceRemoved = unchecked((int)0x887A0005); // DXGI_ERROR_DEVICE_REMOVED
    internal const int DeviceHung = unchecked((int)0x887A0006); // DXGI_ERROR_DEVICE_HUNG
    internal const int DeviceReset = unchecked((int)0x887A0007); // DXGI_ERROR_DEVICE_RESET
    internal const int DriverInternalError = unchecked((int)0x887A0020); // DXGI_ERROR_DRIVER_INTERNAL_ERROR

    internal NativeRendererException(string operation, int hresult)
        : base($"The native renderer failed during {operation} (0x{hresult:X8}).", hresult) => Operation = operation;

    // The device call that failed, named like the render worker's stages.
    internal string Operation { get; }
}
