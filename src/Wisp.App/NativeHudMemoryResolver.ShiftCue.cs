using Wisp.Core;

namespace Wisp.App;

public sealed partial class NativeHudMemoryResolver
{
    private readonly NativeShiftPerformanceReader _shiftReader = new() { ConfigurationOnly = true };
    private ShiftCuePerformance? _shiftPerformance;
    private bool _shiftGameplaySuspended;
    internal bool ShiftCueEnabled { get; set; }

    internal NativeHudSnapshot ApplyShiftGameplayVisibility(NativeHudSnapshot snapshot,
        NativeGameplayVisibility visibility)
    {
        var suspended = visibility != NativeGameplayVisibility.Visible;
        if (suspended || _shiftGameplaySuspended)
        {
            // Visibility is resolved after the vehicle snapshot. On resuming,
            // withdraw that snapshot's profile and let the next existing full
            // audit read the current tune instead of reusing the pre-menu cache.
            ResetShiftPerformance();
            if (snapshot.ShiftPerformance is not null)
                snapshot = snapshot with { ShiftPerformance = null };
        }
        _shiftGameplaySuspended = suspended;
        return snapshot;
    }

    private void ResetShiftPerformance()
    {
        _shiftReader.Reset();
        _shiftPerformance = null;
    }

    private ShiftCuePerformance? ReadShiftPerformance(IReadOnlyProcessMemory memory,
        ulong moduleBase, ulong source, ulong provider, int carOrdinal,
        float rpm, float maximumRpm, bool electric)
    {
        if (!ShiftCueEnabled || electric || _shiftGameplaySuspended)
        {
            ResetShiftPerformance();
            return null;
        }
        var data = _shiftReader.Resolve(memory, moduleBase, source, provider, carOrdinal, _pack);
        if (!TryMatchTelemetryIdentity(memory, source, provider, carOrdinal, rpm, maximumRpm, out _))
        {
            ShiftCaptureHub.Current?.RecordNativeAssociation(carOrdinal, "TelemetryMismatch", data.Fingerprint, data.ObservedTimestamp, false);
            ResetShiftPerformance();
            return null;
        }
        ShiftCaptureHub.Current?.RecordNativeAssociation(carOrdinal, data.Status.ToString(), data.Fingerprint, data.ObservedTimestamp, data.Available);
        if (!data.Available)
        {
            _shiftPerformance = null;
            return new(carOrdinal, 0, "", data.Status.ToString(), null, []);
        }
        if (_shiftPerformance is not { } cached || cached.Fingerprint != data.Fingerprint)
            _shiftPerformance = CreateShiftConfiguration(data);
        // Configuration remains cached at 2 Hz. Only the small live-state block
        // is refreshed by the existing native audit; no new polling worker.
        var available = NativeShiftLiveStateReader.TryRead(memory, moduleBase, source,
            provider, carOrdinal, _pack, out var live, out var read);
        if ((!available || read.Attempts > 1) && ShiftCaptureHub.Current is { } capture)
            capture.Record("native_live_read", new
            {
                outcome = read.Outcome.ToString(),
                attempts = read.Attempts,
                carOrdinal,
                fingerprint = data.Fingerprint,
                observationTimestamp = read.ObservationTimestamp,
                available
            });
        return _shiftPerformance = _shiftPerformance! with
        {
            ObservedTimestamp = data.ObservedTimestamp,
            LiveState = live,
            RequiresLiveState = true,
            Status = available ? "Research" : "LiveStateUnavailable"
        };
    }

    private static ShiftCuePerformance CreateShiftConfiguration(NativeShiftPerformanceSnapshot data)
    {
        // These raw points carry configuration only. The application replaces
        // them with a validated measured profile before enabling any cue.
        var profile = new AccelerationShiftProfile(
            data.TorqueNm.Select((torque, i) => new AccelerationShiftSample(i * data.StepRpm, torque)),
            data.ForwardRatios, data.GearAccelerationFactors,
            data.ConfiguredOperatingCeilingRpm);
        return new(data.CarOrdinal, data.ObservedTimestamp, data.Fingerprint, "Research", profile, [],
            new(data.ExactRedlineRpm, data.ConfiguredOperatingCeilingRpm,
                data.NativeBaselineUpperRpm, data.NativeAdjustedUpperRpm,
                data.ConfiguredPeakTorqueNm, data.ConfiguredPeakPowerWatts, 0, false, "configuration-only"));
    }

    internal static ShiftCuePerformance CreateShiftPerformance(NativeShiftPerformanceSnapshot data)
    {
        if (!data.Available)
            return new(data.CarOrdinal, 0, "", data.Status.ToString(), null, []);
        var runtimeCurve = data.RuntimeCurve;
        var samples = data.TorqueNm.Select((torque, i) =>
        {
            // These samples describe the profile for export/inspection. The
            // runtime solver evaluates the continuous modifier after raw-table
            // interpolation instead of interpolating modified sample knots.
            if (runtimeCurve is not null)
                torque = runtimeCurve.TryEvaluate(i * runtimeCurve.SourceStepOmega, out var value) ? value * 100d : double.NaN;
            return new AccelerationShiftSample(i * data.StepRpm, torque);
        });
        // The recovered limiter compares against this configured boundary;
        // keep it distinct from the tach redline. Inertia factors follow the
        // recovered steady-state estimator, not measured command/recovery time.
        var profile = new AccelerationShiftProfile(samples, data.ForwardRatios,
            gearAccelerationFactors: data.GearAccelerationFactors.Count == 0 ? null : data.GearAccelerationFactors,
            verifiedOperatingCeilingRpm: data.ConfiguredOperatingCeilingRpm);
        var results = Enumerable.Range(1, data.ForwardRatios.Count)
            .Select(gear => runtimeCurve is null
                ? AccelerationShiftSolver.Solve(profile, gear, Math.Max(500, data.ExactRedlineRpm * .3))
                : NativeCombustionRuntimeShiftSolver.Solve(runtimeCurve, profile, gear, Math.Max(500, data.ExactRedlineRpm * .3)))
            .ToArray();
        return new(data.CarOrdinal, data.ObservedTimestamp, data.Fingerprint,
            "Research", profile, Array.AsReadOnly(results), new(data.ExactRedlineRpm,
                data.ConfiguredOperatingCeilingRpm, data.NativeBaselineUpperRpm, data.NativeAdjustedUpperRpm,
                data.ConfiguredPeakTorqueNm, data.ConfiguredPeakPowerWatts,
                data.OfficialExportSourceMaximumRpm, data.HasModifiedExtension,
                runtimeCurve is null ? "configured-export" : "runtime-full-control-equilibrium"));
    }
}
