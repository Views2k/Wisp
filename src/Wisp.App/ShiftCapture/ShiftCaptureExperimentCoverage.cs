using Wisp.Core;

namespace Wisp.App;

// Describes recorded comparison candidates; it does not validate an optimal shift.
internal sealed class ShiftCaptureExperimentCoverage
{
    private const int MaximumProfiles = 8, MaximumSegments = 128, MaximumTransitions = 32;
    private const double MinimumSegmentSeconds = .75, MinimumSpeedRange = 2;
    private readonly object _gate = new();
    private readonly long _frequency;
    private readonly Dictionary<(int Car, string Fingerprint), Profile> _profiles = new();
    private readonly List<Segment> _segments = [];
    private readonly List<ShiftCaptureIdentityTransition> _transitions = [];
    private Segment _active;
    private Profile? _identity;
    private long _identityQpc, _lastPacketQpc, _clockAdvancedQpc, _packets, _invalid, _gaps, _shortSegments;
    private uint? _gameClock;
    private bool _identityParked, _clockStalled, _truncated;

    internal ShiftCaptureExperimentCoverage(long qpcFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        _frequency = qpcFrequency;
    }

    internal void InvalidateContext()
    {
        lock (_gate) Reset("context-lost");
    }

    internal void Observe(VehicleState state, long qpc, string? associatedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _packets++;
            if (qpc <= 0 || qpc <= _lastPacketQpc || !Valid(state) ||
                string.IsNullOrWhiteSpace(associatedFingerprint) || associatedFingerprint.Length > 128)
            {
                _invalid++;
                Reset("invalid-or-unassociated");
                if (qpc > _lastPacketQpc) _lastPacketQpc = qpc;
                return;
            }
            if (_lastPacketQpc > 0 && Seconds(qpc - _lastPacketQpc) > .1) { _gaps++; Reset("packet-gap"); }
            _lastPacketQpc = qpc;
            var key = (state.CarOrdinal, associatedFingerprint);
            if (!_profiles.TryGetValue(key, out var profile))
            {
                if (_profiles.Count == MaximumProfiles) { _truncated = true; Reset("profile-limit"); return; }
                profile = new(state.CarOrdinal, associatedFingerprint);
                _profiles.Add(key, profile);
            }
            var parked = state.GroundSpeedMetersPerSecond <= .5 && state.Accelerator <= 5;
            if (_identity is { } old && !ReferenceEquals(old, profile))
            {
                if (_transitions.Count < MaximumTransitions)
                    _transitions.Add(new(old.Car, old.Fingerprint, _identityQpc, _identityParked,
                        profile.Car, profile.Fingerprint, qpc, parked));
                else _truncated = true;
                Reset("identity-changed");
            }
            _identity = profile;
            _identityQpc = qpc;
            _identityParked = parked;
            if (_gameClock is { } previousClock)
            {
                var advance = unchecked((int)(state.GameTimestampMilliseconds - previousClock));
                if (advance < 0) { _invalid++; Reset("clock-backward"); return; }
                if (advance == 0 && Seconds(qpc - _clockAdvancedQpc) > .25)
                {
                    if (!_clockStalled) { _gaps++; Seal("clock-stalled"); }
                    _clockStalled = true;
                    return;
                }
                if (advance > 100) { _gaps++; Reset("clock-gap"); }
                if (advance > 0) { _clockAdvancedQpc = qpc; _clockStalled = false; }
            }
            else _clockAdvancedQpc = qpc;
            _gameClock = state.GameTimestampMilliseconds;
            if (!Clean(state)) { Seal("load-or-traction-changed"); return; }
            if (_active.Profile is not null && (!ReferenceEquals(_active.Profile, profile) || _active.Gear != (int)state.Gear))
                Seal("gear-changed");
            if (_active.Profile is null) _active = new(profile, state, qpc);
            else _active.Add(state, qpc);
        }
    }

    internal ShiftCaptureExperimentSnapshot Snapshot()
    {
        lock (_gate)
        {
            var segments = _segments.ToList();
            if (Qualifies(_active))
            {
                if (segments.Count < MaximumSegments) segments.Add(_active);
                else _truncated = true;
            }
            var comparisons = new List<ShiftCaptureMatchedSpeedCandidate>();
            foreach (var profile in _profiles.Values)
            {
                for (var gear = 1; gear < 10; gear++)
                {
                    var bestStart = 0d;
                    var bestEnd = 0d;
                    foreach (var segment in segments)
                    {
                        if (!ReferenceEquals(segment.Profile, profile) || segment.Gear != gear && segment.Gear != gear + 1) continue;
                        var start = segment.MinimumSpeed;
                        var end = Math.Min(SecondCoveredEnd(profile, gear, start), SecondCoveredEnd(profile, gear + 1, start));
                        if (end - start < MinimumSpeedRange || end - start <= bestEnd - bestStart) continue;
                        bestStart = start;
                        bestEnd = end;
                    }
                    if (bestEnd - bestStart >= MinimumSpeedRange)
                        comparisons.Add(new(profile.Car, profile.Fingerprint, gear, gear + 1, bestStart, bestEnd,
                            CountCovering(profile, gear, bestStart, bestEnd), CountCovering(profile, gear + 1, bestStart, bestEnd)));
                }
            }
            var profiles = _profiles.Values.Select(profile => new ShiftCaptureExperimentProfile(profile.Car,
                profile.Fingerprint, segments.Count(segment => ReferenceEquals(segment.Profile, profile)),
                segments.Count(segment => ReferenceEquals(segment.Profile, profile) && segment.PositiveBoostMaximum > 0))).ToArray();
            var roundTrips = new List<ShiftCaptureParkedIdentityRoundTrip>();
            for (var i = 1; i < _transitions.Count; i++)
            {
                var first = _transitions[i - 1];
                var second = _transitions[i];
                if (first.FromCarOrdinal == first.ToCarOrdinal && first.ToCarOrdinal == second.FromCarOrdinal &&
                    second.FromCarOrdinal == second.ToCarOrdinal && first.FromFingerprint == second.ToFingerprint &&
                    first.ToFingerprint == second.FromFingerprint && first.FromFingerprint != first.ToFingerprint &&
                    first.FromParked && first.ToParked && second.FromParked && second.ToParked &&
                    first.ToQpc <= second.FromQpc && second.FromQpc < second.ToQpc)
                    roundTrips.Add(new(first.FromCarOrdinal, first.FromFingerprint, first.ToFingerprint,
                        first.FromQpc, first.ToQpc, second.ToQpc));
            }
            var missing = new List<string>();
            if (comparisons.Count == 0)
                missing.Add("No adjacent-gear comparison has two clean acceleration passes in each gear sharing at least 2 m/s of ground-speed range.");
            if (!profiles.Any(profile => profile.PositiveBoostSegments >= 2))
                missing.Add("No single car/tune has two qualifying acceleration passes with observed positive boost; this is not an engine-type test.");
            if (roundTrips.Count == 0)
                missing.Add("No same-car parked A-to-B-to-A configuration identity round trip was observed.");
            if (_truncated) missing.Add("The experiment summary reached its profile, segment or identity-transition limit; raw session records remain authoritative.");
            return new(_frequency, _packets, _invalid, _gaps, _shortSegments, _truncated,
                profiles, segments.Select(segment => segment.Snapshot(_frequency)).ToArray(), comparisons.ToArray(),
                _transitions.ToArray(), roundTrips.ToArray(), missing.ToArray());

            double SecondCoveredEnd(Profile profile, int gear, double start)
            {
                var highest = double.NegativeInfinity;
                var second = double.NegativeInfinity;
                foreach (var segment in segments)
                {
                    if (!ReferenceEquals(segment.Profile, profile) || segment.Gear != gear || segment.MinimumSpeed > start) continue;
                    if (segment.MaximumSpeed >= highest) { second = highest; highest = segment.MaximumSpeed; }
                    else second = Math.Max(second, segment.MaximumSpeed);
                }
                return second;
            }

            int CountCovering(Profile profile, int gear, double start, double end) => segments.Count(segment =>
                ReferenceEquals(segment.Profile, profile) && segment.Gear == gear && segment.MinimumSpeed <= start && segment.MaximumSpeed >= end);
        }
    }

    private void Reset(string reason)
    {
        Seal(reason);
        _gameClock = null;
        _clockAdvancedQpc = 0;
        _clockStalled = false;
    }

    private void Seal(string reason)
    {
        if (_active.Profile is null) return;
        if (Qualifies(_active))
        {
            _active.EndReason = reason;
            if (_segments.Count < MaximumSegments) _segments.Add(_active);
            else _truncated = true;
        }
        else _shortSegments++;
        _active = default;
    }

    private bool Qualifies(Segment segment) => segment.Profile is not null &&
        Seconds(segment.LastQpc - segment.FirstQpc) >= MinimumSegmentSeconds &&
        segment.LastSpeed - segment.FirstSpeed >= MinimumSpeedRange;
    private double Seconds(long ticks) => (double)ticks / _frequency;
    private static bool Valid(VehicleState state) => state.IsRaceOn && state.CarOrdinal > 0 && state.NumCylinders > 0 &&
        (int)state.Gear is >= 0 and <= 10 && float.IsFinite(state.EngineRpm) && state.EngineRpm >= 0 &&
        float.IsFinite(state.GroundSpeedMetersPerSecond) && state.GroundSpeedMetersPerSecond >= 0 &&
        float.IsFinite(state.BoostPressurePsi) && float.IsFinite(state.TorqueNm) && float.IsFinite(state.PowerWatts) &&
        state.TireSlipRatio.AreFinite() && float.IsFinite(state.LateralAccelerationMetersPerSecondSquared);
    private static bool Clean(VehicleState state) => (int)state.Gear > 0 && state.Accelerator >= 250 && state.Brake <= 1 &&
        state.GroundSpeedMetersPerSecond >= 5 && state.TireSlipRatio.MaximumAbsolute() <= .15 &&
        Math.Abs(state.LateralAccelerationMetersPerSecondSquared) <= 1.5;
    private sealed record Profile(int Car, string Fingerprint);

    private struct Segment
    {
        internal Profile? Profile;
        internal int Gear;
        internal long FirstQpc, LastQpc, Samples;
        internal double FirstSpeed, LastSpeed, MinimumSpeed, MaximumSpeed, MinimumRpm, MaximumRpm, PositiveBoostMaximum;
        internal string? EndReason;

        internal Segment(Profile profile, VehicleState state, long qpc)
        {
            Profile = profile;
            Gear = (int)state.Gear;
            FirstQpc = LastQpc = qpc;
            Samples = 1;
            FirstSpeed = LastSpeed = MinimumSpeed = MaximumSpeed = state.GroundSpeedMetersPerSecond;
            MinimumRpm = MaximumRpm = state.EngineRpm;
            PositiveBoostMaximum = Math.Max(0, state.BoostPressurePsi);
            EndReason = null;
        }

        internal void Add(VehicleState state, long qpc)
        {
            LastQpc = qpc;
            Samples++;
            LastSpeed = state.GroundSpeedMetersPerSecond;
            MinimumSpeed = Math.Min(MinimumSpeed, LastSpeed);
            MaximumSpeed = Math.Max(MaximumSpeed, LastSpeed);
            MinimumRpm = Math.Min(MinimumRpm, state.EngineRpm);
            MaximumRpm = Math.Max(MaximumRpm, state.EngineRpm);
            PositiveBoostMaximum = Math.Max(PositiveBoostMaximum, state.BoostPressurePsi);
        }

        internal readonly ShiftCaptureAccelerationSegment Snapshot(long frequency) => new(Profile!.Car, Profile.Fingerprint,
            Gear, FirstQpc, LastQpc, Samples, (double)(LastQpc - FirstQpc) / frequency, FirstSpeed, LastSpeed,
            MinimumSpeed, MaximumSpeed, MinimumRpm, MaximumRpm, PositiveBoostMaximum, EndReason ?? "active");
    }
}

internal sealed record ShiftCaptureExperimentProfile(int CarOrdinal, string Fingerprint, int QualifyingSegments, int PositiveBoostSegments);
internal sealed record ShiftCaptureAccelerationSegment(int CarOrdinal, string Fingerprint, int Gear, long StartedQpc, long EndedQpc,
    long Samples, double DurationSeconds, double StartSpeedMetersPerSecond, double EndSpeedMetersPerSecond,
    double MinimumSpeedMetersPerSecond, double MaximumSpeedMetersPerSecond, double MinimumRpm, double MaximumRpm,
    double MaximumPositiveBoostPsi, string EndReason);
internal sealed record ShiftCaptureMatchedSpeedCandidate(int CarOrdinal, string Fingerprint, int LowerGear, int UpperGear,
    double MinimumSpeedMetersPerSecond, double MaximumSpeedMetersPerSecond, int LowerGearPasses, int UpperGearPasses);
internal sealed record ShiftCaptureIdentityTransition(int FromCarOrdinal, string FromFingerprint, long FromQpc, bool FromParked,
    int ToCarOrdinal, string ToFingerprint, long ToQpc, bool ToParked);
internal sealed record ShiftCaptureParkedIdentityRoundTrip(int CarOrdinal, string OriginalFingerprint, string ChangedFingerprint,
    long OriginalParkedQpc, long ChangedParkedQpc, long ReturnedParkedQpc);
internal sealed record ShiftCaptureExperimentSnapshot(long QpcFrequency, long TelemetryPackets, long InvalidOrUnassociatedSamples,
    long ContinuityGaps, long NonqualifyingSegments, bool Truncated, ShiftCaptureExperimentProfile[] Profiles,
    ShiftCaptureAccelerationSegment[] Segments, ShiftCaptureMatchedSpeedCandidate[] MatchedSpeedCandidates,
    ShiftCaptureIdentityTransition[] IdentityTransitions, ShiftCaptureParkedIdentityRoundTrip[] ParkedIdentityRoundTrips,
    string[] ActionableMissingEvidence)
{
    public bool ExperimentChecklistSatisfied => ActionableMissingEvidence.Length == 0;
    public string Heuristics => "A candidate is one contiguous forward-gear full-throttle segment lasting at least 0.75 seconds with at least 2 m/s net speed gain, speed >=5 m/s, brake <=1/255, throttle >=250/255, absolute tire slip <=0.15 and lateral acceleration <=1.5 m/s². Adjacent-gear comparisons require two segments per gear sharing at least 2 m/s. No new limiter cut is required.";
    public string Scope => "Recorded candidates only, not equivalent road/traction conditions or proof of optimal shift timing. Positive boost describes observed pressure, not engine type. Parked identity round trips describe same-car fingerprint observations, not the specific tuning action or unobserved menu interval. Checklist gaps do not invalidate raw capture data.";
}
