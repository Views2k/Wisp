using System.Diagnostics;
using System.Threading.Channels;
using Wisp.Core;

namespace Wisp.App.Laps;

internal sealed class LapDeltaService(LapReferenceStore? store = null) : IDisposable
{
    private Task _saving = Task.CompletedTask;
    private readonly Channel<(int Generation, VehicleState State)> _samples = Channel.CreateBounded<(int, VehicleState)>(
        new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private Task _worker = Task.CompletedTask;
    private bool _started;
    private sealed record Publication(int Generation, int Reference, LapDeltaReading Reading, LapMapReading? Map = null);
    private Publication _publication = new(0, 0, LapDeltaReading.Waiting);
    private int _generation, _enabled, _reference, _mapEnabled, _resetVersion, _timing;

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

    internal Task Completion => _worker;
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
        var tracker = new LapDeltaTracker();
        if (store?.Load() is { } kept) tracker.RestoreReferences(kept);
        using var recorder = ApplicationVersionInfo.DiagnosticBuildId is null || store is null ? null :
            LapDiagnosticsRecorder.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "LapDiagnostics"));
        var generation = -1;
        var resetVersion = -1;
        await foreach (var sample in _samples.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var current = Volatile.Read(ref _generation);
            if (sample.Generation != current || Volatile.Read(ref _enabled) == 0) continue;
            if (generation != current)
            {
                var reset = Volatile.Read(ref _resetVersion);
                if (reset != resetVersion) tracker.Reset(); else tracker.Interrupt();
                resetVersion = reset; generation = current;
            }
            var reference = Volatile.Read(ref _reference);
            var timing = (LapTimingMode)Volatile.Read(ref _timing);
            var reading = tracker.Update(sample.State, (LapDeltaReference)reference, timing);
            recorder?.Write(sample.State, timing, (LapDeltaReference)reference, reading);
            if (store is not null && tracker.TakeReferenceChange())
            {
                var session = tracker.ExportReferences();
                _saving = _saving.ContinueWith(_ => store.Save(session), TaskScheduler.Default);
            }
            var map = Volatile.Read(ref _mapEnabled) != 0 ? tracker.ReadMap(sample.State.ReceivedTimestamp ?? 0) : null;
            Volatile.Write(ref _publication, new(current, reference, reading, map));
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _enabled, 0);
        Reset();
        _samples.Writer.TryComplete();
    }
}
