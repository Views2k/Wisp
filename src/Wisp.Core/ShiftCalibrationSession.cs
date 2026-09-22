namespace Wisp.Core;

/// <summary>
/// Bounded ingress collection. Observe is constant work without per-packet allocation.
/// Evaluate copies the buffer under a short lock, then fits outside that lock.
/// A context change ends this instance; callers create a new session to start again.
/// </summary>
public sealed class ShiftCalibrationSession
{
    internal const int MaximumSamples = 12_000;
    private readonly object _gate = new();
    private readonly ShiftCalibrationPoint[] _points = new ShiftCalibrationPoint[MaximumSamples];
    private readonly long _frequency;
    private int _count, _epoch;
    private long _revision, _lastTimestamp, _gameAdvancedAt;
    private uint? _gameTimestamp;
    private bool _invalidated, _full;
    private string _reason = "On a straight, make a rolling full-throttle pull in one gear.";

    public ShiftCalibrationSession(ShiftCalibrationContext context, long timestampFrequency)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsValid) throw new ArgumentException("A valid car configuration is required.", nameof(context));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        Context = context;
        _frequency = timestampFrequency;
    }

    public ShiftCalibrationContext Context { get; }

    public void InvalidateContext()
    {
        lock (_gate)
        {
            _invalidated = true;
            _revision++;
        }
    }

    public void BreakObservation()
    {
        lock (_gate)
        {
            if (_invalidated || _full) return;
            _revision++;
            Break("Waiting for fresh, associated car configuration.");
        }
    }

    public void Observe(VehicleState state, string? fingerprint, long timestamp, bool limiterActive = false,
        bool outputSettled = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (_invalidated || _full) return;
            _revision++;
            if (state.IsRaceOn && state.CarOrdinal > 0 && state.CarOrdinal != Context.CarOrdinal ||
                !string.IsNullOrEmpty(fingerprint) && !string.Equals(fingerprint, Context.Fingerprint, StringComparison.Ordinal))
            {
                _invalidated = true;
                return;
            }
            if (timestamp <= 0 || timestamp <= _lastTimestamp)
            {
                Break("Waiting for fresh driving data.");
                return;
            }
            if (_lastTimestamp > 0 && (double)(timestamp - _lastTimestamp) / _frequency > .1)
                Break("The pull had a data gap; continue with a clean pull.");
            _lastTimestamp = timestamp;
            if (!state.IsRaceOn || state.CarOrdinal != Context.CarOrdinal || string.IsNullOrEmpty(fingerprint) || state.IsElectric)
            {
                _gameTimestamp = null;
                Break("Waiting for this car and tune in driving view.");
                return;
            }
            if (_gameTimestamp is { } previous)
            {
                var advance = unchecked((int)(state.GameTimestampMilliseconds - previous));
                if (advance < 0 || advance > 100)
                    Break("The game clock changed; continue with a clean pull.");
                if (advance != 0) _gameAdvancedAt = timestamp;
                else if ((double)(timestamp - _gameAdvancedAt) / _frequency > .25)
                {
                    Break("Waiting for advancing driving data.");
                    return;
                }
            }
            else _gameAdvancedAt = timestamp;
            _gameTimestamp = state.GameTimestampMilliseconds;
            if (!Finite(state) || state.EngineRpm is < 500 or > 30_000 ||
                (int)state.Gear < 0 || (int)state.Gear > Context.ForwardRatios.Count)
            {
                Break("Waiting for valid combustion-engine telemetry.");
                return;
            }
            if (state.Accelerator < 250 || state.Brake > 1 || state.GroundSpeedMetersPerSecond < 5)
            {
                Break("Keep full throttle during the rolling pull and confirming upshift.");
                return;
            }
            if (state.TireSlipRatio.MaximumAbsolute() > .15 || Math.Abs(state.LateralAccelerationMetersPerSecondSquared) > 1.5)
            {
                Break("Use a straight and a gear with grip; wheelspin or cornering interrupted the pull.");
                return;
            }
            _points[_count++] = new(timestamp, _epoch, (int)state.Gear, state.EngineRpm,
                state.TorqueNm, state.PowerWatts, state.BoostPressurePsi, limiterActive, outputSettled);
            _reason = "Collecting the full-load curve; continue through the upper RPM range, then upshift.";
            if (_count == MaximumSamples) _full = true;
        }
    }

    public ShiftCalibrationResult Evaluate()
    {
        ShiftCalibrationPoint[] points;
        long revision;
        bool invalidated, full;
        string reason;
        lock (_gate)
        {
            points = _points.AsSpan(0, _count).ToArray();
            revision = _revision;
            invalidated = _invalidated;
            full = _full;
            reason = _reason;
        }
        if (invalidated)
            return ShiftCalibrationAnalysis.Empty(Context, revision, ShiftCalibrationStatus.ContextChanged,
                "The car or tune changed. Start calibration for the current configuration.");
        var result = ShiftCalibrationAnalysis.Evaluate(Context, revision, points, _frequency, reason);
        lock (_gate)
        {
            if (_invalidated)
                return ShiftCalibrationAnalysis.Empty(Context, _revision, ShiftCalibrationStatus.ContextChanged,
                    "The car or tune changed. Start calibration for the current configuration.");
        }
        return full && !result.Ready ? result with
        {
            Status = ShiftCalibrationStatus.BufferFull,
            Reason = "Calibration recording is full. Start again with a clean pull and one confirming upshift."
        } : result;
    }

    /// <summary>Restore only previously completed local evidence; never trust stored target RPMs.</summary>
    public static bool TryRestore(ShiftCalibrationContext context, IReadOnlyList<AccelerationShiftSample> samples,
        double? empiricalUpperRpm, int confirmingUpshifts, int acceptedSamples, out ShiftCalibrationResult? result)
    {
        result = null;
        if (context is null || !context.IsValid || samples is null ||
            samples.Count is < 8 or > 602 || acceptedSamples is < 30 or > MaximumSamples || acceptedSamples < samples.Count ||
            confirmingUpshifts is < 1 or > MaximumSamples / 2)
            return false;
        var profile = new AccelerationShiftProfile(samples, context.ForwardRatios, context.GearAccelerationFactors);
        if (!ShiftCalibrationAnalysis.ValidCurve(profile) ||
            context.ConfiguredOperatingCeilingRpm is { } configured && profile.Samples[^1].Rpm > configured ||
            empiricalUpperRpm is { } boundary && (!double.IsFinite(boundary) || boundary != profile.Samples[^1].Rpm))
            return false;
        var restored = ShiftCalibrationAnalysis.Complete(context, 0, profile, empiricalUpperRpm,
            acceptedSamples, confirmingUpshifts);
        if (!restored.Ready) return false;
        result = restored;
        return true;
    }

    private void Break(string reason)
    {
        _epoch++;
        _reason = reason;
    }

    private static bool Finite(VehicleState state) => float.IsFinite(state.EngineRpm) &&
        float.IsFinite(state.GroundSpeedMetersPerSecond) && float.IsFinite(state.TorqueNm) &&
        float.IsFinite(state.PowerWatts) && float.IsFinite(state.BoostPressurePsi) &&
        state.TireSlipRatio.AreFinite() && float.IsFinite(state.LateralAccelerationMetersPerSecondSquared);
}

internal readonly record struct ShiftCalibrationPoint(long Timestamp, int Epoch, int Gear,
    double Rpm, double Torque, double Power, double Boost, bool LimiterActive, bool OutputSettled)
{
    internal bool Positive => Gear > 0 && OutputSettled && !LimiterActive && Torque > 1 && Power > 1 &&
        Math.Abs(Power / (Torque * Rpm * Math.PI / 30) - 1) <= .05;
}
