using System.Numerics;

namespace Wisp.Core;

public enum LapTimingMode { GameLaps, TimeAttack }
public enum LapDeltaReference { SessionBest, PreviousLap }
public enum LapDeltaStatus { WaitingForLap, RecordingLap, Comparing, RejoinReference }

// Optional Dash channels; older saved runs deserialize with no lap data.
public readonly record struct LapPosition(float X, float Y, float Z)
{
    public Vector3 ToVector() => new(X, Y, Z);
    public static implicit operator LapPosition(Vector3 value) => new(value.X, value.Y, value.Z);
}

public sealed record LapTelemetry(LapPosition Position, float CurrentLapSeconds, float LastLapSeconds,
    float RaceSeconds, ushort LapNumber, byte RacePosition);

public sealed record LapDeltaReading(LapDeltaStatus Status, double? Seconds = null,
    double? ReferenceSeconds = null, long ReceivedTimestamp = 0)
{
    public static LapDeltaReading Waiting { get; } = new(LapDeltaStatus.WaitingForLap);
}

// One consumer owns the tracker. A lap trace is sampled by distance, and each
// lookup searches only the nearby, forward part of the reference trace.
public sealed class LapDeltaTracker
{
    private readonly TimeAttackClock _timeAttack = new();
    private LapTimingMode _timingMode;
    private bool _interrupted, _paused, _retainLearningMap, _isRecording;
    private LapTelemetry? _gameLapProbe;
    private uint _gameLapProbeTimestamp, _gameLapValueTimestamp, _lastLapAdvanceTimestamp;
    private bool _gameLapActive;
    private LapPosition? _mapPosition;
    private const int MaximumPoints = 40_000;
    private const int CoverageParts = 200;
    private const float FarFromCircuit = 150;
    private const int HeldSamples = 30;
    private const float SampleDistance = 2;
    private readonly List<Point> _current = new();
    private Trace? _best, _previous, _challenger;
    private LapTelemetry? _last;
    private LapDeltaReading _lastReading = LapDeltaReading.Waiting;
    private int _held;
    private uint _lastTimestamp;
    private DateTimeOffset _lastReceived;
    private int _car;
    // A lap can become a reference when it started at the line and nothing broke its recording.
    // A rewind to before the break removes it, as it does in the game.
    private bool _startedAtLine;
    private float? _brokenAt;
    private bool Eligible => _startedAtLine && _brokenAt is null;
    private float _distance;
    private Vector3 _direction;
    private LapTrackOutline? _map;
    private Trace? _mapTrace;
    private float _nextMapTime;
    private sealed class Trace(Point[] points, float duration)
    {
        internal readonly Point[] Points = points;
        internal readonly float Duration = duration;
        internal int Cursor, Search;
        internal bool Reacquiring;
        internal Vector3 SearchPosition;
        internal float SearchLapTime;
        internal int Candidate = -1;
        internal float CandidateScore = float.MaxValue;
        internal double MatchedTime;
        internal float MatchedDistance, DrivenAtMatch;
        internal bool MatchedLast;
        // Which parts of this trace the current lap drove along it, in order. Rejoining further
        // on, rewinding and driving a stretch again add nothing; only a new lap clears it.
        private readonly bool[] _covered = new bool[CoverageParts];
        private int _coveredParts;
        internal bool Followed => _coveredParts >= CoverageParts * 97 / 100;
        internal void Cover(float from, float to, float driven)
        {
            var length = Points[^1].Distance;
            if (to <= from || to - from > driven * 1.5f + 5 || length <= 0) return;
            var last = Math.Min(CoverageParts - 1, (int)(to / length * CoverageParts));
            for (var i = Math.Max(0, (int)(from / length * CoverageParts)); i <= last; i++)
                if (!_covered[i]) { _covered[i] = true; _coveredParts++; }
        }
        internal void Restart(bool reacquire = false)
        {
            Cursor = Search = 0; Candidate = -1; CandidateScore = float.MaxValue; MatchedTime = 0; MatchedDistance = DrivenAtMatch = 0;
            Reacquiring = reacquire;
            MatchedLast = false;
            if (reacquire) return;
            Array.Clear(_covered);
            _coveredParts = 0;
        }
    }
    private readonly record struct Point(Vector3 Position, float Time, float Distance);

    public void Reset()
    {
        _timeAttack.Reset();
        _mapPosition = null;
        _interrupted = _paused = _retainLearningMap = _isRecording = _gameLapActive = false;
        _held = 0;
        _lastReading = LapDeltaReading.Waiting;
        _gameLapProbe = null;
        _best = _previous = _challenger = null;
        _last = null;
        _current.Clear();
        _startedAtLine = false;
        _brokenAt = null;
        _distance = 0;
        _direction = default;
        _map = null;
        _mapTrace = null;
        _nextMapTime = 0;
    }

    // A pause keeps the active recording; the first resumed sample decides whether the same lap
    // continues. Any other interruption breaks the recording at its last usable sample.
    // Completed references and their cached artwork belong to the circuit, not a packet stream.
    public void Interrupt(bool paused = false)
    {
        _isRecording = false;
        _paused = paused && (_paused || !_interrupted);
        if (!_paused && _last is { } last) _brokenAt ??= last.CurrentLapSeconds;
        _interrupted = true;
    }

    private bool NearCircuit(Vector3 position)
    {
        var limit = FarFromCircuit * FarFromCircuit;
        foreach (var trace in new[] { _best, _previous, _challenger })
            if (trace is not null && trace.Points.Any(point => Vector3.DistanceSquared(point.Position, position) <= limit)) return true;
        return _current.Any(point => Vector3.DistanceSquared(point.Position, position) <= limit);
    }

    private void ForgetCircuit()
    {
        _best = _previous = _challenger = null;
        _map = null;
        _mapTrace = null;
        _retainLearningMap = false;
        _nextMapTime = 0;
        _last = null;
        _current.Clear();
        _startedAtLine = false;
        _brokenAt = null;
        _distance = 0;
        _direction = default;
    }

    // A break that keeps this lap's clock going (a reset, missing samples) is remembered, and a
    // rewind to before it removes it. A lap clock that went back without a rewind cannot be
    // matched to this recording, which starts again from here.
    private void Reacquire(LapTelemetry last, LapTelemetry lap)
    {
        if (lap.LapNumber == last.LapNumber && lap.CurrentLapSeconds + .01f >= last.CurrentLapSeconds && _current.Count > 0)
            _brokenAt ??= last.CurrentLapSeconds;
        else
        {
            _retainLearningMap = _map is not null;
            _startedAtLine = false;
            _brokenAt = null;
            _current.Clear();
            _distance = 0;
        }
        _direction = default;
        _best?.Restart(reacquire: true);
        _previous?.Restart(reacquire: true);
        _challenger?.Restart(reacquire: true);
    }

    public LapMapReading? ReadMap(long receivedTimestamp)
    {
        if (_mapPosition is not { } position || _last is not { } lap) return null;
        var trace = _best;
        if (!ReferenceEquals(trace, _mapTrace) || _map is null || trace is null && _isRecording && !_retainLearningMap && _current.Count > 1 && lap.RaceSeconds >= _nextMapTime)
        {
            var points = trace?.Points ?? _current.ToArray();
            // Artwork changes at most once a second while learning. A finished
            // circuit stays cached, regardless of the live car's update rate.
            var stride = Math.Max(1, (int)Math.Ceiling(points.Length / 4095d));
            var outline = new List<LapPosition>(Math.Min(points.Length, 4096));
            for (var i = 0; i < points.Length; i += stride) outline.Add(points[i].Position);
            if (points.Length > 1 && (points.Length - 1) % stride != 0) outline.Add(points[^1].Position);
            _map = new(Array.AsReadOnly(outline.ToArray()), trace is not null);
            _mapTrace = trace;
            _nextMapTime = lap.RaceSeconds + 1;
        }
        return new(_map, position, receivedTimestamp, _isRecording);
    }

    public LapDeltaReading Update(VehicleState state, LapDeltaReference reference, LapTimingMode timing = LapTimingMode.GameLaps)
    {
        _isRecording = false;
        if (timing != _timingMode) { Reset(); _timingMode = timing; }
        if (!state.IsRaceOn || state.Lap is null)
        {
            _gameLapProbe = null;
            _gameLapActive = false;
            Interrupt(paused: !state.IsRaceOn);
            return LapDeltaReading.Waiting;
        }
        if (_last is not null && state.CarOrdinal != _car) Reset();
        // Being moved far from the circuit (to the festival, another event or a new race) leaves
        // it: its laps and map no longer apply. A reset or restart on the circuit keeps them.
        if (_mapPosition is { } before && Vector3.Distance(before.ToVector(), state.Lap.Position.ToVector()) > FarFromCircuit &&
            !NearCircuit(state.Lap.Position.ToVector())) ForgetCircuit();
        _mapPosition = state.Lap.Position;
        var lap = state.Lap;
        if (timing == LapTimingMode.TimeAttack)
        {
            lap = _timeAttack.Update(state);
            if (_timeAttack.CircuitChanged)
            {
                _best = _previous = _challenger = null;
                _map = null;
                _retainLearningMap = false;
                _mapTrace = null;
                _last = null;
                _current.Clear();
            }
            if (lap is null) return LapDeltaReading.Waiting;
        }
        else if (!HasGameLapTiming(state, lap))
        {
            // A zero-position Rivals timer needs a second advancing sample after a
            // pause. Keep the paused recording until that sample can be checked.
            if (!_paused) Interrupt();
            return LapDeltaReading.Waiting;
        }
        _isRecording = true;
        if (_last is { } last)
        {
            var elapsed = unchecked(state.GameTimestampMilliseconds - _lastTimestamp) / 1000f;
            var moved = Vector3.Distance(last.Position.ToVector(), lap.Position.ToVector());
            var direction = lap.Position.ToVector() - last.Position.ToVector();
            var boundary = lap.LapNumber == last.LapNumber + 1 && lap.CurrentLapSeconds < last.CurrentLapSeconds;
            var gap = _interrupted || elapsed > 1 || state.ReceivedAtUtc - _lastReceived > TimeSpan.FromSeconds(1);
            // After a pause, or a pause in the packet stream, the game's lap clocks and the
            // car's position decide whether the same lap continues. Wall time is not lap time.
            var resumed = gap && ContinuesLap(last, lap, moved);
            var continuous = !(gap && !resumed ||
                lap.RaceSeconds + .01f < last.RaceSeconds || lap.LapNumber < last.LapNumber ||
                lap.LapNumber > last.LapNumber + 1 || !resumed && moved > Math.Max(25, elapsed * 180) ||
                (!boundary && lap.CurrentLapSeconds + .01f < last.CurrentLapSeconds));
            // In a race the game rewinds its race clock and lap clock together. Straight after a rewind
            // it can briefly report a lap clock that disagrees with its race clock; wait for one that
            // agrees. (Wisp's Time Attack clocks restart an attempt while the race clock runs on.)
            if (!continuous && timing == LapTimingMode.GameLaps && lap.LapNumber == last.LapNumber &&
                last.RaceSeconds > 0 && lap.RaceSeconds > .5f &&
                Math.Abs(lap.CurrentLapSeconds - last.CurrentLapSeconds - (lap.RaceSeconds - last.RaceSeconds)) > .1f &&
                ++_held <= HeldSamples) return _lastReading;
            if (!continuous)
            {
                // A rewind returns the car to a moment of this recording, with the lap clock at that
                // moment. A lap clock back at zero where laps start (a restart, reset or new attempt)
                // begins a new lap, and the references restart at their own start rather than being
                // searched.
                var clockBack = unchecked((int)(state.GameTimestampMilliseconds - _lastTimestamp)) < 0;
                if ((clockBack || lap.CurrentLapSeconds > .25f) && RewindsTo(last, lap)) Rewind(lap);
                else if (lap.CurrentLapSeconds <= .25f && (lap.RaceSeconds <= .5f || AtLapStart(lap))) StartLap(lap);
                // Straight after a rewind the game can briefly report a lap clock that fits neither.
                // Wait a moment for a consistent sample before giving up on this lap's recording.
                else if (lap.CurrentLapSeconds + .01f < last.CurrentLapSeconds && ++_held <= HeldSamples) return _lastReading;
                else Reacquire(last, lap);
            }
            // FH6 sends about two packets per timestamp tick, and its timestamp can stop ticking
            // after a rewind. A sample only repeats the last one when nothing else changed either.
            else if (elapsed == 0 && !resumed && moved < .01f && lap.CurrentLapSeconds == last.CurrentLapSeconds)
            {
                return _lastReading = Read(lap, reference, state.ReceivedTimestamp ?? 0);
            }
            else if (boundary)
            {
                // A different start line establishes a different circuit.
                if (_best is { } best && Vector3.Distance(best.Points[0].Position, lap.Position.ToVector()) > 100)
                { _best = _previous = _challenger = null; _map = null; _mapTrace = null; }
                var crossing = CompleteLap(last, lap);
                StartLap(lap);
                if (crossing is { } start) _current.Add(new(start, 0, 0));
            }
            else if (lap.LapNumber != last.LapNumber) Reacquire(last, lap);
            else
            {
                _distance += moved;
                // The game kept running and the car kept driving while no samples arrived. Keep
                // tracking this lap, but its recorded line skips that stretch, so it cannot
                // become a reference unless a rewind goes back before it.
                if (resumed && lap.CurrentLapSeconds - last.CurrentLapSeconds > 1 && moved > 25) _brokenAt ??= last.CurrentLapSeconds;
            }
            // A teleport, rewind or restart is not a heading.
            if (continuous && direction.LengthSquared() > .0001f) _direction = direction;
        }
        if (_last is null) StartLap(lap);
        _interrupted = _paused = false;
        _held = 0;
        _last = lap;
        _car = state.CarOrdinal;
        _lastTimestamp = state.GameTimestampMilliseconds;
        _lastReceived = state.ReceivedAtUtc;
        Record(lap);
        return _lastReading = Read(lap, reference, state.ReceivedTimestamp ?? 0);
    }

    private bool HasGameLapTiming(VehicleState state, LapTelemetry lap)
    {
        if (_gameLapProbe is not null && state.GameTimestampMilliseconds == _gameLapProbeTimestamp) return _gameLapActive;
        var previous = _gameLapProbe;
        var elapsed = unchecked(state.GameTimestampMilliseconds - _gameLapProbeTimestamp) / 1000f;
        var timerElapsed = unchecked(state.GameTimestampMilliseconds - _gameLapValueTimestamp) / 1000f;
        if (previous is null || lap.LapNumber != previous.LapNumber || lap.CurrentLapSeconds != previous.CurrentLapSeconds)
            _gameLapValueTimestamp = state.GameTimestampMilliseconds;
        _gameLapProbe = lap;
        _gameLapProbeTimestamp = state.GameTimestampMilliseconds;
        var advancing = false;
        if (previous is not null && elapsed is > 0 and <= 1)
        {
            var sameLap = lap.LapNumber == previous.LapNumber;
            var step = lap.CurrentLapSeconds - previous.CurrentLapSeconds;
            var boundarySpan = lap.LastLapSeconds - previous.CurrentLapSeconds + lap.CurrentLapSeconds;
            advancing = sameLap && lap.CurrentLapSeconds > 0 && step > 0 && step <= timerElapsed * 1.5f + .05f ||
                _gameLapActive && lap.LapNumber == previous.LapNumber + 1 && step < 0 &&
                lap.LastLapSeconds >= previous.CurrentLapSeconds && boundarySpan > 0 && boundarySpan <= timerElapsed * 1.5f + .05f;
        }
        // RaceSeconds also advances in free roam. A zero-position Rivals lap
        // needs its own advancing lap timer, never just the world/session clock.
        if (lap.RacePosition > 0 || advancing)
        {
            _lastLapAdvanceTimestamp = state.GameTimestampMilliseconds;
            return _gameLapActive = true;
        }
        var sameTimer = previous is not null && lap.LapNumber == previous.LapNumber && lap.CurrentLapSeconds == previous.CurrentLapSeconds;
        return _gameLapActive = _gameLapActive && sameTimer &&
            unchecked(state.GameTimestampMilliseconds - _lastLapAdvanceTimestamp) <= 250;
    }

    // The same lap continues when the game's lap clock moved forward and the car is where that
    // time allows. Crossing the line meanwhile is continuous when the reported lap time bridges
    // both samples.
    private static bool ContinuesLap(LapTelemetry last, LapTelemetry lap, float moved)
    {
        var step = lap.LapNumber == last.LapNumber
            ? Math.Abs(lap.LastLapSeconds - last.LastLapSeconds) <= .01f ? lap.CurrentLapSeconds - last.CurrentLapSeconds : -1
            : lap.LapNumber == last.LapNumber + 1 ? lap.LastLapSeconds - last.CurrentLapSeconds + lap.CurrentLapSeconds : -1;
        return step >= -.01f && lap.RaceSeconds + .01f >= last.RaceSeconds && moved <= Math.Max(25, step * 180);
    }

    // A rewind returns to an earlier moment of this lap: the lap clock goes back and the car
    // is where this recording had it at that lap time.
    private bool RewindsTo(LapTelemetry last, LapTelemetry lap)
    {
        if (lap.LapNumber != last.LapNumber || Math.Abs(lap.LastLapSeconds - last.LastLapSeconds) > .01f ||
            lap.CurrentLapSeconds + .01f >= last.CurrentLapSeconds || _current.Count == 0 ||
            lap.CurrentLapSeconds < _current[0].Time) return false;
        var i = RecordedIndexAt(lap.CurrentLapSeconds);
        var at = _current[i];
        var position = lap.Position.ToVector();
        if (Vector3.Distance(at.Position, position) <= 25) return true;
        if (i + 1 >= _current.Count || _current[i + 1].Time <= at.Time) return false;
        // A kept break (a reset) can lie between two recorded samples; either side, or the line
        // between them, is where the car was at that lap time.
        var next = _current[i + 1];
        var expected = Vector3.Lerp(at.Position, next.Position, (lap.CurrentLapSeconds - at.Time) / (next.Time - at.Time));
        return Vector3.Distance(expected, position) <= 25 || Vector3.Distance(next.Position, position) <= 25;
    }

    // Keep the recording up to the rewind point; the rest of the lap is driven again.
    private void Rewind(LapTelemetry lap)
    {
        var i = RecordedIndexAt(lap.CurrentLapSeconds);
        _current.RemoveRange(i + 1, _current.Count - i - 1);
        _distance = _current[i].Distance + Vector3.Distance(_current[i].Position, lap.Position.ToVector());
        if (_brokenAt is { } broken && lap.CurrentLapSeconds <= broken) _brokenAt = null;
        _direction = default;
        _nextMapTime = 0;
        _best?.Restart(reacquire: true);
        _previous?.Restart(reacquire: true);
        _challenger?.Restart(reacquire: true);
    }

    private int RecordedIndexAt(float time)
    {
        int low = 0, high = _current.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_current[middle].Time <= time) low = middle; else high = middle - 1;
        }
        return low;
    }

    // The car is at the line where laps start (the references' first samples, or this recording's
    // when it began there), no further from it than its lap clock allows.
    private bool AtLapStart(LapTelemetry lap)
    {
        var starts = new List<Vector3>(3);
        if (_best is not null) starts.Add(_best.Points[0].Position);
        if (_previous is not null) starts.Add(_previous.Points[0].Position);
        if (_startedAtLine && _current.Count > 0) starts.Add(_current[0].Position);
        var position = lap.Position.ToVector();
        var reach = lap.CurrentLapSeconds * 90 + 10;
        return starts.Count == 0 || starts.Any(start => Vector3.Distance(start, position) <= reach);
    }

    private void StartLap(LapTelemetry lap)
    {
        // A new lap redraws the learning map, including after an interrupted one.
        _retainLearningMap = false;
        _nextMapTime = 0;
        _current.Clear();
        _distance = 0;
        _startedAtLine = lap.CurrentLapSeconds <= .25f;
        _brokenAt = null;
        _direction = default;
        _best?.Restart();
        _previous?.Restart();
        _challenger?.Restart();
    }

    private void Record(LapTelemetry lap)
    {
        var position = lap.Position.ToVector();
        if (_current.Count >= MaximumPoints) { _brokenAt ??= lap.CurrentLapSeconds; return; }
        if (_current.Count == 0) { _current.Add(new(position, lap.CurrentLapSeconds, _distance)); return; }
        var last = _current[^1];
        if (lap.CurrentLapSeconds - last.Time < .1f && Vector3.DistanceSquared(last.Position, position) < SampleDistance * SampleDistance) return;
        // Keep both arrival and departure at a stop. Updating the departure point
        // bounds storage without interpolating stopped time over the next segment.
        if (_current.Count > 1 && Vector3.DistanceSquared(last.Position, position) < .0001f &&
            Vector3.DistanceSquared(_current[^2].Position, position) < .0001f)
            _current[^1] = new(position, lap.CurrentLapSeconds, _distance);
        else _current.Add(new(position, lap.CurrentLapSeconds, _distance));
    }

    private Vector3? CompleteLap(LapTelemetry last, LapTelemetry next)
    {
        var duration = next.LastLapSeconds;
        var span = duration - last.CurrentLapSeconds + next.CurrentLapSeconds;
        if (duration < 5 || duration < last.CurrentLapSeconds || span <= 0 || span > 1) return null;
        var finish = Vector3.Lerp(last.Position.ToVector(), next.Position.ToVector(), (duration - last.CurrentLapSeconds) / span);
        if (!Eligible || _current.Count < 20 || _distance < 100 ||
            Vector3.Distance(_current[0].Position, next.Position.ToVector()) > 35) return finish;
        _current.Add(new(finish, duration, _distance + Vector3.Distance(last.Position.ToVector(), finish)));
        var trace = new Trace(_current.ToArray(), duration);
        // Time Attack laps are inferred from start-line crossings, so a lap must also follow the
        // reference. One that does not is an abandoned attempt, unless the reference came from
        // one. An abandoned attempt returns to the line by a shorter way than the circuit, so two
        // laps that follow each other and are both clearly longer than the reference replace it.
        if (_timingMode == LapTimingMode.TimeAttack && _best is { } reference && !reference.Followed)
        {
            var longer = reference.Points[^1].Distance * 1.05f;
            if (_challenger is { } challenger && challenger.Followed &&
                challenger.Points[^1].Distance > longer && trace.Points[^1].Distance > longer)
            {
                _best = duration < challenger.Duration ? trace : challenger;
                _previous = trace;
                _challenger = null;
            }
            else _challenger = trace;
            return finish;
        }
        _challenger = null;
        _previous = trace;
        if (_best is null || duration < _best.Duration) _best = trace;
        return finish;
    }

    private LapDeltaReading Read(LapTelemetry lap, LapDeltaReference mode, long received)
    {
        // Advance both references together so changing the selector mid-lap is immediate.
        var best = Match(_best, lap, received);
        var previous = ReferenceEquals(_previous, _best) ? best : Match(_previous, lap, received);
        // A lap that may replace an outlier Time Attack reference is followed the same way.
        if (_challenger is not null) Match(_challenger, lap, received);
        return mode == LapDeltaReference.PreviousLap ? previous : best;
    }

    // A reference can begin one sample after its line; its first segment extends back to lap time zero.
    private static float Earliest(Point[] points, int i) =>
        i == 0 && points[1].Time > points[0].Time ? -points[0].Time / (points[1].Time - points[0].Time) : 0;

    private LapDeltaReading ReacquireReference(Trace trace, LapTelemetry lap, long received)
    {
        var points = trace.Points;
        if (trace.Search == 0) { trace.SearchPosition = lap.Position.ToVector(); trace.SearchLapTime = lap.CurrentLapSeconds; trace.Candidate = -1; trace.CandidateScore = float.MaxValue; }
        var end = Math.Min(points.Length - 1, trace.Search + 512);
        for (var i = trace.Search; i < end; i++)
        {
            var edge = points[i + 1].Position - points[i].Position;
            if (edge.LengthSquared() < .0001f || _direction.LengthSquared() > .0001f && Vector3.Dot(_direction, edge) < 0) continue;
            var f = Math.Clamp(Vector3.Dot(trace.SearchPosition - points[i].Position, edge) / edge.LengthSquared(), Earliest(points, i), 1);
            var distance = Vector3.DistanceSquared(trace.SearchPosition, points[i].Position + f * edge);
            if (distance >= 400) continue;
            // The start and finish, or crossing roads, pass the same place at different lap
            // times. Half a metre per second of lap-time difference selects the right visit
            // without moving the match within it.
            var timeError = points[i].Time + f * (points[i + 1].Time - points[i].Time) - trace.SearchLapTime;
            var score = distance + .25f * timeError * timeError;
            if (score >= trace.CandidateScore) continue;
            trace.Candidate = i; trace.CandidateScore = score;
        }
        trace.Search = end;
        if (end == points.Length - 1)
        {
            var i = trace.Candidate;
            trace.Search = 0;
            if (i >= 0)
            {
                // The car may move while the bounded search finishes. Reproject
                // against its current position before accepting the chosen segment.
                var edge = points[i + 1].Position - points[i].Position;
                var f = Math.Clamp(Vector3.Dot(lap.Position.ToVector() - points[i].Position, edge) / edge.LengthSquared(), Earliest(points, i), 1);
                if (Vector3.DistanceSquared(lap.Position.ToVector(), points[i].Position + f * edge) < 400 &&
                    (_direction.LengthSquared() < .0001f || Vector3.Dot(_direction, edge) >= 0))
                {
                    trace.Cursor = i; trace.Search = Math.Max(0, i - 2); trace.Reacquiring = false;
                    trace.MatchedTime = points[i].Time + f * (points[i + 1].Time - points[i].Time);
                    trace.MatchedDistance = points[i].Distance + f * (points[i + 1].Distance - points[i].Distance);
                    trace.DrivenAtMatch = _distance;
                    return new(LapDeltaStatus.Comparing, lap.CurrentLapSeconds - trace.MatchedTime, trace.Duration, received);
                }
            }
        }
        return new(LapDeltaStatus.RejoinReference, ReferenceSeconds: trace.Duration, ReceivedTimestamp: received);
    }

    private LapDeltaReading Match(Trace? trace, LapTelemetry lap, long received)
    {
        if (trace is null)
            return new(Eligible ? LapDeltaStatus.RecordingLap : LapDeltaStatus.WaitingForLap, ReceivedTimestamp: received);
        if (trace.Reacquiring) return ReacquireReference(trace, lap, received);
        var position = lap.Position.ToVector();
        var points = trace.Points;
        var bestDistance = 20f * 20;
        var index = -1;
        double time = 0;
        float matchedDistance = 0;
        // Limit progress by distance actually driven, including a detour. Heading
        // prevents matching the opposite side of a hairpin. Each search is bounded;
        // after a long excursion, later packets continue the reacquisition scan.
        var maximumDistance = trace.MatchedDistance + Math.Max(0, _distance - trace.DrivenAtMatch) * 2 + 20;
        var begin = Math.Max(Math.Max(0, trace.Cursor - 2), trace.Search);
        var end = Math.Min(points.Length - 1, begin + 512);
        var directionLength = _direction.LengthSquared();
        var scanned = begin;
        for (var i = begin; i < end; i++)
        {
            scanned = i + 1;
            if (points[i].Distance > maximumDistance) break;
            var edge = points[i + 1].Position - points[i].Position;
            var length = edge.LengthSquared();
            if (length < .0001f) continue;
            if (directionLength > .0001f && Vector3.Dot(_direction, edge) < .1f * MathF.Sqrt(directionLength * length)) continue;
            var fraction = Math.Clamp(Vector3.Dot(position - points[i].Position, edge) / length, Earliest(points, i), 1);
            var candidateTime = points[i].Time + fraction * (points[i + 1].Time - points[i].Time);
            if (candidateTime + .05 < trace.MatchedTime) continue;
            var progress = points[i].Distance + fraction * (points[i + 1].Distance - points[i].Distance);
            if (progress > maximumDistance) continue;
            var distance = Vector3.DistanceSquared(position, points[i].Position + edge * fraction);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            index = i;
            time = candidateTime;
            matchedDistance = progress;
        }
        if (index < 0)
        {
            trace.Search = scanned == end && end < points.Length - 1 && points[end].Distance <= maximumDistance ? end : Math.Max(0, trace.Cursor - 2);
            trace.MatchedLast = false;
            return new(LapDeltaStatus.RejoinReference, ReferenceSeconds: trace.Duration, ReceivedTimestamp: received);
        }
        trace.Reacquiring = false;
        trace.Cursor = Math.Max(trace.Cursor, index);
        trace.Search = Math.Max(0, trace.Cursor - 2);
        trace.MatchedTime = Math.Max(trace.MatchedTime, time);
        if (trace.MatchedLast) trace.Cover(trace.MatchedDistance, matchedDistance, _distance - trace.DrivenAtMatch);
        trace.MatchedLast = true;
        trace.MatchedDistance = Math.Max(trace.MatchedDistance, matchedDistance);
        trace.DrivenAtMatch = _distance;
        return new(LapDeltaStatus.Comparing, lap.CurrentLapSeconds - time, trace.Duration, received);
    }
}
