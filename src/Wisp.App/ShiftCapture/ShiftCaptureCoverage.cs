using Wisp.Core;

namespace Wisp.App;

// Bounded evidence bookkeeping, not an optimal-shift validator. Raw session rows
// remain authoritative; thresholds below only select useful, uncontaminated checks.
internal sealed class ShiftCaptureCoverage
{
    private const int MaximumProfiles = 8, MaximumShifts = 64, MaximumButtons = 128;
    private readonly object _gate = new();
    private readonly long _frequency;
    private readonly Dictionary<string, Profile> _profiles = new(StringComparer.Ordinal);
    private readonly Queue<ShiftCaptureButtonEvent> _buttons = new();
    private readonly List<Shift> _shifts = [];
    private Observation? _previous;
    private Observation? _neutralFrom;
    private bool _neutralTorqueCut;
    private bool _neutralCleanLoad = true;
    private Shift? _recovering;
    private long _lastPacketQpc, _lastAdvancedQpc, _epochStartedQpc, _packets, _repeatedTimestamps, _invalid, _gaps, _identityChanges;
    private uint? _lastGameTimestamp;
    private bool _clockStalled;
    private bool _truncated;
    private string? _lastProfileKey;

    internal ShiftCaptureCoverage(long qpcFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        _frequency = qpcFrequency;
    }

    internal void ObserveButton(ShiftCaptureButtonEvent button, long qpc)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (!button.Pressed || button.Button != "B" || button.Action != "upshift" ||
            button.EarliestObservedEdgeQpc <= 0 || button.LatestObservedEdgeQpc != qpc ||
            !Within(qpc, button.EarliestObservedEdgeQpc, .15)) return;
        lock (_gate)
        {
            _buttons.Enqueue(button);
            if (_buttons.Count > MaximumButtons) { _buttons.Dequeue(); _truncated = true; }
        }
    }

    internal void InvalidateContext()
    {
        lock (_gate) ResetContinuity();
    }

    internal void ObserveTelemetry(VehicleState state, long qpc, string? profileFingerprint)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _packets++;
            if (qpc <= 0 || qpc <= _lastPacketQpc || !Valid(state) ||
                string.IsNullOrWhiteSpace(profileFingerprint) || profileFingerprint.Length > 128)
            {
                _invalid++;
                ResetContinuity();
                if (qpc > _lastPacketQpc) _lastPacketQpc = qpc;
                return;
            }
            if (_lastPacketQpc > 0 && !Within(qpc, _lastPacketQpc, .1)) { _gaps++; ResetContinuity(); }
            _lastPacketQpc = qpc;
            var key = state.CarOrdinal + ":" + profileFingerprint;
            if (!_profiles.TryGetValue(key, out var profile))
            {
                if (_profiles.Count == MaximumProfiles) { _truncated = true; ResetContinuity(); return; }
                profile = new(state.CarOrdinal, profileFingerprint);
                _profiles.Add(key, profile);
            }
            if (_lastProfileKey is not null && _lastProfileKey != key)
            {
                _identityChanges++;
                ResetContinuity();
            }
            _lastProfileKey = key;
            if (_lastGameTimestamp is { } previousGameTimestamp)
            {
                var advance = unchecked((int)(state.GameTimestampMilliseconds - previousGameTimestamp));
                if (advance < 0)
                {
                    _invalid++;
                    ResetContinuity();
                    return;
                }
                if (advance == 0)
                {
                    // FH6 emits distinct physical observations with the same game
                    // timestamp. Keep their ingress brackets, including neutral.
                    _repeatedTimestamps++;
                    if (!Within(qpc, _lastAdvancedQpc, .25))
                    {
                        if (!_clockStalled) _gaps++;
                        ResetContinuity(resetClock: false);
                        _clockStalled = true;
                        return;
                    }
                }
                else
                {
                    if (advance > 100) { _gaps++; ResetContinuity(); }
                    _lastAdvancedQpc = qpc;
                    _clockStalled = false;
                }
            }
            else _lastAdvancedQpc = qpc;
            _lastGameTimestamp = state.GameTimestampMilliseconds;
            if (_epochStartedQpc == 0) _epochStartedQpc = qpc;
            var fullLoad = FullLoad(state);
            var clean = CleanLoad(state);
            if (state.Gear == TransmissionGear.Neutral)
            {
                _neutralFrom ??= _previous;
                _neutralTorqueCut |= state.TorqueNm <= 1 || state.PowerWatts <= 1;
                _neutralCleanLoad &= clean;
                _previous = null;
                _recovering = null;
                if (_neutralFrom is not { } from || !fullLoad || !Within(qpc, from.Qpc, .5)) ResetContinuity();
                return;
            }
            var current = new Observation(profile, state, qpc, clean);
            var gear = profile.Gears[(int)state.Gear - 1];
            gear.Samples++;
            gear.MaximumRpm = Math.Max(gear.MaximumRpm, state.EngineRpm);
            if (state.Accelerator >= 250 && !clean) gear.ConfoundedFullLoadSamples++;
            if (clean)
            {
                gear.CleanSamples++;
                gear.CleanMinimumRpm = Math.Min(gear.CleanMinimumRpm, state.EngineRpm);
                gear.CleanMaximumRpm = Math.Max(gear.CleanMaximumRpm, state.EngineRpm);
                if (_previous is not { Clean: true } sweepBefore || sweepBefore.State.Gear != state.Gear)
                {
                    gear.SegmentSeconds = 0;
                    gear.SegmentMinimumRpm = state.EngineRpm;
                    gear.SegmentMaximumRpm = state.EngineRpm;
                }
                if (_previous is { Clean: true } before && before.State.Gear == state.Gear)
                {
                    gear.CleanSeconds += Seconds(qpc - before.Qpc);
                    gear.SegmentSeconds += Seconds(qpc - before.Qpc);
                    gear.SegmentMinimumRpm = Math.Min(gear.SegmentMinimumRpm, state.EngineRpm);
                    gear.SegmentMaximumRpm = Math.Max(gear.SegmentMaximumRpm, state.EngineRpm);
                    gear.CleanSweepObserved |= gear.SegmentSeconds >= .75 &&
                        gear.SegmentMaximumRpm - gear.SegmentMinimumRpm >= Math.Max(500, gear.SegmentMaximumRpm * .2);
                    // A same-gear high-RPM power cut is a candidate. It could still
                    // be traction/engine control; do not label it a proven limiter.
                    if (before.State.TorqueNm > 1 && before.State.PowerWatts > 1 &&
                        state.TorqueNm < before.State.TorqueNm * .3 &&
                        state.PowerWatts < before.State.PowerWatts * .3 &&
                        before.State.EngineRpm >= gear.CleanMaximumRpm * .95 &&
                        state.EngineRpm >= gear.CleanMaximumRpm * .9)
                    {
                        gear.PowerCutCandidates++;
                        gear.LastPowerCut = new(before.Qpc, qpc);
                    }
                }
            }
            var shiftPrior = _previous ?? (_neutralFrom is { } fromNeutral && Within(qpc, fromNeutral.Qpc, .5) ? fromNeutral : null);
            if (shiftPrior is { } prior && (int)state.Gear == (int)prior.State.Gear + 1 &&
                prior.State.GroundSpeedMetersPerSecond >= 5 && state.GroundSpeedMetersPerSecond >= 5)
            {
                var shift = new Shift(profile, (int)prior.State.Gear, (int)state.Gear,
                    new(prior.Qpc, qpc), prior.State.EngineRpm, state.EngineRpm,
                    prior.Clean && clean && _neutralCleanLoad, FullLoad(prior.State) && fullLoad,
                    _neutralTorqueCut || prior.State.TorqueNm <= 0 || state.TorqueNm <= 0,
                    _epochStartedQpc);
                if (_shifts.Count < MaximumShifts) { _shifts.Add(shift); _recovering = shift; }
                else { _truncated = true; _recovering = null; }
            }
            else if (_previous is { } changed && state.Gear != changed.State.Gear) _recovering = null;
            if (_recovering is { } recovery)
            {
                if (!ReferenceEquals(recovery.Profile, profile) || (int)state.Gear != recovery.ToGear ||
                    !Within(qpc, recovery.GearChange.LatestQpc, 2) || !fullLoad) _recovering = null;
                else
                {
                    // Slip can disqualify acceleration evidence without erasing
                    // a valid engine-output bracket. Keep those claims separate.
                    recovery.RecoveryTractionQualified &= clean;
                    if (state.TorqueNm > 1 && state.PowerWatts > 1)
                    {
                        recovery.FirstPositiveTorque ??= new(_previous?.Qpc ?? recovery.GearChange.EarliestQpc, qpc);
                        recovery.PositiveSamples++;
                        if (recovery.PositiveSamples >= 3 &&
                            Seconds(qpc - recovery.FirstPositiveTorque.LatestQpc) >= .025)
                        {
                            recovery.PositiveTorqueSustained = true;
                            _recovering = null;
                        }
                    }
                    else
                    {
                        recovery.ObservedTorqueInterruption = true;
                        recovery.FirstPositiveTorque = null;
                        recovery.PositiveSamples = 0;
                    }
                }
            }
            _neutralFrom = null;
            _neutralTorqueCut = false;
            _neutralCleanLoad = true;
            _previous = current;
        }
    }

    internal ShiftCaptureCoverageSnapshot Snapshot()
    {
        lock (_gate)
        {
            var shifts = _shifts.Select(shift =>
            {
                // Input and telemetry producers can arrive in either order.
                var candidates = _buttons.Where(button =>
                    button.EarliestObservedEdgeQpc >= shift.EpochStartedQpc &&
                    button.EarliestObservedEdgeQpc <= shift.GearChange.LatestQpc &&
                    button.LatestObservedEdgeQpc >= shift.GearChange.EarliestQpc - _frequency &&
                    Within(shift.GearChange.LatestQpc, button.EarliestObservedEdgeQpc, 1)).ToArray();
                var command = candidates.Length == 1 ? candidates[0] : null;
                var unique = command is not null && _shifts.Count(other =>
                    command.EarliestObservedEdgeQpc >= other.EpochStartedQpc &&
                    command.EarliestObservedEdgeQpc <= other.GearChange.LatestQpc &&
                    Within(other.GearChange.LatestQpc, command.EarliestObservedEdgeQpc, 1)) == 1;
                return new ShiftCaptureShiftCoverage(shift.Profile.Car, shift.Profile.Fingerprint,
                    shift.FromGear, shift.ToGear, shift.GearChange, shift.BeforeRpm, shift.AfterRpm,
                    shift.CleanFullLoad, unique ? new(command!.EarliestObservedEdgeQpc, command.LatestObservedEdgeQpc) : null,
                    candidates.Length, shift.ObservedTorqueInterruption, shift.FirstPositiveTorque, shift.PositiveTorqueSustained)
                {
                    FullLoadTimingEligible = shift.FullLoadTimingEligible,
                    RecoveryTractionQualified = shift.RecoveryTractionQualified
                };
            }).ToArray();
            var profiles = _profiles.Values.Select(profile => new ShiftCaptureProfileCoverage(profile.Car,
                profile.Fingerprint, profile.Gears.Select((gear, index) => new ShiftCaptureGearCoverage(index + 1,
                    gear.Samples, gear.MaximumRpm, gear.CleanSamples, gear.CleanSeconds,
                    gear.CleanSamples == 0 ? null : gear.CleanMinimumRpm,
                    gear.CleanSamples == 0 ? null : gear.CleanMaximumRpm,
                    gear.CleanSweepObserved,
                    gear.ConfoundedFullLoadSamples, gear.PowerCutCandidates, gear.LastPowerCut))
                .Where(gear => gear.Samples != 0).ToArray())).ToArray();
            var missing = new List<string>();
            var primary = profiles.OrderByDescending(p =>
                (p.Gears.Any(g => g.CleanSweepObserved) ? 1 : 0) +
                (shifts.Count(s => s.CarOrdinal == p.CarOrdinal && s.Fingerprint == p.Fingerprint &&
                    s.CleanFullLoad && s.RecoveryTractionQualified && s.EngineOutputTimingObserved) >= 2 ? 1 : 0))
                .FirstOrDefault();
            if (profiles.Length == 0) missing.Add("Stay parked until a fresh car configuration and telemetry are available.");
            if (primary is null || !primary.Gears.Any(g => g.CleanSweepObserved))
                missing.Add("Continuous clean full-throttle RPM sweep not observed (at least 0.75 seconds and a 20% RPM range, minimum 500 rpm).");
            if (primary is null || shifts.Count(s => s.CarOrdinal == primary.CarOrdinal && s.Fingerprint == primary.Fingerprint &&
                    s.CleanFullLoad && s.RecoveryTractionQualified && s.EngineOutputTimingObserved) < 2)
                missing.Add("Fewer than two clean full-throttle B upshifts have associated command brackets and sustained positive engine output.");
            if (_truncated) missing.Add("This session exceeded a coverage limit; save it before collecting more cars or shifts.");
            return new(_frequency, _packets, _repeatedTimestamps, _invalid, _gaps, _identityChanges,
                _truncated, primary?.Fingerprint, missing.ToArray(), profiles, shifts);
        }
    }

    private void ResetContinuity(bool resetClock = true)
    {
        _previous = null;
        _recovering = null;
        _neutralFrom = null;
        _neutralTorqueCut = false;
        _neutralCleanLoad = true;
        _epochStartedQpc = 0;
        if (resetClock)
        {
            _lastGameTimestamp = null;
            _lastAdvancedQpc = 0;
            _clockStalled = false;
        }
    }
    private double Seconds(long ticks) => (double)ticks / _frequency;
    private bool Within(long now, long before, double seconds) => before > 0 && now >= before && Seconds(now - before) <= seconds;
    private static bool Valid(VehicleState state) => state.IsRaceOn && state.CarOrdinal > 0 && state.NumCylinders > 0 &&
        (int)state.Gear is >= 0 and <= 10 && float.IsFinite(state.EngineRpm) && state.EngineRpm >= 0 &&
        float.IsFinite(state.GroundSpeedMetersPerSecond) && float.IsFinite(state.TorqueNm) && float.IsFinite(state.PowerWatts) &&
        state.TireSlipRatio.AreFinite() && float.IsFinite(state.LateralAccelerationMetersPerSecondSquared);
    private static bool FullLoad(VehicleState state) => state.Accelerator >= 250 && state.Brake <= 1 &&
        state.GroundSpeedMetersPerSecond >= 5;
    private static bool CleanLoad(VehicleState state) => FullLoad(state) && state.TireSlipRatio.MaximumAbsolute() <= .15 &&
        Math.Abs(state.LateralAccelerationMetersPerSecondSquared) <= 1.5;
    private sealed record Observation(Profile Profile, VehicleState State, long Qpc, bool Clean);
    private sealed class Profile(int car, string fingerprint)
    {
        internal int Car { get; } = car;
        internal string Fingerprint { get; } = fingerprint;
        internal Gear[] Gears { get; } = Enumerable.Range(0, 10).Select(_ => new Gear()).ToArray();
    }
    private sealed class Gear
    {
        internal long Samples, CleanSamples, ConfoundedFullLoadSamples, PowerCutCandidates;
        internal double MaximumRpm, CleanMinimumRpm = double.PositiveInfinity, CleanMaximumRpm, CleanSeconds;
        internal double SegmentMinimumRpm, SegmentMaximumRpm, SegmentSeconds;
        internal bool CleanSweepObserved;
        internal ShiftCaptureTimeBracket? LastPowerCut;
    }
    private sealed class Shift(Profile profile, int from, int to, ShiftCaptureTimeBracket bracket,
        double beforeRpm, double afterRpm, bool clean, bool fullLoad, bool interrupted, long epochStartedQpc)
    {
        internal Profile Profile { get; } = profile;
        internal int FromGear { get; } = from;
        internal int ToGear { get; } = to;
        internal ShiftCaptureTimeBracket GearChange { get; } = bracket;
        internal double BeforeRpm { get; } = beforeRpm;
        internal double AfterRpm { get; } = afterRpm;
        internal bool CleanFullLoad { get; } = clean;
        internal bool FullLoadTimingEligible { get; } = fullLoad;
        internal bool RecoveryTractionQualified { get; set; } = clean;
        internal long EpochStartedQpc { get; } = epochStartedQpc;
        internal bool ObservedTorqueInterruption { get; set; } = interrupted;
        internal ShiftCaptureTimeBracket? FirstPositiveTorque { get; set; }
        internal int PositiveSamples { get; set; }
        internal bool PositiveTorqueSustained { get; set; }
    }
}

internal sealed record ShiftCaptureTimeBracket(long EarliestQpc, long LatestQpc);
internal sealed record ShiftCaptureGearCoverage(int Gear, long Samples, double MaximumRpm, long CleanFullLoadSamples,
    double CleanFullLoadSeconds, double? CleanMinimumRpm, double? CleanMaximumRpm, bool CleanSweepObserved,
    long ConfoundedFullLoadSamples, long HighRpmPowerCutCandidates, ShiftCaptureTimeBracket? LastPowerCutBracket);
internal sealed record ShiftCaptureProfileCoverage(int CarOrdinal, string Fingerprint, ShiftCaptureGearCoverage[] Gears);
internal sealed record ShiftCaptureShiftCoverage(int CarOrdinal, string Fingerprint, int FromGear, int ToGear,
    ShiftCaptureTimeBracket GearChange, double BeforeRpm, double AfterRpm, bool CleanFullLoad,
    ShiftCaptureTimeBracket? ButtonBracket, int CandidateButtonCount, bool ObservedTorqueInterruption,
    ShiftCaptureTimeBracket? FirstPositiveTorqueBracket, bool PositiveTorqueSustained)
{
    public bool FullLoadTimingEligible { get; init; }
    public bool RecoveryTractionQualified { get; init; }
    public bool EngineOutputTimingObserved => FullLoadTimingEligible && ButtonBracket is not null && PositiveTorqueSustained;
}
internal sealed record ShiftCaptureCoverageSnapshot(long QpcFrequency, long TelemetryPackets, long RepeatedGameTimestamps,
    long InvalidOrUnassociatedSamples, long ContinuityGaps, long ProfileTransitions, bool Truncated,
    string? DrivingEvidenceFingerprint, string[] ActionableMissingEvidence, ShiftCaptureProfileCoverage[] Profiles, ShiftCaptureShiftCoverage[] Upshifts)
{
    // Retained for readers of prior private exports; never a duplicate-state count.
    public long RepeatedGameStates => RepeatedGameTimestamps;
    public int RecordedMovingUpshifts => Upshifts.Length;
    public int BracketedEngineOutputTimings => Upshifts.Count(shift => shift.EngineOutputTimingObserved);
    public int TractionQualifiedEngineOutputTimings => Upshifts.Count(shift =>
        shift.EngineOutputTimingObserved && shift.CleanFullLoad && shift.RecoveryTractionQualified);
    public string RepeatedTimestampMeaning => "Repeated game timestamps can contain changed physical observations; all valid ingress observations are retained. RepeatedGameStates is a legacy alias for this timestamp count.";
    public string ChecklistMeaning => "These are coverage heuristics, separate from capture integrity. An unmet item does not invalidate retained data or prove another driving run is necessary; review the raw evidence first.";
    public string Scope => "Evidence from these recorded car/tune fingerprints only; not proof of optimal timing or unvisited cars/tunes.";
    public string TorqueTimingMeaning => "Telemetry reports engine torque. Engine-output timing brackets are retained through finite slip or lateral excursions; traction-qualified evidence requires clean load throughout the shift and recovery. Sustained positive output does not prove clutch engagement, wheel force or game command-consumption time.";
    public string LimiterMeaning => "High-RPM power cuts are candidates for offline inspection, not verified limiter events.";
    public bool DrivingEvidenceChecklistSatisfied => ActionableMissingEvidence.Length == 0;
}
