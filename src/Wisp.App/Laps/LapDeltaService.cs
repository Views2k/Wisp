using System.IO;
using System.Diagnostics;
using System.Threading.Channels;
using Wisp.Core;
using Wisp.Core.Runs;
using Wisp.App.Runs;

namespace Wisp.App.Laps;

internal sealed class LapDeltaService(LapReferenceStore? store = null) : IDisposable
{
    private readonly object _persistence = new();
    private Task _saving = Task.CompletedTask;
    private readonly Channel<(int Generation, VehicleState State, RunRecordingContext? Context)> _samples = Channel.CreateBounded<(int, VehicleState, RunRecordingContext?)>(
        new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private RunRecordingContext? _runContext;
    private Task _worker = Task.CompletedTask;
    private bool _started;
    private sealed record Publication(int Generation, int Reference, LapDeltaReading Reading, LapMapReading? Map = null);
    private Publication _publication = new(0, 0, LapDeltaReading.Waiting);
    private int _generation, _enabled, _reference, _mapEnabled, _resetVersion, _timing, _cleared, _recordLaps;
    private LapRunCapture? _capture;
    internal event Action<RecordedRun>? LapSaved;
    internal event Action<string>? LapRecordingStatusChanged;
    internal string LapRecordingStatus => _capture?.Status ?? "Automatic lap recording is off.";

    internal void AttachRunStore(RunStore runStore)
    {
        if (_capture is not null) throw new InvalidOperationException("The lap run store is already attached.");
        _capture = new LapRunCapture(runStore);
        _capture.RunSaved += run => LapSaved?.Invoke(run);
        _capture.StatusChanged += status => LapRecordingStatusChanged?.Invoke(status);
    }

    internal LapMapReading? LatestMap
    {
        get
        {
            var published = Volatile.Read(ref _publication);
            var map = published.Map;
            return Volatile.Read(ref _enabled) != 0 && Volatile.Read(ref _mapEnabled) != 0 &&
                published.Generation == Volatile.Read(ref _generation) && map is { ReceivedTimestamp: > 0 } &&
                Stopwatch.GetElapsedTime(map.ReceivedTimestamp) <= TimeSpan.FromMilliseconds(500) ? map : null;
        }
    }

    // The worker's lifetime includes writing the reference laps it kept, so a clean shutdown
    // leaves the latest ones on disk for a restart while the game keeps running.
    internal Task Completion => CompleteAsync();

    private async Task CompleteAsync()
    {
        await _worker.ConfigureAwait(false);
        await Persisted().ConfigureAwait(false);
        if (_capture is { } capture) await capture.CompleteAsync().ConfigureAwait(false);
    }

    private Task Persisted()
    {
        lock (_persistence) return _saving;
    }

    private void Persist(Action write)
    {
        lock (_persistence) _saving = _saving.ContinueWith(_ => write(), TaskScheduler.Default);
    }
    internal int Generation => Volatile.Read(ref _generation);
    internal LapDeltaReading Latest
    {
        get
        {
            var published = Volatile.Read(ref _publication);
            if (Volatile.Read(ref _enabled) == 0 || published.Generation != Volatile.Read(ref _generation) || published.Reference != Volatile.Read(ref _reference)) return LapDeltaReading.Waiting;
            var reading = published.Reading;
            return reading.ReceivedTimestamp <= 0 ||
                Stopwatch.GetElapsedTime(reading.ReceivedTimestamp) > TimeSpan.FromMilliseconds(500)
                ? LapDeltaReading.Waiting : reading;
        }
    }

    internal void Configure(bool enabled, LapDeltaReference reference, bool mapEnabled = false, LapTimingMode timing = LapTimingMode.GameLaps,
        bool recordLaps = false)
    {
        _capture?.Configure(enabled && recordLaps);
        if (Interlocked.Exchange(ref _recordLaps, recordLaps ? 1 : 0) != (recordLaps ? 1 : 0)) Interrupt();
        if (enabled && !_started) { _started = true; _worker = Task.Run(ProcessAsync); }
        Volatile.Write(ref _reference, (int)reference);
        Volatile.Write(ref _mapEnabled, mapEnabled ? 1 : 0);
        if (Interlocked.Exchange(ref _timing, (int)timing) != (int)timing) Reset();
        if (Interlocked.Exchange(ref _enabled, enabled ? 1 : 0) != (enabled ? 1 : 0)) Interrupt();
    }

    internal void Reset()
    {
        Interlocked.Increment(ref _resetVersion);
        Interrupt();
    }

    // Reset reference laps: forget them now, including kept ones not yet restored and the copy on
    // disk, without waiting for more telemetry.
    internal void ResetReferences()
    {
        Interlocked.Increment(ref _cleared);
        Reset();
        if (store is not null) Persist(() => store.Save(null));
    }

    private void Interrupt()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _publication, new(Volatile.Read(ref _generation), Volatile.Read(ref _reference), LapDeltaReading.Waiting));
    }

    // Called on the receiver worker; never blocks packet reception or writes to disk.
    internal void Observe(VehicleState? state)
    {
        if (Volatile.Read(ref _enabled) == 0) return;
        if (state is null || !_samples.Writer.TryWrite((Volatile.Read(ref _generation), state, Volatile.Read(ref _runContext)))) Interrupt();
    }

    internal void UpdateRunContext(RunRecordingContext context) => Volatile.Write(ref _runContext, context);

    private async Task ProcessAsync()
    {
        // A reset before the worker started has already cleared the kept laps.
        await Persisted().ConfigureAwait(false);
        var resetVersion = Volatile.Read(ref _resetVersion);
        // This tracker has not consumed an explicit clear yet. A startup load
        // must not make a pending clear look as though it was already applied.
        var applied = 0;
        var tracker = new LapDeltaTracker();
        if (store?.Load() is { } kept) tracker.RestoreReferences(kept);
        using var recorder = !ApplicationVersionInfo.LapDiagnosticsEnabled || store is null ? null :
            LapDiagnosticsRecorder.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "LapDiagnostics"));
        var generation = -1;
        await foreach (var sample in _samples.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Read before the generation: a reset changes both, in that order.
            var cleared = Volatile.Read(ref _cleared);
            var current = Volatile.Read(ref _generation);
            if (sample.Generation != current || Volatile.Read(ref _enabled) == 0) continue;
            if (generation != current)
            {
                _capture?.Interrupt();
                var reset = Volatile.Read(ref _resetVersion);
                // The first sample starts a fresh tracker, which keeps any laps waiting to be restored.
                if (reset != resetVersion || cleared != applied) tracker.ResetReferences(); else if (generation >= 0) tracker.Interrupt();
                resetVersion = reset; generation = current; applied = cleared;
            }
            var reference = Volatile.Read(ref _reference);
            var timing = (LapTimingMode)Volatile.Read(ref _timing);
            var reading = tracker.Update(sample.State, (LapDeltaReference)reference, timing,
                capture: Volatile.Read(ref _recordLaps) != 0);
            _capture?.Observe(sample.State, timing, tracker.CaptureFrame, tracker.CompletedCapture, sample.Context);
            recorder?.Write(sample.State, timing, (LapDeltaReference)reference, reading);
            if (store is not null && tracker.TakeReferenceChange())
            {
                var session = tracker.ExportReferences();
                var version = applied;
                // Laps from before a reset never overwrite the cleared file.
                Persist(() => { if (Volatile.Read(ref _cleared) == version) store.Save(session); });
            }
            var map = Volatile.Read(ref _mapEnabled) != 0 ? tracker.ReadMap(sample.State.ReceivedTimestamp ?? 0) : null;
            Volatile.Write(ref _publication, new(current, reference, reading, map));
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _enabled, 0);
        Interrupt();
        _samples.Writer.TryComplete();
    }
}
