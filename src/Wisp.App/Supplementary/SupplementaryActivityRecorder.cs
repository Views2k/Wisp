namespace Wisp.App.Supplementary;

// The identity fields are local continuity guards only. They are never part of the reporting DTO.
internal readonly record struct SupplementaryActivitySample(long ReceivedTimestamp, uint GameTimestampMilliseconds,
    int CarOrdinal, int Drivetrain, double SpeedMetersPerSecond, bool IsRaceOn, bool GameplayVisible);

// Existing UI updates feed this O(1) accumulator at most ten times a second. It stores no sample history.
// Distance is a trapezoidal speed estimate over accepted intervals, not the game's odometer.
internal sealed class SupplementaryActivityRecorder(TimeProvider? clock = null)
{
    internal static SupplementaryActivityRecorder Current { get; } = new();
    internal const long MaximumValue = 1_000_000_000_000;
    internal const int SampleIntervalMs = 100, MaximumIntervalMs = 250;
    internal const double MovingThresholdMetersPerSecond = 1, MaximumSpeedMetersPerSecond = 500;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private long _startedAt, _age, _moving, _stationary, _unknown, _unobserved, _discarded, _peak;
    private double _distance;
    private bool _started, _enabled, _hasPrevious;
    private SupplementaryActivitySample? _previous;
    private string _state = "unknown", _previousState = "unknown";

    internal void Start()
    {
        lock (_gate)
        {
            _startedAt = _clock.GetTimestamp();
            _age = _moving = _stationary = _unknown = _unobserved = _discarded = _peak = 0;
            _distance = 0;
            _started = _enabled = true;
            _hasPrevious = false;
            _previous = null;
            _state = _previousState = "unknown";
        }
    }

    internal bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set
        {
            lock (_gate)
            {
                if (_enabled == value) return;
                _enabled = value && _started;
                _hasPrevious = false;
                _previous = null;
                _state = _previousState = "unknown";
            }
        }
    }

    internal void Observe(SupplementaryActivitySample? sample)
    {
        if (!Enabled) return;
        var now = _clock.GetTimestamp();
        var elapsed = _clock.GetElapsedTime(_startedAt, now).TotalMilliseconds;
        if (!double.IsFinite(elapsed) || elapsed < 0 || elapsed > MaximumValue) return;
        var age = (long)Math.Floor(elapsed);
        if (age - Volatile.Read(ref _age) < SampleIntervalMs) return;
        lock (_gate)
        {
            if (!_enabled || age - _age < SampleIntervalMs) return;
            var interval = age - _age;
            var currentState = Classify(sample, now);
            _state = "unknown";
            if (!_hasPrevious || interval > MaximumIntervalMs)
            {
                _unobserved += interval;
                _discarded++;
            }
            else if (currentState == "unknown" || _previousState != currentState ||
                !Continuous(_previous, sample, interval))
            {
                _unknown += interval;
                _discarded++;
            }
            else
            {
                var priorSpeed = _previous!.Value.SpeedMetersPerSecond;
                var speed = sample!.Value.SpeedMetersPerSecond;
                var distance = (priorSpeed + speed) * .5 * interval; // m/s * ms = millimetres.
                if (_distance + distance > MaximumValue)
                {
                    _unknown += interval;
                    _discarded++;
                    currentState = "unknown";
                }
                else
                {
                    if (currentState == "moving") _moving += interval;
                    else _stationary += interval;
                    _distance += distance;
                    _peak = Math.Max(_peak, (long)Math.Floor(Math.Max(priorSpeed, speed) * 1000));
                    _state = currentState;
                }
            }
            _age = age;
            _previous = sample;
            _previousState = currentState;
            _hasPrevious = true;
        }
    }

    private string Classify(SupplementaryActivitySample? sample, long now)
    {
        if (sample is not { IsRaceOn: true, GameplayVisible: true } value ||
            !double.IsFinite(value.SpeedMetersPerSecond) || value.SpeedMetersPerSecond is < 0 or > MaximumSpeedMetersPerSecond ||
            value.ReceivedTimestamp > now) return "unknown";
        var packetAge = _clock.GetElapsedTime(value.ReceivedTimestamp, now).TotalMilliseconds;
        if (!double.IsFinite(packetAge) || packetAge is < 0 or > 300) return "unknown";
        return value.SpeedMetersPerSecond >= MovingThresholdMetersPerSecond ? "moving" : "stationary";
    }

    private bool Continuous(SupplementaryActivitySample? previous, SupplementaryActivitySample? current, long interval)
    {
        if (previous is not { } a || current is not { } b || a.CarOrdinal != b.CarOrdinal || a.Drivetrain != b.Drivetrain ||
            b.ReceivedTimestamp <= a.ReceivedTimestamp) return false;
        var receivedMs = _clock.GetElapsedTime(a.ReceivedTimestamp, b.ReceivedTimestamp).TotalMilliseconds;
        var gameMs = unchecked(b.GameTimestampMilliseconds - a.GameTimestampMilliseconds);
        return receivedMs is > 0 and <= MaximumIntervalMs && gameMs is > 0 and <= MaximumIntervalMs &&
            Math.Abs(receivedMs - interval) <= 50 && Math.Abs(gameMs - receivedMs) <= 50 &&
            Math.Abs(b.SpeedMetersPerSecond - a.SpeedMetersPerSecond) <= 100 * interval / 1000d;
    }

    internal SupplementaryActivitySummary Snapshot()
    {
        lock (_gate)
        {
            var age = _clock.GetElapsedTime(_startedAt).TotalMilliseconds;
            var state = _enabled && age >= _age && age - _age <= 500 ? _state : "unknown";
            return new(_moving, _stationary, _unknown, _unobserved, _age, (long)Math.Floor(_distance), _peak, _discarded, state);
        }
    }
}
