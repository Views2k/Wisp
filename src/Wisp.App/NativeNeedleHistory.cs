using Wisp.Core;

namespace Wisp.App;

internal interface INativeNeedleHistorySource
{
    NativeNeedleHistoryRead CopySince(int carOrdinal, long sourceIdentity,
        ref NativeNeedleHistoryCursor cursor, Span<NativeNeedleObservation> destination);
}

internal readonly record struct NativeNeedleObservation(int CarOrdinal, uint GameTimestampMilliseconds,
    long ObservedTimestamp, double Angle, double Blur, bool SourceInvalidated);
internal readonly record struct NativeNeedleHistoryCursor(long SourceIdentity, long Sequence);
internal readonly record struct NativeNeedleHistoryRead(bool MatchesSource, bool Reset, int Count,
    long CopiedTimestamp = 0);

// The service serializes publication, invalidation and copies under its state lock.
// Each renderer keeps its own cursor; copying never consumes another HUD's history.
internal sealed class NativeNeedleHistory
{
    internal const int Capacity = 64;
    private static long _nextIdentity;
    private readonly NativeNeedleObservation[] _observations = new NativeNeedleObservation[Capacity];
    private long _sequence;
    private int _count;
    internal long SourceIdentity { get; private set; }
    internal int CarOrdinal { get; private set; }

    internal NativeNeedleHistory() => Reset(0);

    internal void Reset(int carOrdinal)
    {
        SourceIdentity = Interlocked.Increment(ref _nextIdentity);
        CarOrdinal = carOrdinal;
        _sequence = 0;
        _count = 0;
    }

    internal void Publish(NativeHudSnapshot snapshot, uint gameTimestampMilliseconds)
    {
        _observations[(int)(_sequence % Capacity)] = new(snapshot.CarOrdinal, gameTimestampMilliseconds,
            snapshot.NativeGaugeObservedTimestamp, snapshot.NativeNeedleAngleDegrees,
            snapshot.NativeNeedleBlurAmount, snapshot.NativeGaugeSourceInvalidated);
        _sequence++;
        _count = Math.Min(_count + 1, Capacity);
    }

    internal NativeNeedleHistoryRead CopySince(int carOrdinal, long sourceIdentity,
        ref NativeNeedleHistoryCursor cursor, Span<NativeNeedleObservation> destination)
    {
        if (destination.Length < Capacity) throw new ArgumentException("History buffer is too small.", nameof(destination));
        if (carOrdinal <= 0 || carOrdinal != CarOrdinal || sourceIdentity != SourceIdentity)
        {
            cursor = default;
            return new(false, true, 0);
        }

        var oldest = _sequence - _count;
        var reset = cursor.SourceIdentity != SourceIdentity || cursor.Sequence < oldest;
        var start = reset ? oldest : cursor.Sequence;
        var count = (int)(_sequence - start);
        for (var index = 0; index < count; index++)
            destination[index] = _observations[(int)((start + index) % Capacity)];
        cursor = new(SourceIdentity, _sequence);
        return new(true, reset, count);
    }
}
