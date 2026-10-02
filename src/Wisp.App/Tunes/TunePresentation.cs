using System.Globalization;
using Wisp.Core.Tunes;

namespace Wisp.App.Tunes;

public sealed record TuneCategoryOption(TuneCategory Category, string Label);
public sealed record TuneSortOption(TuneSort Sort, string Label);
public enum TuneSort { Newest, Oldest, NameAscending, NameDescending }
public enum TuneWorkspace { Current, Saved, Compare }

public sealed record TuneDisplayRow(string Label, string Value, string Range, string OtherValue,
    string Difference, bool Changed, bool HasRange, double RangePosition);

internal static class TunePresentation
{
    internal static string Car(TuneSnapshot snapshot) => snapshot.CarName ?? $"Car {snapshot.Identity.CarOrdinal}";

    internal static string DefaultName(TuneSnapshot snapshot)
    {
        var name = snapshot.CarName ?? $"Car {snapshot.Identity.CarOrdinal} · {snapshot.CapturedAtUtc.ToLocalTime():MMM d HH:mm}";
        return name.Length <= TuneStore.MaximumNameLength ? name : name[..(TuneStore.MaximumNameLength - 1)] + "…";
    }

    internal static string CaptureStatus(TuneSnapshot snapshot)
    {
        if (snapshot.Fields.Any(field => field.Status == TuneFieldStatus.UnsupportedUnit))
            return "Tune reading currently supports imperial game units only. Change Forza's units to imperial, then refresh.";
        if (snapshot.Fields.Any(field => field.Status == TuneFieldStatus.UnsupportedConversion))
            return "This tune includes a value conversion Wisp does not support yet. It cannot be saved until all values can be read.";
        return snapshot.IsComplete ? "Tune read. Save a copy to keep it in Wisp."
            : "Some tune values could not be read. Refresh to try again; saving is available when all values are read.";
    }

    internal static string Category(TuneCategory category) => category == TuneCategory.AntirollBars ? "Antiroll Bars" : category.ToString();

    internal static string Label(TuneFieldId id) => id switch
    {
        TuneFieldId.FrontTirePressure => "Front pressure",
        TuneFieldId.RearTirePressure => "Rear pressure",
        TuneFieldId.FinalDrive => "Final drive",
        >= TuneFieldId.Gear1 and <= TuneFieldId.Gear10 => $"Gear {(int)id - (int)TuneFieldId.Gear1 + 1}",
        TuneFieldId.FrontCamber => "Front camber",
        TuneFieldId.RearCamber => "Rear camber",
        TuneFieldId.FrontToe => "Front toe",
        TuneFieldId.RearToe => "Rear toe",
        TuneFieldId.FrontCaster => "Front caster angle",
        TuneFieldId.FrontAntiroll => "Front",
        TuneFieldId.RearAntiroll => "Rear",
        TuneFieldId.FrontSprings => "Front spring rate",
        TuneFieldId.RearSprings => "Rear spring rate",
        TuneFieldId.FrontRideHeight => "Front ride height",
        TuneFieldId.RearRideHeight => "Rear ride height",
        TuneFieldId.FrontRebound => "Front rebound",
        TuneFieldId.RearRebound => "Rear rebound",
        TuneFieldId.FrontBump => "Front bump",
        TuneFieldId.RearBump => "Rear bump",
        TuneFieldId.FrontDownforce => "Front downforce",
        TuneFieldId.RearDownforce => "Rear downforce",
        TuneFieldId.BrakeBalance => "Balance",
        TuneFieldId.BrakePressure => "Pressure",
        TuneFieldId.FrontDiffAcceleration => "Front acceleration",
        TuneFieldId.FrontDiffDeceleration => "Front deceleration",
        TuneFieldId.RearDiffAcceleration => "Rear acceleration",
        TuneFieldId.RearDiffDeceleration => "Rear deceleration",
        TuneFieldId.CenterDiffBalance => "Center balance (% rear)",
        _ => id.ToString()
    };

    internal static string Value(TuneField? field) => field?.Status switch
    {
        TuneFieldStatus.Available => $"{field.DisplayText ?? field.DisplayValue?.ToString($"F{field.DisplayDecimals}", CultureInfo.CurrentCulture)}{Unit(field.Unit)}",
        TuneFieldStatus.NotApplicable => "Not applicable",
        TuneFieldStatus.NotAdjustable => "Not adjustable",
        TuneFieldStatus.UnsupportedUnit => "Imperial units required",
        TuneFieldStatus.UnsupportedConversion => "Unsupported conversion",
        TuneFieldStatus.UnresolvedPartLevel => "Part setting not read",
        TuneFieldStatus.UnresolvedDefault => "Default not read",
        _ => "Unavailable"
    };

    private static string Unit(TuneUnit? unit) => unit switch
    {
        TuneUnit.Percent => "%",
        TuneUnit.Psi => " psi",
        TuneUnit.Degrees => "°",
        TuneUnit.PoundsPerInch => " lb/in",
        TuneUnit.Inches => " in",
        TuneUnit.Pounds => " lb",
        _ => ""
    };

    internal static TuneDisplayRow Row(TuneField? field, TuneField? other, TuneFieldId id, bool comparison, double? displayDelta = null, bool rawEqual = false)
    {
        var hasRange = !comparison && field is { Status: TuneFieldStatus.Available, Minimum: { } min, Maximum: { } max, RangeValue: { } value } && max > min;
        var range = hasRange ? $"{field!.Minimum!.Value:0.##} – {field.Maximum!.Value:0.##}" : "";
        // The source range is not necessarily in the player's display units. Only the
        // marker uses it; labels stay qualitative until endpoint conversion is verified.
        if (hasRange) range = "Low                              High";
        var position = hasRange ? Math.Clamp((field!.RangeValue!.Value - field.Minimum!.Value) / (field.Maximum!.Value - field.Minimum!.Value) * 100, 0, 100) : 0;
        var comparable = comparison && displayDelta is not null && field is { Status: TuneFieldStatus.Available } && other is { Status: TuneFieldStatus.Available };
        var changed = comparison && !rawEqual;
        var difference = "";
        if (comparable)
        {
            var delta = displayDelta!.Value;
            var decimals = Math.Max(field!.DisplayDecimals, other!.DisplayDecimals);
            var decimalFormat = decimals == 0 ? "0" : $"0.{new string('0', decimals)}";
            if (Math.Round(delta, decimals) != 0) difference = $"{delta.ToString($"+{decimalFormat};-{decimalFormat};0", CultureInfo.CurrentCulture)}{Unit(field.Unit)}";
            else if (!rawEqual) difference = delta == 0 ? "Raw setting differs" : "Below display precision";
        }
        return new(Label(id), Value(field), range, comparison ? Value(other) : "", difference, changed, hasRange, position);
    }
}
