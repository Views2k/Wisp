using System.Diagnostics;
using Wisp.Core;

namespace Wisp.App;

internal sealed class ShiftCalibrationManager(string? directory, Func<long>? timestamp = null)
{
    private readonly object _gate = new();
    private readonly ShiftCalibrationStore _store = new(directory);
    private readonly Func<long> _clock = timestamp ?? Stopwatch.GetTimestamp;
    private ShiftCalibrationContext? _context;
    private ShiftCalibrationSession? _session;
    private ShiftCalibrationResult? _calibrated;
    private ShiftCuePerformance? _native;
    private string _build = "";
    private string _status = "Enable shift guidance, then return to driving to calibrate.";
    private long _generation;
    private long _nextEvaluation;
    private bool _available;
    private bool _canArm;
    private bool _loadPending;
    private Task _work = Task.CompletedTask;

    internal string Status { get { lock (_gate) return _status; } }
    internal bool CanStart { get { lock (_gate) return _canArm && _session is null && !_loadPending; } }
    internal bool Active { get { lock (_gate) return _session is not null; } }
    internal Task PendingWork { get { lock (_gate) return _work; } }

    internal void Update(VehicleState? state, NativeHudSnapshot native, string build, bool enabled, long now)
    {
        lock (_gate)
        {
            var data = native.ShiftPerformance;
            _canArm = CanArmCachedContext(state, build, enabled);
            _available = enabled && state is { IsRaceOn: true, IsElectric: false } &&
                state.CarOrdinal == data?.CarOrdinal && native.CarOrdinal == state.CarOrdinal &&
                native.Available && native.Status == NativeAssistProviderStatus.Ready &&
                native.GameplayVisibility == NativeGameplayVisibility.Visible &&
                data.Profile is { IsValid: true } && data.Fingerprint.Length > 0 && build.Length > 0 &&
                now >= data.ObservedTimestamp && now - data.ObservedTimestamp <= Stopwatch.Frequency &&
                now >= native.VisibilityObservedTimestamp && native.VisibilityObservedTimestamp > 0 &&
                now - native.VisibilityObservedTimestamp <= Stopwatch.Frequency / 4 &&
                state.ReceivedTimestamp is { } receipt && now >= receipt &&
                now - receipt <= Stopwatch.Frequency * .15;
            _native = _available ? data : null;
            if (!_available)
            {
                if (!enabled) CancelLocked("Shift guidance is off.");
                else _status = _session is null
                    ? _canArm ? "Ready to arm calibration. Return to driving after pressing Calibrate."
                    : state?.IsElectric == true ? "Electric vehicles do not need shift calibration."
                    : data?.Status is "UnsupportedBuild"
                        ? "Shift guidance requires Steam FH6 6.440.853.0. It is not available on the Xbox app / Microsoft Store (Game Pass) edition."
                    : data?.Status is "UnsupportedPowertrain" or "UnsupportedTransmission"
                        ? "Calibration is unavailable for this vehicle configuration."
                        : "Return to driving briefly so Wisp can read the car, then come back here to start calibration."
                    : "Calibration paused — return to driving in the same car and tune.";
                return;
            }
            if (_context is null || _context.Fingerprint != data!.Fingerprint || _context.CarOrdinal != data.CarOrdinal || _build != build)
            {
                _session?.InvalidateContext();
                _session = null;
                _calibrated = null;
                _context = new(data!.CarOrdinal, data.Fingerprint, data.Profile!.ForwardRatios,
                    data.Profile.GearAccelerationFactors, data.Metadata?.ConfiguredOperatingCeilingRpm);
                _build = build;
                _generation++;
                if (!_context.IsValid)
                {
                    _context = null;
                    _available = false;
                    _canArm = false;
                    _native = null;
                    _loadPending = false;
                    _status = "Calibration is unavailable for this vehicle configuration.";
                    return;
                }
                _loadPending = true;
                _status = "Checking saved calibration for this car and tune…";
            }
            _canArm = CanArmCachedContext(state, build, enabled);
            if (!_work.IsCompleted) return;
            if (_loadPending)
            {
                var context = _context!;
                var generation = _generation;
                var identity = _build;
                _work = Task.Run(() =>
                {
                    var saved = _store.Load(identity, context);
                    lock (_gate)
                    {
                        if (generation != _generation) return;
                        _loadPending = false;
                        _calibrated = saved;
                        _status = saved is null ? "Ready to calibrate this car and tune." : SavedStatus(saved);
                    }
                });
            }
            else if (_session is { } session && now >= _nextEvaluation)
            {
                var generation = _generation;
                var identity = _build;
                _nextEvaluation = now + Stopwatch.Frequency;
                _work = Task.Run(() => Evaluate(session, generation, identity));
            }
            else if (_session is null)
                _status = _calibrated is null ? "Ready to calibrate this car and tune." : SavedStatus(_calibrated);
        }
    }

    internal bool Start()
    {
        lock (_gate)
        {
            if (!_canArm || _context is null || _session is not null || _loadPending) return false;
            _generation++;
            _session = new(_context, Stopwatch.Frequency);
            _nextEvaluation = 0;
            _status = "Calibration armed. Return to driving, make the rolling pull, then upshift and keep accelerating.";
            return true;
        }
    }

    internal void Cancel()
    {
        lock (_gate) CancelLocked(_calibrated is null ? "Calibration cancelled. Saved data was not changed." : SavedStatus(_calibrated));
    }

    internal Task Suspend()
    {
        lock (_gate)
        {
            _available = false;
            _canArm = false;
            _native = null;
            _session?.InvalidateContext();
            _session = null;
            _generation++;
            _loadPending = _context is not null && _calibrated is null;
            _status = "Calibration stopped. Saved calibrations were preserved.";
            return _work;
        }
    }

    private bool CanArmCachedContext(VehicleState? state, string build, bool enabled) =>
        enabled && _context is { IsValid: true } && build == _build &&
        state is not null && (!state.IsElectric && state.CarOrdinal == _context.CarOrdinal ||
            !state.IsRaceOn && state.CarOrdinal <= 0);

    private void CancelLocked(string status)
    {
        if (_session is not null)
        {
            _session.InvalidateContext();
            _session = null;
            _generation++;
        }
        _status = status;
    }

    internal void Observe(VehicleState? state)
    {
        if (Volatile.Read(ref _session) is null) return;
        ShiftCalibrationSession? session;
        ShiftCuePerformance? data;
        lock (_gate) { session = _session; data = _available ? _native : null; }
        if (session is null) return;
        if (state is null || data is null)
        {
            session.BreakObservation();
            return;
        }
        var now = state.ReceivedTimestamp ?? _clock();
        if (now < data.ObservedTimestamp || now - data.ObservedTimestamp > Stopwatch.Frequency ||
            data.LiveState is not { } live || live.CarOrdinal != state.CarOrdinal || now < live.ObservedTimestamp ||
            now - live.ObservedTimestamp > Stopwatch.Frequency * .15 || live.AlternateLimiterBranch)
        {
            session.BreakObservation();
            return;
        }
        var sameGear = live.CurrentGear == (int)state.Gear && live.RequestedGear == (int)state.Gear;
        // Keep transition packets so the learner can bracket a real upshift;
        // engine-output interventions must not enter its steady curve.
        var outputSettled = sameGear && live.OutputControl >= .99 && !live.SecondaryLimiterActive;
        session.Observe(state, data.Fingerprint, now,
            sameGear && live.LimiterActive && !live.SecondaryLimiterActive, outputSettled);
    }

    internal ShiftCuePerformance? Apply(ShiftCuePerformance? native)
    {
        lock (_gate)
        {
            if (native is null) return null;
            if (!_available || _session is not null || _calibrated is not { Ready: true, Profile: { } profile } result ||
                result.Context.Fingerprint != native.Fingerprint || result.Context.CarOrdinal != native.CarOrdinal)
                return native with { Status = "CalibrationRequired", Profile = null, Gears = [] };
            return native with
            {
                Profile = profile,
                Gears = result.Gears,
                Metadata = native.Metadata is { } metadata ? metadata with { TorqueModel = "measured-calibration" } : null
            };
        }
    }

    private void Evaluate(ShiftCalibrationSession session, long generation, string build)
    {
        var result = session.Evaluate();
        lock (_gate)
        {
            if (generation != _generation || !ReferenceEquals(session, _session)) return;
            _status = result.Reason;
            if (!result.Ready || !_available) return;
        }
        var saved = _store.Save(build, result, commit =>
        {
            lock (_gate)
            {
                if (generation != _generation || !ReferenceEquals(session, _session) || !_available) return false;
                commit();
                _calibrated = result;
                _session = null;
                _status = SavedStatus(result);
                return true;
            }
        });
        if (!saved)
            lock (_gate)
                if (generation == _generation && ReferenceEquals(session, _session))
                    _status = "Calibration passed, but could not be saved. Check available disk space and folder access.";
    }

    private static string SavedStatus(ShiftCalibrationResult result) =>
        $"Calibration saved · {result.CoveredMinimumRpm:N0}–{result.CoveredMaximumRpm:N0} rpm · matching car and tune";
}
