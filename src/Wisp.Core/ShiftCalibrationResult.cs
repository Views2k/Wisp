using System.Collections.ObjectModel;

namespace Wisp.Core;

/// <summary>Immutable configuration supplied by the existing, identity-checked native reader.</summary>
public sealed class ShiftCalibrationContext
{
    public ShiftCalibrationContext(int carOrdinal, string fingerprint, IEnumerable<double> forwardRatios,
        IEnumerable<double>? gearAccelerationFactors = null, double? configuredOperatingCeilingRpm = null)
    {
        ArgumentNullException.ThrowIfNull(forwardRatios);
        CarOrdinal = carOrdinal;
        Fingerprint = fingerprint;
        ForwardRatios = Array.AsReadOnly(forwardRatios.ToArray());
        GearAccelerationFactors = Array.AsReadOnly(gearAccelerationFactors?.ToArray()
            ?? Enumerable.Repeat(1d, ForwardRatios.Count).ToArray());
        ConfiguredOperatingCeilingRpm = configuredOperatingCeilingRpm;
        var shape = new AccelerationShiftProfile([new(500, 1), new(1_000, 1)], ForwardRatios,
            GearAccelerationFactors, configuredOperatingCeilingRpm);
        IsValid = carOrdinal > 0 && !string.IsNullOrWhiteSpace(fingerprint) && fingerprint.Length <= 256 &&
            ForwardRatios.Count is >= 2 and <= 10 && shape.IsValid &&
            configuredOperatingCeilingRpm is null or >= 1_000 and <= 30_000;
    }

    public int CarOrdinal { get; }
    public string Fingerprint { get; }
    public ReadOnlyCollection<double> ForwardRatios { get; }
    public ReadOnlyCollection<double> GearAccelerationFactors { get; }
    public double? ConfiguredOperatingCeilingRpm { get; }
    public bool IsValid { get; }
}

public enum ShiftCalibrationStatus
{
    WaitingForPull,
    Collecting,
    InsufficientCurveCoverage,
    UnstableOutput,
    NeedConfirmingUpshift,
    NoDistinctTarget,
    Ready,
    ContextChanged,
    BufferFull
}

/// <summary>
/// Measured full-load targets, not a prediction of optimal button timing or arbitrary boost transients.
/// The profile deliberately has no verified ceiling: an observed safe upper sample is not the native cutoff.
/// </summary>
public sealed record ShiftCalibrationResult(
    ShiftCalibrationContext Context,
    long Revision,
    ShiftCalibrationStatus Status,
    string Reason,
    AccelerationShiftProfile? Profile,
    IReadOnlyList<AccelerationShiftResult> Gears,
    int AcceptedSamples,
    int ConfirmingUpshifts,
    double? EmpiricalUpperRpm)
{
    public bool Ready => Status == ShiftCalibrationStatus.Ready;
    public double? CoveredMinimumRpm => Profile?.Samples[0].Rpm;
    public double? CoveredMaximumRpm => Profile?.Samples[^1].Rpm;
    public double? ConfiguredOperatingCeilingRpm => Context.ConfiguredOperatingCeilingRpm;
}
