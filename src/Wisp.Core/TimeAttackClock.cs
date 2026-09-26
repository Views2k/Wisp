using System.Numerics;

namespace Wisp.Core;

// FH6 Time Attack leaves the Dash lap clocks at zero. These gates use world
// coordinates from the MIT-licensed FH6 Time Attack Tracker (see notices).
internal sealed class TimeAttackClock
{
    private sealed record Gate(Vector2 Center, Vector2 Forward, float HalfWidth = 10)
    {
        internal bool Cross(Vector3 a, Vector3 b, out float fraction)
        {
            var p = new Vector2(a.X, a.Z) - Center;
            var q = new Vector2(b.X, b.Z) - Center;
            var before = Vector2.Dot(p, Forward);
            var after = Vector2.Dot(q, Forward);
            fraction = 0;
            if (before > 0 || after <= 0) return false;
            fraction = -before / (after - before);
            var crossing = Vector2.Lerp(p, q, fraction);
            return Math.Abs(crossing.X * Forward.Y - crossing.Y * Forward.X) <= HalfWidth;
        }
    }
    private static readonly Gate[] Gates =
    [
        new(new(4267.63f, -5273.08f), Vector2.Normalize(new(-.16602f, .98612f))),
        new(new(2826.10f, 2697.94f), Vector2.Normalize(new(-.63556f, .77205f))),
        new(new(2785.70f, 4990.71f), Vector2.Normalize(new(.94037f, .34014f))),
        new(new(2495.16f, -5063.40f), Vector2.Normalize(new(-.01200f, -.99993f)))
    ];
    private readonly List<History> _history = new();
    private readonly record struct History(Vector3 Position, uint GameTimestamp, double Clock, double Start, ushort Lap, float Last, float Distance, float Speed);
    private VehicleState? _lastState;
    private int _gate = -1;
    private double _clock, _start = double.NaN;
    private float _distance, _lastLap;
    private ushort _lap;
    private double _bridged;
    internal bool CircuitChanged { get; private set; }

    internal void Reset()
    {
        _lastState = null; _gate = -1; _clock = 0; _start = double.NaN;
        _distance = _lastLap = 0; _lap = 0; _bridged = 0; _history.Clear();
    }

    internal LapTelemetry? Update(VehicleState state)
    {
        CircuitChanged = false;
        var position = state.Lap!.Position.ToVector();
        var near = Array.FindIndex(Gates, g => Vector2.DistanceSquared(g.Center, new(position.X, position.Z)) < 150 * 150);
        if (near >= 0 && near != _gate)
        {
            Reset(); _gate = near; CircuitChanged = true;
        }
        var previous = _lastState;
        _lastState = state;
        if (previous is null || _gate < 0) return null;
        // The game's timestamp is its simulation clock: it stands still while the game is paused,
        // like the game's own timer. Wall time is not lap time.
        var delta = unchecked((int)(state.GameTimestampMilliseconds - previous.GameTimestampMilliseconds)) / 1000d;
        var wall = (state.ReceivedAtUtc - previous.ReceivedAtUtc).TotalSeconds;
        var oldPosition = previous.Lap!.Position.ToVector();
        var moved = Vector3.Distance(position, oldPosition);
        var clockReversed = delta < 0;
        double step;
        // FH6 ticks its timestamp about every 16 ms and sends about two packets per tick, and after
        // some rewinds the timestamp stops ticking while the car drives on. Real time stands in for a
        // sample without a tick, and the next tick adds only what it did not already cover.
        if (delta == 0 && moved > .05f && wall is > 0 and <= .15)
        {
            step = wall;
            _bridged += wall;
        }
        else
        {
            step = Math.Max(0, delta - _bridged);
            // A tick after a long stall, or one that jumps further than the car could have driven
            // meanwhile, is the timestamp catching up. Only the time the car needed for the distance
            // it moved, at its speed, was spent driving.
            var speed = state.GroundSpeedMetersPerSecond;
            if (delta > 0 && (_bridged > .1 || delta > 1 && moved < speed * delta * .5f))
                step = Math.Min(step, Math.Max(.1, speed > 1 ? moved / speed : 0));
            _bridged = 0;
        }
        // Movement faster than any car is a reset, restart or fast travel: it ends the attempt, and
        // the next start-line crossing begins a new one.
        var jumped = !clockReversed && moved > Math.Max(25, step * 180);

        // Position alone cannot distinguish rewind from a spin or rolling back. A rewind shows as a
        // backwards game clock, or as the game resuming after a break in its data (it sends nothing
        // while rewinding) at a moment already driven, at that moment's speed.
        if (clockReversed && !double.IsNaN(_start) &&
            (_history.Count == 0 || _history[^1].GameTimestamp != previous.GameTimestampMilliseconds))
            _history.Add(new(oldPosition, previous.GameTimestampMilliseconds, _clock, _start, _lap, _lastLap, _distance, previous.GroundSpeedMetersPerSecond));
        var restored = clockReversed
            ? TryRestore(position, state.GameTimestampMilliseconds)
            : (wall > .3 || jumped) && moved > 5 && TryRestoreVisited(position, state.GroundSpeedMetersPerSecond);
        var teleported = jumped && !restored;
        if (!restored)
        {
            if (clockReversed || teleported) _start = double.NaN;
            else
            {
                _clock += step;
                _distance += moved;
            }
        }
        if (!restored && !clockReversed && !teleported && Gates[_gate].Cross(oldPosition, position, out var fraction))
        {
            if (delta > 1) _start = double.NaN; // No samples near the line: its crossing time is unknown.
            else
            {
                // Every forward crossing starts a new attempt. The previous one counts as a lap only
                // if it was long enough to be one; otherwise it was abandoned at the line.
                var crossing = _clock - step * (1 - fraction);
                if (!double.IsNaN(_start) && crossing - _start >= 15 && _distance >= 500)
                {
                    _lastLap = (float)(crossing - _start);
                    _lap++;
                }
                _start = crossing;
                _distance = moved * (1 - fraction);
            }
        }
        if (double.IsNaN(_start)) return null;
        if (restored || _history.Count == 0 || _clock - _history[^1].Clock >= .1)
        {
            _history.Add(new(position, state.GameTimestampMilliseconds, _clock, _start, _lap, _lastLap, _distance, state.GroundSpeedMetersPerSecond));
            if (_history.Count > 2400) _history.RemoveRange(0, 600);
        }
        return new(position, (float)(_clock - _start), _lastLap, (float)_clock, _lap, 1);
    }

    // Restores the moment of this attempt, at least 0.3 s earlier, that the car is back at with that
    // moment's speed: the closest recorded point, the latest visit on a tie. A reset leaves the car
    // at rest instead, so it is not a rewind.
    private bool TryRestoreVisited(Vector3 position, float speed)
    {
        if (double.IsNaN(_start)) return false;
        var best = -1;
        var bestDistance = 25f;
        var bestFraction = 0f;
        for (var i = _history.Count - 2; i >= 0; i--)
        {
            var from = _history[i];
            var to = _history[i + 1];
            if (from.Start != _start || to.Start != _start) break;
            var edge = to.Position - from.Position;
            var fraction = edge.LengthSquared() < .0001f ? 0 : Math.Clamp(Vector3.Dot(position - from.Position, edge) / edge.LengthSquared(), 0, 1);
            if (_clock - (from.Clock + (to.Clock - from.Clock) * fraction) < .3) continue;
            var distance = Vector3.DistanceSquared(position, from.Position + fraction * edge);
            if (distance > bestDistance - .5f) continue;
            var recordedSpeed = from.Speed + fraction * (to.Speed - from.Speed);
            if (Math.Abs(speed - recordedSpeed) > Math.Max(3, recordedSpeed * .2f)) continue;
            best = i;
            bestDistance = distance;
            bestFraction = fraction;
        }
        if (best < 0) return false;
        var start = _history[best];
        var end = _history[best + 1];
        _clock = start.Clock + (end.Clock - start.Clock) * bestFraction;
        _distance = start.Distance + (end.Distance - start.Distance) * bestFraction;
        _history.RemoveRange(best + 1, _history.Count - best - 1);
        return true;
    }

    private bool TryRestore(Vector3 position, uint timestamp)
    {
        // A position may recur at a stop or crossing. The game timestamp identifies
        // which visit is being restored; geometry only checks that it is consistent.
        for (var i = Math.Max(0, _history.Count - 512); i < _history.Count - 1; i++)
        {
            var start = _history[i]; var end = _history[i + 1];
            var span = unchecked((int)(end.GameTimestamp - start.GameTimestamp));
            var offset = unchecked((int)(timestamp - start.GameTimestamp));
            if (span <= 0 || offset < 0 || offset > span) continue;
            var fraction = offset / (double)span;
            var expected = Vector3.Lerp(start.Position, end.Position, (float)fraction);
            if (Vector3.DistanceSquared(position, expected) > 25) return false;
            _clock = start.Clock + (end.Clock - start.Clock) * fraction;
            var selected = start.Lap == end.Lap || _clock < end.Start ? start : end;
            _start = selected.Start; _lap = selected.Lap; _lastLap = selected.Last;
            _distance = start.Lap == end.Lap
                ? start.Distance + (end.Distance - start.Distance) * (float)fraction
                : _clock >= end.Start
                    ? end.Distance * (float)((_clock - end.Start) / Math.Max(.000001, end.Clock - end.Start))
                    : start.Distance + Vector3.Distance(start.Position, expected);
            _history.RemoveRange(i + 1, _history.Count - i - 1);
            return true;
        }
        return false;
    }
}
