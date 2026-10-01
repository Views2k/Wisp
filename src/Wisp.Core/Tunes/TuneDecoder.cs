using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Wisp.Core.Tunes;

public static class TuneDecoder
{
    public const string SupportedGameVersion = "6.440.853.0";
    public const string SupportedExecutableSha256 = "FEC4A63CDEAD26F6528564F0E33C3D7FD02CD887337ED6E1AEACA79EB2843FCD";
    public const string ReaderVersion = "fh6-tune-1";
    public const int FieldCount = 37;
    public const int NormalizedWordCount = 46;

    internal sealed record Definition(TuneFieldId Id, int Offset, TuneCategory Category,
        TuneQuantity Quantity, int Decimals, TunePartId? Part = null, int Threshold = 2, int? ExactLevel = null);

    internal static readonly ImmutableArray<Definition> Definitions = CreateDefinitions();

    public static bool TryDecode(TuneDecodeInput? input, out TuneSnapshot? snapshot, out TuneDecodeFailure failure)
    {
        snapshot = null;
        failure = ValidateInput(input);
        if (failure != TuneDecodeFailure.None || input is null) return false;
        var parts = input.Parts.ToDictionary(part => part.Kind);
        var rows = ImmutableArray.CreateBuilder<TuneField>(FieldCount);
        foreach (var definition in Definitions)
        {
            uint bits = input.NormalizedCopies[0][definition.Offset / 4];
            float normalized = BitConverter.UInt32BitsToSingle(bits);
            bool applicable = IsApplicable(definition.Id, input.Drivetrain, input.ObservedGearEntryCount - 1);
            bool? adjustable = IsAdjustable(definition, parts, input.PartLevelsResolved);
            var status = !applicable ? TuneFieldStatus.NotApplicable
                : adjustable is null ? TuneFieldStatus.UnresolvedPartLevel
                : !adjustable.Value ? TuneFieldStatus.NotAdjustable
                : normalized == -1 ? TuneFieldStatus.UnresolvedDefault
                : TuneFieldStatus.Available;
            var conversion = input.Conversions[definition.Quantity];
            if (status == TuneFieldStatus.Available)
            {
                if (conversion.HasCallback) status = TuneFieldStatus.UnsupportedConversion;
                else if (conversion.UnitId != UnitDefinition(definition.Quantity).Id) status = TuneFieldStatus.UnsupportedUnit;
            }

            if (status != TuneFieldStatus.Available)
            {
                rows.Add(new(definition.Id, definition.Category, definition.Quantity, status, bits, adjustable,
                    null, null, null,
                    status is TuneFieldStatus.UnsupportedConversion or TuneFieldStatus.UnsupportedUnit ? conversion.UnitId : null,
                    null, null, null, definition.Decimals, null));
                continue;
            }

            var range = GetRange(input, definition);
            float value = Interpolate(range.Minimum, range.Maximum, normalized);
            double display = value * conversion.Factor;
            if (!double.IsFinite(display) || Math.Abs(display) >= 1e8)
            {
                failure = TuneDecodeFailure.InvalidValue;
                return false;
            }
            rows.Add(new(definition.Id, definition.Category, definition.Quantity, status, bits, adjustable,
                range.Minimum, range.Maximum, value, conversion.UnitId, UnitDefinition(definition.Quantity).Unit,
                conversion.Factor, display, definition.Decimals, FormatNumber(display, definition.Decimals)));
        }
        snapshot = new(Guid.NewGuid(), input.CapturedAtUtc.ToUniversalTime(),
            new(input.GameVersion, input.ExecutableSha256.ToUpperInvariant(), ReaderVersion, input.CarOrdinal,
                input.Drivetrain, input.ObservedGearEntryCount - 1), input.UnitPreference,
            input.Parts.Select(part => input.PartLevelsResolved ? part : part with { Level = null })
                .OrderBy(part => part.Kind).ToImmutableArray(), rows.MoveToImmutable());
        return true;
    }

    // The game uses separate float32 subtraction, multiplication and addition, without FMA.
    public static float Interpolate(float minimum, float maximum, float normalized)
    {
        float difference = (float)((double)maximum - minimum);
        float scaled = (float)((double)difference * normalized);
        return (float)((double)scaled + minimum);
    }

    public static string FormatNumber(double value, int decimals)
    {
        if (!double.IsFinite(value) || Math.Abs(value) >= 1e8 || decimals is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(value));
        double adjustment = value >= 0 ? .5 : -.5;
        for (int i = 0; i < decimals; i++) adjustment *= .1;
        double adjusted = value + adjustment;
        double whole = Math.Truncate(adjusted);
        bool negative = whole < 0 || (whole == 0 && adjusted < -Math.Pow(10, -decimals));
        var result = new StringBuilder();
        if (negative) result.Append('-');
        result.Append(Math.Abs(whole).ToString("0", CultureInfo.InvariantCulture));
        if (decimals > 0)
        {
            result.Append('.');
            double fraction = Math.Abs(adjusted) - Math.Floor(Math.Abs(adjusted));
            for (int i = 0; i < decimals; i++)
            {
                fraction *= 10;
                int digit = (int)Math.Truncate(fraction);
                result.Append((char)('0' + digit));
                fraction -= digit;
            }
        }
        return result.ToString();
    }

    internal static (int Id, TuneUnit Unit) UnitDefinition(TuneQuantity quantity) => quantity switch
    {
        TuneQuantity.Number => (0, TuneUnit.None),
        TuneQuantity.Percentage => (1, TuneUnit.Percent),
        TuneQuantity.Pressure => (41, TuneUnit.Psi),
        TuneQuantity.Angle => (47, TuneUnit.Degrees),
        TuneQuantity.SpringRate => (54, TuneUnit.PoundsPerInch),
        TuneQuantity.RideHeight => (14, TuneUnit.Inches),
        TuneQuantity.Downforce => (60, TuneUnit.Pounds),
        _ => throw new ArgumentOutOfRangeException(nameof(quantity))
    };

    internal static bool IsApplicable(TuneFieldId id, TuneDrivetrain drivetrain, int gears)
    {
        if (id is >= TuneFieldId.Gear1 and <= TuneFieldId.Gear10) return id - TuneFieldId.Gear1 < gears;
        return id switch
        {
            TuneFieldId.FrontDiffAcceleration or TuneFieldId.FrontDiffDeceleration => drivetrain != TuneDrivetrain.RearWheelDrive,
            TuneFieldId.RearDiffAcceleration or TuneFieldId.RearDiffDeceleration => drivetrain != TuneDrivetrain.FrontWheelDrive,
            TuneFieldId.CenterDiffBalance => drivetrain == TuneDrivetrain.AllWheelDrive,
            _ => true
        };
    }

    internal static bool? IsAdjustable(Definition definition, IReadOnlyDictionary<TunePartId, TunePart> parts, bool resolved)
    {
        if (definition.Part is not { } partId) return true;
        if (!resolved || parts[partId].Level is not { } level) return null;
        return definition.ExactLevel is { } exact ? level == exact : level > definition.Threshold;
    }

    private static TuneDecodeFailure ValidateInput(TuneDecodeInput? input)
    {
        if (input is null || !input.CaptureComplete) return TuneDecodeFailure.IncompleteCapture;
        if (input.GameVersion != SupportedGameVersion) return TuneDecodeFailure.UnsupportedBuild;
        if (!input.ExecutableVerified || !string.Equals(input.ExecutableSha256, SupportedExecutableSha256,
            StringComparison.OrdinalIgnoreCase)) return TuneDecodeFailure.UnverifiedExecutable;
        if (input.LocalProviderCount != 1) return TuneDecodeFailure.AmbiguousVehicle;
        if (!input.Coherent) return TuneDecodeFailure.IncoherentCapture;
        if (input.CarOrdinal <= 0 || !Enum.IsDefined(input.Drivetrain) || input.ObservedGearEntryCount is < 2 or > 11
            || input.UnitPreference is < 0 or > 6 || input.CapturedAtUtc == default) return TuneDecodeFailure.InvalidIdentity;
        if (input.NormalizedCopies.IsDefault || input.NormalizedCopies.Length != 3
            || input.NormalizedCopies.Any(copy => copy.IsDefault || copy.Length != NormalizedWordCount)
            || input.Bounds is null || input.Conversions is null || input.CarRanges is null)
            return TuneDecodeFailure.InvalidStructure;
        if (input.Format is null || input.Format.PositiveHalf != .5 || input.Format.NegativeHalf != -.5
            || input.Format.OneTenth != .1 || input.Format.Ten != 10
            || !float.IsFinite(input.Format.DegreesToRadians) || input.Format.DegreesToRadians <= 0)
            return TuneDecodeFailure.InvalidFormatter;
        if (!float.IsFinite(input.SpringScale) || input.SpringScale <= 0 || input.SpringScale >= 1e7)
            return TuneDecodeFailure.InvalidRange;
        if (!ValidParts(input.Parts, input.PartLevelsResolved)) return TuneDecodeFailure.InvalidParts;
        foreach (var quantity in Enum.GetValues<TuneQuantity>())
        {
            if (!input.Conversions.TryGetValue(quantity, out var conversion) || conversion is null
                || conversion.UnitId is < 0 or >= 128 || !double.IsFinite(conversion.Factor) || conversion.Factor <= 0)
                return TuneDecodeFailure.InvalidConversion;
        }
        foreach (var definition in Definitions)
        {
            uint bits = input.NormalizedCopies[0][definition.Offset / 4];
            if (input.NormalizedCopies.Any(copy => copy[definition.Offset / 4] != bits))
                return TuneDecodeFailure.IncoherentCapture;
            float value = BitConverter.UInt32BitsToSingle(bits);
            if (!float.IsFinite(value) || (value != -1 && value is < 0 or > 1)) return TuneDecodeFailure.InvalidValue;
            if (UsesCarRange(definition.Id) && !input.CarRanges.ContainsKey(definition.Id))
                return TuneDecodeFailure.InvalidRange;
            var range = GetRange(input, definition);
            if (!float.IsFinite(range.Minimum) || !float.IsFinite(range.Maximum) || range.Minimum > range.Maximum)
                return TuneDecodeFailure.InvalidRange;
        }
        return TuneDecodeFailure.None;
    }

    internal static bool ValidParts(ImmutableArray<TunePart> parts, bool resolved)
    {
        if (parts.IsDefault || parts.Length != 12 || parts.Any(part => part is null || !Enum.IsDefined(part.Kind))
            || parts.Select(part => part.Kind).Distinct().Count() != 12) return false;
        return parts.All(part => !resolved && part.Level is null ||
            (part.InstalledId < 0 ? part.Level == -1 : part.Level is >= 0 and <= 100));
    }

    private static bool UsesCarRange(TuneFieldId id) => id is >= TuneFieldId.FrontAntiroll and <= TuneFieldId.RearDownforce;

    private static TuneRange GetRange(TuneDecodeInput input, Definition definition)
    {
        TuneRange range;
        if (UsesCarRange(definition.Id)) range = input.CarRanges[definition.Id];
        else range = definition.Id switch
        {
            TuneFieldId.FrontTirePressure or TuneFieldId.RearTirePressure => input.Bounds.TirePressure,
            TuneFieldId.FinalDrive => input.Bounds.FinalDrive,
            >= TuneFieldId.Gear1 and <= TuneFieldId.Gear10 => input.Bounds.GearRatio,
            TuneFieldId.FrontCamber or TuneFieldId.RearCamber => input.Bounds.CamberDegrees,
            TuneFieldId.FrontToe or TuneFieldId.RearToe => input.Bounds.ToeDegrees,
            TuneFieldId.FrontCaster => input.Bounds.CasterDegrees,
            TuneFieldId.BrakePressure => new(0, 200),
            _ => new(0, 100)
        };
        float scale = definition.Quantity == TuneQuantity.Angle ? input.Format.DegreesToRadians
            : definition.Quantity == TuneQuantity.SpringRate ? input.SpringScale : 1;
        return new((float)((double)range.Minimum * scale), (float)((double)range.Maximum * scale));
    }

    private static ImmutableArray<Definition> CreateDefinitions()
    {
        var definitions = ImmutableArray.CreateBuilder<Definition>(FieldCount);
        void Add(TuneFieldId id, int offset, TuneCategory category, TuneQuantity quantity, int decimals,
            TunePartId? part = null, int threshold = 2, int? exact = null) =>
            definitions.Add(new(id, offset, category, quantity, decimals, part, threshold, exact));
        Add(TuneFieldId.FrontTirePressure, 0x30, TuneCategory.Tires, TuneQuantity.Pressure, 1);
        Add(TuneFieldId.RearTirePressure, 0x5C, TuneCategory.Tires, TuneQuantity.Pressure, 1);
        Add(TuneFieldId.FinalDrive, 0x08, TuneCategory.Gearing, TuneQuantity.Number, 2, TunePartId.Transmission, 1);
        for (int i = 0; i < 10; i++) Add(TuneFieldId.Gear1 + i, 0x90 + i * 4, TuneCategory.Gearing, TuneQuantity.Number, 2, TunePartId.Transmission);
        Add(TuneFieldId.FrontCamber, 0x34, TuneCategory.Alignment, TuneQuantity.Angle, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.RearCamber, 0x60, TuneCategory.Alignment, TuneQuantity.Angle, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.FrontToe, 0x38, TuneCategory.Alignment, TuneQuantity.Angle, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.RearToe, 0x64, TuneCategory.Alignment, TuneQuantity.Angle, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.FrontCaster, 0x3C, TuneCategory.Alignment, TuneQuantity.Angle, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.FrontAntiroll, 0x44, TuneCategory.AntirollBars, TuneQuantity.Number, 2, TunePartId.FrontAntiroll);
        Add(TuneFieldId.RearAntiroll, 0x70, TuneCategory.AntirollBars, TuneQuantity.Number, 2, TunePartId.RearAntiroll);
        Add(TuneFieldId.FrontSprings, 0x40, TuneCategory.Springs, TuneQuantity.SpringRate, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.RearSprings, 0x6C, TuneCategory.Springs, TuneQuantity.SpringRate, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.FrontRideHeight, 0x48, TuneCategory.Springs, TuneQuantity.RideHeight, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.RearRideHeight, 0x74, TuneCategory.Springs, TuneQuantity.RideHeight, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.FrontRebound, 0x50, TuneCategory.Damping, TuneQuantity.Number, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.RearRebound, 0x7C, TuneCategory.Damping, TuneQuantity.Number, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.FrontBump, 0x4C, TuneCategory.Damping, TuneQuantity.Number, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.RearBump, 0x78, TuneCategory.Damping, TuneQuantity.Number, 1, TunePartId.SpringDamper);
        Add(TuneFieldId.FrontDownforce, 0x00, TuneCategory.Aero, TuneQuantity.Downforce, 0, TunePartId.FrontAero, exact: 3);
        Add(TuneFieldId.RearDownforce, 0x04, TuneCategory.Aero, TuneQuantity.Downforce, 0, TunePartId.RearAero, exact: 3);
        Add(TuneFieldId.BrakeBalance, 0x10, TuneCategory.Brake, TuneQuantity.Percentage, 0, TunePartId.Brakes);
        Add(TuneFieldId.BrakePressure, 0x0C, TuneCategory.Brake, TuneQuantity.Percentage, 0, TunePartId.Brakes);
        Add(TuneFieldId.FrontDiffAcceleration, 0x54, TuneCategory.Differential, TuneQuantity.Percentage, 0, TunePartId.Differential, 1);
        Add(TuneFieldId.FrontDiffDeceleration, 0x58, TuneCategory.Differential, TuneQuantity.Percentage, 0, TunePartId.Differential);
        Add(TuneFieldId.RearDiffAcceleration, 0x80, TuneCategory.Differential, TuneQuantity.Percentage, 0, TunePartId.Differential, 1);
        Add(TuneFieldId.RearDiffDeceleration, 0x84, TuneCategory.Differential, TuneQuantity.Percentage, 0, TunePartId.Differential);
        Add(TuneFieldId.CenterDiffBalance, 0x18, TuneCategory.Differential, TuneQuantity.Percentage, 0, TunePartId.Differential);
        return definitions.MoveToImmutable();
    }
}
