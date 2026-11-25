using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace Wisp.App.Tunes;

// Stable FACILITY_ITF codes carried by existing Tune incidents and copied read details.
internal enum TuneReadCheck : uint
{
    CarOrdinal = 0x8004A601,
    Drivetrain = 0x8004A602,
    GearCount = 0x8004A603,
    GearRatio = 0x8004A604,
    FinalDrive = 0x8004A605,
    WheelIdentity = 0x8004A606,
    DescriptorCar = 0x8004A607,
    SpringOwner = 0x8004A608,
    SpringScale = 0x8004A609,
    UnitProfile = 0x8004A60A,
    UnitPreference = 0x8004A60B,
    PartIndex = 0x8004A60C,
    CarVector = 0x8004A60D,
    CarAddress = 0x8004A60E,
    CarProvider = 0x8004A60F,
    ConversionReady = 0x8004A610,
    UnitOverride = 0x8004A611,
    UnitIndex = 0x8004A612,
    UnitIdentity = 0x8004A613,
    UnitCallback = 0x8004A614,
    AssetStreamFlag = 0x8004A615,
    AssetStreamLengthBelowMinimum = 0x8004A616,
    AssetStreamLengthAboveMaximum = 0x8004A617,
    AssetStreamLengthAlignment = 0x8004A618,
    AssetStreamChunkBelowMinimum = 0x8004A619,
    AssetStreamChunkAboveMaximum = 0x8004A61A,
    AssetStreamAllocationBelowLength = 0x8004A61B,
    AssetStreamAllocationAboveMaximum = 0x8004A61C,
    AssetStreamVectorBelowMinimum = 0x8004A61D,
    AssetStreamVectorAboveMaximum = 0x8004A61E,
    AssetStreamVectorAlignment = 0x8004A61F,
}

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

    // A check that fails while bytes read so far are changing describes a transition (car or session
    // change, loading), reported as such; only a stable mismatch is a failure of the reader.
    internal void Require(bool condition, TuneReadCheck check)
    {
        if (condition) return;
        VerifyStable();
        throw new InvalidDataException("The current car's tune is not available yet.") { HResult = unchecked((int)check) };
    }

    // A stable check that fails because the game has not finished setting up what the reader needs (an
    // actor without its provider, a locale without an active unit profile) is not a mismatch either: the
    // player refreshes once the game is ready. The capture service reports the state once it persists.
    internal void RequireReady(bool condition, TuneReadCheck check, long observed)
    {
        if (condition) return;
        VerifyStable();
        throw new TuneNotReadyException(check, observed);
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

internal sealed class TuneNotReadyException : IOException
{
    internal TuneNotReadyException(TuneReadCheck check, long observed) : base("The current car's tune is not available yet.")
    {
        if (!Enum.IsDefined(check)) throw new ArgumentOutOfRangeException(nameof(check));
        Check = check;
        Observed = observed;
        HResult = unchecked((int)check);
    }
    internal TuneReadCheck Check { get; }
    // The value the check rejected (a profile index, a count of actors), for the copied read details.
    internal long Observed { get; }
}
