namespace Wisp.App;

internal enum ShiftCaptureCanaryReadiness
{
    Waiting,
    Ready,
    TimedOut
}

// Caller holds the session lock. QPC is supplied so startup fades and the
// bounded wait can be tested without polling the game or reading desktop pixels.
internal sealed class ShiftCaptureCanaryStartGate(long qpcFrequency)
{
    internal const double SettleSeconds = .25;
    internal const double MaximumWaitSeconds = 10;
    private readonly long _frequency = qpcFrequency > 0 ? qpcFrequency : throw new ArgumentOutOfRangeException(nameof(qpcFrequency));
    private long _waitStarted, _stableSince, _generation, _lastObserved;
    private bool _timedOut;

    internal void Reset()
    {
        _waitStarted = _stableSince = _generation = _lastObserved = 0;
        _timedOut = false;
    }

    internal ShiftCaptureCanaryReadiness Observe(long now, long targetGeneration, bool eligible)
    {
        if (_timedOut) return ShiftCaptureCanaryReadiness.TimedOut;
        if (now <= 0 || _lastObserved > now)
        {
            _timedOut = true;
            return ShiftCaptureCanaryReadiness.TimedOut;
        }
        _lastObserved = now;
        if (eligible && _waitStarted == 0) _waitStarted = now;
        if (_waitStarted != 0 && (double)(now - _waitStarted) / _frequency >= MaximumWaitSeconds)
        {
            _timedOut = true;
            return ShiftCaptureCanaryReadiness.TimedOut;
        }
        if (!eligible || targetGeneration <= 0)
        {
            _stableSince = _generation = 0;
            return ShiftCaptureCanaryReadiness.Waiting;
        }
        if (_generation != targetGeneration)
        {
            _generation = targetGeneration;
            _stableSince = now;
        }
        return (double)(now - _stableSince) / _frequency >= SettleSeconds
            ? ShiftCaptureCanaryReadiness.Ready
            : ShiftCaptureCanaryReadiness.Waiting;
    }
}
