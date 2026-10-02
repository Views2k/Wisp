using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace Wisp.App.Tunes;

// Per-request read budget. Nothing here writes to, invokes, or scans the game.
internal sealed class NativeTuneRead(IReadOnlyProcessMemory memory, CancellationToken cancellationToken,
    int maximumBytes = 96 * 1024, TimeSpan? timeout = null)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(2);
    private readonly List<(ulong Address, byte[] Bytes)> _observations = [];
    private int _bytes;

    internal byte[] Bytes(ulong address, int count, bool remember = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count <= 0 || count > 4096 || count > maximumBytes - _bytes ||
            !NativeHudProcessMemory.IsValidReadSpan(address, (ulong)count) ||
            Stopwatch.GetElapsedTime(_started) > _timeout)
            throw new InvalidDataException("Tune read exceeded its bounds.");
        _bytes += count;
        var bytes = new byte[count];
        if (!memory.TryReadBytes(address, bytes))
            throw new InvalidDataException("The current car could not be read.");
        if (remember) _observations.Add((address, bytes));
        return bytes;
    }

    internal uint UInt32(ulong address) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(address, 4));
    internal int Int32(ulong address) => BinaryPrimitives.ReadInt32LittleEndian(Bytes(address, 4));
    internal ulong UInt64(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(address, 8));
    internal ulong Pointer(ulong address)
    {
        var value = UInt64(address);
        if (!NativeHudProcessMemory.IsValidReadSpan(value, 8) || value % 8 != 0)
            throw new InvalidDataException("The current car structure is unavailable.");
        return value;
    }
    internal float Single(ulong address)
    {
        var value = BitConverter.Int32BitsToSingle(Int32(address));
        if (!float.IsFinite(value)) throw new InvalidDataException("The tune contains an invalid value.");
        return value;
    }
    internal double Double(ulong address)
    {
        var value = BitConverter.Int64BitsToDouble(unchecked((long)UInt64(address)));
        if (!double.IsFinite(value)) throw new InvalidDataException("The tune contains an invalid conversion.");
        return value;
    }

    internal void VerifyStable()
    {
        foreach (var (address, bytes) in _observations)
            if (!Bytes(address, bytes.Length, remember: false).AsSpan().SequenceEqual(bytes))
                throw new TuneChangedException();
    }
}

internal sealed class TuneChangedException : IOException
{
    internal TuneChangedException() : base("The car or tune changed while reading. Refresh and try again.") { }
}
