using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wisp.Core;

namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    private readonly Queue<ShiftTestSample> _shiftTestSamples = new();
    private readonly Dictionary<string, ShiftCuePerformance> _shiftTestProfiles = new();
    private readonly Dictionary<ShiftTestDecisionKey, ShiftTestDecisionCount> _shiftTestDecisions = new();
    private (int Car, uint Time, long? Received)? _lastShiftTestSample;
    private long _shiftTestSequence;
    private long _shiftTestDiscardedDecisionGroups;

    private sealed record ShiftTestSample(long Sequence, int Car, uint GameMilliseconds, int Gear,
        double Rpm, double SpeedMetersPerSecond, double TorqueNm, double PowerWatts,
        byte Throttle, byte Brake, double Slip, double LateralAcceleration,
        string? Profile, int Stage, double? TargetRpm, string Status, string Decision,
        bool IsRaceOn, sbyte Steering, double BoostPressurePsi, bool? LaunchControlActive,
        long ObservedTimestamp, double? PacketAgeMilliseconds, long? MetadataObservedTimestamp,
        long? VisibilityObservedTimestamp, string? NativeStatus, string? GameplayVisibility,
        bool CueEnabled, bool CueFlashOn, bool CueVisible, long? RedStartedTimestamp,
        long? CrossingObservedTimestamp, ShiftCueLiveState? LiveState, string? RedTrigger,
        long? EvaluationTimestamp, double? RawPacketAgeMilliseconds);

    private sealed record ShiftTestDecisionKey(int Car, string? Profile, int Gear,
        string Decision, int Stage, double? TargetRpm);

    private sealed record ShiftTestDecisionCount(ShiftTestDecisionKey Key, long Samples,
        long FirstSequence, long LastSequence, uint FirstGameMilliseconds, uint LastGameMilliseconds);

    private void RecordShiftTestSample(VehicleState state, ShiftCuePerformance? profile, ShiftCueVisualState cue,
        NativeHudSnapshot? native = null, TimeSpan? packetAge = null)
    {
        long? evaluatedAt = _shiftEvaluationTimestamp > 0 ? _shiftEvaluationTimestamp : null;
        double? rawPacketAge = evaluatedAt.HasValue && state.ReceivedTimestamp is long received
            ? (evaluatedAt.Value - received) * 1000d / Stopwatch.Frequency : null;
        if (ShiftCaptureHub.Current is { } capture)
        {
            capture.RecordEvaluation(new
            {
                state.ReceivedTimestamp,
                state.CarOrdinal,
                state.GameTimestampMilliseconds,
                gear = (int)state.Gear,
                state.EngineRpm,
                cue,
                decision = _shiftDecision,
                profile = profile?.Fingerprint,
                profileObserved = profile?.ObservedTimestamp,
                nativeStatus = native?.Status.ToString(),
                visibility = native?.GameplayVisibility.ToString(),
                visibilityObserved = native?.VisibilityObservedTimestamp,
                packetAgeMs = packetAge?.TotalMilliseconds,
                evaluationTimestamp = evaluatedAt,
                rawPacketAgeMs = rawPacketAge,
                redStarted = _shiftTargetReached ? (long?)_shiftTargetReachedAt : null,
                crossingObserved = _shiftTargetReached ? _shiftCrossingObservedAt : null,
                redTrigger = _shiftTargetReached ? _shiftRedTrigger : null,
                liveState = profile?.LiveState,
                assists = native?.Assists,
                cueSettingEnabled = AccelerationShiftCueEnabled
            }, _shiftTimestamp(),
                AccelerationShiftCueEnabled, profile?.Profile is not null ? profile : null);
            if (profile?.Profile is not null)
                capture.RecordProfile("model:" + profile.Fingerprint, new
                {
                    kind = "calculated-model",
                    profile.CarOrdinal,
                    profile.Fingerprint,
                    profile.Metadata,
                    profile.Profile.Samples,
                    profile.Profile.ForwardRatios,
                    profile.Profile.GearAccelerationFactors,
                    profile.Gears
                });
        }
        if (!AccelerationShiftCueEnabled || _lastShiftTestSample == (state.CarOrdinal, state.GameTimestampMilliseconds, state.ReceivedTimestamp)) return;
        _lastShiftTestSample = (state.CarOrdinal, state.GameTimestampMilliseconds, state.ReceivedTimestamp);
        if (profile?.Profile is not null) _shiftTestProfiles[profile.Fingerprint] = profile;
        var gear = (int)state.Gear;
        var target = cue.TargetRpm > 0 ? cue.TargetRpm :
            profile is { } matching && matching.CarOrdinal == state.CarOrdinal && gear >= 1 && gear <= matching.Gears.Count
                ? matching.Gears[gear - 1].EstimatedTargetRpm : null;
        _shiftTestSamples.Enqueue(new(++_shiftTestSequence, state.CarOrdinal, state.GameTimestampMilliseconds,
            gear, state.EngineRpm, state.GroundSpeedMetersPerSecond, state.TorqueNm, state.PowerWatts,
            state.Accelerator, state.Brake, state.TireSlipRatio.MaximumAbsolute(),
            state.LateralAccelerationMetersPerSecondSquared, profile?.Fingerprint, cue.Stage,
            target, ShiftCueStatus, _shiftDecision, state.IsRaceOn, state.Steering, state.BoostPressurePsi,
            native?.Assists.IsLCOn, _shiftTimestamp(), packetAge?.TotalMilliseconds,
            profile?.ObservedTimestamp, native?.VisibilityObservedTimestamp,
            native?.Status.ToString(), native?.GameplayVisibility.ToString(),
            cue.Enabled, cue.FlashOn, cue.IsVisible, _shiftTargetReached ? _shiftTargetReachedAt : null,
            _shiftTargetReached ? _shiftCrossingObservedAt : null, profile?.LiveState,
            _shiftTargetReached ? _shiftRedTrigger : null, evaluatedAt, rawPacketAge));
        while (_shiftTestSamples.Count > 2_000) _shiftTestSamples.Dequeue();
        var key = new ShiftTestDecisionKey(state.CarOrdinal, profile?.Fingerprint, gear, _shiftDecision, cue.Stage, target);
        _shiftTestDecisions[key] = _shiftTestDecisions.TryGetValue(key, out var count)
            ? count with
            {
                Samples = count.Samples + 1,
                LastSequence = _shiftTestSequence,
                LastGameMilliseconds = state.GameTimestampMilliseconds
            }
            : new(key, 1, _shiftTestSequence, _shiftTestSequence, state.GameTimestampMilliseconds, state.GameTimestampMilliseconds);
        // Preserve earlier-gear decisions after their detailed samples roll out,
        // while bounding sessions containing many cars and tune fingerprints.
        if (_shiftTestDecisions.Count > 256)
        {
            var oldest = _shiftTestDecisions.MinBy(pair => pair.Value.LastSequence).Key;
            _shiftTestDecisions.Remove(oldest);
            _shiftTestDiscardedDecisionGroups++;
        }
        // Tune edits can introduce many fingerprints; retain only bounded,
        // referenced models rather than an unbounded per-car history.
        if (_shiftTestProfiles.Count > 8)
        {
            var retained = _shiftTestSamples.Reverse().Where(s => !string.IsNullOrEmpty(s.Profile))
                .Select(s => s.Profile!).Distinct().Take(8).ToHashSet();
            foreach (var profileKey in _shiftTestProfiles.Keys.Where(k => !retained.Contains(k)).ToArray())
                _shiftTestProfiles.Remove(profileKey);
        }
    }

    internal string ExportShiftTestData() => JsonSerializer.Serialize(new
    {
        schemaVersion = 2,
        exportedAtUtc = DateTimeOffset.UtcNow,
        version = ApplicationVersionInfo.MachineVersion,
        diagnosticBuildId = ApplicationVersionInfo.DiagnosticBuildId,
        purpose = "Private steady-state shift-crossover validation. Not proof of fastest shift timing.",
        retention = "Most recent 2000 distinct received samples, up to 8 recent profiles, and up to 256 per-car/tune/gear/decision/stage/target count groups across the test session. Equal game timestamps do not discard fresh receipts. Cue visibility is sampled state, not a displayed-frame log. Invalid numeric samples are exported as named strings.",
        totalUniqueSamples = _shiftTestSequence,
        discardedDecisionGroups = _shiftTestDiscardedDecisionGroups,
        stopwatchFrequency = Stopwatch.Frequency,
        samples = _shiftTestSamples.ToArray(),
        decisionCounts = _shiftTestDecisions.Values.OrderBy(d => d.FirstSequence).Select(d => new
        {
            d.Key.Car,
            d.Key.Profile,
            d.Key.Gear,
            d.Key.Decision,
            d.Key.Stage,
            d.Key.TargetRpm,
            d.Samples,
            d.FirstSequence,
            d.LastSequence,
            d.FirstGameMilliseconds,
            d.LastGameMilliseconds
        }),
        profiles = _shiftTestProfiles.Values.Select(p => new
        {
            p.CarOrdinal,
            p.Fingerprint,
            p.Status,
            p.Metadata,
            samples = p.Profile!.Samples,
            ratios = p.Profile.ForwardRatios,
            gearAccelerationFactors = p.Profile.GearAccelerationFactors,
            results = p.Gears.Select(g => new
            {
                status = g.Status.ToString(),
                g.EstimatedTargetRpm,
                g.MinimumAnalysisRpm,
                g.MaximumAnalysisRpm,
                g.OperatingCeilingVerified
            })
        })
    }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
}
