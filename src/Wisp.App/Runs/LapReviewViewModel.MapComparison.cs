using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed partial class LapReviewViewModel
{
    private LapReviewColorRange? _sharedMapRange;
    private LapReviewContact[] _referenceMapContacts = [];
    public bool HasMapComparison => Plot.HasDistinctReference && _reverseComparison?.CanCompare == true;
    public bool IsMapComparison => Is3D && HasMapComparison;
    public string MapATitle => Lap is { } lap ? $"A · {lap.RunName} · {lap.Label}" : "Run A";
    public string MapBTitle => Reference is { } lap ? $"B · {lap.RunName} · {lap.Label}" : "Run B";
    public string MapComparisonHint => HasMapComparison
        ? "Both laps use the same color scale. Click a track to focus it; zoom out to see both."
        : "Use Compare above to choose Run B, or choose another reference lap in Lap settings.";
    public string MapBCursorValue
    {
        get
        {
            if (!HasMapComparison) return "";
            var data = ReferencePlot();
            var value = LapReviewPlot.ReferencePosition(Plot, Cursor) is null ? null :
                Channel.Channel == LapReviewChannel.Delta
                    ? -_comparison?.Points.ElementAtOrDefault(Cursor)?.DeltaSeconds : LapReviewPlot.ReferenceValue(Plot, Cursor);
            return $"{Channel.Label}: {RunPresentation.Number(value, " " + LapReviewPlot.Unit(data))}";
        }
    }
    public LapReviewPlotData MapPlotA => Plot with { ShowReferencePath = !Is3D, ColorRangeOverride = _sharedMapRange };
    public LapReviewPlotData? ComparisonMapPlot => IsMapComparison ? MapPlotB : null;
    public LapReviewPlotData MapPlotB
    {
        get
        {
            var data = ReferencePlot();
            var position = LapReviewPlot.ReferencePosition(Plot, Cursor);
            return data with
            {
                ShowReferencePath = false,
                ColorRangeOverride = _sharedMapRange,
                Cursor = position.HasValue ? data.Cursor : -1,
                CursorPositionOverride = position
            };
        }
    }

    private LapReviewPlotData ReferencePlot()
    {
        int Match(int index) => _comparison?.Points.ElementAtOrDefault(index)?.ReferencePointIndex ?? -1;
        var start = Match(_sectionStart); var end = Match(_sectionEnd);
        var matchedSection = start >= 0 && end >= start;
        return new(Reference, Lap, _reverseComparison, Channel.Channel, _settings.SpeedUnit, Match(Cursor),
            matchedSection ? start : -1, matchedSection ? end : -1,
            SelectedWheel, _settings.TireTemperatureUnit, _settings.TorqueUnit, ShowContacts ? _referenceMapContacts : null);
    }

    public void PickReferencePoint(int index)
    {
        if (!HasMapComparison || _reverseComparison!.Points.ElementAtOrDefault(index)?.ReferencePointIndex is not { } matched) return;
        Cursor = matched;
    }

    private LapReviewColorRange SharedMapRange(LapReviewPlotData data)
    {
        var a = LapReviewColorRange.From(data);
        if (!HasMapComparison) return a;
        var b = LapReviewColorRange.From(ReferencePlot());
        if (!a.HasValues) return b;
        if (!b.HasValues) return a;
        return new(Math.Min(a.Minimum, b.Minimum), Math.Max(a.Maximum, b.Maximum), true);
    }
}
