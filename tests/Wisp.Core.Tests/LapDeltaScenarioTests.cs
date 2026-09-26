using System.Numerics;
using Xunit;

namespace Wisp.Core.Tests;

// Randomised sessions mixing driving with what players do: pausing, rewinding, resets,
// restarts, lost packets and abandoned attempts. After each event and one second of normal
// driving, what Wisp shows must agree with the game:
//   - with a reference lap: the delta is this lap's time minus the reference's time at the same
//     place, against the fastest (or the previous) lap that the game counted as a clean lap;
//   - without one: SETTING REFERENCE while this lap can still become the reference, otherwise
//     START A LAP.
// Packets are timed as FH6 sends them: at the game's frame rate, stamped with the PC's uptime in
// 15.625 ms ticks (which runs on through pauses and rewinds), and received a little later.
public sealed class LapDeltaScenarioTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    private const int Sessions = 150;

    [Theory]
    [InlineData(LapDeltaReference.SessionBest)]
    [InlineData(LapDeltaReference.PreviousLap)]
    public void RaceSessionsAgreeWithTheGame(LapDeltaReference mode)
    {
        for (var seed = 1; seed <= Sessions; seed++) new RaceSession(seed, mode).Run();
    }

    [Theory]
    [InlineData(LapDeltaReference.SessionBest)]
    [InlineData(LapDeltaReference.PreviousLap)]
    public void TimeAttackSessionsAgreeWithTheGame(LapDeltaReference mode)
    {
        for (var seed = 1; seed <= Sessions; seed++) new TimeAttackSession(seed, mode).Run();
    }

    private abstract class Session(int seed, LapDeltaReference mode)
    {
        protected readonly Random Random = new(seed);
        protected readonly LapDeltaTracker Tracker = new();
        protected readonly LapDeltaReference Mode = mode;
        // Seconds between packets: the game's frame time, 30 to 120 frames a second.
        protected readonly double Dt = 1d / (30 + seed * 37 % 91);
        private readonly double _boot = 1000 + seed * 7.3;
        private readonly List<string> _events = [];
        private readonly Queue<string> _packets = new();
        protected double Wall = 100, Pace = 60;
        protected double? Best, Previous;
        // Lap time of the last sample before a break in this lap; a rewind to before it repairs it.
        protected double? BrokenAt;
        protected bool Valid => BrokenAt is null;
        protected bool AtRest;
        private double _received;
        private int _settle;
        private bool _quiet;

        protected double NextPace() => Pace = 55 + Random.NextDouble() * 10;

        // The PC's uptime in whole milliseconds, advancing in 15.625 ms ticks.
        protected uint Timestamp() => unchecked((uint)Math.Floor(Math.Floor((Wall + _boot) * 64) * 15.625));

        // Packets reach Wisp a fraction of a millisecond after they are sent, some of them later.
        protected DateTimeOffset Received()
        {
            var delay = .0002 - Math.Log(1 - Random.NextDouble()) * .0004;
            if (Random.NextDouble() < .02) delay += .002 + Random.NextDouble() * .008;
            _received = Math.Max(_received, Wall + delay);
            return Epoch.AddTicks((long)Math.Round(_received * TimeSpan.TicksPerSecond));
        }

        protected int Frames(double seconds) => (int)(seconds / Dt);

        // Frame times vary a little from frame to frame.
        protected double Frame() => Dt * (.95 + Random.NextDouble() * .1);

        protected void Begin(string name)
        {
            _events.Add(name);
            _quiet = true;
        }

        protected void End()
        {
            _quiet = false;
            _settle = (int)Math.Ceiling(1 / Dt);
        }

        protected void Observe(LapDeltaReading reading, bool raceOn, double? lapTime, double fraction)
        {
            _packets.Enqueue($"[{(raceOn ? "" : "off ")}t={lapTime:F3} f={fraction:F4} {reading.Status} {reading.Seconds:F4}/{reading.ReferenceSeconds:F3}{(_quiet ? " q" : "")}]");
            if (_packets.Count > 40) _packets.Dequeue();
            if (!raceOn || _quiet) return;
            if (_settle > 0) { _settle--; return; }
            if (lapTime is not { } time)
            {
                Require(reading.Status == LapDeltaStatus.WaitingForLap, $"expected START A LAP, got {reading.Status}", fraction);
                return;
            }
            if ((Mode == LapDeltaReference.PreviousLap ? Previous : Best) is { } reference)
            {
                Require(reading.Status == LapDeltaStatus.Comparing, $"expected a delta, got {reading.Status}", fraction);
                Require(Math.Abs(reading.ReferenceSeconds!.Value - reference) < Accuracy,
                    $"reference {reading.ReferenceSeconds:F4} s instead of {reference:F4} s", fraction);
                var expected = time - reference * fraction;
                Require(Math.Abs(reading.Seconds!.Value - expected) < Accuracy,
                    $"delta {reading.Seconds:F4} s instead of {expected:F4} s", fraction);
            }
            else
            {
                var expected = Valid ? LapDeltaStatus.RecordingLap : LapDeltaStatus.WaitingForLap;
                Require(reading.Status == expected, $"expected {expected}, got {reading.Status}", fraction);
            }
        }

        // How closely the reference lap time and the delta must match the game's.
        protected const double Accuracy = .005;

        private void Require(bool condition, string message, double fraction) =>
            Assert.True(condition, $"{GetType().Name} seed {seed} at {1 / Dt:F0} packets/s, {Mode}, at {fraction:P1} of the lap: {message}. " +
                $"Recent events: {string.Join(", ", _events.TakeLast(6))}. Packets: {string.Join(" ", _packets)}");
    }

    // A lapped race: the game reports lap number, lap time, last lap and race time.
    private sealed class RaceSession(int seed, LapDeltaReference mode) : Session(seed, mode)
    {
        private readonly record struct Moment(double Game, double Race, double Lap, double Fraction);
        private readonly List<Moment> _history = [];
        private Moment _now;
        private double _lastLap;
        private int _number;

        private static Vector3 Position(double fraction) =>
            new((float)(150 * Math.Cos(fraction * Math.Tau)), 0, (float)(150 * Math.Sin(fraction * Math.Tau)));

        internal void Run()
        {
            NextPace();
            Send();
            for (var step = 0; step < 70; step++)
            {
                var roll = Random.NextDouble();
                if (roll < .38) Drive(1 + Random.NextDouble() * 25);
                else if (roll < .46) Pause(.3 + Random.NextDouble() * 30, packets: true);
                else if (roll < .54) Pause(.3 + Random.NextDouble() * (Random.NextDouble() < .5 ? 1 : 30), packets: false);
                else if (roll < .66) Rewind(.3 + Random.NextDouble() * 8);
                else if (roll < .73) LosePackets(.02 + Random.NextDouble() * 3);
                else if (roll < .81) ResetOnTrack(30 + Random.NextDouble() * 90);
                else if (roll < .86) Restart();
                else Drive(40 + Random.NextDouble() * 60);
                // Nobody acts again within a few frames: the game sends for a moment before the next event.
                Drive(.1 + Random.NextDouble() * .2);
            }
        }

        private void Drive(double seconds)
        {
            for (var i = 0; i < Frames(seconds); i++) Step();
        }

        private void Step(bool send = true)
        {
            var frame = Frame();
            Wall += frame;
            var next = _now with { Game = _now.Game + frame, Race = _now.Race + frame, Lap = _now.Lap + frame, Fraction = _now.Fraction + frame / Pace };
            if (next.Fraction >= 1)
            {
                var past = (next.Fraction - 1) * Pace;
                var duration = next.Lap - past;
                var pace = NextPace();
                if (Valid)
                {
                    Best = Best is { } best ? Math.Min(best, duration) : duration;
                    Previous = duration;
                }
                _lastLap = duration;
                _number++;
                BrokenAt = null;
                next = next with { Lap = past, Fraction = past / pace };
                _history.Clear();
            }
            _now = next;
            _history.Add(_now);
            if (send) Send();
        }

        private void Send(bool raceOn = true, double? lapClock = null)
        {
            var state = LapDeltaTests.State(_now.Race, lapClock ?? _now.Lap, _number, Position(_now.Fraction), _lastLap) with
            {
                IsRaceOn = raceOn,
                GameTimestampMilliseconds = Timestamp(),
                ReceivedAtUtc = Received(),
                GroundSpeedMetersPerSecond = AtRest ? 0 : (float)(Math.Tau * 150 / Pace)
            };
            AtRest = false;
            Observe(Tracker.Update(state, Mode), raceOn, _now.Lap, _now.Fraction);
        }

        // The game stops; the PC's uptime runs on. It either keeps sending race-off packets or none.
        private void Pause(double seconds, bool packets)
        {
            Begin($"pause {seconds:F2}s{(packets ? "" : " without packets")}");
            if (packets) for (var i = 0; i < Frames(seconds); i++) { Wall += Dt; Send(raceOn: false); }
            else Wall += seconds;
            End();
        }

        // Nothing arrives while rewinding, sometimes after a brief race-off moment. The game then
        // continues from the chosen earlier moment, its lap and race clocks back at that moment, and
        // its lap timer can read about zero for a sample first.
        private void Rewind(double seconds)
        {
            var steps = Math.Min(Frames(seconds), _history.Count - 3);
            if (steps < 2 || steps * Dt < .2) return;
            Begin($"rewind {steps * Dt:F2}s");
            if (Random.NextDouble() < .5) { Wall += Dt; Send(raceOn: false); }
            Wall += .5 + Random.NextDouble() * 3;
            _history.RemoveRange(_history.Count - steps, steps);
            _now = _history[^1];
            if (BrokenAt is { } broken && _now.Lap <= broken) BrokenAt = null;
            // Back at the very start of the race, the lap starts afresh.
            if (_number == 0 && _now.Race <= .5 && Math.Abs(_now.Race - _now.Lap) <= .1) BrokenAt = null;
            if (Random.NextDouble() < .5) Send(lapClock: .05 + Random.NextDouble() * .35);
            Send();
            End();
        }

        // The game keeps running but no packets arrive. A stretch the recording cannot see
        // means the lap cannot become a reference, unless a rewind goes back before it.
        private void LosePackets(double seconds)
        {
            var steps = Frames(seconds);
            if (steps < 2 || _now.Fraction + steps * Dt / Pace >= .97) return;
            Begin($"lost {steps * Dt:F2}s");
            var from = Position(_now.Fraction);
            var before = _now.Lap;
            for (var i = 0; i < steps; i++) Step(send: false);
            if (_now.Lap - before > 1 && Vector3.Distance(from, Position(_now.Fraction)) > 25) BrokenAt ??= before;
            Send();
            End();
        }

        // A reset puts the car back on the road behind where it was, at rest; the lap timer continues.
        private void ResetOnTrack(double meters)
        {
            var back = meters / (Math.Tau * 150);
            if (_now.Fraction - back < .02) return;
            Begin($"reset {meters:F0}m back");
            BrokenAt ??= _now.Lap;
            var gap = Random.NextDouble() < .5 ? Dt : .5 + Random.NextDouble() * 1.5;
            Wall += gap;
            _now = _now with { Game = _now.Game + gap, Race = _now.Race + gap, Lap = _now.Lap + gap, Fraction = _now.Fraction - back };
            _history.Add(_now);
            AtRest = true;
            Send();
            End();
        }

        // Every clock returns to zero at the start line.
        private void Restart()
        {
            Begin("restart");
            var gap = Random.NextDouble() < .5 ? Dt : .5 + Random.NextDouble() * 2;
            Wall += gap;
            _now = new(_now.Game + gap, 0, 0, 0);
            _number = 0;
            _lastLap = 0;
            BrokenAt = null;
            NextPace();
            _history.Clear();
            _history.Add(_now);
            Send();
            End();
        }
    }

    // Time Attack: the game sends no lap clocks, so laps are measured from start-line crossings.
    private sealed class TimeAttackSession(int seed, LapDeltaReference mode) : Session(seed, mode)
    {
        private static readonly Vector2 Gate = new(4267.63f, -5273.08f);
        private static readonly Vector2 Forward = Vector2.Normalize(new(-.16602f, .98612f));
        private readonly record struct Moment(double Game, double Fraction, Vector3 Position);
        private readonly List<Moment> _history = [];
        private Moment _now = new(100, -.02, Position(-.02));
        private double? _start;

        private static bool PastLine(double fraction)
        {
            var p = Position(fraction);
            return Vector2.Dot(new Vector2(p.X, p.Z) - Gate, Forward) > 0;
        }

        private static Vector3 Position(double fraction)
        {
            var right = new Vector2(Forward.Y, -Forward.X);
            var angle = fraction * Math.Tau;
            var point = Gate + Forward * (float)(150 * Math.Sin(angle)) + right * (float)(150 * (1 - Math.Cos(angle)));
            return new(point.X, 0, point.Y);
        }

        internal void Run()
        {
            NextPace();
            Send();
            for (var step = 0; step < 70; step++)
            {
                var roll = Random.NextDouble();
                if (roll < .38) Drive(1 + Random.NextDouble() * 25);
                else if (roll < .46) Pause(.3 + Random.NextDouble() * 30, packets: true);
                else if (roll < .53) Pause(.3 + Random.NextDouble() * (Random.NextDouble() < .5 ? 1 : 30), packets: false);
                else if (roll < .65) Rewind(.3 + Random.NextDouble() * 8);
                else if (roll < .72) LosePackets(.02 + Random.NextDouble() * 3);
                else if (roll < .78) ResetOnTrack(30 + Random.NextDouble() * 90);
                else if (roll < .84) ResetToStart();
                else if (roll < .90) Abandon();
                else Drive(40 + Random.NextDouble() * 60);
                // Nobody acts again within a few frames: the game sends for a moment before the next event.
                Drive(.1 + Random.NextDouble() * .2);
            }
        }

        private void Drive(double seconds)
        {
            for (var i = 0; i < Frames(seconds); i++) Step();
        }

        private void Step(bool send = true)
        {
            var frame = Frame();
            Wall += frame;
            var fraction = _now.Fraction + frame / Pace;
            // The gate counts a crossing once the car is past the line.
            if (Math.Abs(fraction) < .01 && !PastLine(_now.Fraction) && PastLine(fraction))
            {
                _start = _now.Game - _now.Fraction * Pace;
                BrokenAt = null;
                _history.Clear();
            }
            else if (fraction > .99 && !PastLine(_now.Fraction) && PastLine(fraction))
            {
                var crossing = _now.Game + (1 - _now.Fraction) * Pace;
                if (_start is { } start && Valid)
                {
                    Best = Best is { } best ? Math.Min(best, crossing - start) : crossing - start;
                    Previous = crossing - start;
                }
                _start = crossing;
                BrokenAt = null;
                fraction = (fraction - 1) * Pace / NextPace();
                _history.Clear();
            }
            _now = new(_now.Game + frame, fraction, Position(fraction));
            _history.Add(_now);
            if (send) Send();
        }

        private void Send(bool raceOn = true)
        {
            var state = LapDeltaTests.State(_now.Game, 0, 0, _now.Position) with
            {
                IsRaceOn = raceOn,
                GameTimestampMilliseconds = Timestamp(),
                ReceivedAtUtc = Received(),
                GroundSpeedMetersPerSecond = AtRest ? 0 : (float)(Math.Tau * 150 / Pace),
                Lap = new(_now.Position, 0, 0, 0, 0, 0)
            };
            AtRest = false;
            var reading = Tracker.Update(state, Mode, LapTimingMode.TimeAttack);
            Observe(reading, raceOn, _now.Game - _start, _now.Fraction);
        }

        // The game stops, and its lap timer with it; the PC's uptime runs on.
        private void Pause(double seconds, bool packets)
        {
            Begin($"pause {seconds:F2}s{(packets ? "" : " without packets")}");
            if (packets) for (var i = 0; i < Frames(seconds); i++) { Wall += Dt; Send(raceOn: false); }
            else Wall += seconds;
            End();
        }

        // Nothing arrives while rewinding, sometimes after a brief race-off moment. The car then
        // continues from the chosen earlier moment of this attempt, at that moment's speed. Rewinds
        // of at least a second, as players use them.
        private void Rewind(double seconds)
        {
            var steps = Math.Min(Math.Max(Frames(1), Frames(seconds)), _history.Count - 3);
            if (steps < Frames(1)) return;
            Begin($"rewind {steps * Dt:F2}s");
            if (Random.NextDouble() < .5) { Wall += Dt; Send(raceOn: false); }
            Wall += .5 + Random.NextDouble() * 3;
            _history.RemoveRange(_history.Count - steps, steps);
            _now = _history[^1];
            if (BrokenAt is { } broken && _now.Game - _start <= broken) BrokenAt = null;
            Send();
            End();
        }

        private void LosePackets(double seconds)
        {
            var steps = Frames(seconds);
            if (steps < 2 || _now.Fraction < .02 || _now.Fraction + steps * Dt / Pace >= .97) return;
            Begin($"lost {steps * Dt:F2}s");
            var from = _now.Position;
            var before = _now.Game - _start;
            for (var i = 0; i < steps; i++) Step(send: false);
            if (before is { } time && _now.Game - _start - time > 1 && Vector3.Distance(from, _now.Position) > 25) BrokenAt ??= time;
            Send();
            End();
        }

        // A reset on the circuit puts the car down at rest and ends the attempt; the next
        // start-line crossing begins one.
        private void ResetOnTrack(double meters)
        {
            var back = meters / (Math.Tau * 150);
            if (_now.Fraction - back < .02) return;
            Begin($"reset {meters:F0}m back");
            var gap = Random.NextDouble() < .5 ? Dt : .5 + Random.NextDouble() * 1.5;
            Wall += gap;
            _now = new(_now.Game + gap, _now.Fraction - back, Position(_now.Fraction - back));
            _history.Clear();
            _history.Add(_now);
            _start = null;
            AtRest = true;
            Send();
            End();
        }

        // A crash reset (or a restart) that places the car behind the start line.
        private void ResetToStart()
        {
            if (_now.Fraction is < .1 or > .9) return;
            Begin("reset to the start");
            var gap = Random.NextDouble() < .5 ? Dt : .5 + Random.NextDouble() * 1.5;
            Wall += gap;
            _now = new(_now.Game + gap, -.03, Position(-.03));
            _history.Clear();
            _history.Add(_now);
            _start = null;
            AtRest = true;
            Send();
            End();
        }

        // Give up on an attempt and drive straight back to behind the start line. Crossing it starts
        // a new attempt; the abandoned one is not a lap. Tested with a reference; a first attempt
        // that is abandoned has its own test.
        private void Abandon()
        {
            // Turning back from well into the lap; from near the end the way back is just a corner cut to the line.
            if (Best is null || _start is null || _now.Fraction is < .2 or > .7) return;
            Begin("abandon and drive back to the start");
            var from = _now.Position;
            var to = Position(-.03);
            var steps = (int)Math.Ceiling(Vector3.Distance(from, to) / (18 * Dt));
            for (var i = 1; i <= steps; i++)
            {
                Wall += Dt;
                _now = new(_now.Game + Dt, -.03, Vector3.Lerp(from, to, i / (float)steps));
                Send();
            }
            _history.Clear();
            _start = null;
            // Wisp can only tell the attempt was abandoned at the line; check from the new attempt on.
            while (_start is null) Step();
            End();
        }
    }
}
