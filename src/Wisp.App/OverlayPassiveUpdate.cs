using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wisp.App;

internal readonly record struct OverlayPassiveUpdateState(
    long WindowHandle, string WindowKind, bool RequestedEnabled,
    long StartedTimestamp, long CompletedTimestamp, int SetHResult);

internal static class OverlayPassiveUpdate
{
    internal const int Capacity = 128;
    private const int PassiveUpdateModeAttribute = 16;
    private static readonly ConcurrentDictionary<IntPtr, OverlayPassiveUpdateState> Windows = new();
    private static readonly object RegistrationGate = new();

    internal static OverlayPassiveUpdateState Apply(IntPtr handle, bool native)
    {
        var started = Stopwatch.GetTimestamp();
        var enabled = 1;
        int result;
        try
        {
            // The Windows SDK marks this BOOL attribute as set-only. Its return
            // value establishes API acceptance, not compositor/display behavior.
            result = DwmSetWindowAttribute(handle, PassiveUpdateModeAttribute, ref enabled, sizeof(int));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            result = error.HResult;
        }
        var state = new OverlayPassiveUpdateState(handle.ToInt64(),
            native ? "native_composition" : "wpf_overlay", true, started, Stopwatch.GetTimestamp(), result);
        // Initialization/message handling only; never called by a frame loop.
        // Keep failures too, so an unsupported request cannot look like success.
        if (handle != IntPtr.Zero)
        {
            lock (RegistrationGate)
            {
                if (Windows.ContainsKey(handle) || Windows.Count < Capacity) Windows[handle] = state;
            }
        }
        return state;
    }

    internal static void Forget(IntPtr handle) => Windows.TryRemove(handle, out _);

    internal static OverlayPassiveUpdateState[] Snapshot() =>
        Windows.Values.OrderBy(value => value.WindowHandle).ToArray();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int valueSize);
}
