using Wisp.Core;

namespace Wisp.App;

// Correlates a parked button check with subsequent telemetry. This establishes
// observed binding behavior, not the game's command-consumption timestamp.
internal sealed class ShiftCaptureBindingCheck
{
    private readonly object _gate = new();
    private readonly long _frequency;
    private readonly Queue<ObservedState> _history = new();
    private VehicleState? _lastState;
    private long _lastStateQpc;
    private Pending? _pending;
    private bool _upVerified, _downVerified;

    internal ShiftCaptureBindingCheck(long qpcFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        _frequency = qpcFrequency;
    }

    internal bool UpVerified { get { lock (_gate) return _upVerified; } }
    internal bool DownVerified { get { lock (_gate) return _downVerified; } }

    internal void ObserveState(VehicleState state, long qpc)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (qpc <= 0 || (_lastState is not null && qpc < _lastStateQpc))
            {
                ClearPendingAndBaseline();
                return;
            }
            _lastState = state;
            _lastStateQpc = qpc;
            _history.Enqueue(new(state, qpc));
            while (_history.Count > 32 || (_history.TryPeek(out var oldest) && !Within(qpc, oldest.Qpc, 2)))
                _history.Dequeue();
            ApplyResult(state, qpc);
        }
    }

    internal void ObserveButton(ShiftCaptureButtonEvent button, long qpc)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (!button.Pressed) return;
        lock (_gate)
        {
            // A second press makes the previous command/result pairing ambiguous.
            _pending = null;
            var direction = (button.Button, button.Action) switch
            {
                ("B", "upshift") => 1,
                ("X", "downshift") => -1,
                _ => 0
            };
            if (direction == 0 || !TryBracket(button, qpc, out var earliest, out var latest, out var measured)) return;
            var baseline = measured
                ? _history.LastOrDefault(observation => observation.Qpc <= earliest)
                : _lastState is { } last ? new ObservedState(last, _lastStateQpc) : null;
            if (baseline is null || !IsParkedCombustion(baseline.State) ||
                !Within(earliest, baseline.Qpc, .150) || !Within(latest, baseline.Qpc, .150)) return;
            var state = baseline.State;
            var gear = (int)state.Gear;
            var next = gear + direction;
            if (next is < 1 or > 10) return;
            _pending = new Pending(state.CarOrdinal, gear, next, state.GameTimestampMilliseconds, earliest);
            if (measured)
            {
                // Telemetry can reach this observer before the poll that discovers the
                // button edge. Replay that bounded interval, not an invented press time.
                foreach (var observation in _history)
                    if (_pending is not null && observation.Qpc > earliest && observation.Qpc <= latest)
                        ApplyResult(observation.State, observation.Qpc);
            }
        }
    }

    private void ApplyResult(VehicleState state, long qpc)
    {
        if (_pending is not { } pending) return;
        var gear = (int)state.Gear;
        if (!IsParkedCombustion(state, allowNeutral: true) || state.CarOrdinal != pending.CarOrdinal ||
            (gear != pending.FromGear && gear != pending.ToGear && state.Gear != TransmissionGear.Neutral))
        {
            _pending = null;
            return;
        }
        // The input worker can overtake queued telemetry. A pre-edge packet in
        // the original gear preserves the baseline; an earlier shift does not.
        if (qpc <= pending.EarliestQpc)
        {
            if (gear != pending.FromGear) _pending = null;
            return;
        }
        if (!Within(qpc, pending.EarliestQpc, 2) ||
            (pending.NeutralStartedQpc != 0 && !Within(qpc, pending.NeutralStartedQpc, .5)))
        {
            _pending = null;
            return;
        }
        // FH6 can briefly report neutral between the two engaged gears.
        // It is neither a valid button baseline nor a completed binding check.
        if (state.Gear == TransmissionGear.Neutral)
        {
            if (pending.NeutralStartedQpc == 0) _pending = pending with { NeutralStartedQpc = qpc };
            return;
        }
        // Unsigned game-clock wrap is valid; repeated or backwards states are not evidence.
        if (gear != pending.ToGear || unchecked((int)(state.GameTimestampMilliseconds - pending.GameTimestamp)) <= 0)
            return;
        if (pending.ToGear > pending.FromGear) _upVerified = true;
        else _downVerified = true;
        _pending = null;
    }

    private bool TryBracket(ShiftCaptureButtonEvent button, long qpc,
        out long earliest, out long latest, out bool measured)
    {
        measured = button.PreviousPollStartedQpc != 0 || button.PreviousPollCompletedQpc != 0 ||
            button.PollStartedQpc != 0 || button.PollCompletedQpc != 0 ||
            button.EarliestObservedEdgeQpc != 0 || button.LatestObservedEdgeQpc != 0;
        earliest = measured ? button.EarliestObservedEdgeQpc > 0
            ? button.EarliestObservedEdgeQpc : button.PreviousPollStartedQpc : qpc;
        latest = measured ? button.LatestObservedEdgeQpc > 0
            ? button.LatestObservedEdgeQpc : button.PollCompletedQpc : qpc;
        if (!measured) return qpc > 0;
        return earliest > 0 && latest == qpc && Within(latest, earliest, .150) &&
            (button.PreviousPollStartedQpc == 0 || button.PreviousPollStartedQpc == earliest) &&
            (button.PollCompletedQpc == 0 || button.PollCompletedQpc == latest) &&
            (button.PreviousPollCompletedQpc == 0 ||
                button.PreviousPollCompletedQpc >= earliest && button.PreviousPollCompletedQpc <= latest) &&
            (button.PollStartedQpc == 0 || button.PollStartedQpc >= earliest &&
                button.PollStartedQpc >= button.PreviousPollCompletedQpc && button.PollStartedQpc <= latest);
    }

    internal void ResetPending()
    {
        lock (_gate) ClearPendingAndBaseline();
    }

    internal void Reset()
    {
        lock (_gate)
        {
            ClearPendingAndBaseline();
            _upVerified = _downVerified = false;
        }
    }

    private void ClearPendingAndBaseline()
    {
        _pending = null;
        _lastState = null;
        _lastStateQpc = 0;
        _history.Clear();
    }

    private bool Within(long now, long before, double maximumSeconds) =>
        before > 0 && now >= before && (double)(now - before) / _frequency <= maximumSeconds;

    private static bool IsParkedCombustion(VehicleState state, bool allowNeutral = false) =>
        state.IsRaceOn && state.NumCylinders > 0 && float.IsFinite(state.GroundSpeedMetersPerSecond) &&
        MathF.Abs(state.GroundSpeedMetersPerSecond) <= .5f && state.Accelerator <= 5 &&
        ((int)state.Gear is >= 1 and <= 10 || allowNeutral && state.Gear == TransmissionGear.Neutral);

    private sealed record Pending(int CarOrdinal, int FromGear, int ToGear, uint GameTimestamp, long EarliestQpc,
        long NeutralStartedQpc = 0);
    private sealed record ObservedState(VehicleState State, long Qpc);
}
