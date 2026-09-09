using System.Collections.ObjectModel;
using System.Windows.Input;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed record RunTimeOption(int Seconds, string Label);
public sealed record RunGraphViewOption(RunPlotMode Mode, string Label);
public sealed record RunGearOption(TransmissionGear? Gear, string Label);
public sealed record RunMarkerItem(string Label, string Time, bool Comparison, double Seconds, ICommand JumpCommand);

public sealed partial class RunsViewModel
{
    public const string ImageExportSavedMessage = "Report image saved. Nothing was uploaded.";
    private readonly TimeProvider _timeProvider;
    private long? _countdownStartedAt;
    private int _armedCountdownSeconds;
    private TimeSpan? _activeStopAfter;
    private string? _recordingNotice;
    private string _markerHotkeyStatus;
    private Func<bool, OverlayHotkeyChord, string?>? _applyMarkerHotkey;
    private long _chartRevision;
    private RunGraphViewOption _graphView = new(RunPlotMode.TimeSeries, "Over time");
    private RunGearOption _gearFilter = new(null, "All forward gears");
    private bool _fullThrottleOnly = true;
    private bool _preparingCharts;
    private RunFinding[] _imageFindings = [];
    private string _selectedPointContext = "";
    private RunPlotMarker[] _plotMarkers = [];

    public RunTimeOption[] CountdownOptions { get; } = [new(0, "No countdown"), new(3, "3 seconds"), new(5, "5 seconds"), new(10, "10 seconds")];
    public RunTimeOption[] StopAfterOptions { get; } = [new(0, "Until I stop · 10 min max"), new(30, "30 seconds"), new(60, "1 minute"), new(120, "2 minutes"), new(300, "5 minutes"), new(600, "10 minutes")];
    public int CountdownSeconds
    {
        get => _settings.RecordingCountdownSeconds;
        set
        {
            if (!CanEditRecordingOptions || value == CountdownSeconds || !CountdownOptions.Any(option => option.Seconds == value)) return;
            _settings.RecordingCountdownSeconds = value; OnChanged(); PreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public int StopAfterSeconds
    {
        get => _settings.RecordingStopAfterSeconds;
        set
        {
            if (!CanEditRecordingOptions || value == StopAfterSeconds || !StopAfterOptions.Any(option => option.Seconds == value)) return;
            _settings.RecordingStopAfterSeconds = value; OnChanged(); PreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public bool IsCountingDown => _countdownStartedAt is not null;
    private bool RecordingActive => IsCountingDown || _service.IsRecording || _service.IsPreparing;
    public bool CanEditRecordingOptions => !RecordingActive;
    public int CountdownRemainingSeconds => _countdownStartedAt is { } started
        ? Math.Max(0, (int)Math.Ceiling(_armedCountdownSeconds - _timeProvider.GetElapsedTime(started).TotalSeconds)) : 0;
    public bool CanMarkMoment => _service.IsRecording;
    public string MarkerStatus => _service.MarkerStatus;
    public ICommand MarkMomentCommand { get; }
    public bool MarkMoment()
    {
        if (_disposed || !CanMarkMoment) return false;
        var marked = _service.MarkMoment(); OnChanged(nameof(MarkerStatus)); return marked;
    }
    public void CancelCountdown(string reason = "Countdown canceled. Nothing was recorded.")
    {
        if (!IsCountingDown) return;
        _countdownStartedAt = null; _recordingNotice = reason;
        NotifyRecordingOptions(); OnChanged(nameof(RecordingStatus)); OnChanged(nameof(RecordButtonText));
        OnChanged(nameof(CanToggleRecording)); OnChanged(nameof(CanManageLibrary)); OnChanged(nameof(CanManageRun)); OnChanged(nameof(LibraryHelp)); RaiseCommands();
    }
    private void RefreshCountdown()
    {
        if (!IsCountingDown) return;
        if (!_service.CanStart)
        {
            CancelCountdown("Countdown canceled because live driving telemetry stopped. Return to free roam and try again.");
            return;
        }
        if (CountdownRemainingSeconds > 0) return;
        _countdownStartedAt = null;
        try { StartNow(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Fail("The recording could not start after the countdown. Check live telemetry and local storage, then try again."); }
    }
    private void StartNow()
    {
        _recordingNotice = null;
        BeforeStart?.Invoke();
        if (!_service.Start(new RunRecordingOptions(_activeStopAfter))) Error = _service.Error ?? _service.Status;
    }
    private void NotifyRecordingOptions()
    {
        foreach (var property in new[] { nameof(IsCountingDown), nameof(CountdownRemainingSeconds), nameof(CanEditRecordingOptions), nameof(CanMarkMoment), nameof(MarkerStatus), nameof(CanExportImage) }) OnChanged(property);
    }
    public bool MarkerHotkeyEnabled { get => _settings.MarkerShortcutEnabled; set => ConfigureMarkerHotkey(value, SavedMarkerChord); }
    public string MarkerHotkeyText => SavedMarkerChord.ToString();
    public string MarkerHotkeyStatus { get => _markerHotkeyStatus; private set => Set(ref _markerHotkeyStatus, value); }
    private OverlayHotkeyChord SavedMarkerChord => new(_settings.MarkerShortcutModifiers, _settings.MarkerShortcutKey);
    public void SetMarkerHotkeyHandler(Func<bool, OverlayHotkeyChord, string?> apply) => _applyMarkerHotkey = apply;
    public bool ConfigureMarkerHotkey(bool enabled, OverlayHotkeyChord chord)
    {
        var error = _applyMarkerHotkey?.Invoke(enabled, chord) ?? (_applyMarkerHotkey is null ? "Available after Wisp starts." : null);
        MarkerHotkeyStatus = error ?? (enabled ? "Ready while recording" : "Off");
        OnChanged(nameof(MarkerHotkeyEnabled)); OnChanged(nameof(MarkerHotkeyText));
        return error is null;
    }
    public void RefreshMarkerHotkey() => ConfigureMarkerHotkey(MarkerHotkeyEnabled, SavedMarkerChord);
    public void SuspendMarkerHotkeyStatus() => MarkerHotkeyStatus = MarkerHotkeyEnabled ? "Paused" : "Off";

    public ObservableCollection<RunAlternativePlotPanel> AlternativeCharts { get; } = [];
    public bool IsPreparingCharts => _preparingCharts;
    public string SelectedPointContext { get => _selectedPointContext; private set { if (Set(ref _selectedPointContext, value)) OnChanged(nameof(HasSelectedPoint)); } }
    public bool HasSelectedPoint => SelectedPointContext.Length > 0;
    public ObservableCollection<RunMarkerItem> Markers { get; } = [];
    public bool HasMarkers => Markers.Count > 0;
    public RunPlotMarker[] PlotMarkers { get => _plotMarkers; private set => Set(ref _plotMarkers, value); }
    public RunGraphViewOption[] AvailableGraphViews => ChartGroup switch
    {
        RunChartGroup.Engine => [new(RunPlotMode.TimeSeries, "Over time"), new(RunPlotMode.PowerByRpm, "Power / torque vs RPM")],
        RunChartGroup.Handling => [new(RunPlotMode.TimeSeries, "Over time"), new(RunPlotMode.GForce, "G-force circle")],
        RunChartGroup.Tires => [new(RunPlotMode.TimeSeries, "Over time"), new(RunPlotMode.TireChange, "Temperature change")],
        _ => [new(RunPlotMode.TimeSeries, "Over time")]
    };
    public RunGraphViewOption GraphView
    {
        get => _graphView;
        set
        {
            if (RecordingActive || value is null || !AvailableGraphViews.Any(option => option.Mode == value.Mode) || !Set(ref _graphView, value)) return;
            NotifyGraphMode(); RequestCharts();
        }
    }
    public bool IsTimeGraph => GraphView.Mode == RunPlotMode.TimeSeries;
    public bool IsAlternativeGraph => !IsTimeGraph;
    public bool IsRpmGraph => GraphView.Mode == RunPlotMode.PowerByRpm;
    public bool HasGraphViewChoice => AvailableGraphViews.Length > 1;
    public bool FullThrottleOnly { get => _fullThrottleOnly; set { if (!RecordingActive && Set(ref _fullThrottleOnly, value)) RequestCharts(); } }
    public RunGearOption[] GearOptions { get; } = [new(null, "All forward gears"), .. Enumerable.Range(1, 10).Select(value => new RunGearOption((TransmissionGear)value, $"Gear {value}"))];
    public RunGearOption GearFilter { get => _gearFilter; set { if (!RecordingActive && value is not null && Set(ref _gearFilter, value)) RequestCharts(); } }
    public string GraphHelp => GraphView.Mode switch
    {
        RunPlotMode.PowerByRpm => "Recorded engine power and torque by RPM. Filters apply to these plots only; report statistics keep the selected time range. Choose a point to inspect its moment over time.",
        RunPlotMode.GForce => "Each point is a recorded braking, acceleration, or cornering load. Choose a point to inspect its moment over time.",
        RunPlotMode.TireChange => "Temperature change compares the first and last valid readings in each run's selected range. Each bar shows one axle; missing temperatures remain unavailable.",
        _ => "Move over a graph for shared readouts. Drag to select a section; shaded breaks mark missing data. Arrow keys move the cursor."
    };
    private void NotifyGraphMode()
    {
        if (Status == ImageExportSavedMessage) Status = "Report ready.";
        SelectedPointContext = "";
        if (!RecordingActive) { Charts.Clear(); AlternativeCharts.Clear(); }
        foreach (var property in new[] { nameof(AvailableGraphViews), nameof(GraphView), nameof(IsTimeGraph), nameof(IsAlternativeGraph), nameof(IsRpmGraph), nameof(HasGraphViewChoice), nameof(GraphHelp) }) OnChanged(property);
        OnChanged(nameof(RunALabel)); OnChanged(nameof(RunBLabel));
        OnChanged(nameof(CanExportImage));
        OnChanged(nameof(SelectedGraph));
    }
    private RunInterval? ReportBounds(bool comparison)
    {
        var bounds = comparison ? _matchedB : _matchedA;
        var offset = comparison ? _offsetB : _offsetA;
        if (_interval is not { } selected) return bounds;
        return new(selected.StartSeconds + offset, Math.Min(selected.EndSeconds + offset, bounds?.EndSeconds ?? double.MaxValue));
    }
    private void RefreshMarkers()
    {
        Markers.Clear();
        var plotMarkers = new List<RunPlotMarker>();
        Add(_runA, false, _offsetA, _matchedA); Add(_runB, true, _offsetB, _matchedB);
        PlotMarkers = plotMarkers.ToArray();
        OnChanged(nameof(HasMarkers));
        void Add(RecordedRun? run, bool comparison, double offset, RunInterval? bounds)
        {
            if (run is null) return;
            foreach (var marker in run.Markers.Take(128))
            {
                if (!double.IsFinite(marker.ElapsedSeconds) || marker.ElapsedSeconds < 0 ||
                    (bounds is { } range && (marker.ElapsedSeconds < range.StartSeconds || marker.ElapsedSeconds > range.EndSeconds))) continue;
                var seconds = marker.ElapsedSeconds - offset;
                plotMarkers.Add(new(seconds, marker.Label, comparison));
                Markers.Add(new($"{(comparison ? "B" : "A")} · {marker.Label}", RunPresentation.Time(seconds), comparison, seconds,
                    new RunUiCommand(() => { JumpToMoment(seconds); return Task.CompletedTask; }, () => !IsBusy && !RecordingActive, () => Fail("This marker could not be opened."))));
            }
        }
    }
    public void SelectAlternativePoint(RunAlternativeSelection point)
    {
        if (RecordingActive) return;
        var run = point.Comparison ? _runB : _runA;
        if (run is null || point.SampleIndex < 0 || point.SampleIndex >= run.Samples.Length) return;
        var sample = run.Samples[point.SampleIndex];
        var bounds = ReportBounds(point.Comparison);
        if (!sample.IsDriving || !sample.State.IsRaceOn || Math.Abs(sample.ElapsedSeconds - point.SourceSeconds) > .000001 ||
            (bounds is { } interval && (sample.ElapsedSeconds < interval.StartSeconds || sample.ElapsedSeconds > interval.EndSeconds))) return;
        JumpToMoment(point.SourceSeconds - (point.Comparison ? _offsetB : _offsetA));
        var gear = sample.State.Gear switch
        {
            TransmissionGear.Reverse => "reverse",
            TransmissionGear.Neutral => "neutral",
            >= TransmissionGear.First and <= TransmissionGear.Tenth => $"gear {(int)sample.State.Gear}",
            _ => "gear unavailable"
        };
        SelectedPointContext = $"Selected point · {(point.Comparison ? "B" : "A")} · {sample.ElapsedSeconds:0.000}s in that run · {sample.State.EngineRpm:N0} RPM · {gear}";
    }
    private void JumpToMoment(double seconds)
    {
        if (_runA is null || !double.IsFinite(seconds)) return;
        SelectedPointContext = "";
        GraphView = AvailableGraphViews[0];
        var end = Math.Max((_matchedA?.EndSeconds ?? _runA.Samples.LastOrDefault()?.ElapsedSeconds ?? 0) - _offsetA,
            (_matchedB?.EndSeconds ?? _runB?.Samples.LastOrDefault()?.ElapsedSeconds ?? 0) - _offsetB);
        CursorSeconds = Math.Clamp(seconds, 0, Math.Max(0, end));
        ViewStart = Math.Max(0, CursorSeconds - 2); ViewEnd = Math.Max(ViewStart + .1, Math.Min(end, CursorSeconds + 2));
        FocusChartsRequested?.Invoke(this, EventArgs.Empty);
    }
    public bool CanExportImage => CanManageRun && !_preparingCharts && Charts.Count + AlternativeCharts.Count > 0;
    public async Task ExportImageAsync(string destination)
    {
        if (!CanExportImage) return;
        Error = "";
        Status = "Saving report image…";
        var context = GraphView.Label + (IsTimeGraph ? $" · displayed {RunPresentation.Time(ViewStart)}–{RunPresentation.Time(ViewEnd)}." : ".") + " " + QualityNote + " " + ComparisonNote;
        if (IsRpmGraph) context += $" · {GearFilter.Label} · {(FullThrottleOnly ? "Full throttle only" : "All throttle positions")}. Plot filters do not change report statistics.";
        static string Label(RecordedRun run) => run.Name + (string.IsNullOrWhiteSpace(run.Tune) ? "" : " · " + run.Tune);
        var snapshot = new RunImageSnapshot(Label(_runA!), _runB is null ? null : Label(_runB), "Report statistics: " + IntervalLabel, context,
            _imageFindings.ToArray(), Metrics.ToArray(), Charts.ToArray(), AlternativeCharts.ToArray(), ViewStart, ViewEnd);
        IsBusy = true;
        try
        {
            var bitmap = RunImageExporter.Render(snapshot);
            await StoreOperationAsync(() => RunImageExporter.WriteAsync(bitmap, destination));
            Status = ImageExportSavedMessage;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Fail("The report image could not be saved. Choose a new filename and try again."); }
        finally { IsBusy = false; OnChanged(nameof(CanExportImage)); }
    }
}
