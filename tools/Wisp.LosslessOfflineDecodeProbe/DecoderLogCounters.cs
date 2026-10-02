using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wisp.LosslessOfflineDecodeProbe;

internal sealed class DecoderLogCounters : IDisposable
{
    private readonly long _started;
    private readonly LogCallback _callback;
    private readonly UnsetLog _unset;
    private IntPtr _library, _engine;
    private int _observed, _fifo, _hurry, _late, _callbackFailed;
    private long _firstFifo, _firstHurry, _firstLate;

    internal DecoderLogCounters(string nativeDirectory, IntPtr engine, long started)
    {
        _started = started;
        _callback = Observe;
        _library = NativeLibrary.Load(Path.Combine(nativeDirectory, "libvlc.dll"));
        try
        {
            var set = Marshal.GetDelegateForFunctionPointer<SetLog>(NativeLibrary.GetExport(_library, "libvlc_log_set"));
            _unset = Marshal.GetDelegateForFunctionPointer<UnsetLog>(NativeLibrary.GetExport(_library, "libvlc_log_unset"));
            _engine = engine;
            set(engine, _callback, IntPtr.Zero);
        }
        catch
        {
            NativeLibrary.Free(_library); _library = IntPtr.Zero;
            throw;
        }
    }

    private unsafe void Observe(IntPtr data, int level, IntPtr context, IntPtr format, IntPtr arguments)
    {
        try
        {
            Interlocked.Increment(ref _observed);
            if (format == IntPtr.Zero) return;
            var bytes = (byte*)format;
            var length = 0;
            while (length < 256 && bytes[length] != 0) length++;
            if (length == 256) return;
            var messageFormat = new ReadOnlySpan<byte>(bytes, length);
            if (messageFormat.SequenceEqual("decoder/packetizer fifo full (data not consumed quickly enough), resetting fifo!"u8))
                Count(ref _fifo, ref _firstFifo);
            else if (messageFormat.SequenceEqual("More than 11 late frames, dropping frame"u8))
                Count(ref _hurry, ref _firstHurry);
            else if (messageFormat.SequenceEqual("more than 5 seconds of late video -> dropping frame (computer too slow ?)"u8))
                Count(ref _late, ref _firstLate);
        }
        catch { Interlocked.Exchange(ref _callbackFailed, 1); }
    }

    private static void Count(ref int count, ref long first)
    {
        Interlocked.Increment(ref count);
        Interlocked.CompareExchange(ref first, Stopwatch.GetTimestamp(), 0);
    }

    internal Snapshot Read() => new(Volatile.Read(ref _observed), Volatile.Read(ref _fifo),
        Volatile.Read(ref _hurry), Volatile.Read(ref _late), Time(Volatile.Read(ref _firstFifo)),
        Time(Volatile.Read(ref _firstHurry)), Time(Volatile.Read(ref _firstLate)), Volatile.Read(ref _callbackFailed) != 0);

    private double? Time(long timestamp) => timestamp == 0 ? null : Stopwatch.GetElapsedTime(_started, timestamp).TotalMilliseconds;

    public void Dispose()
    {
        // libvlc_log_unset is a callback barrier; keep the delegate alive through it.
        if (_engine != IntPtr.Zero) { _unset(_engine); _engine = IntPtr.Zero; }
        GC.KeepAlive(_callback);
        if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
    }

    internal sealed record Snapshot(int ObservedMessages, int CompressedFifoResets, int HurryFrameDrops,
        int FiveSecondFrameDrops, double? FirstFifoResetElapsedMs, double? FirstHurryDropElapsedMs,
        double? FirstFiveSecondDropElapsedMs, bool CallbackFailed);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LogCallback(IntPtr data, int level, IntPtr context, IntPtr format, IntPtr arguments);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetLog(IntPtr instance, LogCallback callback, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UnsetLog(IntPtr instance);
}
