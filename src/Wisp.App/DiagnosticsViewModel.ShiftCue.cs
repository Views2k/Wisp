using System.Diagnostics;
using Wisp.Core;

namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    private readonly Func<long> _shiftTimestamp;
    private AppSettings? _shiftSettings;
    private bool _accelerationShiftCueEnabled;
    private string _shiftCueStatus = "Off";
    private string _shiftIdentity = "";
    private int _shiftGear;
    private bool _shiftTargetReached;
    private long _shiftTargetReachedAt;
    private string _shiftDecision = "Disabled";
    private uint? _shiftLastGameTime;
    private (int Car, uint Time)? _shiftBlockedSample;
    private long _shiftGameAdvancedAt;
    private readonly ShiftCueCrossingTracker _shiftCrossings = new();
    private long _shiftObservationEpoch;
    private long? _shiftCrossingObservedAt;
    private string? _shiftRedTrigger;
    private double _shiftLatchFloorRpm;
    private long _shiftEvaluationTimestamp;
    private VehicleState? _captureCanaryState;

    private ShiftCueVisualState? CaptureCanary(VehicleState? state, NativeHudSnapshot native, TimeSpan? age)
    {
        var cue = AccelerationShiftCueEnabled && state is not null && age >= TimeSpan.Zero && age <= TimeSpan.FromMilliseconds(150)
            ? ShiftCaptureHub.Current?.Canary(state, native) : null;
        _captureCanaryState = cue.HasValue ? state : null;
        return cue;
    }

    // Runs on the existing receiver worker; never reads or changes WPF state.
    internal void ObserveShiftCueTelemetry(VehicleState? state)
    {
        _shiftCrossings.Observe(state);
        _shiftCalibration?.Observe(state);
    }
    internal void ResetShiftCueObservations() => _shiftCrossings.Reset();

    public bool AccelerationShiftCueEnabled
    {
        get => _accelerationShiftCueEnabled;
        set
        {
            if (!Set(ref _accelerationShiftCueEnabled, value)) return;
            ResetShiftCue();
            NativeGaugeFrame = NativeGaugeFrame with { ShiftCue = default };
            _shiftDecision = value ? "WaitingForMetadata" : "Disabled";
            if (!value) _shiftCalibration?.Cancel();
            ShiftCueStatus = value ? "Waiting for supported car data" : "Off";
            PublishShiftCalibrationStatus();
        }
    }

    public string ShiftCueStatus { get => _shiftCueStatus; private set => Set(ref _shiftCueStatus, value); }

    internal void InitializeShiftCueSettings(AppSettings settings)
    {
        _shiftSettings = settings;
        AccelerationShiftCueEnabled = settings.AccelerationShiftCueEnabled;
    }

    private void ResetShiftCue()
    {
        _shiftCrossings.Reset();
        _shiftObservationEpoch = 0;
        _shiftCrossingObservedAt = null;
        _shiftRedTrigger = null;
        _shiftLatchFloorRpm = 0;
        _shiftIdentity = "";
        _shiftGear = 0;
        _shiftTargetReached = false;
        _shiftTargetReachedAt = 0;
        _shiftLastGameTime = null;
        _shiftGameAdvancedAt = 0;
    }

    private void InvalidateShiftCue(string status)
    {
        if (NativeGaugeFrame.ShiftCue != default)
            ShiftCaptureHub.Current?.Record("cue_invalidation", new { status, NativeGaugeFrame.ReceivedTimestamp });
        if (_shiftLastGameTime is uint previous)
            _shiftBlockedSample = (NativeGaugeFrame.CarOrdinal, previous);
        ResetShiftCue();
        _shiftDecision = "StaleDrivingData";
        if (NativeGaugeFrame.ShiftCue != default)
            NativeGaugeFrame = NativeGaugeFrame with { ShiftCue = default };
        if (AccelerationShiftCueEnabled) ShiftCueStatus = status;
    }

    private static bool HasFreshShiftMetadata(NativeHudSnapshot native, int carOrdinal, long now)
    {
        var data = native.ShiftPerformance;
        return data?.Profile is not null && data.Status == "Research" &&
            data.CarOrdinal == carOrdinal && native.CarOrdinal == carOrdinal &&
            native.Available && native.Status == NativeAssistProviderStatus.Ready &&
            data.ObservedTimestamp > 0 && now >= data.ObservedTimestamp &&
            now - data.ObservedTimestamp <= Stopwatch.Frequency &&
            native.VisibilityObservedTimestamp > 0 && now >= native.VisibilityObservedTimestamp &&
            now - native.VisibilityObservedTimestamp <= Stopwatch.Frequency * .25 &&
            native.GameplayVisibility == NativeGameplayVisibility.Visible &&
            HasFreshShiftLiveState(data, carOrdinal, now);
    }

    private static bool HasFreshShiftLiveState(ShiftCuePerformance data, int carOrdinal, long now) =>
        data.LiveState is { } live
            ? live.CarOrdinal == carOrdinal && live.ObservedTimestamp > 0 && now >= live.ObservedTimestamp &&
                now - live.ObservedTimestamp <= Stopwatch.Frequency * .15
            : !data.RequiresLiveState;

    private static bool HasStableShiftGear(NativeHudSnapshot native, int gear) =>
        native.ShiftPerformance?.LiveState is not { } live ||
        !live.AlternateLimiterBranch && live.CurrentGear == gear && live.RequestedGear == gear;

    // Called by the existing UI timer even when no new packet arrives. This
    // only withdraws stale guidance; it does not calculate targets or read memory.
    internal void InvalidateStaleShiftCue(VehicleState? state, NativeHudSnapshot native,
        TimeSpan? packetAge, long? timestamp = null)
    {
        if (CaptureCanary(state, native, packetAge) is { } canary)
        {
            _shiftDecision = "ParkedRecorderCanary";
            NativeGaugeFrame = NativeGaugeFrame with { ShiftCue = canary };
            return;
        }
        if (_shiftDecision == "ParkedRecorderCanary")
            InvalidateShiftCue("Recorder check ended");
        if (!AccelerationShiftCueEnabled || _shiftIdentity.Length == 0 && !NativeGaugeFrame.ShiftCue.Enabled)
            return;
        var now = timestamp ?? _shiftTimestamp();
        if (state is null || !state.IsRaceOn || state.IsElectric ||
            state.Gear is < TransmissionGear.First or > TransmissionGear.Tenth ||
            (int)state.Gear != _shiftGear || state.CarOrdinal != NativeGaugeFrame.CarOrdinal ||
            packetAge is null || packetAge < TimeSpan.Zero || packetAge > TimeSpan.FromMilliseconds(150) ||
            !HasFreshShiftMetadata(native, state.CarOrdinal, now) ||
            !HasStableShiftGear(native, (int)state.Gear) ||
            native.ShiftPerformance?.Fingerprint != _shiftIdentity ||
            _shiftGameAdvancedAt <= 0 || now < _shiftGameAdvancedAt ||
            now - _shiftGameAdvancedAt > Stopwatch.Frequency * .15)
            InvalidateShiftCue("Waiting for fresh driving data");
    }

    private ShiftCueVisualState CalculateShiftCue(VehicleState state, NativeHudSnapshot native, TimeSpan packetAge)
    {
        _shiftEvaluationTimestamp = 0;
        if (CaptureCanary(state, native, packetAge) is { } checkCue)
        {
            _shiftDecision = "ParkedRecorderCanary";
            return checkCue;
        }
        if (!AccelerationShiftCueEnabled || _shiftSettings is null)
        {
            _shiftDecision = "Disabled";
            return default;
        }
        var now = _shiftTimestamp();
        _shiftEvaluationTimestamp = now;
        var data = CalibratedShiftPerformance(native.ShiftPerformance);
        if (data?.Profile is null || !data.Profile.IsValid ||
            !HasFreshShiftMetadata(native, state.CarOrdinal, now) ||
            !state.IsRaceOn || state.IsElectric || packetAge < TimeSpan.Zero || packetAge > TimeSpan.FromMilliseconds(150))
        {
            ResetShiftCue();
            _shiftDecision = !state.IsRaceOn ? "NotDriving" : state.IsElectric ? "Electric" :
                packetAge < TimeSpan.Zero || packetAge > TimeSpan.FromMilliseconds(150) ? "StaleTelemetry" :
                data?.Profile is null ? data?.Status ?? "MissingProfile" :
                !data.Profile.IsValid ? "InvalidProfile" : "StaleOrMismatchedMetadata";
            ShiftCueStatus = data?.Status switch
            {
                "CalibrationRequired" => _shiftCalibration?.Active == true
                    ? "Calibrating — follow the calibration status below."
                    : "Calibrate this car and tune to enable shift guidance.",
                "UnsupportedModifiers" => "This car's output curve is not supported",
                "UnsupportedBuild" => "Shift guidance is unavailable for this game build",
                "UnsupportedTransmission" => "Shift guidance is unavailable for this transmission",
                "UnsupportedPowertrain" => "Shift guidance is unavailable for this powertrain",
                "CurveMismatch" => "Car output data did not pass validation — shift guidance unavailable",
                _ => "Waiting for fresh supported car data"
            };
            return default;
        }
        var gear = (int)state.Gear;
        // Expiry must not re-arm the same frozen game sample simply because
        // it cleared the previous target identity. Wait for actual advancement.
        if (_shiftBlockedSample == (state.CarOrdinal, state.GameTimestampMilliseconds))
        {
            _shiftCrossings.Reset();
            _shiftTargetReached = false;
            _shiftDecision = "FrozenGameTime";
            ShiftCueStatus = "Waiting for advancing driving data";
            return default;
        }
        _shiftBlockedSample = null;
        if (data.Fingerprint != _shiftIdentity || gear != _shiftGear ||
            _shiftLastGameTime is uint prior && state.GameTimestampMilliseconds < prior)
        {
            ResetShiftCue();
            _shiftIdentity = data.Fingerprint;
            _shiftGear = gear;
        }
        if (_shiftLastGameTime != state.GameTimestampMilliseconds) _shiftGameAdvancedAt = now;
        _shiftLastGameTime = state.GameTimestampMilliseconds;
        if (now - _shiftGameAdvancedAt > Stopwatch.Frequency * .15)
        {
            _shiftCrossings.Reset();
            _shiftTargetReached = false;
            _shiftDecision = "FrozenGameTime";
            ShiftCueStatus = "Waiting for advancing driving data";
            return default;
        }
        if (gear < 1 || gear > data.Gears.Count || !data.Gears[gear - 1].HasEstimatedTarget)
        {
            _shiftCrossings.Reset();
            _shiftTargetReached = false;
            _shiftDecision = gear < 1 || gear > data.Gears.Count ? "InvalidGear" :
                gear == data.Profile.ForwardRatios.Count ? "TopGear" : data.Gears[gear - 1].Status.ToString();
            ShiftCueStatus = gear == data.Profile.ForwardRatios.Count ? "Top gear — no upshift" :
                _shiftCalibration is null ? "No supported crossover in this gear — cue stays off" :
                "Calibration needs more RPM coverage for this gear. Start another rolling pull from lower revs.";
            return default;
        }
        var target = data.Gears[gear - 1].EstimatedTargetRpm!.Value;
        var result = data.Gears[gear - 1];
        var limitBound = result.Status == AccelerationShiftStatus.VerifiedLimitBound;
        var live = data.LiveState;
        // This is the configured full-load target. Cornering, tire slip, boost
        // transients and instantaneous delivered torque do not erase that model.
        // Those channels remain in the diagnostic record for validation.
        _shiftDecision = !double.IsFinite(state.GroundSpeedMetersPerSecond) || !double.IsFinite(state.EngineRpm) || state.EngineRpm < 0
            ? "InvalidLiveState" : state.GroundSpeedMetersPerSecond < .5 ? "Stationary" :
            state.Accelerator == 0 ? "NoThrottle" : state.Brake > 1 ? "Braking" :
            native.Assists.IsLCOn ? "LaunchControl" :
            live is { AlternateLimiterBranch: true } ? "UnsupportedLimiterMode" :
            live is not null && (live.CurrentGear != gear || live.RequestedGear != gear) ? "GearTransition" :
            limitBound ? "FullLoadLimitTarget" : "FullLoadTarget";
        if (_shiftDecision is not ("FullLoadTarget" or "FullLoadLimitTarget"))
        {
            _shiftCrossings.Reset();
            _shiftTargetReached = false;
            _shiftRedTrigger = null;
            var reason = _shiftDecision switch
            {
                "Stationary" => "stationary",
                "NoThrottle" => "coasting",
                "Braking" => "braking",
                "LaunchControl" => "launch control",
                "GearTransition" => "gear change in progress",
                "UnsupportedLimiterMode" => "unsupported engine-limit mode",
                _ => "invalid driving data"
            };
            ShiftCueStatus = $"{(_shiftCalibration is null ? "Estimated" : "Calibrated")} full-load {gear} → {gear + 1}: {Math.Round(target / 50) * 50:N0} rpm · {reason}";
            return default;
        }
        ShiftCueStatus = $"{(_shiftCalibration is null ? "Estimated" : "Calibrated")} full-load {gear} → {gear + 1}: {target:N0} rpm · " +
            (result.Status == AccelerationShiftStatus.MeasuredUpperBoundary ? "measured upper-range target" :
                limitBound ? "engine-limit target" : "acceleration crossover");
        var observationExpiry = Math.Min(data.ObservedTimestamp + Stopwatch.Frequency,
            Math.Min(native.VisibilityObservedTimestamp + Stopwatch.Frequency / 4,
                _shiftGameAdvancedAt + (long)(Stopwatch.Frequency * .15)));
        var limiterActive = live is { LimiterActive: true };
        var releaseFloor = limiterActive
            ? Math.Min(target, Math.Max(state.EngineRpm, live!.EngineRpm)) * .85
            : _shiftTargetReached && _shiftRedTrigger == "NativeLimiter" ? _shiftLatchFloorRpm : target * .85;
        var epoch = _shiftCrossings.Arm(data.Fingerprint, state.CarOrdinal, gear, target,
            state.ReceivedTimestamp ?? now, observationExpiry, now, releaseFloor, state);
        if (epoch != _shiftObservationEpoch)
        {
            _shiftObservationEpoch = epoch;
            _shiftTargetReached = false;
            _shiftCrossingObservedAt = null;
        }
        var observedCrossing = _shiftCrossings.TryConsume(epoch, now, out var observedAt);
        var predictedCrossing = _shiftCrossings.TryPredict(epoch, now, out var predictedFrom);
        // Native RPM is an independently timed, single read of a value that may
        // change within a physics step. It cannot alone establish a target crossing.
        // A limiter cut can lower RPM immediately after the target was reached.
        // Keep the shift notification through that cut, within the same pull.
        if (!_shiftTargetReached && (state.EngineRpm >= target || observedCrossing ||
            limiterActive || predictedCrossing))
        {
            _shiftTargetReached = true;
            _shiftTargetReachedAt = now;
            _shiftRedTrigger = limiterActive ? "NativeLimiter" : observedCrossing ? "IngressCrossing" :
                state.EngineRpm >= target ? "TelemetryCrossing" : "MeasuredCadence";
            _shiftCrossingObservedAt = _shiftRedTrigger switch
            {
                "IngressCrossing" => observedAt,
                "MeasuredCadence" => predictedFrom,
                _ => state.ReceivedTimestamp ?? now
            };
            _shiftLatchFloorRpm = Math.Min(target, limiterActive ? Math.Max(state.EngineRpm, live!.EngineRpm) : target) * .85;
        }
        else if (!limiterActive && state.EngineRpm < _shiftLatchFloorRpm)
        {
            _shiftTargetReached = false;
            _shiftRedTrigger = null;
        }
        if (limiterActive)
            ShiftCueStatus = live!.SecondaryLimiterActive
                ? $"Limiter activity observed · boundary {live.SecondaryBoundaryRpm:N0} rpm · shift cue active"
                : "Limiter activity observed · shift cue active";
        var stage = _shiftTargetReached ? 3 : state.EngineRpm >= target * .95 ? 2 :
            state.EngineRpm >= target * .85 ? 1 : 0;
        var color = ResolveShiftCueColor(_shiftSettings, stage);
        // Every new red indication starts visibly on. A process-wide phase can
        // otherwise hide the first target crossing for up to 250 milliseconds.
        var flashOn = stage != 3 || (now - _shiftTargetReachedAt) / (Stopwatch.Frequency / 4) % 2 == 0;
        return new(true, stage, color, flashOn, target);
    }

    internal static double InterpolateShiftTorque(AccelerationShiftProfile profile, double rpm)
    {
        if (!double.IsFinite(rpm) || rpm < profile.Samples[0].Rpm || rpm > profile.Samples[^1].Rpm) return double.NaN;
        for (var i = 1; i < profile.Samples.Count; i++)
        {
            var right = profile.Samples[i];
            if (rpm > right.Rpm) continue;
            var left = profile.Samples[i - 1];
            return left.Torque + (right.Torque - left.Torque) * (rpm - left.Rpm) / (right.Rpm - left.Rpm);
        }
        return double.NaN;
    }

    internal static uint ResolveShiftCueColor(AppSettings settings, int stage)
    {
        var value = stage == 1 ? settings.ShiftCueGreenColor : stage == 2 ? settings.ShiftCueYellowColor : settings.ShiftCueRedColor;
        var fallback = stage == 1 ? "#FF70E7A1" : stage == 2 ? "#FFFFD166" : "#FFFF5364";
        if (!ColorCustomization.TryParse(value, out var color)) ColorCustomization.TryParse(fallback, out color);
        return 0xFF000000u | (uint)color.R << 16 | (uint)color.G << 8 | color.B;
    }
}
