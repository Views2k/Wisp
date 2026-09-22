using System.Collections.ObjectModel;

namespace Wisp.Core;

public readonly record struct AccelerationShiftSample(double Rpm, double Torque);

public readonly record struct AccelerationShiftBand(double MinimumRpm, double MaximumRpm);

public enum AccelerationShiftStatus
{
    Unavailable,
    InvalidProfile,
    NoNextGear,
    InsufficientCurveDomain,
    NonPositiveOutput,
    NoCrossoverInDomain,
    AlreadyNextGearBeneficial,
    EqualOutputPlateau,
    NonMonotonicAdvantage,
    EstimatedCrossover,
    VerifiedLimitBound,
    MeasuredUpperBoundary
}

/// <summary>
/// A copied, immutable metadata snapshot. Identity and live freshness checks
/// belong to the provider; a mathematically valid profile is not proof of its
/// provenance or of optimal real-world shift timing.
/// </summary>
public sealed class AccelerationShiftProfile
{
    public AccelerationShiftProfile(
        IEnumerable<AccelerationShiftSample> samples,
        IEnumerable<double> forwardRatios,
        IEnumerable<double>? gearAccelerationFactors = null,
        double? verifiedOperatingCeilingRpm = null)
    {
        Samples = Array.AsReadOnly(samples.ToArray());
        ForwardRatios = Array.AsReadOnly(forwardRatios.ToArray());
        GearAccelerationFactors = Array.AsReadOnly(gearAccelerationFactors?.ToArray()
            ?? Enumerable.Repeat(1d, ForwardRatios.Count).ToArray());
        VerifiedOperatingCeilingRpm = verifiedOperatingCeilingRpm;
        IsValid = Validate();
    }

    public ReadOnlyCollection<AccelerationShiftSample> Samples { get; }
    public ReadOnlyCollection<double> ForwardRatios { get; }
    public ReadOnlyCollection<double> GearAccelerationFactors { get; }
    public double? VerifiedOperatingCeilingRpm { get; }

    public bool IsValid { get; }

    private bool Validate() =>
        Samples.Count is >= 2 and <= 4096 &&
        ForwardRatios.Count is >= 1 and <= 16 &&
        GearAccelerationFactors.Count == ForwardRatios.Count &&
        Samples.All(p => double.IsFinite(p.Rpm) && p.Rpm >= 0 && double.IsFinite(p.Torque)) &&
        Samples.Zip(Samples.Skip(1)).All(pair => pair.First.Rpm < pair.Second.Rpm) &&
        ForwardRatios.All(r => double.IsFinite(r) && r > 0) &&
        ForwardRatios.Zip(ForwardRatios.Skip(1)).All(pair => pair.First > pair.Second) &&
        GearAccelerationFactors.All(f => double.IsFinite(f) && f > 0) &&
        (VerifiedOperatingCeilingRpm is null ||
            double.IsFinite(VerifiedOperatingCeilingRpm.Value) && VerifiedOperatingCeilingRpm.Value > 0);
}

/// <summary>
/// EstimatedTargetRpm is supplied for a stable interior model crossover or a
/// globally preferred boundary at the exact verified operating ceiling. The
/// latter has VerifiedLimitBound status; it is not a performance crossover.
/// ModelCandidateRpm is diagnostic and must not be used as a fallback cue.
/// Force equivalence bands describe model sensitivity, not confidence bounds.
/// </summary>
public sealed record AccelerationShiftResult(
    AccelerationShiftStatus Status,
    double? EstimatedTargetRpm,
    double? ModelCandidateRpm,
    double MinimumAnalysisRpm,
    double MaximumAnalysisRpm,
    bool OperatingCeilingVerified,
    IReadOnlyList<double> IntersectionRpms,
    IReadOnlyList<AccelerationShiftBand> EquivalentForceBands)
{
    public bool HasEstimatedTarget =>
        (Status is AccelerationShiftStatus.EstimatedCrossover or AccelerationShiftStatus.VerifiedLimitBound or AccelerationShiftStatus.MeasuredUpperBoundary) &&
        EstimatedTargetRpm.HasValue;
}
