using System.Diagnostics;
using Wisp.Core;

namespace Wisp.App;

// Preserves an observed crossing while normal HUD publication keeps only the newest state.
// The UI owns watch eligibility; ingress never reads metadata or changes the view model.
internal sealed class ShiftCueCrossingTracker
{
    private readonly object _gate = new();
    private readonly long _maximumAge;
    private readonly ShiftCueCadencePredictor _cadence;
    private long _epoch;
    private bool _armed;
    private string _fingerprint = "";
    private int _car, _gear;
    private double _target, _releaseRpm;
    private long _minimumSampleTimestamp, _expiresAt, _lastReceivedTimestamp;
    private uint? _lastGameTime;
    private bool _crossingCaptured;
    private bool _belowApproach;
    private long _pendingTimestamp;

    internal ShiftCueCrossingTracker(long? frequency = null)
    {
        var ticksPerSecond = frequency ?? Stopwatch.Frequency;
        if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _maximumAge = Math.Max(1L, (long)(ticksPerSecond * .15));
        _cadence = new(ticksPerSecond);
    }

    internal long Arm(string fingerprint, int car, int gear, double target,
        long minSampleTimestamp, long expiresAt, long now, double? releaseRpm = null,
        VehicleState? initialState = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(fingerprint) || car <= 0 || gear is < 1 or > 10 ||
                !double.IsFinite(target) || target <= 0 || now <= 0 ||
                releaseRpm is { } release && (!double.IsFinite(release) || release <= 0 || release > target) ||
                minSampleTimestamp <= 0 || minSampleTimestamp > now || expiresAt <= now ||
                now - minSampleTimestamp > _maximumAge)
            {
                ResetCore();
                return 0;
            }

            var sameWatch = _armed && now <= _expiresAt &&
                string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal) &&
                _car == car && _gear == gear && _target == target;
            if (!sameWatch)
            {
                ResetCore();
                _armed = true;
                _fingerprint = fingerprint;
                _car = car;
                _gear = gear;
                _target = target;
                _minimumSampleTimestamp = minSampleTimestamp;
                // The first packet can precede creation of its UI-validated watch.
                // Seed it once; same-watch UI updates never replace raw cadence history.
                if (initialState is { } initial && initial.ReceivedTimestamp == minSampleTimestamp &&
                    initial.CarOrdinal == car && (int)initial.Gear == gear)
                    _cadence.ObserveAndPredict(initial, minSampleTimestamp, target);
            }

            // A fresh UI selection may be newer than an unconsumed crossing.
            // Refresh the expiry without advancing that watch's original sample floor.
            _expiresAt = expiresAt;
            _releaseRpm = releaseRpm ?? target * .85;
            return _epoch;
        }
    }

    internal void Observe(VehicleState? state)
    {
        lock (_gate)
        {
            if (!_armed) return;
            if (state is null || state.ReceivedTimestamp is not long received || received <= 0)
            {
                ResetCore();
                return;
            }

            if (received < _minimumSampleTimestamp) return;
            if (received > _expiresAt || received <= _lastReceivedTimestamp ||
                _lastReceivedTimestamp > 0 && received - _lastReceivedTimestamp > _maximumAge ||
                _lastGameTime is uint previousGameTime && state.GameTimestampMilliseconds < previousGameTime ||
                !state.IsRaceOn || state.IsElectric || state.CarOrdinal != _car || (int)state.Gear != _gear ||
                state.Accelerator == 0 || state.Brake > 1 ||
                !float.IsFinite(state.GroundSpeedMetersPerSecond) || state.GroundSpeedMetersPerSecond < .5f ||
                !float.IsFinite(state.EngineRpm) || state.EngineRpm < 0)
            {
                ResetCore();
                return;
            }

            _lastReceivedTimestamp = received;
            _lastGameTime = state.GameTimestampMilliseconds;
            _cadence.ObserveAndPredict(state, received, _target);
            if (state.EngineRpm < _releaseRpm)
            {
                if (!_belowApproach)
                {
                    AdvanceEpoch();
                    _crossingCaptured = false;
                    _pendingTimestamp = 0;
                    _cadence.Reset();
                }
                _belowApproach = true;
                return;
            }
            _belowApproach = false;

            if (!_crossingCaptured && state.EngineRpm >= _target)
            {
                _crossingCaptured = true;
                _pendingTimestamp = received;
            }
        }
    }

    internal bool TryConsume(long epoch, long now, out long observedTimestamp)
    {
        lock (_gate)
        {
            observedTimestamp = 0;
            if (!_armed || epoch == 0 || epoch != _epoch) return false;
            if (now <= 0 || now > _expiresAt ||
                _pendingTimestamp > 0 && now >= _pendingTimestamp && now - _pendingTimestamp > _maximumAge)
            {
                ResetCore();
                return false;
            }

            // Ingress can run after the caller captured 'now'; leave its newer marker pending.
            if (_pendingTimestamp <= 0 || now < _pendingTimestamp) return false;
            observedTimestamp = _pendingTimestamp;
            _pendingTimestamp = 0;
            return true;
        }
    }

    internal bool TryPredict(long epoch, long now, out long receivedTimestamp)
    {
        lock (_gate)
        {
            receivedTimestamp = 0;
            if (!_armed || epoch == 0 || epoch != _epoch || _belowApproach ||
                now <= 0 || now > _expiresAt) return false;
            // Unlike an observed crossing, a projection is never latched here.
            // New contrary input replaces it; its age is at most one measured interval.
            return _cadence.TryPredict(now, out receivedTimestamp);
        }
    }

    internal void Reset()
    {
        lock (_gate) ResetCore();
    }

    private void ResetCore()
    {
        AdvanceEpoch();
        _armed = false;
        _fingerprint = "";
        _car = _gear = 0;
        _target = _releaseRpm = 0;
        _minimumSampleTimestamp = _expiresAt = _lastReceivedTimestamp = 0;
        _lastGameTime = null;
        _crossingCaptured = false;
        _belowApproach = false;
        _pendingTimestamp = 0;
        _cadence.Reset();
    }

    private void AdvanceEpoch() => _epoch = _epoch == long.MaxValue ? 1 : _epoch + 1;
}
