using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed partial class RunsViewModel
{
    public LapReviewViewModel LapReview { get; private set; } = null!;
    private bool _lapCursorSync;
    private void InitializeLapReview()
    {
        LapReview = new(_settings, _service.Store, () => PreferencesChanged?.Invoke(this, EventArgs.Empty));
        LapReview.CursorMoved += seconds =>
        {
            if (_lapCursorSync) return;
            _lapCursorSync = true;
            try { CursorSeconds = seconds - _offsetA; }
            finally { _lapCursorSync = false; }
        };
        LapReview.SectionChosen += (start, end) =>
        {
            if (RecordingActive) return;
            var absoluteCursor = CursorSeconds + _offsetA;
            _sameSpeed = false; _matchedA = _matchedB = null; _offsetA = _offsetB = 0;
            OnChanged(nameof(SameSpeed));
            _interval = new(start, end);
            SelectionStart = start; SelectionEnd = end;
            ViewStart = SelectionStart; ViewEnd = Math.Max(SelectionStart + .01, SelectionEnd);
            CursorSeconds = Math.Clamp(absoluteCursor, SelectionStart, SelectionEnd);
            ShowGraphs(); RequestAnalysis(); FocusChartsRequested?.Invoke(this, EventArgs.Empty);
        };
    }
    private void SynchronizeLapCursor()
    {
        if (_lapCursorSync || LapReview?.Lap is not { Points.Length: > 0 } lap) return;
        var seconds = CursorSeconds + _offsetA;
        if (seconds < lap.Points[0].RunSeconds || seconds > lap.Points[^1].RunSeconds) return;
        var lo = 0; var hi = lap.Points.Length - 1;
        while (lo < hi) { var mid = (lo + hi) / 2; if (lap.Points[mid].RunSeconds < seconds) lo = mid + 1; else hi = mid; }
        if (lo > 0 && seconds - lap.Points[lo - 1].RunSeconds < lap.Points[lo].RunSeconds - seconds) lo--;
        _lapCursorSync = true;
        try { LapReview.Cursor = lo; }
        finally { _lapCursorSync = false; }
    }
    public void NotifyLapSaved(RecordedRun run) => OnUi(() =>
    {
        LapReview.CaptureStatus = $"Saved {run.Name}. Open it from the run library to review it.";
        if (!RecordingActive && !_metadataClosing) { _pendingLibraryRefresh = false; _ = LoadLibraryAsync(); }
        else _pendingLibraryRefresh = true;
    });
    public void UpdateLapCaptureStatus(string status) => OnUi(() => LapReview.CaptureStatus = status);
}
