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
        if (_disposed || !HasMapComparison || Reference is not { } reference || (uint)index >= (uint)reference.Points.Length) return;
        if (UsesRelativeReferenceCursor) { Cursor = RelativePrimaryCursor(index); return; }
        if (!UsesMatchedReferenceCursor)
        {
            SetReferenceCursor(index);
            return;
        }
        var matched = NearestPrimaryIndexForReference(index);
        if (matched >= 0) Cursor = matched;
    }

    public void NavigateReferenceCursor(LapReviewCursorNavigation navigation)
    {
        if (_disposed || !HasMapComparison || Reference is not { Points.Length: > 0 } reference ||
            !Enum.IsDefined(navigation)) return;
        var forward = navigation is LapReviewCursorNavigation.Next or LapReviewCursorNavigation.Start;
        var endpoint = navigation is LapReviewCursorNavigation.Start or LapReviewCursorNavigation.End;
        var current = MapPlotB.Cursor;
        var requested = navigation switch
        {
            LapReviewCursorNavigation.Start => 0,
            LapReviewCursorNavigation.End => reference.Points.Length - 1,
            _ => Math.Clamp(current + (forward ? 1 : -1), 0, reference.Points.Length - 1)
        };
        if (UsesRelativeReferenceCursor) { NavigateRelativeReference(requested, forward, endpoint); return; }
        if (!UsesMatchedReferenceCursor) { SetReferenceCursor(requested); return; }

        // B can have more samples than A. Mapping its next sample to the lower A
        // sample repeatedly would stick, so require progress in the requested direction.
        var candidate = forward ? 0 : MaximumCursor;
        if (!endpoint)
        {
            var nearest = current >= 0 ? NearestPrimaryIndexForReference(requested) : -1;
            candidate = forward ? Math.Max(Cursor + 1, nearest) : Math.Min(Cursor - 1, nearest < 0 ? Cursor - 1 : nearest);
        }
        var previousSeconds = _comparison?.Points.ElementAtOrDefault(Cursor)?.ReferenceLapSeconds;
        var plot = Plot;
        for (var index = candidate; (uint)index <= (uint)MaximumCursor; index += forward ? 1 : -1)
        {
            if (LapReviewPlot.ReferencePosition(plot, index) is null ||
                _comparison?.Points.ElementAtOrDefault(index)?.ReferenceLapSeconds is not { } seconds) continue;
            if (!endpoint && previousSeconds is { } previous && (forward ? seconds <= previous : seconds >= previous)) continue;
            Cursor = index;
            return;
        }
    }

    private int NearestPrimaryIndexForReference(int referenceIndex)
    {
        if (Lap is not { Points.Length: > 0 } lap ||
            _reverseComparison?.Points.ElementAtOrDefault(referenceIndex) is not { ReferencePointIndex: { } lower, ReferenceLapSeconds: { } seconds } ||
            (uint)lower >= (uint)lap.Points.Length || !double.IsFinite(seconds)) return -1;
        var upper = Math.Min(lower + 1, lap.Points.Length - 1);
        var nearest = Math.Abs(lap.Points[upper].LapSeconds - seconds) < Math.Abs(seconds - lap.Points[lower].LapSeconds) ? upper : lower;
        var alternate = nearest == lower ? upper : lower;
        // Keep A on an actual recorded sample and retain only genuine forward matches.
        if (LapReviewPlot.ReferencePosition(Plot, nearest) is not null) return nearest;
        return LapReviewPlot.ReferencePosition(Plot, alternate) is not null ? alternate : -1;
    }

    private void RefreshMapMode()
    {
        SynchronizeRelativeReferenceCursor(Cursor);
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
