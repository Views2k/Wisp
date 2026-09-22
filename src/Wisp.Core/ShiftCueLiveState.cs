namespace Wisp.Core;

/// <summary>Bounded, timestamped native observations; these are not an optimal shift-command model.</summary>
public sealed record ShiftCueLiveState(
    int CarOrdinal,
    long ObservedTimestamp,
    double EngineRpm,
    int CurrentGear,
    int RequestedGear,
    int PreviousGear,
    bool LimiterActive,
    bool SecondaryLimiterActive,
    double SecondaryBoundaryRpm,
    double OutputControl,
    bool AlternateLimiterBranch);
