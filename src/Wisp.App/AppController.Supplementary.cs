using Wisp.App.Supplementary;
using Wisp.Core.Runs;

namespace Wisp.App;

public sealed partial class AppController
{
    private readonly object _supplementaryObservationGate = new();
    private bool _supplementaryObservationsAttached, _supplementaryRunRecording, _supplementaryRunPreparing;
    private SupplementaryOperation? _supplementaryRunSave, _supplementaryFirstTelemetry;
    private bool _supplementaryHudRequested, _supplementaryDriftRequested;

    private void ObserveSupplementaryPresentationRequested(bool hud, bool drift)
    {
        if (!_supplementaryObservationsAttached) return;
        // This records policy-approved window requests with fresh telemetry, never display adoption.
        if (hud && !_supplementaryHudRequested)
        {
            _supplementaryHudRequested = true;
            SupplementaryObservations.Record("feature", "hud", "detected", "presentation-requested");
        }
        if (drift && !_supplementaryDriftRequested)
        {
            _supplementaryDriftRequested = true;
            SupplementaryObservations.Record("feature", "drift", "detected", "presentation-requested");
        }
    }

    internal void InitializeSupplementaryObservations()
    {
        if (_supplementaryObservationsAttached) return;
        _supplementaryObservationsAttached = true;
        _runRecording.StateChanged += ObserveSupplementaryRunState;
        _runRecording.RunSaved += ObserveSupplementaryRunSaved;
    }

    internal void DisposeSupplementaryObservations()
    {
        if (!_supplementaryObservationsAttached) return;
        _supplementaryObservationsAttached = false;
        _runRecording.StateChanged -= ObserveSupplementaryRunState;
        _runRecording.RunSaved -= ObserveSupplementaryRunSaved;
        _receiver.PacketAvailable -= ObserveSupplementaryFirstTelemetry;
        Interlocked.Exchange(ref _supplementaryFirstTelemetry, null)?.Complete("unknown");
        lock (_supplementaryObservationGate)
        { _supplementaryRunSave?.Dispose(); _supplementaryRunSave = null; }
    }

    private void BeginSupplementaryFirstTelemetry()
    {
        if (!_supplementaryObservationsAttached) return;
        var next = SupplementaryObservations.Begin("connection", "telemetry", "first-telemetry");
        Interlocked.Exchange(ref _supplementaryFirstTelemetry, next)?.Complete("unknown");
        _receiver.PacketAvailable -= ObserveSupplementaryFirstTelemetry;
        if (next is not null)
        {
            _receiver.PacketAvailable += ObserveSupplementaryFirstTelemetry;
            if (_receiver.Latest is not null) ObserveSupplementaryFirstTelemetry(null, EventArgs.Empty);
        }
    }

    private void ObserveSupplementaryFirstTelemetry(object? sender, EventArgs args)
    {
        // Receiver raises this only after accepting a parsed packet. Unsubscribe after the one observation.
        if (Interlocked.Exchange(ref _supplementaryFirstTelemetry, null) is not { } operation) return;
        _receiver.PacketAvailable -= ObserveSupplementaryFirstTelemetry;
        operation.Complete("success");
    }

    private void ObserveSupplementaryRunState(object? sender, EventArgs args)
    {
        lock (_supplementaryObservationGate)
        {
            var recording = _runRecording.IsRecording;
            var preparing = _runRecording.IsPreparing;
            if (recording && !_supplementaryRunRecording)
                SupplementaryObservations.Record("feature", "runs", "success", "capture");
            if (preparing && !_supplementaryRunPreparing)
            {
                // A prior no-samples save has no RunSaved or Error event. Keep its terminal outcome unknown.
                _supplementaryRunSave?.Dispose();
                _supplementaryRunSave = SupplementaryObservations.Begin("saved-data", "runs", "save");
            }
            if (!recording && !preparing && _runRecording.Error is not null)
            { _supplementaryRunSave?.Complete("failure"); _supplementaryRunSave = null; }
            _supplementaryRunRecording = recording; _supplementaryRunPreparing = preparing;
        }
    }

    private void ObserveSupplementaryRunSaved(RecordedRun unused)
    {
        // The event establishes durable save completion. The recorded gameplay object is never inspected or forwarded.
        lock (_supplementaryObservationGate)
        {
            if (_supplementaryRunSave is { } operation) operation.Complete("success");
            else SupplementaryObservations.Record("saved-data", "runs", "success", "save");
            _supplementaryRunSave = null;
        }
    }
}
