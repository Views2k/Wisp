namespace Wisp.Core;

/// <summary>Private research result. A torque crossover is not a validated driver-command time.</summary>
public sealed record ShiftCuePerformance(
    int CarOrdinal,
    long ObservedTimestamp,
    string Fingerprint,
    string Status,
    AccelerationShiftProfile? Profile,
    IReadOnlyList<AccelerationShiftResult> Gears,
    ShiftCueMetadata? Metadata = null,
    ShiftCueLiveState? LiveState = null,
    bool RequiresLiveState = false);

/// <summary>Native configuration observations and curve provenance for test replay.</summary>
public sealed record ShiftCueMetadata(
    double ExactRedlineRpm,
    double ConfiguredOperatingCeilingRpm,
    IReadOnlyList<double> NativeBaselineUpperRpm,
    IReadOnlyList<double> NativeAdjustedUpperRpm,
    double ConfiguredPeakTorqueNm,
    double ConfiguredPeakPowerWatts,
    double OfficialExportSourceMaximumRpm,
    bool HasModifiedExtension,
    string TorqueModel = "configured-export");
