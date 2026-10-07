namespace Wisp.App.Runs;

public sealed partial class LapReviewViewModel
{
    private enum ScrubTarget { Both, A, B }
    private ScrubTarget _scrubTarget;
    internal event Action? ScrubContextChanging;
    internal event Action? ScrubContextChanged;
    public bool ScrubBoth { get => _scrubTarget == ScrubTarget.Both; set { if (value) SetScrubTarget(ScrubTarget.Both); } }
    public bool ScrubA { get => _scrubTarget == ScrubTarget.A; set { if (value) SetScrubTarget(ScrubTarget.A); } }
    public bool ScrubB { get => _scrubTarget == ScrubTarget.B; set { if (value) SetScrubTarget(ScrubTarget.B); } }
    private bool ScrubsReferenceOnly => IsMapComparison && ScrubB;
    private bool UsesMatchedReferenceCursor => HasMatchedMapComparison && (!IsMapComparison || ScrubBoth);
    public int MaximumScrubCursor => ScrubsReferenceOnly ? Math.Max(0, (Reference?.Points.Length ?? 1) - 1) : MaximumCursor;
    public int ScrubCursor
    {
        get => ScrubsReferenceOnly ? Math.Clamp(_referenceCursor, 0, MaximumScrubCursor) : Cursor;
        set
        {
            if (_disposed) return;
            value = Math.Clamp(value, 0, MaximumScrubCursor);
            if (ScrubsReferenceOnly) { SetReferenceCursor(value); return; }
            var referenceChanged = false;
            if (IsMapComparison && ScrubBoth && !HasMatchedMapComparison)
            {
                var reference = RelativeReferenceCursor(value);
                referenceChanged = _referenceCursor != reference;
                _referenceCursor = reference;
            }
            if (Cursor != value) Cursor = value;
            else if (referenceChanged) NotifyReferenceCursor();
        }
    }
    public string ScrubPosition => !IsMapComparison ? CursorDetails?.Position ?? "" : ScrubA
        ? "A · " + (CursorDetails?.Position ?? "Unavailable") : ScrubB ? "B · " + ReferenceCursorPosition()
        : "A · " + (CursorDetails?.Position ?? "Unavailable") + "   B · " + ReferenceCursorPosition();
    public string ScrubHint => !IsMapComparison ? "" : ScrubA ? "Move A only; B stays at its selected position."
        : ScrubB ? "Move B only; A and the graph stay at their selected position."
        : HasMatchedMapComparison ? "Move both along matching track positions."
        : "Move both by relative lap progress (recorded distance); matched timing is unavailable.";

    private void SetScrubTarget(ScrubTarget target)
    {
        if (_disposed || _scrubTarget == target) return;
        ChangeScrubContext(() =>
        {
            // Keep the visible B sample when leaving the spatially matched mode.
            var visibleReference = MapPlotB.Cursor;
            if (visibleReference >= 0) _referenceCursor = visibleReference;
            _scrubTarget = target;
            if (IsMapComparison && target == ScrubTarget.Both && !HasMatchedMapComparison)
                _referenceCursor = RelativeReferenceCursor(Cursor);
            Changed(nameof(ScrubBoth)); Changed(nameof(ScrubA)); Changed(nameof(ScrubB));
            NotifyReferenceCursor();
        });
    }

    private void ChangeScrubContext(Action change)
    {
        ScrubContextChanging?.Invoke();
        try { change(); }
        finally { NotifyScrubState(); ScrubContextChanged?.Invoke(); }
    }

    private void NotifyScrubState()
    {
        Changed(nameof(MaximumScrubCursor)); Changed(nameof(ScrubCursor));
        Changed(nameof(ScrubPosition)); Changed(nameof(ScrubHint));
    }

    private void SetReferenceCursor(int value)
    {
        if (Reference is not { Points.Length: > 0 } reference) return;
        value = Math.Clamp(value, 0, reference.Points.Length - 1);
        if (_referenceCursor == value) return;
        _referenceCursor = value;
        NotifyReferenceCursor();
    }

    private void NotifyReferenceCursor()
    {
        Changed(nameof(MapPlotB)); Changed(nameof(ComparisonMapPlot)); Changed(nameof(MapBCursorValue));
        NotifyScrubState();
    }

    private int RelativeReferenceCursor(int index)
    {
        if (Lap is not { Points.Length: > 1 } lap || Reference is not { Points.Length: > 1 } reference) return 0;
        index = Math.Clamp(index, 0, lap.Points.Length - 1);
        if (index == 0) return 0;
        if (index == lap.Points.Length - 1) return reference.Points.Length - 1;
        var first = lap.Points[0].DistanceMeters;
        var span = lap.Points[^1].DistanceMeters - first;
        if (!double.IsFinite(span) || span <= 0) return 0;
        var progress = Math.Clamp((lap.Points[index].DistanceMeters - first) / span, 0, 1);
        // Gaps and a stationary finish can share the same cumulative distance.
        if (progress >= 1) return reference.Points.Length - 1;
        var distance = reference.Points[0].DistanceMeters + progress *
            (reference.Points[^1].DistanceMeters - reference.Points[0].DistanceMeters);
        // Recorded cumulative distance is monotonic; avoid scanning either lap while dragging.
        var low = 0; var high = reference.Points.Length - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (reference.Points[middle].DistanceMeters < distance) low = middle + 1;
            else high = middle;
        }
        return low > 0 && distance - reference.Points[low - 1].DistanceMeters < reference.Points[low].DistanceMeters - distance ? low - 1 : low;
    }

    private string ReferenceCursorPosition()
    {
        var data = MapPlotB;
        if (data.Lap is not { } lap || (uint)data.Cursor >= (uint)lap.Points.Length) return "Unavailable";
        var point = lap.Points[data.Cursor];
        var seconds = point.LapSeconds; var distance = point.DistanceMeters;
        if (UsesMatchedReferenceCursor && _comparison?.Points.ElementAtOrDefault(Cursor)?.ReferenceLapSeconds is { } matchedSeconds)
        {
            seconds = matchedSeconds;
            if (data.Cursor + 1 < lap.Points.Length)
            {
                var next = lap.Points[data.Cursor + 1];
                if (next.LapSeconds > point.LapSeconds)
                    distance += (next.DistanceMeters - distance) * Math.Clamp((seconds - point.LapSeconds) / (next.LapSeconds - point.LapSeconds), 0, 1);
            }
        }
        return $"{seconds:0.000} s · {distance:0} m";
    }
}
