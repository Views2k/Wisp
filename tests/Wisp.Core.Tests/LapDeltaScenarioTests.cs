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
        private readonly List<string> _events = [];
        protected double Wall = 100, Pace = 60;
        protected double? Best, Previous;
        protected bool Valid = true;
        private int _settle;
        private bool _quiet;

        protected double NextPace() => Pace = 55 + Random.NextDouble() * 10;

        protected void Begin(string name)
        {
            _events.Add(name);
            _quiet = true;
        }

        protected void End()
        {
            _quiet = false;
            _settle = 10;
        }

        protected void Observe(LapDeltaReading reading, bool raceOn, double? lapTime, double fraction)
        {
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
                Require(Math.Abs(reading.ReferenceSeconds!.Value - reference) < .01,
                    $"reference {reading.ReferenceSeconds:F3} s instead of {reference:F3} s", fraction);
                var expected = time - reference * fraction;
                Require(Math.Abs(reading.Seconds!.Value - expected) < .03,
                    $"delta {reading.Seconds:F3} s instead of {expected:F3} s", fraction);
            }
            else
            {
                var expected = Valid ? LapDeltaStatus.RecordingLap : LapDeltaStatus.WaitingForLap;
                Require(reading.Status == expected, $"expected {expected}, got {reading.Status}", fraction);
            }
        }

        private void Require(bool condition, string message, double fraction) =>
            Assert.True(condition, $"{GetType().Name} seed {seed}, {Mode}, at {fraction:P1} of the lap: {message}. " +
                $"Recent events: {string.Join(", ", _events.TakeLast(6))}");
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
                if (roll < .40) Drive(1 + Random.NextDouble() * 25);
                else if (roll < .50) Pause(.2 + Random.NextDouble() * 30, packets: true);
                else if (roll < .57) Pause(1.5 + Random.NextDouble() * 30, packets: false);
                else if (roll < .69) Rewind(.3 + Random.NextDouble() * 8);
                else if (roll < .77) LosePackets(.2 + Random.NextDouble() * 3);
                else if (roll < .85) ResetOnTrack(30 + Random.NextDouble() * 90);
                else if (roll < .90) Restart();
                else Drive(40 + Random.NextDouble() * 60);
            }
        }

        private void Drive(double seconds)
        {
            for (var i = 0; i < seconds * 10; i++) Step();
        }

        private void Step(bool send = true)
        {
            Wall += .1;
            var next = _now with { Game = _now.Game + .1, Race = _now.Race + .1, Lap = _now.Lap + .1, Fraction = _now.Fraction + .1 / Pace };
            if (next.Fraction >= 1)
            {
                var past = (next.Fraction - 1) * Pace;
                var duration = next.Lap - past;
                if (Valid)
                {
                    Best = Best is { } best ? Math.Min(best, duration) : duration;
                    Previous = duration;
                }
                _lastLap = duration;
                _number++;
                Valid = true;
                next = next with { Lap = past, Fraction = next.Fraction - 1 };
                NextPace();
                _history.Clear();
            }
            _now = next;
            _history.Add(_now);
            if (send) Send();
        }

        private void Send(bool raceOn = true)
        {
            var state = LapDeltaTests.State(_now.Race, _now.Lap, _number, Position(_now.Fraction), _lastLap) with
            {
                IsRaceOn = raceOn,
                GameTimestampMilliseconds = unchecked((uint)Math.Round(_now.Game * 1000)),
                ReceivedAtUtc = Epoch.AddSeconds(Wall)
            };
            Observe(Tracker.Update(state, Mode), raceOn, _now.Lap, _now.Fraction);
        }

        // The game stops, including its timestamp. It either keeps sending race-off packets or none.
        private void Pause(double seconds, bool packets)
        {
            Begin($"pause {seconds:F1}s{(packets ? "" : " without packets")}");
            if (packets) for (var i = 0; i < seconds * 10; i++) { Wall += .1; Send(raceOn: false); }
            else Wall += seconds;
            End();
        }

        // The game returns to earlier moments of this lap, one per packet.
        private void Rewind(double seconds)
        {
            var steps = Math.Min((int)(seconds * 10), _history.Count - 3);
            if (steps < 2) return;
            Begin($"rewind {steps / 10d:F1}s");
            for (var i = 0; i < steps; i++)
            {
                _history.RemoveAt(_history.Count - 1);
                _now = _history[^1];
                Wall += .1;
                Send();
            }
            End();
        }

        // The game keeps running but no packets arrive. A stretch the recording cannot see
        // means the lap cannot become a reference.
        private void LosePackets(double seconds)
        {
            var steps = (int)(seconds * 10);
            if (steps < 2 || _now.Fraction + steps * .1 / Pace >= .97) return;
            Begin($"lost {steps / 10d:F1}s");
            var from = Position(_now.Fraction);
            for (var i = 1; i < steps; i++) Step(send: false);
            Step(send: false);
            if (steps > 10 && Vector3.Distance(from, Position(_now.Fraction)) > 25) Valid = false;
            Send();
            End();
        }

        // A reset puts the car back on the road behind where it was; the lap timer continues.
        private void ResetOnTrack(double meters)
        {
            var back = meters / (Math.Tau * 150);
            if (_now.Fraction - back < .02) return;
            Begin($"reset {meters:F0}m back");
            Wall += .1;
            _now = _now with { Game = _now.Game + .1, Race = _now.Race + .1, Lap = _now.Lap + .1, Fraction = _now.Fraction - back };
            _history.Add(_now);
            Valid = false;
            Send();
            End();
        }

        // Every clock returns to zero at the start line.
        private void Restart()
        {
            Begin("restart");
            Wall += .1;
            _now = new(_now.Game + .1, 0, 0, 0);
            _number = 0;
            _lastLap = 0;
            Valid = true;
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
                if (roll < .40) Drive(1 + Random.NextDouble() * 25);
                else if (roll < .50) Pause(.2 + Random.NextDouble() * 30, packets: true);
                else if (roll < .56) Pause(1.5 + Random.NextDouble() * 30, packets: false);
                else if (roll < .68) Rewind(.3 + Random.NextDouble() * 8);
                else if (roll < .75) LosePackets(.2 + Random.NextDouble() * 3);
                else if (roll < .81) ResetOnTrack(30 + Random.NextDouble() * 90);
                else if (roll < .87) ResetToStart();
                else if (roll < .92) Abandon();
                else Drive(40 + Random.NextDouble() * 60);
            }
        }

        private void Drive(double seconds)
        {
            for (var i = 0; i < seconds * 10; i++) Step();
        }

        private void Step(bool send = true)
        {
            Wall += .1;
            var fraction = _now.Fraction + .1 / Pace;
            if (_now.Fraction < 0 && fraction >= 0)
            {
                _start = _now.Game - _now.Fraction * Pace;
                Valid = true;
                _history.Clear();
            }
            else if (fraction >= 1)
            {
                var crossing = _now.Game + (1 - _now.Fraction) * Pace;
                if (_start is { } start && Valid)
                {
                    Best = Best is { } best ? Math.Min(best, crossing - start) : crossing - start;
                    Previous = crossing - start;
                }
                _start = crossing;
                Valid = true;
                fraction -= 1;
                NextPace();
                _history.Clear();
            }
            _now = new(_now.Game + .1, fraction, Position(fraction));
            _history.Add(_now);
            if (send) Send();
        }

        private void Send(bool raceOn = true)
        {
            var state = LapDeltaTests.State(_now.Game, 0, 0, _now.Position) with
            {
                IsRaceOn = raceOn,
                ReceivedAtUtc = Epoch.AddSeconds(Wall),
                Lap = new(_now.Position, 0, 0, 0, 0, 0)
            };
            var reading = Tracker.Update(state, Mode, LapTimingMode.TimeAttack);
            Observe(reading, raceOn, _now.Game - _start, _now.Fraction);
        }

        private void Pause(double seconds, bool packets)
        {
            Begin($"pause {seconds:F1}s{(packets ? "" : " without packets")}");
            if (packets) for (var i = 0; i < seconds * 10; i++) { Wall += .1; Send(raceOn: false); }
            else Wall += seconds;
            End();
        }

        private void Rewind(double seconds)
        {
            var steps = Math.Min((int)(seconds * 10), _history.Count - 3);
            if (steps < 2) return;
            Begin($"rewind {steps / 10d:F1}s");
            for (var i = 0; i < steps; i++)
            {
                _history.RemoveAt(_history.Count - 1);
                _now = _history[^1];
                Wall += .1;
                Send();
            }
            End();
        }

        private void LosePackets(double seconds)
        {
            var steps = (int)(seconds * 10);
            if (steps < 2 || _now.Fraction < .02 || _now.Fraction + steps * .1 / Pace >= .97) return;
            Begin($"lost {steps / 10d:F1}s");
            var from = _now.Position;
            for (var i = 1; i < steps; i++) Step(send: false);
            Step(send: false);
            if (steps > 10 && Vector3.Distance(from, _now.Position) > 25) Valid = false;
            Send();
            End();
        }

        // A reset on the circuit ends the attempt; the next start-line crossing begins one.
        private void ResetOnTrack(double meters)
        {
            var back = meters / (Math.Tau * 150);
            if (_now.Fraction - back < .02) return;
            Begin($"reset {meters:F0}m back");
            Wall += .1;
            _now = new(_now.Game + .1, _now.Fraction - back, Position(_now.Fraction - back));
            _history.Clear();
            _history.Add(_now);
            _start = null;
            Valid = false;
            Send();
            End();
        }

        // A crash reset (or a restart) that places the car behind the start line.
        private void ResetToStart()
        {
            if (_now.Fraction is < .1 or > .9) return;
            Begin("reset to the start");
            Wall += .1;
            _now = new(_now.Game + .1, -.03, Position(-.03));
            _history.Clear();
            _history.Add(_now);
            _start = null;
            Valid = false;
            Send();
            End();
        }

        // Give up on an attempt and drive straight back to behind the start line. Crossing it starts
        // a new attempt; the abandoned one is not a lap. Tested with a reference; a first attempt
        // that is abandoned has its own test.
        private void Abandon()
        {
            if (Best is null || _start is null || _now.Fraction is < .2 or > .9) return;
            Begin("abandon and drive back to the start");
            var from = _now.Position;
            var to = Position(-.03);
            var steps = (int)Math.Ceiling(Vector3.Distance(from, to) / 1.8);
            for (var i = 1; i <= steps; i++)
            {
                Wall += .1;
                _now = new(_now.Game + .1, -.03, Vector3.Lerp(from, to, i / (float)steps));
                Send();
            }
            _history.Clear();
            _start = null;
            Valid = false;
            // The crossing itself is checked after the usual second of driving.
            Drive(.5);
            End();
        }
    }
}
