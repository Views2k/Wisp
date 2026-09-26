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
    // FH6's timestamp is the PC's uptime in whole milliseconds (the Windows tick count). It advances
    // in 15.625 ms ticks and runs on while the game is paused, in a menu or rewinding; the game's lap
    // timer stops for a pause and goes back with a rewind. The game sends nothing meanwhile.
    // A packet is sent within its tick, which can begin up to a millisecond after its reported start.
    private const double TickSpan = .0167;
    // A packet placed at least this closely times the next one by its own placing.
    private const double Placed = .003;
    // Longer than any interval between packets while the game runs.
    private const double LongInterval = .2;
    private readonly List<History> _history = new();
    private readonly record struct History(Vector3 Position, double Clock, double Start, float Distance, float Speed);
    private VehicleState? _lastState;
    private int _gate = -1;
    private double _clock, _start = double.NaN;
    private float _distance, _lastLap;
    private ushort _lap;
    private DateTimeOffset _origin;
    // The previous packet: its tick, when it arrived, and the earliest and latest it can have been sent.
    private double _uptime, _arrival, _early, _late;
    private Vector3 _heading;
    internal bool CircuitChanged { get; private set; }

    internal void Reset()
    {
        _lastState = null; _gate = -1; _clock = 0; _start = double.NaN;
        _distance = _lastLap = 0; _lap = 0; _heading = default; _history.Clear();
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
        if (previous is null)
        {
            _origin = state.ReceivedAtUtc;
            _uptime = _arrival = _early = 0;
            _late = TickSpan;
            return null;
        }
        var oldPosition = previous.Lap!.Position.ToVector();
        var move = position - oldPosition;
        var moved = move.Length();
        var speed = (previous.GroundSpeedMetersPerSecond + state.GroundSpeedMetersPerSecond) / 2;
        // When each packet was sent: within its tick, and as far on from the previous packet as the
        // car needed to drive between them at its speed (or, barely moving, about as much later as it
        // arrived). Ticks and motion together place a packet to about a millisecond.
        var arrival = (state.ReceivedAtUtc - _origin).TotalSeconds;
        var uptime = _uptime + unchecked((int)(state.GameTimestampMilliseconds - previous.GameTimestampMilliseconds)) / 1000d;
        var driven = speed > 2 ? moved / speed : Math.Max(0, arrival - _arrival);
        var spread = speed > 2 ? driven * .02 + .0001 : .002;
        double early = uptime, late = uptime + TickSpan;
        var linked = uptime - _uptime <= LongInterval;
        if (linked)
        {
            var from = Math.Max(early, _early + driven - spread);
            var to = Math.Min(late, _late + driven + spread);
            if (from <= to) { early = from; late = to; }
        }
        // Game time between the two packets: from their placings when both are placed closely,
        // otherwise from the car's motion.
        var elapsed = Math.Max(0, (early + late - _early - _late) / 2);
        var step = linked && speed > 2 && (_late - _early > Placed || late - early > Placed) ? driven : elapsed;
        var shortest = Math.Max(0, early - _late);
        var longest = late - _early;
        _uptime = uptime; _arrival = arrival; _early = early; _late = late;
        if (_gate < 0) return null;

        // Movement faster than any car is a reset, restart or fast travel.
        var driving = moved <= Math.Max(25, elapsed * 180);
        if (elapsed > LongInterval)
        {
            // Nothing arrived: the game was paused, in a menu or rewinding, or it stalled or its
            // packets were lost. The time the car needed for the distance it moved, at its speed,
            // is how far the game got. A reset puts the car down at rest, and a reset or rewind
            // moves it back.
            var before = previous.GroundSpeedMetersPerSecond;
            var after = state.GroundSpeedMetersPerSecond;
            var needed = speed > 1 ? moved / speed : moved < .05f ? 0 : elapsed;
            // Driving on through the break, the ticks bound how long it lasted and the car's motion
            // times it within them.
            step = needed < elapsed * .8 ? needed : Math.Clamp(needed, shortest, longest);
            driving = needed <= elapsed * 1.25 && !(before >= 3 && after < 1) &&
                (moved < .5f || _heading == Vector3.Zero || Vector3.Dot(move, _heading) >= 0);
        }

        // A rewind shows as the game resuming, after a break in its data, at a moment of this
        // attempt already driven, at that moment's speed. Position alone cannot tell it from a
        // spin or rolling back.
        var restored = (elapsed > LongInterval || !driving) && moved > 5 && TryRestoreVisited(position, state.GroundSpeedMetersPerSecond);
        if (restored || !driving) _heading = default;
        else
        {
            if (elapsed <= LongInterval && moved > .05f) _heading = move / moved;
            _clock += step;
            _distance += moved;
        }
        // A reset, restart or fast travel ends the attempt, and the next start-line crossing begins one.
        if (!restored && !driving) _start = double.NaN;
        if (!restored && driving && Gates[_gate].Cross(oldPosition, position, out var fraction))
        {
            if (step > 1) _start = double.NaN; // No samples near the line: its crossing time is unknown.
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
                // The attempt's history begins at the line, so a rewind to just after it restores exactly.
                _history.Add(new(Vector3.Lerp(oldPosition, position, fraction), crossing, crossing, 0, state.GroundSpeedMetersPerSecond));
            }
        }
        if (double.IsNaN(_start)) return null;
        if (restored || _history.Count == 0 || _clock - _history[^1].Clock >= .1)
        {
            _history.Add(new(position, _clock, _start, _distance, state.GroundSpeedMetersPerSecond));
            if (_history.Count > 2400) _history.RemoveRange(0, 600);
        }
        return new(position, (float)(_clock - _start), _lastLap, (float)_clock, _lap, 1);
    }

    // Restores the moment of this attempt, at least 0.3 s earlier, that the car is back at with that
    // moment's speed: the closest recorded point, where an earlier visit to the same place has to be
    // clearly closer than a later one. A reset leaves the car at rest instead, so it is not a rewind.
    private bool TryRestoreVisited(Vector3 position, float speed)
    {
        if (double.IsNaN(_start)) return false;
        var best = -1;
        var bestDistance = 25f;
        var bestFraction = 0f;
        var bestClock = 0d;
        for (var i = _history.Count - 2; i >= 0; i--)
        {
            var from = _history[i];
            var to = _history[i + 1];
            if (from.Start != _start || to.Start != _start) break;
            var edge = to.Position - from.Position;
            var fraction = edge.LengthSquared() < .0001f ? 0 : Math.Clamp(Vector3.Dot(position - from.Position, edge) / edge.LengthSquared(), 0, 1);
            var clock = from.Clock + (to.Clock - from.Clock) * fraction;
            if (_clock - clock < .3) continue;
            var distance = Vector3.DistanceSquared(position, from.Position + fraction * edge);
            if (distance >= bestDistance || best >= 0 && bestClock - clock > .5 && distance > bestDistance - .5f) continue;
            var recordedSpeed = from.Speed + fraction * (to.Speed - from.Speed);
            if (Math.Abs(speed - recordedSpeed) > Math.Max(3, recordedSpeed * .2f)) continue;
            best = i;
            bestDistance = distance;
            bestFraction = fraction;
            bestClock = clock;
        }
        if (best < 0) return false;
        var start = _history[best];
        var end = _history[best + 1];
        _clock = start.Clock + (end.Clock - start.Clock) * bestFraction;
        _distance = start.Distance + (end.Distance - start.Distance) * bestFraction;
        _history.RemoveRange(best + 1, _history.Count - best - 1);
        return true;
    }
}
