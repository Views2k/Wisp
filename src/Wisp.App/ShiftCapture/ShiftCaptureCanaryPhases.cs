namespace Wisp.App;

internal enum ShiftCaptureCanaryPhaseStatus
{
    Inactive,
    Running,
    Completed,
    TimedOut,
    Cancelled
}

internal readonly record struct ShiftCaptureCanaryPhaseToken(int Attempt, long TargetGeneration, int Phase, long StartedQpc);
internal sealed record ShiftCaptureCanaryPhaseWindow(int Phase, long StartedQpc, long SamplesClosedQpc,
    long EndedQpc, int AcceptedReads, string EndReason);

// One caller-held session lock protects visual phase selection and read assignment.
// Phase windows describe requested cue states, not compositor or photon timestamps.
internal sealed class ShiftCaptureCanaryPhases
{
    internal const int PhaseCount = 5, MinimumReads = 5;
    internal const double MinimumPhaseSeconds = 2, SettleSeconds = .25, TailSeconds = .15, MaximumPhaseSeconds = 8;
    private readonly long _frequency, _minimumTicks, _settleTicks, _tailTicks, _maximumTicks;
    private readonly List<ShiftCaptureCanaryPhaseWindow> _windows = new(PhaseCount);
    private int _attempt, _phase, _samples;
    private long _targetGeneration, _phaseStarted, _closedAt, _advanceAt, _lastQpc, _lastReadFinished;

    internal ShiftCaptureCanaryPhases(long qpcFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        _frequency = qpcFrequency;
        _minimumTicks = checked((long)Math.Ceiling(qpcFrequency * MinimumPhaseSeconds));
        _settleTicks = checked((long)Math.Ceiling(qpcFrequency * SettleSeconds));
        _tailTicks = checked((long)Math.Ceiling(qpcFrequency * TailSeconds));
        _maximumTicks = checked((long)Math.Ceiling(qpcFrequency * MaximumPhaseSeconds));
    }

    internal ShiftCaptureCanaryPhaseStatus Status { get; private set; }
    internal ShiftCaptureCanaryPhaseToken? Current => Status == ShiftCaptureCanaryPhaseStatus.Running
        ? new(_attempt, _targetGeneration, _phase, _phaseStarted) : null;
    internal int AcceptedReads => _samples;
    internal bool FlashOn => Status == ShiftCaptureCanaryPhaseStatus.Running && _phase is 1 or 3;
    internal long QpcFrequency => _frequency;
    internal ShiftCaptureCanaryPhaseWindow[] Windows => _windows.ToArray();

    internal void Reset()
    {
        Status = ShiftCaptureCanaryPhaseStatus.Inactive;
        _windows.Clear();
        _attempt = _phase = _samples = 0;
        _targetGeneration = _phaseStarted = _closedAt = _advanceAt = _lastQpc = _lastReadFinished = 0;
    }

    internal void Start(long now, int attempt, long targetGeneration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(now);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetGeneration);
        Reset();
        _attempt = attempt;
        _targetGeneration = targetGeneration;
        _phaseStarted = _lastQpc = now;
        Status = ShiftCaptureCanaryPhaseStatus.Running;
    }

    internal void Advance(long now)
    {
        if (Status != ShiftCaptureCanaryPhaseStatus.Running) return;
        if (now < _lastQpc || now - _phaseStarted >= _maximumTicks)
        {
            Finish(Math.Max(now, _lastQpc), ShiftCaptureCanaryPhaseStatus.TimedOut);
            return;
        }
        _lastQpc = now;
        if (_advanceAt != 0 && now >= _advanceAt)
        {
            _windows.Add(new(_phase, _phaseStarted, _closedAt, now, _samples, "Completed"));
            if (++_phase == PhaseCount)
            {
                Status = ShiftCaptureCanaryPhaseStatus.Completed;
                return;
            }
            _phaseStarted = now;
            _closedAt = _advanceAt = _lastReadFinished = 0;
            _samples = 0;
        }
        TryCloseSamples(now);
    }

    internal bool TryObserve(ShiftCaptureCanaryPhaseToken token, long readStarted, long readFinished, long now)
    {
        if (Status != ShiftCaptureCanaryPhaseStatus.Running || Current != token || _closedAt != 0 ||
            now < _lastQpc || readFinished > now || readFinished < readStarted ||
            readStarted < _phaseStarted + _settleTicks || readStarted <= _lastReadFinished ||
            now - _phaseStarted >= _maximumTicks) return false;
        _lastQpc = now;
        _lastReadFinished = readFinished;
        _samples++;
        TryCloseSamples(now);
        return true;
    }

    internal void Cancel(long now)
    {
        if (Status == ShiftCaptureCanaryPhaseStatus.Running)
            Finish(Math.Max(now, _lastQpc), ShiftCaptureCanaryPhaseStatus.Cancelled);
    }

    private void TryCloseSamples(long now)
    {
        if (_closedAt != 0 || _samples < MinimumReads || now - _phaseStarted < _minimumTicks - _tailTicks) return;
        _closedAt = now;
        _advanceAt = checked(now + _tailTicks);
    }

    private void Finish(long now, ShiftCaptureCanaryPhaseStatus status)
    {
        _windows.Add(new(_phase, _phaseStarted, _closedAt, now, _samples, status.ToString()));
        Status = status;
    }
}
