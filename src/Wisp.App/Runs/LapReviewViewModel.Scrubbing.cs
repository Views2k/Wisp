using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public enum LapReviewCursorNavigation { Previous, Next, Start, End }

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
    private bool UsesRelativeReferenceCursor => IsMapComparison && ScrubBoth && !HasMatchedMapComparison;
    public int MaximumScrubCursor => ScrubsReferenceOnly ? Math.Max(0, (Reference?.Points.Length ?? 1) - 1) : MaximumCursor;
    public int ScrubCursor
    {
        get => ScrubsReferenceOnly ? Math.Clamp(_referenceCursor, 0, MaximumScrubCursor) : Cursor;
        set
        {
            if (_disposed) return;
            value = Math.Clamp(value, 0, MaximumScrubCursor);
            if (ScrubsReferenceOnly) { SetReferenceCursor(value); return; }
            Cursor = value;
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

    private bool SynchronizeRelativeReferenceCursor(int primaryIndex)
    {
        if (!UsesRelativeReferenceCursor) return false;
        var referenceIndex = RelativeReferenceCursor(primaryIndex);
        if (_referenceCursor == referenceIndex) return false;
        _referenceCursor = referenceIndex;
        return true;
    }

    private int RelativeReferenceCursor(int index) => RelativeCursor(Lap, Reference, index);
    private int RelativePrimaryCursor(int index) => RelativeCursor(Reference, Lap, index);

    private static int RelativeCursor(LapReviewLap? source, LapReviewLap? target, int index)
    {
        if (source is not { Points.Length: > 1 } || target is not { Points.Length: > 1 }) return 0;
        index = Math.Clamp(index, 0, source.Points.Length - 1);
        if (index == 0) return 0;
        if (index == source.Points.Length - 1) return target.Points.Length - 1;
        var first = source.Points[0].DistanceMeters;
        var span = source.Points[^1].DistanceMeters - first;
        var targetFirst = target.Points[0].DistanceMeters;
        var targetSpan = target.Points[^1].DistanceMeters - targetFirst;
        if (!double.IsFinite(first) || !double.IsFinite(span) || span <= 0 ||
            !double.IsFinite(source.Points[index].DistanceMeters) || !double.IsFinite(targetFirst) ||
            !double.IsFinite(targetSpan) || targetSpan < 0) return 0;
        var progress = Math.Clamp((source.Points[index].DistanceMeters - first) / span, 0, 1);
        // Gaps and a stationary finish can share the same cumulative distance.
        if (progress >= 1) return target.Points.Length - 1;
        var distance = targetFirst + progress * targetSpan;
        // Recorded cumulative distance is monotonic; avoid scanning either lap while dragging.
        var low = 0; var high = target.Points.Length - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (target.Points[middle].DistanceMeters < distance) low = middle + 1;
            else high = middle;
        }
        return low > 0 && distance - target.Points[low - 1].DistanceMeters < target.Points[low].DistanceMeters - distance ? low - 1 : low;
    }

    private void NavigateRelativeReference(int requested, bool forward, bool endpoint)
    {
        if (endpoint) { Cursor = forward ? 0 : MaximumCursor; return; }
        var currentReference = RelativeReferenceCursor(Cursor);
        var nearest = RelativePrimaryCursor(requested);
        if (forward ? nearest > Cursor && RelativeReferenceCursor(nearest) > currentReference
            : nearest < Cursor && RelativeReferenceCursor(nearest) < currentReference)
        { Cursor = nearest; return; }

        // Unequal densities or stationary spans can round back to the same A
        // sample. Find the next representable shared progress without a full scan.
        var low = forward ? Cursor + 1 : 0;
        var high = forward ? MaximumCursor : Cursor - 1;
        var selected = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var value = RelativeReferenceCursor(middle);
            var advances = forward ? value > currentReference : value < currentReference;
            if (advances) selected = middle;
            if (forward ? advances : !advances) high = middle - 1;
            else low = middle + 1;
        }
        if (selected >= 0) Cursor = selected;
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
