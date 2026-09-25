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
    private bool _interrupted, _retainLearningMap, _isRecording;
    private LapTelemetry? _gameLapProbe;
    private uint _gameLapProbeTimestamp, _gameLapValueTimestamp, _lastLapAdvanceTimestamp;
    private bool _gameLapActive;
    private LapPosition? _mapPosition;
    private const int MaximumPoints = 40_000;
    private const float SampleDistance = 2;
    private readonly List<Point> _current = new();
    private Trace? _best, _previous;
    private LapTelemetry? _last;
    private uint _lastTimestamp;
    private DateTimeOffset _lastReceived;
    private int _car;
    private bool _completeStart;
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
        internal int Candidate = -1;
        internal float CandidateDistance = 400;
        internal double MatchedTime;
        internal float MatchedDistance, DrivenAtMatch;
        internal void Restart(bool reacquire = false) { Cursor = Search = 0; Candidate = -1; CandidateDistance = 400; MatchedTime = 0; MatchedDistance = DrivenAtMatch = 0; Reacquiring = reacquire; }
    }
    private readonly record struct Point(Vector3 Position, float Time, float Distance);

    public void Reset()
    {
        _timeAttack.Reset();
        _mapPosition = null;
        _interrupted = _retainLearningMap = _isRecording = _gameLapActive = false;
        _gameLapProbe = null;
        _best = _previous = null;
        _last = null;
        _current.Clear();
        _completeStart = false;
        _distance = 0;
        _direction = default;
        _map = null;
        _mapTrace = null;
        _nextMapTime = 0;
    }

    // A pause or dropped sample invalidates only the active recording. Completed
    // references and their cached artwork belong to the circuit, not a packet stream.
    public void Interrupt(bool paused = false)
    {
        _isRecording = false;
        _retainLearningMap = _map is not null;
        _interrupted = true;
        _completeStart = false;
        _timeAttack.Interrupt(paused);
    }

    private void Reacquire()
    {
        _retainLearningMap = _map is not null;
        _completeStart = false;
        _current.Clear();
        _distance = 0;
        _direction = default;
        _best?.Restart(reacquire: true);
        _previous?.Restart(reacquire: true);
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
        _mapPosition = state.Lap.Position;
        var lap = state.Lap;
        if (timing == LapTimingMode.TimeAttack)
        {
            lap = _timeAttack.Update(state);
            if (_timeAttack.CircuitChanged)
            {
                _best = _previous = null;
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
            Interrupt();
            return LapDeltaReading.Waiting;
        }
        _isRecording = true;
        if (_last is { } last)
        {
            var elapsed = unchecked(state.GameTimestampMilliseconds - _lastTimestamp) / 1000f;
            var moved = Vector3.Distance(last.Position.ToVector(), lap.Position.ToVector());
            var direction = lap.Position.ToVector() - last.Position.ToVector();
            var boundary = lap.LapNumber == last.LapNumber + 1 && lap.CurrentLapSeconds < last.CurrentLapSeconds;
            if (_interrupted || elapsed > 1 || state.ReceivedAtUtc - _lastReceived > TimeSpan.FromSeconds(1) ||
                lap.RaceSeconds + .01f < last.RaceSeconds || lap.LapNumber < last.LapNumber ||
                lap.LapNumber > last.LapNumber + 1 || moved > Math.Max(25, elapsed * 180) ||
                (!boundary && lap.CurrentLapSeconds + .01f < last.CurrentLapSeconds))
            {
                Reacquire();
            }
            else if (elapsed == 0)
            {
                return Read(lap, reference, state.ReceivedTimestamp ?? 0);
            }
            else if (boundary)
            {
                // A different start line establishes a different circuit.
                if (_best is { } best && Vector3.Distance(best.Points[0].Position, lap.Position.ToVector()) > 100)
                { _best = _previous = null; _map = null; _mapTrace = null; }
                var crossing = CompleteLap(last, lap);
                StartLap(lap);
                if (crossing is { } start) _current.Add(new(start, 0, 0));
            }
            else if (lap.LapNumber != last.LapNumber) Reacquire();
            else _distance += moved;
            if (direction.LengthSquared() > .0001f && !_interrupted) _direction = direction;
        }
        if (_last is null) StartLap(lap);
        _interrupted = false;
        _last = lap;
        _car = state.CarOrdinal;
        _lastTimestamp = state.GameTimestampMilliseconds;
        _lastReceived = state.ReceivedAtUtc;
        Record(lap);
        return Read(lap, reference, state.ReceivedTimestamp ?? 0);
    }

    private bool HasGameLapTiming(VehicleState state, LapTelemetry lap)
    {
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

    private void StartLap(LapTelemetry lap)
    {
        _current.Clear();
        _distance = 0;
        _completeStart = lap.CurrentLapSeconds <= .25f;
        _direction = default;
        _best?.Restart();
        _previous?.Restart();
    }

    private void Record(LapTelemetry lap)
    {
        var position = lap.Position.ToVector();
        if (_current.Count >= MaximumPoints) { _completeStart = false; return; }
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
        if (!_completeStart || _current.Count < 20 || _distance < 100 ||
            Vector3.Distance(_current[0].Position, next.Position.ToVector()) > 35) return finish;
        _current.Add(new(finish, duration, _distance + Vector3.Distance(last.Position.ToVector(), finish)));
        var trace = new Trace(_current.ToArray(), duration);
        _previous = trace;
        if (_best is null || duration < _best.Duration) _best = trace;
        return finish;
    }

    private LapDeltaReading Read(LapTelemetry lap, LapDeltaReference mode, long received)
    {
        // Advance both references together so changing the selector mid-lap is immediate.
        var best = Match(_best, lap, received);
        var previous = ReferenceEquals(_previous, _best) ? best : Match(_previous, lap, received);
        return mode == LapDeltaReference.PreviousLap ? previous : best;
    }

    private LapDeltaReading ReacquireReference(Trace trace, LapTelemetry lap, long received)
    {
        var points = trace.Points;
        if (trace.Search == 0) { trace.SearchPosition = lap.Position.ToVector(); trace.Candidate = -1; trace.CandidateDistance = 400; }
        var end = Math.Min(points.Length - 1, trace.Search + 512);
        for (var i = trace.Search; i < end; i++)
        {
            var edge = points[i + 1].Position - points[i].Position;
            if (edge.LengthSquared() < .0001f || _direction.LengthSquared() > .0001f && Vector3.Dot(_direction, edge) < 0) continue;
            var f = Math.Clamp(Vector3.Dot(trace.SearchPosition - points[i].Position, edge) / edge.LengthSquared(), 0, 1);
            var distance = Vector3.DistanceSquared(trace.SearchPosition, points[i].Position + f * edge);
            if (distance >= trace.CandidateDistance) continue;
            trace.Candidate = i; trace.CandidateDistance = distance;
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
                var f = Math.Clamp(Vector3.Dot(lap.Position.ToVector() - points[i].Position, edge) / edge.LengthSquared(), 0, 1);
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
            return new(_completeStart ? LapDeltaStatus.RecordingLap : LapDeltaStatus.WaitingForLap, ReceivedTimestamp: received);
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
            var fraction = Math.Clamp(Vector3.Dot(position - points[i].Position, edge) / length, 0, 1);
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
            return new(LapDeltaStatus.RejoinReference, ReferenceSeconds: trace.Duration, ReceivedTimestamp: received);
        }
        trace.Reacquiring = false;
        trace.Cursor = Math.Max(trace.Cursor, index);
        trace.Search = Math.Max(0, trace.Cursor - 2);
        trace.MatchedTime = Math.Max(trace.MatchedTime, time);
        trace.MatchedDistance = Math.Max(trace.MatchedDistance, matchedDistance);
        trace.DrivenAtMatch = _distance;
        return new(LapDeltaStatus.Comparing, lap.CurrentLapSeconds - time, trace.Duration, received);
    }
}
