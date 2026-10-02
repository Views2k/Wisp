using System.Collections.Immutable;

namespace Wisp.Core.Tunes;

public sealed record TuneComparisonRow(TuneFieldId Id, TuneCategory Category, TuneField? A, TuneField? B,
    double? DisplayDelta, bool RawEqual);

public static class TuneComparison
{
    public static ImmutableArray<TuneComparisonRow> Compare(TuneSnapshot? a, TuneSnapshot? b)
    {
        Dictionary<TuneFieldId, TuneField> left = a is null ? [] : a.Fields.ToDictionary(field => field.Id);
        Dictionary<TuneFieldId, TuneField> right = b is null ? [] : b.Fields.ToDictionary(field => field.Id);
        return TuneDecoder.Definitions.Select(definition =>
        {
            left.TryGetValue(definition.Id, out var x);
            right.TryGetValue(definition.Id, out var y);
            double? delta = x?.Status == TuneFieldStatus.Available && y?.Status == TuneFieldStatus.Available
                && x.Quantity == y.Quantity && x.RangeValue is { } xv && y.RangeValue is { } yv
                && x.ConversionFactor is { } factor ? ((double)yv - xv) * factor : null;
            return new TuneComparisonRow(definition.Id, definition.Category, x, y, delta, HaveSameRawValue(x, y));
        }).ToImmutableArray();
    }

    // This compares the supported tuning controls/part context, not an entire installed upgrade list.
    public static bool HaveSameSetupIdentity(TuneSnapshot a, TuneSnapshot b)
    {
        if (!TuneSnapshotValidator.TryValidate(a, out _) || !TuneSnapshotValidator.TryValidate(b, out _)
            || !a.IsComplete || !b.IsComplete ||
            (a.Identity with { Verification = null }) != (b.Identity with { Verification = null })
            || !a.Parts.OrderBy(part => part.Kind).SequenceEqual(b.Parts.OrderBy(part => part.Kind))) return false;
        return Compare(a, b).All(row => row.RawEqual);
    }

    private static bool HaveSameRawValue(TuneField? a, TuneField? b)
    {
        if (a is null || b is null || a.Id != b.Id || a.Status != b.Status || a.Adjustable != b.Adjustable) return false;
        if (a.Status == TuneFieldStatus.NotApplicable) return true;
        return a.NormalizedBits == b.NormalizedBits && SameBits(a.Minimum, b.Minimum)
            && SameBits(a.Maximum, b.Maximum) && SameBits(a.RangeValue, b.RangeValue);
    }

    private static bool SameBits(float? a, float? b) => a.HasValue == b.HasValue
        && (!a.HasValue || BitConverter.SingleToUInt32Bits(a.Value) == BitConverter.SingleToUInt32Bits(b!.Value));
}
