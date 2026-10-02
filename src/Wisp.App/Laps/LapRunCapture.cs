using System.Diagnostics;
using System.Threading.Channels;
using Wisp.Core;
using Wisp.Core.Runs;
using Wisp.App.Runs;

namespace Wisp.App.Laps;

// Called only by the lap worker. Disk work has its own bounded queue and uses
// the same RunStore as manual recordings and library operations.
internal sealed class LapRunCapture
{
    private readonly Func<RecordedRun, Task> _save;
    private readonly Channel<RecordedRun> _pending = Channel.CreateBounded<RecordedRun>(
        new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _saving;
    private readonly List<RunSample> _samples = new(8192);
    private VehicleState? _previous;
    private RunRecordingContext? _previousContext;
    private VehicleState? _firstPacket;
    private long _traversal = -1;
    private long _skippedTraversal = -1;
    private int _enabled, _generation, _appliedGeneration = -1;
    private float _lastLapSeconds;
    private LapTimingMode _timing;
    private string _status = "Automatic lap recording is off.";
    private string? _error;

    internal LapRunCapture(RunStore store) : this(async run => { await store.SaveAsync(run).ConfigureAwait(false); }) { }

    internal LapRunCapture(Func<RecordedRun, Task> save)
    {
        _save = save;
        _saving = Task.Run(SaveAsync);
    }

    internal event Action<RecordedRun>? RunSaved;
    internal event Action<string>? StatusChanged;
    internal string Status => Volatile.Read(ref _error) ?? Volatile.Read(ref _status);

    internal void Configure(bool enabled)
    {
        if (Interlocked.Exchange(ref _enabled, enabled ? 1 : 0) == (enabled ? 1 : 0)) return;
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _error, null);
        SetStatus(enabled ? "Waiting for a complete lap from the start line." : "Automatic lap recording is off.");
    }

    internal void Interrupt()
    {
        _samples.Clear();
        _previous = null;
        _previousContext = null;
        _skippedTraversal = _traversal;
        _traversal = -1;
        if (Volatile.Read(ref _enabled) != 0)
            SetStatus("Interrupted lap skipped. Waiting for the next start line.");
    }

    internal void Observe(VehicleState state, LapTimingMode timing, LapCaptureFrame? frame, LapCaptureCompletion? completed,
        RunRecordingContext? context = null)
    {
        var generation = Volatile.Read(ref _generation);
        if (_appliedGeneration != generation)
        {
            Interrupt();
            _appliedGeneration = generation;
        }
        if (Volatile.Read(ref _enabled) == 0 || Volatile.Read(ref _error) is not null) return;
        var previous = _previous;
        var previousContext = _previousContext;
        _previous = state;
        _previousContext = context;
        if (frame is null)
        {
            if (_samples.Count > 0) Skip("Lap skipped because timing or driving data was interrupted.");
            return;
        }
        if (_samples.Count > 0 && (previous is null ||
            state.CarOrdinal != previous.CarOrdinal || state.Drivetrain != previous.Drivetrain || timing != _timing ||
            ReceiptSeconds(previous, state) is < 0 or > .25 ||
            unchecked((int)(state.GameTimestampMilliseconds - previous.GameTimestampMilliseconds)) is < 0 or > 250))
            Skip("Lap skipped because telemetry had a gap or the car changed.");

        if (completed is { } finish && _samples.Count > 0 && finish.Traversal == _traversal)
        {
            if (Add(state, context))
            {
                var run = new RecordedRun
                {
                    Name = AutomaticLapName(_samples[0].State, timing),
                    StartedAtUtc = _samples[0].State.ReceivedAtUtc,
                    FinishReason = timing == LapTimingMode.TimeAttack ? "Completed Time Attack lap" : "Completed game lap",
                    LapTimingMode = timing,
                    Samples = _samples.ToArray()
                };
                if (!_pending.Writer.TryWrite(run))
                    Fail("Lap saving stopped because storage could not keep up. Check storage, then turn automatic lap recording off and on.");
                else SetStatus("Saving completed lap…");
            }
            _samples.Clear();
            _traversal = -1;
        }

        if (_samples.Count > 0 && (frame.Traversal != _traversal || !frame.Eligible ||
            frame.Lap.CurrentLapSeconds + .01f < _lastLapSeconds))
            Skip("Partial or rewound lap skipped. Waiting for the next start line.");

        if (_samples.Count == 0 && frame.Traversal != _skippedTraversal && frame.Eligible &&
            frame.Lap.CurrentLapSeconds <= .25f && Volatile.Read(ref _error) is null)
        {
            _traversal = frame.Traversal;
            _timing = timing;
            // Preserve the game fields on both sides of a crossing. In
            // Time Attack their raw lap clocks remain zero for offline replay.
            if (previous is { IsRaceOn: true, Lap: not null } && previous.CarOrdinal == state.CarOrdinal &&
                previous.Drivetrain == state.Drivetrain && ReceiptSeconds(previous, state) is >= 0 and <= .25)
                Add(previous, previousContext);
            SetStatus("Recording lap for review.");
        }
        if (_traversal == frame.Traversal && frame.Traversal != _skippedTraversal)
        {
            Add(state, context);
            _lastLapSeconds = frame.Lap.CurrentLapSeconds;
        }
    }

    internal static string AutomaticLapName(VehicleState first, LapTimingMode timing) =>
        $"{(timing == LapTimingMode.TimeAttack ? "Time Attack lap" : "Game lap")} · Car {first.CarOrdinal} · {first.ReceivedAtUtc.ToLocalTime():MMM d, h:mm:ss.fff tt}";

    private bool Add(VehicleState state, RunRecordingContext? context)
    {
        if (_samples.Count == 0) _firstPacket = state;
        // Like manual recording, elapsed follows the game tick; two packets
        // in one FH6 tick must keep the same elapsed time for continuity checks.
        var elapsed = unchecked(state.GameTimestampMilliseconds - _firstPacket!.GameTimestampMilliseconds) / 1000d;
        var arrival = ReceiptSeconds(_firstPacket, state);
        if (_samples.Count >= RunStore.MaximumSamples || elapsed > 600 || arrival is < 0 or > 600)
        {
            Skip("Lap skipped because it exceeded the ten-minute recording limit.");
            return false;
        }
        // Persist monotonic arrival intervals as UTC for Time Attack replay;
        // wall-clock corrections and process-specific Stopwatch ticks are not replay clocks.
        var recorded = state with
        {
            ReceivedAtUtc = _firstPacket.ReceivedTimestamp is not null && state.ReceivedTimestamp is not null
                ? _firstPacket.ReceivedAtUtc.AddSeconds(arrival) : state.ReceivedAtUtc,
            ReceivedTimestamp = null
        };
        var radii = RecordingRadii(state, context);
        _samples.Add(new RunSample
        {
            ElapsedSeconds = elapsed,
            IsDriving = true,
            State = recorded,
            FrontRadiusMeters = radii?.FrontMeters,
            RearRadiusMeters = radii?.RearMeters,
            WheelSpeedMetersPerSecond = radii is { } calibrated ? DrivenWheelSpeed.MetersPerSecond(state, calibrated) : null
        });
        return true;
    }

    private static RollingRadii? RecordingRadii(VehicleState state, RunRecordingContext? context)
    {
        // Use the context captured with this packet, including the packet before a start-line crossing.
        // A later calibration must never be applied to an earlier sample.
        if (context is not { IsDriving: true, EffectiveTimestamp: > 0, FrontRadiusMeters: { } front, RearRadiusMeters: { } rear } ||
            state.ReceivedTimestamp is not { } timestamp || timestamp < context.EffectiveTimestamp ||
            Stopwatch.GetElapsedTime(context.EffectiveTimestamp, timestamp).TotalSeconds > .3 ||
            context.DrivingValidUntilTimestamp is { } until && timestamp > until ||
            context.CarOrdinal != state.CarOrdinal || context.Drivetrain != state.Drivetrain)
            return null;
        var radii = new RollingRadii(front, rear);
        return radii.IsPlausible ? radii : null;
    }

    private static double ReceiptSeconds(VehicleState first, VehicleState last) =>
        first.ReceivedTimestamp is { } from && last.ReceivedTimestamp is { } to
            ? (to - from) / (double)Stopwatch.Frequency
            : (last.ReceivedAtUtc - first.ReceivedAtUtc).TotalSeconds;

    private void Skip(string reason)
    {
        _skippedTraversal = _traversal;
        _traversal = -1;
        _samples.Clear();
        SetStatus(reason);
    }

    private void Fail(string message)
    {
        Volatile.Write(ref _error, message);
        SetStatus(message);
    }

    private void SetStatus(string status)
    {
        if (Interlocked.Exchange(ref _status, status) == status) return;
        StatusChanged?.Invoke(Status);
    }

    private async Task SaveAsync()
    {
        await foreach (var run in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _save(run).ConfigureAwait(false);
                SetStatus("Lap saved to Runs.");
                RunSaved?.Invoke(run);
            }
            catch (RunLibraryFullException)
            {
                Fail("Lap saving stopped because the run library is full. Export or remove runs, then turn automatic lap recording off and on.");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Fail("A lap could not be saved. Check available storage and folder access, then turn automatic lap recording off and on.");
            }
        }
    }

    internal async Task CompleteAsync()
    {
        _samples.Clear();
        _pending.Writer.TryComplete();
        await _saving.ConfigureAwait(false);
    }
}
