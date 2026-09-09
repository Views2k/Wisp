namespace Wisp.App;

public enum NativeGaugeReadStage
{
    None,
    Input,
    Registry,
    RegistryBucket,
    RegistryEntry,
    Hud,
    HudSignature,
    TypeVector,
    TypeEntry,
    Instance,
    OuterControl,
    OuterVtable,
    OuterBackReference,
    Source,
    Child,
    ChildVtable,
    Provider,
    GaugeBlock,
    Mode,
    Angle,
    Blur,
    TachometerMaximum,
    ElectricValues,
    OwnershipRecheck,
    CachedOwnership
}

public enum NativeGaugeReadFailure
{
    None,
    LayoutUnavailable,
    InvalidInput,
    ValidationFailed,
    ReadFailed,
    NotFound,
    Ambiguous,
    MissingChild,
    WrongSource,
    UnexpectedMode,
    InvalidAngle,
    InvalidBlur,
    InvalidMaximum,
    InvalidElectricValues,
    OwnershipChanged,
    ReadThrew
}

public enum NativeGaugeCacheOutcome
{
    NotUsed,
    Miss,
    Hit,
    RejectedThenResolved,
    RejectedThenUnavailable
}

/// <summary>One optional read diagnostic. No process addresses or unvalidated gauge values.</summary>
public readonly record struct NativeGaugeReadDiagnostics(
    bool Enabled,
    NativeGaugeReadFailure Failure,
    NativeGaugeReadStage Stage,
    NativeGaugeCacheOutcome CacheOutcome,
    bool HasValidatedSample,
    uint Mode,
    bool HasNeedlePair,
    double Angle,
    double Blur,
    long ElapsedStopwatchTicks);
