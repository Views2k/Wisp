namespace Wisp.App.Supplementary;

internal readonly record struct SupplementaryResourceObservation(long SessionAgeMs, DateTimeOffset ObservedAt, double? CpuPercent,
    long WorkingSetBytes, long ManagedHeapBytes, string Platform, string? GameBuild);

// The existing two-second health sampler is the only producer. Transmission never drains these totals,
// so a dropped batch does not erase earlier observed time. No exit or network timeout extends a state.
internal sealed class SupplementarySessionRecorder(TimeProvider? clock = null)
{
    internal static SupplementarySessionRecorder Current { get; } = new();
    internal const long MaximumValue = 1_000_000_000_000;
    internal const long MaximumObservedGapMs = 10_000;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private long _startedAt, _age, _idle, _connected, _unknown, _unobserved;
    private bool _started, _enabled, _hasPrevious;
    private State _previous;
    private SupplementaryResourceObservation? _resource;
    private enum State { Idle, Connected, Unknown }

    internal void Start()
    {
        lock (_gate)
        {
            _startedAt = _clock.GetTimestamp();
            _age = _idle = _connected = _unknown = _unobserved = 0;
            _started = _enabled = true;
            _hasPrevious = false;
            _resource = null;
        }
    }

    internal bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set
        {
            lock (_gate)
            {
                if (_enabled == value) return;
                _enabled = value && _started;
                _hasPrevious = false;
                _resource = null;
            }
        }
    }

    internal void RecordSample(bool listenerRunning, bool listenerError, double? packetAgeMs,
        double? cpuPercent, long workingSetBytes, long managedHeapBytes, string platform = "unknown", string? gameBuild = null)
    {
        lock (_gate)
        {
            if (!_enabled || !_started) return;
            var elapsed = _clock.GetElapsedTime(_startedAt).TotalMilliseconds;
            if (!double.IsFinite(elapsed) || elapsed < 0 || elapsed > MaximumValue) return;
            var age = (long)Math.Floor(elapsed);
            if (age <= _age) return;
            var gap = age - _age;
            var state = listenerError || packetAgeMs is { } invalid && (!double.IsFinite(invalid) || invalid < 0)
                ? State.Unknown
                : listenerRunning && packetAgeMs is >= 0 and <= 300 ? State.Connected : State.Idle;
            if (!_hasPrevious || gap > MaximumObservedGapMs) _unobserved += gap;
            else if (state != _previous || state == State.Unknown) _unknown += gap;
            else if (state == State.Connected) _connected += gap;
            else _idle += gap;
            _age = age;
            _previous = state;
            _hasPrevious = true;
            if (workingSetBytes is >= 0 and <= MaximumValue && managedHeapBytes is >= 0 and <= MaximumValue)
                _resource = new(age, _clock.GetUtcNow(), cpuPercent is >= 0 and <= 100 && double.IsFinite(cpuPercent.Value) ? cpuPercent : null,
                    workingSetBytes, managedHeapBytes, platform is "steam" or "store" ? platform : "unknown",
                    SupplementarySchema.NumericVersion(gameBuild, app: false) ? gameBuild : null);
        }
    }

    internal SupplementarySessionSummary Snapshot()
    {
        lock (_gate)
            return new(_idle + _connected + _unknown, _idle, _connected, _unknown, _unobserved, _age);
    }

    internal SupplementaryResourceObservation? LatestResource()
    {
        lock (_gate) return _resource;
    }
}
