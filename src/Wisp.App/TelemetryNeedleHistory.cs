using Wisp.Core;

namespace Wisp.App;

internal interface ITelemetryNeedleHistorySource
{
    TelemetryNeedleHistoryRead CopyTelemetrySince(int carOrdinal, long frameReceivedTimestamp,
        ref NativeNeedleHistoryCursor cursor, Span<TelemetryNeedleObservation> destination);
}

internal readonly record struct TelemetryNeedleObservation(int CarOrdinal, uint GameTimestampMilliseconds,
    long ReceivedTimestamp, double Rpm);
internal readonly record struct TelemetryNeedleHistoryRead(bool Initialized, bool MatchesFrame, bool Reset,
    int Count, long CopiedTimestamp = 0);

// Accepted telemetry has its own bounded mailbox. UI publication never consumes
// it, and each render/motion worker advances an independent cursor.
internal sealed class TelemetryNeedleHistory
{
    internal const int Capacity = 64;
    private readonly object _gate = new();
    private readonly TelemetryNeedleObservation[] _samples = new TelemetryNeedleObservation[Capacity];
    private long _epoch = 1, _sequence, _firstReceipt, _lastReceipt;
    private uint _lastGameTimestamp;
    private int _count, _carOrdinal;
    private float _maximumRpm;
    private bool _initialized, _closed;

    internal void Publish(VehicleState state)
    {
        if (state.ReceivedTimestamp is not > 0) return;
        lock (_gate)
        {
            var received = state.ReceivedTimestamp.Value;
            if (_closed || received <= _lastReceipt) return;
            _initialized = true;
            _lastReceipt = received;
            // The controller owns the existing brief race-off hysteresis. Do
            // not turn a transient packet into a visible needle disappearance.
            if (!state.IsRaceOn) return;
            if (state.IsElectric || state.CarOrdinal <= 0 ||
                !double.IsFinite(state.EngineRpm) || state.EngineRpm < 0)
            {
                ResetCore(0, received);
                return;
            }
            if (_carOrdinal != state.CarOrdinal ||
                BitConverter.SingleToInt32Bits(_maximumRpm) != BitConverter.SingleToInt32Bits(state.EngineMaximumRpm) ||
                (_count > 0 && unchecked(state.GameTimestampMilliseconds - _lastGameTimestamp) > 250))
                ResetCore(state.CarOrdinal, received);
            _maximumRpm = state.EngineMaximumRpm;
            _lastGameTimestamp = state.GameTimestampMilliseconds;
            _samples[(int)(_sequence % Capacity)] = new(state.CarOrdinal, state.GameTimestampMilliseconds,
                received, state.EngineRpm);
            _sequence++;
            _count = Math.Min(_count + 1, Capacity);
        }
    }

    internal void Reset(long timestamp, bool close = false, bool onlyIfActive = false)
    {
        lock (_gate)
        {
            if (onlyIfActive && _carOrdinal == 0) return;
            _initialized = true;
            _lastReceipt = Math.Max(_lastReceipt, timestamp);
            _closed |= close;
            ResetCore(0, _lastReceipt);
        }
    }

    private void ResetCore(int carOrdinal, long firstReceipt)
    {
        _epoch++;
        _sequence = 0;
        _count = 0;
        _carOrdinal = carOrdinal;
        _firstReceipt = firstReceipt;
    }

    internal TelemetryNeedleHistoryRead CopySince(int carOrdinal, long frameReceivedTimestamp,
        ref NativeNeedleHistoryCursor cursor, Span<TelemetryNeedleObservation> destination)
    {
        if (destination.Length < Capacity) throw new ArgumentException("History buffer is too small.", nameof(destination));
        lock (_gate)
        {
            if (!_initialized) return default;
            if (_closed || carOrdinal <= 0 || carOrdinal != _carOrdinal || frameReceivedTimestamp < _firstReceipt)
            {
                var reset = cursor.SourceIdentity != _epoch || cursor.Sequence != 0;
                cursor = new(_epoch, 0);
                return new(true, false, reset, 0);
            }
            var oldest = _sequence - _count;
            var changed = cursor.SourceIdentity != _epoch || cursor.Sequence < oldest;
            var start = changed ? oldest : cursor.Sequence;
            var count = (int)(_sequence - start);
            for (var index = 0; index < count; index++)
                destination[index] = _samples[(int)((start + index) % Capacity)];
            cursor = new(_epoch, _sequence);
            return new(true, true, changed, count);
        }
    }
}
