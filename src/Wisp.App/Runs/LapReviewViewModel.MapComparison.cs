using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed partial class LapReviewViewModel
{
    private LapReviewColorRange? _sharedMapRange;
    private LapReviewContact[] _referenceMapContacts = [];
    private bool _sharedSpace;
    private int _referenceCursor;
    public bool HasMapComparison => Plot.HasDistinctMapReference;
    private bool HasMatchedMapComparison => Plot.HasDistinctReference && _reverseComparison?.CanCompare == true;
    public bool CanUseSharedSpace => Is3D && HasMapComparison;
    public bool SharedSpace { get => _sharedSpace; set { if (_sharedSpace != value) ChangeScrubContext(() => { Set(ref _sharedSpace, value); RefreshMapMode(); }); } }
    public bool IsMapComparison => CanUseSharedSpace && SharedSpace;
    public string MapATitle => IsMapComparison ? Lap is { } lap ? $"A · {lap.RunName} · {lap.Label}" : "Run A" : MapTitle;
    public string MapBTitle => Reference is { } lap ? $"B · {lap.RunName} · {lap.Label}" : "Run B";
    public string MapComparisonHint => IsMapComparison
        ? HasMatchedMapComparison ? "Both laps use the same color scale. Click a track to focus it; zoom out to see both."
        : "Both laps use the same color scale. Matched sections and time delta are unavailable."
        : HasMapComparison ? "Enable Shared space to view both laps beside each other."
        : _comparisonRun is not null ? "Run B has no second lap with recorded positions. Choose another run or lap."
        : "Choose Run B using Compare to enable Shared space.";
    public string MapBCursorValue
    {
        get
        {
            if (!HasMapComparison) return "";
            var data = ReferencePlot();
            var value = !UsesMatchedReferenceCursor
                ? data.Lap is { } lap && (uint)data.Cursor < (uint)lap.Points.Length
                    ? LapReviewPlot.Value(lap.Points[data.Cursor], data, data.Cursor) : null
                : LapReviewPlot.ReferencePosition(Plot, Cursor) is null ? null :
                Channel.Channel == LapReviewChannel.Delta
                    ? -_comparison?.Points.ElementAtOrDefault(Cursor)?.DeltaSeconds : LapReviewPlot.ReferenceValue(Plot, Cursor);
            return $"{Channel.Label}: {RunPresentation.Number(value, " " + LapReviewPlot.Unit(data))}";
        }
    }
    public LapReviewPlotData MapPlotA => IsMapComparison
        ? Plot with { ShowReferencePath = false, ColorRangeOverride = _sharedMapRange } : Plot;
    public LapReviewPlotData? ComparisonMapPlot => IsMapComparison ? MapPlotB : null;
    public LapReviewPlotData MapPlotB
    {
        get
        {
            var data = ReferencePlot();
            var position = UsesMatchedReferenceCursor ? LapReviewPlot.ReferencePosition(Plot, Cursor) : null;
            return data with
            {
                ShowReferencePath = false,
                ColorRangeOverride = _sharedMapRange,
                Cursor = !UsesMatchedReferenceCursor || position.HasValue ? data.Cursor : -1,
                CursorPositionOverride = position
            };
        }
    }

    private LapReviewPlotData ReferencePlot()
    {
        if (!HasMatchedMapComparison)
            return new(Reference, Lap, null, Channel.Channel, _settings.SpeedUnit,
                Reference is { Points.Length: > 0 } reference ? Math.Clamp(_referenceCursor, 0, reference.Points.Length - 1) : -1,
                -1, -1, SelectedWheel, _settings.TireTemperatureUnit, _settings.TorqueUnit, ShowContacts ? _referenceMapContacts : null);
        int Match(int index) => _comparison?.Points.ElementAtOrDefault(index)?.ReferencePointIndex ?? -1;
        var start = Match(_sectionStart); var end = Match(_sectionEnd);
        var matchedSection = start >= 0 && end >= start;
        return new(Reference, Lap, _reverseComparison, Channel.Channel, _settings.SpeedUnit,
            UsesMatchedReferenceCursor ? Match(Cursor) : Math.Clamp(_referenceCursor, 0, Math.Max(0, (Reference?.Points.Length ?? 1) - 1)),
            matchedSection ? start : -1, matchedSection ? end : -1,
            SelectedWheel, _settings.TireTemperatureUnit, _settings.TorqueUnit, ShowContacts ? _referenceMapContacts : null);
    }

    public void PickReferencePoint(int index)
    {
        if (!HasMapComparison || Reference is not { } reference || (uint)index >= (uint)reference.Points.Length) return;
        if (!UsesMatchedReferenceCursor)
        {
            SetReferenceCursor(index);
            return;
        }
        if (_reverseComparison!.Points.ElementAtOrDefault(index)?.ReferencePointIndex is not { } matched) return;
        Cursor = matched;
    }

    private void RefreshMapMode()
    {
        _legendData = null;
        RefreshMapDetails();
        Changed(nameof(CanUseSharedSpace));
    }

    private LapReviewColorRange SharedMapRange(LapReviewPlotData data)
    {
        var a = LapReviewColorRange.From(data);
        if (!IsMapComparison) return a;
        var b = LapReviewColorRange.From(ReferencePlot());
        if (!a.HasValues) return b;
        if (!b.HasValues) return a;
        return new(Math.Min(a.Minimum, b.Minimum), Math.Max(a.Maximum, b.Maximum), true);
    }
}
