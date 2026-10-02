using System.Numerics;

namespace Wisp.Core.Runs;

public static partial class LapReviewAnalysis
{
    public const int MaximumPoints = 180_000;
    public const int MaximumLaps = 256;
    public const byte InputActiveMinimum = 13;

    public static LapReviewResult Build(RecordedRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Build(run, run.LapTimingMode ?? LapTimingMode.GameLaps, cancellationToken);
    }

    public static LapReviewResult Build(RecordedRun run, LapTimingMode timing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!Enum.IsDefined(timing)) throw new ArgumentOutOfRangeException(nameof(timing));
        cancellationToken.ThrowIfCancellationRequested();
        if (run.Samples.Length > MaximumPoints)
            return new([], "This recording exceeds the lap review sample limit; it was not truncated into an apparently complete lap.");

        var result = new List<LapReviewLap>();
        var clock = timing == LapTimingMode.TimeAttack ? new TimeAttackClock() : null;
        LapBuilder? current = null;
        RunSample? previous = null;
        LapTelemetry? previousLap = null;
        var breakPending = false;
        var hasPosition = false;
        for (var index = 0; index < run.Samples.Length; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var sample = run.Samples[index];
            if (!HasPosition(sample))
            {
                if (current is not null) current.Quality |= LapReviewQuality.MissingPosition | LapReviewQuality.TelemetryGap;
                breakPending = true;
                previous = null;
                previousLap = null;
                clock?.EndAttempt();
                continue;
            }
            hasPosition = true;
            if (!sample.IsDriving || !sample.State.IsRaceOn)
            {
                if (current is not null) current.Quality |= LapReviewQuality.TelemetryGap;
                breakPending = true;
                previous = null;
                previousLap = null;
                clock?.EndAttempt();
                continue;
            }

            // Saved Stopwatch ticks have no meaning in another process. The recorder's UTC
            // receipt time was derived from its monotonic clock, and is the clock's replay path.
            var lap = clock is null ? sample.State.Lap : clock.Update(sample.State with { ReceivedTimestamp = null });
            if (lap is null || !ValidTiming(lap))
            {
                Finish(null, false, LapReviewQuality.PartialEnd | LapReviewQuality.MissingTiming);
                previous = null;
                previousLap = null;
                breakPending = true;
                continue;
            }

            var timed = lap.CurrentLapSeconds > 0 || lap.LapNumber > 0 || lap.RacePosition > 0;
            var initialQuality = timing == LapTimingMode.TimeAttack ? LapReviewQuality.InferredTiming : LapReviewQuality.None;
            if (!timed) initialQuality |= LapReviewQuality.MissingTiming;
            var continuous = previous is not null && !breakPending && ContinuousSamples(previous, sample);
            var boundary = previousLap is not null && lap.LapNumber == previousLap.LapNumber + 1 &&
                lap.CurrentLapSeconds < previousLap.CurrentLapSeconds;
            // A continuous packet stream does not establish a continuous lap clock.
            // Use the same covered-span limits as section statistics; equal clocks
            // still preserve successive recorded positions and observed events.
            if (!boundary && previousLap is not null && lap.LapNumber == previousLap.LapNumber &&
                lap.CurrentLapSeconds - previousLap.CurrentLapSeconds is < 0 or > .25f)
                continuous = false;
            // The live Time Attack clock can correct a late packet at the line. Keep the
            // same bounded allowance as LapDeltaTracker without relaxing game lap clocks.
            var finishAllowance = timing == LapTimingMode.TimeAttack ? .05f : 0;
            var crossing = boundary && continuous && previousLap is not null &&
                lap.LastLapSeconds >= previousLap.CurrentLapSeconds - finishAllowance &&
                lap.LastLapSeconds - previousLap.CurrentLapSeconds + lap.CurrentLapSeconds is > 0 and <= 1;
            var changedCar = current is not null && current.Car != sample.State.CarOrdinal;
            var rewind = !boundary && current is { Points.Count: > 0 } &&
                (lap.LapNumber + 1 < current.Number || lap.LapNumber + 1 == current.Number &&
                    lap.CurrentLapSeconds < current.Points[^1].LapSeconds - .05);
            var moved = previous is null ? 0 : Vector3.Distance(previous.State.Lap!.Position.ToVector(), lap.Position.ToVector());
            var dt = previous is null ? 0 : Math.Max(0, sample.ElapsedSeconds - previous.ElapsedSeconds);
            var jumped = previous is not null && moved > Math.Max(25, dt * 180);
            var changedLap = current is not null && lap.LapNumber + 1 != current.Number;
            var changedCircuit = clock?.CircuitChanged == true;
            if (boundary)
            {
                double? line = null;
                if (crossing)
                {
                    var span = lap.LastLapSeconds - previousLap!.CurrentLapSeconds + lap.CurrentLapSeconds;
                    line = previous!.ElapsedSeconds + dt * Math.Clamp((lap.LastLapSeconds - previousLap.CurrentLapSeconds) / span, 0, 1);
                }
                Finish(line, crossing && !changedCar && !jumped, crossing ? LapReviewQuality.None : LapReviewQuality.Discontinuity,
                    lap.LastLapSeconds);
                current = new(sample.State.CarOrdinal, lap.LapNumber + 1, line ?? sample.ElapsedSeconds,
                    crossing && !changedCar && !jumped, initialQuality);
            }
            else if (changedCar || rewind || jumped || changedLap || changedCircuit)
            {
                var reason = rewind ? LapReviewQuality.Rewind : LapReviewQuality.Discontinuity;
                Finish(null, false, reason);
                current = new(sample.State.CarOrdinal, lap.LapNumber + 1, sample.ElapsedSeconds, false, initialQuality | reason);
                continuous = false;
            }
            else if (current is null)
            {
                // Race starts may be explicitly present. Otherwise only an observed crossing
                // establishes the beginning; joining a lap near its line is still partial.
                var atStart = timing == LapTimingMode.TimeAttack
                    ? lap.CurrentLapSeconds <= .25
                    : lap.CurrentLapSeconds == 0 && lap.RaceSeconds <= .25 && lap.RacePosition > 0;
                current = new(sample.State.CarOrdinal, lap.LapNumber + 1,
                    atStart ? Math.Max(0, sample.ElapsedSeconds - lap.CurrentLapSeconds) : sample.ElapsedSeconds, atStart, initialQuality);
            }

            if (current.Points.Count > 0 && (!continuous || breakPending)) current.Quality |= LapReviewQuality.TelemetryGap;
            var broken = current.Points.Count == 0 || !continuous || breakPending || jumped;
            var distance = current.Points.Count == 0 ? 0 : current.Points[^1].DistanceMeters + (broken ? 0 : moved);
            current.Points.Add(new(index, sample.ElapsedSeconds, lap.CurrentLapSeconds, distance, lap.Position, broken, sample));
            previous = sample;
            previousLap = lap;
            breakPending = false;
            if (result.Count >= MaximumLaps)
                return new(result.ToArray(), "Lap review reached its lap limit; later samples were not analyzed.");
        }
        Finish(null, false, LapReviewQuality.None);
        var complete = result.Count(lap => lap.IsComplete);
        return new(result.ToArray(), !hasPosition
            ? "This recording has no usable position telemetry. A track map cannot be reconstructed."
            : result.Count == 0 ? "No timed lap was captured in this timing mode. Time Attack needs a crossing on a supported circuit."
            : complete == 0 ? "No complete lap boundaries were captured. Partial recorded paths are available; missing portions are not reconstructed."
            : $"{complete} complete lap{(complete == 1 ? "" : "s")} captured. Wisp's recording checks do not establish an official clean lap.");

        void Finish(double? line, bool crossed, LapReviewQuality extra, double? duration = null)
        {
            if (current is null) return;
            if (current.Points.Count >= 2)
            {
                var quality = current.Quality | extra;
                if (!current.StartedAtLine) quality |= LapReviewQuality.PartialStart;
                if (!crossed) quality |= LapReviewQuality.PartialEnd;
                var complete = current.StartedAtLine && crossed && duration is > 0 &&
                    (quality & (LapReviewQuality.Rewind | LapReviewQuality.Discontinuity | LapReviewQuality.MissingTiming)) == 0;
                result.Add(new()
                {
                    RunId = run.Id,
                    RunName = run.Name,
                    Tune = run.Tune,
                    Number = current.Number,
                    CarOrdinal = current.Car,
                    TimingMode = timing,
                    StartSeconds = current.Start,
                    EndSeconds = line ?? current.Points[^1].RunSeconds,
                    DurationSeconds = complete ? duration : null,
                    IsComplete = complete,
                    Quality = quality,
                    Points = current.Points.ToArray()
                });
            }
            current = null;
        }
    }

    private static bool HasPosition(RunSample sample) => sample is not null && sample.State?.Lap is { } lap &&
        sample.State.CarOrdinal > 0 && double.IsFinite(sample.ElapsedSeconds) && sample.ElapsedSeconds >= 0 &&
        float.IsFinite(lap.Position.X) && float.IsFinite(lap.Position.Y) && float.IsFinite(lap.Position.Z) &&
        lap.Position.ToVector().LengthSquared() <= 1e12f;

    private static bool ValidTiming(LapTelemetry lap) => float.IsFinite(lap.CurrentLapSeconds) &&
        float.IsFinite(lap.LastLapSeconds) && float.IsFinite(lap.RaceSeconds) &&
        lap.CurrentLapSeconds is >= 0 and <= 86_400 && lap.LastLapSeconds is >= 0 and <= 86_400 && lap.RaceSeconds is >= 0 and <= 604_800;

    private static bool ContinuousSamples(RunSample a, RunSample b) => RunAnalysis.AreContinuous(a, b) ||
        a.IsDriving && b.IsDriving && a.State.IsRaceOn && b.State.IsRaceOn && a.Segment == b.Segment &&
        a.State.CarOrdinal == b.State.CarOrdinal && a.State.Drivetrain == b.State.Drivetrain &&
        a.ElapsedSeconds == b.ElapsedSeconds && a.State.GameTimestampMilliseconds == b.State.GameTimestampMilliseconds;

    private sealed class LapBuilder(int car, int number, double start, bool startedAtLine, LapReviewQuality quality)
    {
        internal int Car { get; } = car;
        internal int Number { get; } = number;
        internal double Start { get; } = start;
        internal bool StartedAtLine { get; } = startedAtLine;
        internal LapReviewQuality Quality { get; set; } = quality;
        internal List<LapReviewPoint> Points { get; } = [];
    }
}
