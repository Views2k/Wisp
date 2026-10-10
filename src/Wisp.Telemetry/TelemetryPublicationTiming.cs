using Wisp.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Wisp.Telemetry.Tests")]

namespace Wisp.Telemetry;

public readonly record struct TelemetryPublicationTiming(long Received, long ParseStarted, long Parsed, long Published)
{
    public bool IsOrdered => Received > 0 && ParseStarted >= Received && Parsed >= ParseStarted && Published >= Parsed;
}

// One receive writer, bounded readers. The UI tries each of eight slots once; it never spins or waits.
// State references are local correlation keys and never leave this process.
internal sealed class TelemetryPublicationTimingLedger
{
    private readonly Slot[] _slots = Enumerable.Range(0, 8).Select(_ => new Slot()).ToArray();
    private long _sequence;

    internal void Record(VehicleState state, TelemetryPublicationTiming timing)
    {
        if (!timing.IsOrdered || state.ReceivedTimestamp != timing.Received) return;
        var sequence = Interlocked.Increment(ref _sequence);
        var slot = _slots[(int)(sequence & 7)];
        Interlocked.Exchange(ref slot.Version, sequence * 2 - 1);
        Volatile.Write(ref slot.State, state);
        slot.Received = timing.Received; slot.ParseStarted = timing.ParseStarted;
        slot.Parsed = timing.Parsed; slot.Published = timing.Published;
        Volatile.Write(ref slot.Version, sequence * 2);
    }

    internal bool TryGet(VehicleState state, out TelemetryPublicationTiming timing)
    {
        foreach (var slot in _slots)
        {
            var version = Volatile.Read(ref slot.Version);
            if (version == 0 || (version & 1) != 0 || !ReferenceEquals(Volatile.Read(ref slot.State), state)) continue;
            var candidate = new TelemetryPublicationTiming(slot.Received, slot.ParseStarted, slot.Parsed, slot.Published);
            Thread.MemoryBarrier();
            if (version != Volatile.Read(ref slot.Version) || !candidate.IsOrdered || state.ReceivedTimestamp != candidate.Received) continue;
            timing = candidate;
            return true;
        }
        timing = default;
        return false;
    }

    private sealed class Slot
    {
        internal VehicleState? State;
        internal long Version, Received, ParseStarted, Parsed, Published;
    }
}
