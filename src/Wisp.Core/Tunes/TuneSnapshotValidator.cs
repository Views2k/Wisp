using System.Globalization;

namespace Wisp.Core.Tunes;

public static class TuneSnapshotValidator
{
    public static bool TryValidate(TuneSnapshot? snapshot, out string reason)
    {
        reason = "Tune data is invalid.";
        if (snapshot is null || snapshot.Id == Guid.Empty || snapshot.CapturedAtUtc == default
            || snapshot.CapturedAtUtc.Offset != TimeSpan.Zero || snapshot.Identity is not { } identity)
            return false;
        if (identity.ReaderVersion != TuneDecoder.ReaderVersion ||
            !TuneVerificationProfiles.IsSupported(identity.GameVersion, identity.ExecutableSha256, identity.Verification))
        {
            reason = "This tune uses an unsupported game build or reader.";
            return false;
        }
        if (!ValidCarName(snapshot.CarName) || identity.CarOrdinal <= 0 || !Enum.IsDefined(identity.Drivetrain) || identity.ForwardGearCount is < 1 or > 10
            || snapshot.UnitPreference is < 0 or > 6 || !TuneDecoder.ValidParts(snapshot.Parts, false)
            || snapshot.Fields.IsDefault || snapshot.Fields.Length != TuneDecoder.FieldCount)
            return false;
        var parts = snapshot.Parts.ToDictionary(part => part.Kind);
        for (int i = 0; i < TuneDecoder.FieldCount; i++)
        {
            var field = snapshot.Fields[i];
            var definition = TuneDecoder.Definitions[i];
            if (field is null || field.Id != definition.Id || field.Category != definition.Category
                || field.Quantity != definition.Quantity || field.DisplayDecimals != definition.Decimals
                || !Enum.IsDefined(field.Status)) return false;
            float normalized = BitConverter.UInt32BitsToSingle(field.NormalizedBits);
            if (!float.IsFinite(normalized) || (normalized != -1 && normalized is < 0 or > 1)) return false;
            bool applicable = TuneDecoder.IsApplicable(field.Id, identity.Drivetrain, identity.ForwardGearCount);
            if (applicable == (field.Status == TuneFieldStatus.NotApplicable)) return false;
            bool? adjustable = TuneDecoder.IsAdjustable(definition, parts, true);
            if (field.Status == TuneFieldStatus.UnresolvedPartLevel)
            {
                if (definition.Part is null || field.Adjustable is not null) return false;
            }
            else if (field.Adjustable != adjustable) return false;

            if (field.Status == TuneFieldStatus.Available)
            {
                var unit = TuneDecoder.UnitDefinition(field.Quantity);
                if (field.Adjustable != true || normalized == -1 || field.Minimum is not { } minimum
                    || field.Maximum is not { } maximum || field.RangeValue is not { } rangeValue
                    || !float.IsFinite(minimum) || !float.IsFinite(maximum) || minimum > maximum
                    || !float.IsFinite(rangeValue) || field.UnitId != unit.Id || field.Unit != unit.Unit
                    || field.ConversionFactor is not { } factor || !double.IsFinite(factor) || factor <= 0
                    || field.DisplayValue is not { } display || !double.IsFinite(display) || Math.Abs(display) >= 1e8
                    || BitConverter.SingleToUInt32Bits(rangeValue) != BitConverter.SingleToUInt32Bits(
                        TuneDecoder.Interpolate(minimum, maximum, normalized))
                    || display != rangeValue * factor || field.DisplayText != TuneDecoder.FormatNumber(display, field.DisplayDecimals))
                    return false;
                continue;
            }
            if (field.Minimum is not null || field.Maximum is not null || field.RangeValue is not null
                || field.Unit is not null || field.ConversionFactor is not null || field.DisplayValue is not null
                || field.DisplayText is not null) return false;
            if (field.Status is TuneFieldStatus.UnsupportedConversion or TuneFieldStatus.UnsupportedUnit)
            {
                if (field.Adjustable != true || normalized == -1 || field.UnitId is not (>= 0 and < 128)) return false;
                if (field.Status == TuneFieldStatus.UnsupportedUnit && field.UnitId == TuneDecoder.UnitDefinition(field.Quantity).Id)
                    return false;
            }
            else if (field.UnitId is not null) return false;
            if (field.Status == TuneFieldStatus.UnresolvedDefault && (normalized != -1 || field.Adjustable != true)) return false;
            if (field.Status == TuneFieldStatus.NotAdjustable && field.Adjustable != false) return false;
        }
        reason = string.Empty;
        return true;
    }

    internal static bool ValidCarName(string? value) => value is null ||
        value.Length is > 0 and <= 200 && value == value.Trim() && !value.Any(character =>
            char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
}
