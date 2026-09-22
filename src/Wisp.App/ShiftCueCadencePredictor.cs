using System.Diagnostics;
using Wisp.Core;

namespace Wisp.App;

// Compensates for one measured telemetry interval, not driver or transmission latency.
// The caller owns eligibility and must reset this history when the native configuration changes.
internal sealed class ShiftCueCadencePredictor
{
    private readonly double _frequency;
    private readonly long _maximumAge;
    private int _car, _gear;
    private long _received, _positiveInterval, _gameAdvancedAt;
    private uint _gameTime;
    private float _rpm;
    private byte _accelerator;
    private double _target, _positiveRate;
    private double _predictionRate;
    private long _predictionInterval;

    internal ShiftCueCadencePredictor(long? frequency = null)
    {
        var ticksPerSecond = frequency ?? Stopwatch.Frequency;
        if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _frequency = ticksPerSecond;
        _maximumAge = Math.Max(1L, (long)(ticksPerSecond * .15));
    }

    internal bool ObserveAndPredict(VehicleState state, long now, double targetRpm)
    {
        if (state.ReceivedTimestamp is not long received || received <= 0 || now < received ||
            now - received > _maximumAge || !double.IsFinite(targetRpm) || targetRpm is <= 0 or > 30_000 ||
            !state.IsRaceOn || state.IsElectric || state.CarOrdinal <= 0 || (int)state.Gear is < 1 or > 10 ||
            state.Accelerator == 0 || state.Brake > 1 || !float.IsFinite(state.GroundSpeedMetersPerSecond) ||
            state.GroundSpeedMetersPerSecond < .5f || !float.IsFinite(state.EngineRpm) ||
            state.EngineRpm is < 0 or > 30_000)
        {
            Reset();
            return false;
        }

        if (_received == 0 || _car != state.CarOrdinal || _gear != (int)state.Gear ||
            _target != targetRpm)
        {
            Seed(state, received, targetRpm, now);
            return false;
        }

        // Reusing the same UI sample is not another interval and cannot extend prediction.
        if (received == _received && state.GameTimestampMilliseconds == _gameTime && state.EngineRpm == _rpm)
            return false;

        var interval = received - _received;
        if (interval <= 0 || state.GameTimestampMilliseconds < _gameTime ||
            state.Accelerator < _accelerator)
        {
            Seed(state, received, targetRpm, now);
            return false;
        }

        // FH6 can send changed physical observations with the same game timestamp.
        // Receipt QPC measures their interval; a genuinely frozen game clock still
        // expires even if new packets keep arriving with changing values.
        if (state.GameTimestampMilliseconds > _gameTime) _gameAdvancedAt = now;
        var rate = (state.EngineRpm - _rpm) * _frequency / interval;
        var previousRate = _positiveRate;
        var previousInterval = _positiveInterval;
        _received = received;
        _gameTime = state.GameTimestampMilliseconds;
        _rpm = state.EngineRpm;
        // Increasing throttle does not invalidate already measured acceleration.
        // Keep the conservative two-slope check; a lift starts new history.
        _accelerator = state.Accelerator;
        _predictionRate = 0;
        _predictionInterval = 0;

        // Discontinuities are not acceleration evidence. Raw crossing detection remains independent.
        if (interval > _maximumAge || now < _gameAdvancedAt || now - _gameAdvancedAt > _maximumAge ||
            rate is <= 0 or > 100_000 || previousInterval > 0 &&
            (interval > previousInterval * 2d || previousInterval > interval * 2d))
        {
            _positiveRate = 0;
            _positiveInterval = 0;
            return false;
        }

        _positiveRate = rate;
        _positiveInterval = interval;
        if (previousRate <= 0 || rate > previousRate * 2 || previousRate > rate * 2)
            return false;

        _predictionRate = Math.Min(rate, previousRate);
        _predictionInterval = Math.Min(interval, previousInterval);
        return TryPredict(now, out _);
    }

    // Queries the latest raw observation without treating a UI refresh as a new interval.
    internal bool TryPredict(long now, out long receivedTimestamp)
    {
        receivedTimestamp = 0;
        var age = now - _received;
        if (_received <= 0 || _predictionRate <= 0 || _predictionInterval <= 0 ||
            age < 0 || age > _predictionInterval || now < _gameAdvancedAt ||
            now - _gameAdvancedAt > _maximumAge || _rpm >= _target) return false;
        var horizon = Math.Min(_maximumAge, _predictionInterval + (double)age) / _frequency;
        if (_rpm + _predictionRate * horizon < _target) return false;
        receivedTimestamp = _received;
        return true;
    }

    internal void Reset()
    {
        _car = _gear = 0;
        _received = _positiveInterval = _gameAdvancedAt = 0;
        _gameTime = 0;
        _rpm = 0;
        _accelerator = 0;
        _target = _positiveRate = 0;
        _predictionRate = 0;
        _predictionInterval = 0;
    }

    private void Seed(VehicleState state, long received, double targetRpm, long now)
    {
        _car = state.CarOrdinal;
        _gear = (int)state.Gear;
        _received = received;
        _gameTime = state.GameTimestampMilliseconds;
        _gameAdvancedAt = now;
        _rpm = state.EngineRpm;
        _accelerator = state.Accelerator;
        _target = targetRpm;
        _positiveRate = 0;
        _positiveInterval = 0;
        _predictionRate = 0;
        _predictionInterval = 0;
    }
}
