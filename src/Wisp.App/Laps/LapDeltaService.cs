using System.IO;
using System.Diagnostics;
using System.Threading.Channels;
using Wisp.Core;

namespace Wisp.App.Laps;

internal sealed class LapDeltaService(LapReferenceStore? store = null) : IDisposable
{
    private readonly object _persistence = new();
    private Task _saving = Task.CompletedTask;
    private readonly Channel<(int Generation, VehicleState State)> _samples = Channel.CreateBounded<(int, VehicleState)>(
        new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private Task _worker = Task.CompletedTask;
    private bool _started;
    private sealed record Publication(int Generation, int Reference, LapDeltaReading Reading, LapMapReading? Map = null);
    private Publication _publication = new(0, 0, LapDeltaReading.Waiting);
    private int _generation, _enabled, _reference, _mapEnabled, _resetVersion, _timing, _cleared;

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

    internal void Configure(bool enabled, LapDeltaReference reference, bool mapEnabled = false, LapTimingMode timing = LapTimingMode.GameLaps)
    {
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
        if (state is null || !_samples.Writer.TryWrite((Volatile.Read(ref _generation), state))) Interrupt();
    }

    private async Task ProcessAsync()
    {
        // A reset before the worker started has already cleared the kept laps.
        await Persisted().ConfigureAwait(false);
        var resetVersion = Volatile.Read(ref _resetVersion);
        var applied = Volatile.Read(ref _cleared);
        var tracker = new LapDeltaTracker();
        if (store?.Load() is { } kept) tracker.RestoreReferences(kept);
        using var recorder = ApplicationVersionInfo.DiagnosticBuildId is null || store is null ? null :
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
                var reset = Volatile.Read(ref _resetVersion);
                // The first sample starts a fresh tracker, which keeps any laps waiting to be restored.
                if (reset != resetVersion) tracker.ResetReferences(); else if (generation >= 0) tracker.Interrupt();
                resetVersion = reset; generation = current; applied = cleared;
            }
            var reference = Volatile.Read(ref _reference);
            var timing = (LapTimingMode)Volatile.Read(ref _timing);
            var reading = tracker.Update(sample.State, (LapDeltaReference)reference, timing);
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
