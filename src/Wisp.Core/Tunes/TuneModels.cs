using System.Collections.Immutable;

namespace Wisp.Core.Tunes;

public enum TuneCategory { Tires, Gearing, Alignment, AntirollBars, Springs, Damping, Aero, Brake, Differential }
public enum TuneDrivetrain { FrontWheelDrive, RearWheelDrive, AllWheelDrive }
public enum TuneQuantity { Number, Percentage, Pressure, Angle, SpringRate, RideHeight, Downforce }
public enum TuneUnit { None, Percent, Psi, Degrees, PoundsPerInch, Inches, Pounds }
public enum TuneFieldStatus
{
    Available, NotApplicable, NotAdjustable, UnresolvedPartLevel, UnresolvedDefault,
    UnsupportedConversion, UnsupportedUnit
}
public enum TuneFieldId
{
    FrontTirePressure, RearTirePressure, FinalDrive,
    Gear1, Gear2, Gear3, Gear4, Gear5, Gear6, Gear7, Gear8, Gear9, Gear10,
    FrontCamber, RearCamber, FrontToe, RearToe, FrontCaster,
    FrontAntiroll, RearAntiroll, FrontSprings, RearSprings, FrontRideHeight, RearRideHeight,
    FrontRebound, RearRebound, FrontBump, RearBump, FrontDownforce, RearDownforce,
    BrakeBalance, BrakePressure, FrontDiffAcceleration, FrontDiffDeceleration,
    RearDiffAcceleration, RearDiffDeceleration, CenterDiffBalance
}
public enum TunePartId
{
    Engine, Drivetrain, CarBody, Motor, Brakes, SpringDamper, FrontAntiroll, RearAntiroll,
    RearAero, Transmission, Differential, FrontAero
}

public sealed record TuneIdentity(string GameVersion, string ExecutableSha256, string ReaderVersion,
    int CarOrdinal, TuneDrivetrain Drivetrain, int ForwardGearCount);
public sealed record TunePart(TunePartId Kind, int InstalledId, int? Level);
public readonly record struct TuneRange(float Minimum, float Maximum);
public sealed record TuneConversion(int UnitId, double Factor, bool HasCallback);
public sealed record TuneGlobalBounds(TuneRange FinalDrive, TuneRange GearRatio, TuneRange TirePressure,
    TuneRange CamberDegrees, TuneRange ToeDegrees, TuneRange CasterDegrees);
public sealed record TuneFormatConstants(float DegreesToRadians, double PositiveHalf,
    double NegativeHalf, double OneTenth, double Ten);

public sealed record TuneField(TuneFieldId Id, TuneCategory Category, TuneQuantity Quantity,
    TuneFieldStatus Status, uint NormalizedBits, bool? Adjustable,
    float? Minimum, float? Maximum, float? RangeValue, int? UnitId,
    TuneUnit? Unit, double? ConversionFactor, double? DisplayValue,
    int DisplayDecimals, string? DisplayText);

public sealed record TuneSnapshot(Guid Id, DateTimeOffset CapturedAtUtc, TuneIdentity Identity,
    int UnitPreference, ImmutableArray<TunePart> Parts, ImmutableArray<TuneField> Fields)
{
    public const int SchemaVersion = 1;
    public bool IsComplete => !Fields.IsDefault && Fields.Length == TuneDecoder.FieldCount && Fields.All(field => field is not null && field.Status is
        TuneFieldStatus.Available or TuneFieldStatus.NotApplicable or TuneFieldStatus.NotAdjustable);
}

// The native reader supplies only stable, bounded observations. No process pointers are persisted.
public sealed record TuneDecodeInput
{
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required string GameVersion { get; init; }
    public required string ExecutableSha256 { get; init; }
    public required bool ExecutableVerified { get; init; }
    public required bool CaptureComplete { get; init; }
    public required bool Coherent { get; init; }
    public required int LocalProviderCount { get; init; }
    public required int CarOrdinal { get; init; }
    public required TuneDrivetrain Drivetrain { get; init; }
    public required int ObservedGearEntryCount { get; init; }
    public required int UnitPreference { get; init; }
    public required ImmutableArray<ImmutableArray<uint>> NormalizedCopies { get; init; }
    public required TuneGlobalBounds Bounds { get; init; }
    public required ImmutableDictionary<TuneQuantity, TuneConversion> Conversions { get; init; }
    public required TuneFormatConstants Format { get; init; }
    public required ImmutableDictionary<TuneFieldId, TuneRange> CarRanges { get; init; }
    public required float SpringScale { get; init; }
    public required bool PartLevelsResolved { get; init; }
    public required ImmutableArray<TunePart> Parts { get; init; }
}

public enum TuneDecodeFailure
{
    None, IncompleteCapture, UnsupportedBuild, UnverifiedExecutable, AmbiguousVehicle,
    IncoherentCapture, InvalidIdentity, InvalidStructure, InvalidValue, InvalidRange,
    InvalidParts, InvalidConversion, InvalidFormatter
}
