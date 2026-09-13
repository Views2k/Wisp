using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed record SavedRunItem(RunSummary Summary)
{
    public Guid Id => Summary.Id;
    public string Name => Summary.Name;
    public string Detail => $"{Summary.StartedAtUtc.ToLocalTime():MMM d, h:mm tt} · {RunPresentation.Time(Summary.DurationSeconds)}";
    public string Tune => string.IsNullOrWhiteSpace(Summary.Tune) ? "No tune label" : Summary.Tune;
    public string Quality => Summary.IsIncomplete ? "Partial recording" : "";
}

public sealed class RunFindingItem(string title, string detail, ICommand showCommand, bool canShow) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Title { get; private set; } = title;
    public string Detail { get; private set; } = detail;
    public ICommand ShowCommand { get; private set; } = showCommand;
    public bool CanShow { get; private set; } = canShow;

    internal void Update(RunFindingItem next)
    {
        Title = next.Title; Detail = next.Detail; ShowCommand = next.ShowCommand; CanShow = next.CanShow;
        foreach (var name in new[] { nameof(Title), nameof(Detail), nameof(ShowCommand), nameof(CanShow) })
            PropertyChanged?.Invoke(this, new(name));
    }
}

public sealed partial class RunsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RunRecordingService _service;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly List<RunUiCommand> _commands = [];
    private readonly Dictionary<Guid, RecordedRun> _reviewRuns = [];
    private SavedRunItem? _selectedRun, _comparisonChoice;
    private SavedRunItem? _pendingSelection;
    private RecordedRun? _pendingSavedRun;
    private RecordedRun? _runA, _runB;
    private RunReport? _reportA, _reportB;
    private RunInterval? _interval;
    private RunInterval? _matchedA, _matchedB;
    private long _selectionRevision, _analysisRevision;
    private int _storageOperations;
    private bool _loadingSelection;
    private bool _loadingComparison, _comparisonNeedsAnalysis;
    private bool _pendingLibraryRefresh;
    private bool _initialized, _initializing, _busy, _pageVisible, _disposed, _sameSpeed, _pendingAnalysis, _wasRecording;
    private string _status = "Record a drive, then explore what changed.", _error = "", _name = "", _tune = "", _notes = "";
    private string _fromSpeed = "20", _toSpeed = "60", _selectionFrom = "0", _selectionTo = "0";
    private string _quality = "", _comparisonNote = "", _hotkeyStatus;
    private double _cursor, _viewStart, _viewEnd = 1, _offsetA, _offsetB, _selectionStart = double.NaN, _selectionEnd = double.NaN;
    private RunChartGroup _chartGroup;
    private SpeedUnit _lastSpeedUnit;
    private TorqueUnit _lastTorqueUnit;
    private TireTemperatureUnit _lastTemperatureUnit;
    private BoostPressureUnit _lastBoostUnit;
    private Func<bool, OverlayHotkeyChord, string?>? _applyHotkey;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? PreferencesChanged;
    public event EventHandler? FocusChartsRequested;
    public event EventHandler? ReportOpened;
    public Action? BeforeStart { get; set; }
    internal bool ShortcutCaptureActive { get; set; }
    public ObservableCollection<SavedRunItem> Library { get; } = [];
    public ObservableCollection<RunFindingItem> Findings { get; } = [];
    public ObservableCollection<RunMetric> Metrics { get; } = [];
    public ObservableCollection<RunPlotPanel> Charts { get; } = [];
    public RunPurpose[] Purposes { get; } = Enum.GetValues<RunPurpose>();
    public RunChartGroup[] ChartGroups { get; } = Enum.GetValues<RunChartGroup>();
    public ICommand ToggleRecordingCommand { get; }
    public ICommand SaveDetailsCommand { get; }
    public ICommand CompareCommand { get; }
    public ICommand ComparePreviousCommand { get; }
    public ICommand RemoveComparisonCommand { get; }
    public ICommand ApplyIntervalCommand { get; }
    public ICommand ZoomSelectionCommand { get; }
    public ICommand ResetViewCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand ApplySpeedRangeCommand { get; }
    public ICommand RefreshLibraryCommand { get; }
    public RunsViewModel(RunRecordingService service, AppSettings settings, Dispatcher dispatcher, TimeProvider? timeProvider = null)
    {
        _service = service; _settings = settings; _dispatcher = dispatcher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _markerHotkeyStatus = settings.MarkerShortcutEnabled ? "Not active" : "Off";
        _lastSpeedUnit = settings.SpeedUnit; _lastTorqueUnit = settings.TorqueUnit;
        _lastTemperatureUnit = settings.TireTemperatureUnit; _lastBoostUnit = settings.BoostPressureUnit;
        _hotkeyStatus = settings.RecordingShortcutEnabled ? "Not active" : "Off";
        ToggleRecordingCommand = Command(ToggleRecordingAsync, () => CanToggleRecording);
        SaveDetailsCommand = Command(SaveDetailsAsync, () => CanManageRun);
        CompareCommand = Command(CompareChosenAsync, () => CanManageRun && ComparisonChoice is not null && ComparisonChoice.Id != _runA?.Id);
        ComparePreviousCommand = Command(ComparePreviousAsync, () => CanManageRun && PreviousRun() is not null);
        RemoveComparisonCommand = Command(() => { _runB = null; _sameSpeed = false; _matchedA = _matchedB = _interval = null; _offsetA = _offsetB = 0; SelectionStart = SelectionEnd = double.NaN; ResetView(); OnChanged(nameof(SameSpeed)); NotifyRun(); RequestAnalysis(); return Task.CompletedTask; }, () => HasComparison && !IsBusy && !RecordingActive);
        ApplyIntervalCommand = Command(() => { ApplyTypedInterval(); return Task.CompletedTask; }, () => HasRun && !IsBusy && !RecordingActive);
        ZoomSelectionCommand = Command(() => { if (HasSelection) { ViewStart = SelectionStart; ViewEnd = SelectionEnd; } return Task.CompletedTask; }, () => HasSelection);
        ResetViewCommand = Command(() => { ResetView(); return Task.CompletedTask; }, () => HasRun);
        ClearSelectionCommand = Command(() => { _interval = null; SelectionStart = SelectionEnd = double.NaN; RequestAnalysis(); return Task.CompletedTask; }, () => HasSelection && !IsBusy && !RecordingActive);
        ApplySpeedRangeCommand = Command(() => { _sameSpeed = true; _interval = null; SelectionStart = SelectionEnd = double.NaN; OnChanged(nameof(SameSpeed)); return AnalyzeAsync(); }, () => HasComparison && !IsBusy && !RecordingActive);
        RefreshLibraryCommand = Command(LoadLibraryAsync, () => CanManageLibrary);
        MarkMomentCommand = Command(() => { MarkMoment(); return Task.CompletedTask; }, () => CanMarkMoment);
        InitializeWorkspace();
        InitializeMetadata();
        InitializeLibrarySearch();
        _service.StateChanged += ServiceStateChanged;
        _service.RunSaved += ServiceRunSaved;
        if (settings.SpeedUnit != SpeedUnit.MilesPerHour) { _fromSpeed = "30"; _toSpeed = "100"; }
    }

    public SavedRunItem? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (RecordingActive || _metadataClosing || value is null || _disposed) return;
            var sameRun = _selectedRun?.Id == value.Id;
            if (!Set(ref _selectedRun, value)) return;
            if (!sameRun) { _ = FlushMetadataAsync(); _ = LoadSelectionAsync(value); }
            RefreshLibrarySearch();
        }
    }
    public SavedRunItem? ComparisonChoice { get => _comparisonChoice; set { if (RecordingActive) return; Set(ref _comparisonChoice, value); RaiseCommands(); } }
    public string RecordButtonText => IsCountingDown ? "Cancel countdown" : _service.IsRecording ? "Stop recording" : _service.IsPreparing ? "Saving run…" : "Record run";
    public bool CanToggleRecording => (!_metadataClosing || _service.IsRecording) && (IsCountingDown || (!_service.IsPreparing && (_service.IsRecording || (_storageOperations == 0 && _service.CanStart))));
    public bool IsRecording => _service.IsRecording;
    public string RecordingStatus => IsCountingDown ? $"Recording starts in {CountdownRemainingSeconds}… Return to Forza; the shortcut can cancel."
        : _service.IsRecording ? $"Recording · {RunPresentation.Time(_service.Elapsed.TotalSeconds)}" + (_activeStopAfter is { } stop ? $" · stops at {RunPresentation.Time(stop.TotalSeconds)}" : "")
        : _recordingNotice is { } notice ? notice
        : _storageOperations > 0 ? "Finishing library work before the next recording."
        : !_service.CanStart && _service.Status == "Ready to record" && _service.Error is null
            ? _service.Store.IsFull ? "The run library is full. Remove a saved run before recording another."
                : "Open Forza and return to free roam with live telemetry to record a run."
            : _service.Status;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); OnChanged(nameof(CanManageRun)); OnChanged(nameof(CanManageLibrary)); OnChanged(nameof(CanSelectRun)); OnChanged(nameof(CanManageAllRuns)); OnChanged(nameof(CanExportImage)); RaiseCommands(); } }
    public bool CanManageRun => HasRun && CanManageLibrary;
    public bool CanManageLibrary => !_metadataClosing && !IsBusy && _storageOperations == 0 && !RecordingActive;
    public bool CanSelectRun => !_metadataClosing && !_disposed && !RecordingActive && (_loadingSelection || CanManageLibrary);
    public bool CanManageAllRuns => CanManageLibrary && Library.Count > 0;
    public string LibraryHelp => RecordingActive ? "Library changes resume after the recording is saved or canceled." : "Run files stay on this PC.";
    public bool HasRun => _runA is not null;
    public bool HasComparison => _runB is not null;
    public bool HasSelection => double.IsFinite(SelectionStart) && double.IsFinite(SelectionEnd);
    public bool IsLibraryEmpty => Library.Count == 0;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error { get => _error; private set { Set(ref _error, value); OnChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string Name { get => _name; set { if (!RecordingActive && (!_metadataClosing || _restoringMetadata) && Set(ref _name, value ?? "")) MetadataChanged(); } }
    public string Tune { get => _tune; set { if (!RecordingActive && (!_metadataClosing || _restoringMetadata) && Set(ref _tune, value ?? "")) MetadataChanged(); } }
    public string Notes { get => _notes; set { if (!RecordingActive && (!_metadataClosing || _restoringMetadata) && Set(ref _notes, value ?? "")) MetadataChanged(); } }
    public string RunALabel => _runA is null ? "" : "A · " + _runA.Name;
    public string RunBLabel => _runB is null ? "" : "B · " + _runB.Name;
    public string RunDescription => _runA is null ? "" : $"{_runA.StartedAtUtc.ToLocalTime():f} · {RunPresentation.Time(_runA.Samples.LastOrDefault()?.ElapsedSeconds ?? 0)}";
    public string CarIdentifier => _runA is null ? "" : $"Game car identifier: {_runA.Samples.FirstOrDefault()?.State.CarOrdinal}";
    public string QualityNote { get => _quality; private set => Set(ref _quality, value); }
    public string ComparisonNote { get => _comparisonNote; private set => Set(ref _comparisonNote, value); }
    public string SpeedUnitText => RunPresentation.SpeedLabel(_settings.SpeedUnit);
    public string IntervalLabel => HasSelection ? $"Selected: {RunPresentation.Time(SelectionStart)}–{RunPresentation.Time(SelectionEnd)}" : _matchedA is not null ? $"Matched {FromSpeed}–{ToSpeed} {SpeedUnitText}" : "Whole run";
    public RunPurpose Purpose
    {
        get => _settings.RunPurpose;
        set { if (RecordingActive || _settings.RunPurpose == value) return; _settings.RunPurpose = value; OnChanged(); PreferencesChanged?.Invoke(this, EventArgs.Empty); RequestAnalysis(); }
    }
    public RunChartGroup ChartGroup { get => _chartGroup; set { if (!RecordingActive && Set(ref _chartGroup, value)) { _graphView = AvailableGraphViews[0]; NotifyGraphMode(); RequestCharts(); } } }
    public bool SameSpeed { get => _sameSpeed; set { if (!RecordingActive && Set(ref _sameSpeed, value)) { _interval = null; SelectionStart = SelectionEnd = double.NaN; RequestAnalysis(); } } }
    public string FromSpeed { get => _fromSpeed; set { if (!RecordingActive) Set(ref _fromSpeed, value); } }
    public string ToSpeed { get => _toSpeed; set { if (!RecordingActive) Set(ref _toSpeed, value); } }
    public string SelectionFrom { get => _selectionFrom; set { if (!RecordingActive) Set(ref _selectionFrom, value); } }
    public string SelectionTo { get => _selectionTo; set { if (!RecordingActive) Set(ref _selectionTo, value); } }
    public double CursorSeconds { get => _cursor; set { if (Set(ref _cursor, value)) SelectedPointContext = ""; OnChanged(nameof(CursorText)); OnChanged(nameof(CursorVehicleContext)); } }
    public string CursorText => "Cursor · " + RunPresentation.Time(CursorSeconds);
    public string CursorVehicleContext => _runA is null ? "" : RunCursor.Describe("A", _runA, CursorSeconds + _offsetA, _matchedA) +
        (_runB is null ? "" : "   |   " + RunCursor.Describe("B", _runB, CursorSeconds + _offsetB, _matchedB));
    public double ViewStart { get => _viewStart; private set => Set(ref _viewStart, value); }
    public double ViewEnd { get => _viewEnd; private set => Set(ref _viewEnd, value); }
    public double SelectionStart { get => _selectionStart; private set { Set(ref _selectionStart, value); OnChanged(nameof(HasSelection)); OnChanged(nameof(IntervalLabel)); RaiseCommands(); } }
    public double SelectionEnd { get => _selectionEnd; private set { Set(ref _selectionEnd, value); OnChanged(nameof(HasSelection)); OnChanged(nameof(IntervalLabel)); RaiseCommands(); } }
    public bool HotkeyEnabled { get => _settings.RecordingShortcutEnabled; set => ConfigureHotkey(value, SavedChord); }
    public string HotkeyText => SavedChord.ToString();
    public string HotkeyStatus { get => _hotkeyStatus; private set => Set(ref _hotkeyStatus, value); }
    private OverlayHotkeyChord SavedChord => new(_settings.RecordingShortcutModifiers, _settings.RecordingShortcutKey);
    public void SetHotkeyHandler(Func<bool, OverlayHotkeyChord, string?> apply) => _applyHotkey = apply;
    public bool ConfigureHotkey(bool enabled, OverlayHotkeyChord chord)
    {
        var error = _applyHotkey?.Invoke(enabled, chord) ?? (_applyHotkey is null ? "Available after Wisp starts." : null);
        HotkeyStatus = error ?? (enabled ? "Ready" : "Off");
        OnChanged(nameof(HotkeyEnabled)); OnChanged(nameof(HotkeyText));
        return error is null;
    }
    public void RefreshHotkey() => ConfigureHotkey(HotkeyEnabled, SavedChord);
    public void SuspendHotkeyStatus() { CancelCountdown("Countdown canceled because Wisp was paused."); HotkeyStatus = HotkeyEnabled ? "Paused" : "Off"; }

    public async Task InitializeAsync()
    {
        if (_initialized || _initializing || _disposed) return;
        _initializing = true;
        try { await LoadLibraryAsync(); _initialized = true; }
        finally { _initializing = false; RefreshStatus(); }
    }
    public void RefreshStatus()
    {
        if (_disposed) return;
        RefreshCountdown();
        if (_lastSpeedUnit != _settings.SpeedUnit || _lastTorqueUnit != _settings.TorqueUnit ||
            _lastTemperatureUnit != _settings.TireTemperatureUnit || _lastBoostUnit != _settings.BoostPressureUnit)
        {
            var conversion = RunPresentation.SpeedFactor(_settings.SpeedUnit) / RunPresentation.SpeedFactor(_lastSpeedUnit);
            if (double.TryParse(FromSpeed, out var from) && double.IsFinite(from)) Set(ref _fromSpeed, (from * conversion).ToString("0.##", CultureInfo.CurrentCulture), nameof(FromSpeed));
            if (double.TryParse(ToSpeed, out var to) && double.IsFinite(to)) Set(ref _toSpeed, (to * conversion).ToString("0.##", CultureInfo.CurrentCulture), nameof(ToSpeed));
            _lastSpeedUnit = _settings.SpeedUnit; _lastTorqueUnit = _settings.TorqueUnit;
            _lastTemperatureUnit = _settings.TireTemperatureUnit; _lastBoostUnit = _settings.BoostPressureUnit;
            RequestAnalysis();
        }
        if (_service.IsRecording && !_wasRecording)
        {
            _pendingAnalysis |= IsBusy || _preparingCharts; ++_analysisRevision; ++_chartRevision; _preparingCharts = false; IsBusy = false;
        }
        _wasRecording = _service.IsRecording;
        if (!RecordingActive && !_metadataClosing)
        {
            if (_pendingSavedRun is { } saved)
            {
                _pendingSavedRun = null; _pendingLibraryRefresh = false; _pendingAnalysis = false;
                _ = ShowSavedAsync(saved);
            }
            else
            {
                if (_pendingLibraryRefresh) { _pendingLibraryRefresh = false; _ = LoadLibraryAsync(); }
                if (_pendingSelection is { } selection) { _pendingSelection = null; _pendingAnalysis = false; _ = LoadSelectionAsync(selection); }
                else if (_pendingAnalysis) { _pendingAnalysis = false; RequestAnalysis(); }
            }
        }
        OnChanged(nameof(RecordButtonText)); OnChanged(nameof(CanToggleRecording)); OnChanged(nameof(IsRecording)); OnChanged(nameof(RecordingStatus));
        OnChanged(nameof(CanManageRun)); OnChanged(nameof(CanManageLibrary)); OnChanged(nameof(CanSelectRun)); OnChanged(nameof(CanManageAllRuns));
        OnChanged(nameof(LibraryHelp));
        NotifyRecordingOptions();
        OnChanged(nameof(SpeedUnitText)); RaiseCommands();
    }
    public async Task ToggleRecordingAsync()
    {
        if (_disposed || _service.IsPreparing || (_metadataClosing && !_service.IsRecording)) return;
        Error = "";
        try
        {
            if (IsCountingDown) { CancelCountdown(); }
            else if (_service.IsRecording) { Status = "Saving your run…"; await _service.StopAsync(); }
            else
            {
                if (_storageOperations > 0) { Error = "Let the current library action finish, then start recording."; return; }
                _recordingNotice = null;
                if (!_service.CanStart) { Error = RecordingStatus; return; }
                _activeStopAfter = StopAfterSeconds == 0 ? null : TimeSpan.FromSeconds(StopAfterSeconds);
                if (CountdownSeconds > 0)
                {
                    _countdownStartedAt = _timeProvider.GetTimestamp(); _armedCountdownSeconds = CountdownSeconds;
                    _pendingAnalysis |= IsBusy || _preparingCharts; ++_analysisRevision; ++_chartRevision; _preparingCharts = false; OnChanged(nameof(IsPreparingCharts)); IsBusy = false;
                }
                else StartNow();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail("The run could not be started or saved. Check local storage and try again."); }
        finally { RefreshStatus(); }
    }
    public void SetPageVisible(bool visible)
    {
        _pageVisible = visible;
        if (!visible && !_disposed) _ = FlushMetadataAsync();
        if (visible)
        {
            var revision = _analysisRevision;
            RefreshStatus();
            if (revision == _analysisRevision) RequestCharts();
        }
    }
    private void ServiceStateChanged(object? sender, EventArgs e) => OnUi(RefreshStatus);
    private void ServiceRunSaved(RecordedRun run) => OnUi(() => _ = ShowSavedAsync(run));
    private async Task ShowSavedAsync(RecordedRun run)
    {
        if (_metadataClosing) { _pendingSavedRun = run; _pendingLibraryRefresh = true; return; }
        if (RecordingActive) { _pendingSavedRun = run; _pendingLibraryRefresh = true; return; }
        try
        {
            await LoadLibraryAsync();
            if (RecordingActive) { _pendingSavedRun = run; _pendingLibraryRefresh = true; return; }
            var item = Library.FirstOrDefault(item => item.Id == run.Id);
            if (item is not null) { _selectedRun = item; OnChanged(nameof(SelectedRun)); await LoadSelectionAsync(item, run); }
            Status = "Run saved. Open Runs to explore it.";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail("The run was saved, but its report could not be opened. Refresh the run list."); }
        finally { RefreshStatus(); }
    }
    private async Task LoadLibraryAsync()
    {
        if (RecordingActive) { _pendingLibraryRefresh = true; return; }
        try
        {
            var summaries = await StoreOperationAsync(() => _service.Store.ListAsync());
            var selectedId = _selectedRun?.Id; var comparisonId = _comparisonChoice?.Id;
            SynchronizeItems(Library, summaries.OrderByDescending(item => item.StartedAtUtc)
                .Select(summary => new SavedRunItem(_metadataEdits.TryGetValue(summary.Id, out var edit) && edit.LastSaved is { } saved ? saved : summary)));
            _selectedRun = Library.FirstOrDefault(item => item.Id == selectedId);
            _comparisonChoice = Library.FirstOrDefault(item => item.Id == comparisonId);
            OnChanged(nameof(SelectedRun)); OnChanged(nameof(ComparisonChoice));
            OnChanged(nameof(IsLibraryEmpty)); OnChanged(nameof(CanManageAllRuns)); RaiseCommands();
            RefreshLibrarySearch();
            if (_service.Store.Warning is { } warning) Error = warning;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail("Saved runs could not be read. Check local storage, then refresh."); }
    }
    private async Task LoadSelectionAsync(SavedRunItem selected, RecordedRun? knownRun = null)
    {
        if (RecordingActive)
        {
            if (knownRun is not null) _pendingSavedRun = knownRun;
            else _pendingSelection = selected;
            Status = "Stop recording to open the selected run. Your current report stays available."; return;
        }
        var revision = ++_selectionRevision;
        _loadingComparison = false; _comparisonNeedsAnalysis = false;
        ++_analysisRevision;
        ++_chartRevision;
        _preparingCharts = false; OnChanged(nameof(IsPreparingCharts)); OnChanged(nameof(CanExportImage));
        var hadRun = HasRun;
        _loadingSelection = true;
        IsBusy = true; Error = ""; Status = "Opening the selected run…";
        try
        {
            var run = knownRun is { } saved && saved.Id == selected.Id ? saved : _reviewRuns.TryGetValue(selected.Id, out var review) ? review : await StoreOperationAsync(() => _service.Store.LoadAsync(selected.Id));
            if (revision != _selectionRevision || _disposed) return;
            if (_metadataClosing) { _pendingSelection = selected; return; }
            await RestoreMetadataDraftAsync(run);
            if (revision != _selectionRevision || _disposed) return;
            if (_metadataClosing) { _pendingSelection = selected; return; }
            if (RecordingActive) { _pendingSelection = selected; return; }
            var opened = await AnalyzeAsync(run);
            if (revision != _selectionRevision) return;
            if (opened)
            {
                if (!hadRun) ReportOpened?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _selectedRun = Library.FirstOrDefault(item => item.Id == _runA?.Id);
                OnChanged(nameof(SelectedRun));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (revision == _selectionRevision)
            {
                _selectedRun = Library.FirstOrDefault(item => item.Id == _runA?.Id);
                OnChanged(nameof(SelectedRun));
                Fail("This run could not be opened. The file may be incomplete or unavailable.");
            }
        }
        finally { if (revision == _selectionRevision) { _loadingSelection = false; IsBusy = false; } }
    }
    public async Task SaveDetailsAsync()
    {
        if (_runA is null || !CanManageRun) return;
        foreach (var edit in _metadataEdits.Values.Where(edit => edit.Failed)) edit.AttemptedRevision = -1;
        await FlushMetadataAsync();
    }
    private async Task CompareChosenAsync()
    {
        if (_runA is null || !CanManageRun || ComparisonChoice is null || ComparisonChoice.Id == _runA.Id) return;
        var id = _runA.Id;
        var selectionRevision = _selectionRevision;
        _loadingComparison = true; _comparisonNeedsAnalysis = false;
        IsBusy = true;
        try
        {
            var selected = ComparisonChoice;
            var run = _reviewRuns.TryGetValue(selected.Id, out var review) ? review : await StoreOperationAsync(() => _service.Store.LoadAsync(selected.Id));
            if (_runA?.Id != id || _selectionRevision != selectionRevision || _disposed) return;
            bool opened;
            do
            {
                _comparisonNeedsAnalysis = false;
                opened = await AnalyzeAsync(comparisonRun: run);
                if (_runA?.Id != id || _selectionRevision != selectionRevision || _disposed) return;
            } while (_comparisonNeedsAnalysis && !RecordingActive);
            if (!opened) return;
            if (!IsGraphWorkspaceOpen) { ShowSummary(); ReportOpened?.Invoke(this, EventArgs.Empty); }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (_runA?.Id == id && _selectionRevision == selectionRevision) Fail("The comparison run could not be opened. Choose another run or try again."); }
        finally
        {
            if (_runA?.Id == id && _selectionRevision == selectionRevision)
            { _loadingComparison = false; _comparisonNeedsAnalysis = false; IsBusy = false; }
        }
    }
    private Task ComparePreviousAsync()
    {
        if (!CanManageRun || PreviousRun() is not { } previous) return Task.CompletedTask;
        ComparisonChoice = previous;
        return CompareChosenAsync();
    }
    private SavedRunItem? PreviousRun() => Library.FirstOrDefault(item => item.Id != _runA?.Id && item.Summary.StartedAtUtc < _runA?.StartedAtUtc);
    private void RequestAnalysis()
    {
        if (_disposed) return;
        if (_loadingSelection && _selectedRun is { } selected) _ = LoadSelectionAsync(selected);
        else if (_loadingComparison) { _comparisonNeedsAnalysis = true; ++_analysisRevision; }
        else if (_runA is not null) _ = AnalyzeAsync();
    }
    private async Task<bool> AnalyzeAsync(RecordedRun? openingRun = null, RecordedRun? comparisonRun = null)
    {
        if (openingRun is null && _runA is null || _disposed) return false;
        var revision = ++_analysisRevision;
        if (RecordingActive)
        { _pendingAnalysis = true; IsBusy = false; Status = "Stop recording to prepare the changed report. Recording is still running."; return false; }
        var a = openingRun ?? _runA!;
        var b = comparisonRun ?? (openingRun is null || _runB?.Id != openingRun.Id ? _runB : null);
        var purpose = Purpose;
        var interval = _interval;
        var hadMatchedSelection = interval is not null && _matchedA is not null;
        var removesDuplicateComparison = openingRun is not null && _runB?.Id == openingRun.Id;
        bool sameSpeed = SameSpeed && b is not null;
        var previousView = (Start: ViewStart, End: ViewEnd, Cursor: CursorSeconds, Whole: ViewStart == 0 && ViewEnd == ReviewDuration());
        var openingWorkspace = (openingRun is not null || comparisonRun is not null) && CreateWorkspaceRequest() is { } currentWorkspace
            ? currentWorkspace with
            {
                RunA = a,
                RunB = b,
                OffsetA = 0,
                OffsetB = 0,
                TimeBoundsA = null,
                TimeBoundsB = null,
                AlternativeBoundsA = null,
                AlternativeBoundsB = null
            }
            : null;
        if (!double.TryParse(FromSpeed, out var from) || !double.TryParse(ToSpeed, out var to) || !double.IsFinite(from) || !double.IsFinite(to) || from < 0 || to <= from)
        { if (sameSpeed) { IsBusy = false; Fail("Choose a speed range, with the ending speed above the starting speed."); return false; } from = 20; to = 60; }
        from /= RunPresentation.SpeedFactor(_settings.SpeedUnit); to /= RunPresentation.SpeedFactor(_settings.SpeedUnit);
        IsBusy = true; Status = "Preparing the report…";
        try
        {
            var result = await Task.Run(() =>
            {
                if (revision != Volatile.Read(ref _analysisRevision)) throw new OperationCanceledException();
                RunSpeedRange? accelerationA = sameSpeed ? RunAnalysis.MeasureAcceleration(a, from, to) : null;
                RunSpeedRange? accelerationB = sameSpeed && b is not null ? RunAnalysis.MeasureAcceleration(b, from, to) : null;
                bool matched = accelerationA is not null && accelerationB is not null;
                RunInterval? rangeA = matched ? accelerationA!.Interval : null;
                RunInterval? rangeB = matched ? accelerationB!.Interval : null;
                var selectedInterval = openingRun is null ? interval : hadMatchedSelection && !matched ? null : ClampInterval(interval,
                    Math.Min(rangeA is { } firstRange ? firstRange.EndSeconds - firstRange.StartSeconds : a.Samples.LastOrDefault()?.ElapsedSeconds ?? 0,
                        b is null ? double.MaxValue : rangeB is { } secondRange ? secondRange.EndSeconds - secondRange.StartSeconds : b.Samples.LastOrDefault()?.ElapsedSeconds ?? 0));
                if (revision != Volatile.Read(ref _analysisRevision)) throw new OperationCanceledException();
                RunInterval? SelectRange(RunInterval? range) => selectedInterval is { } selected ? range is { } matchedRange
                    ? new(Math.Min(matchedRange.EndSeconds, matchedRange.StartSeconds + selected.StartSeconds),
                        Math.Min(matchedRange.EndSeconds, matchedRange.StartSeconds + selected.EndSeconds)) : selected : range;
                var selectedA = SelectRange(rangeA); var selectedB = SelectRange(rangeB);
                var comparisonPurpose = matched && selectedInterval is not null && purpose == RunPurpose.Acceleration ? RunPurpose.General : purpose;
                var comparison = b is null ? null : RunAnalysis.Compare(a, b, comparisonPurpose, selectedA, selectedB, from, to);
                var report = comparison?.RunA ?? RunAnalysis.BuildReport(a, purpose, selectedA);
                var workspace = openingWorkspace is null ? null : RunWorkspacePreparation.Prepare(openingWorkspace with
                {
                    OffsetA = rangeA?.StartSeconds ?? 0,
                    OffsetB = rangeB?.StartSeconds ?? 0,
                    TimeBoundsA = rangeA,
                    TimeBoundsB = rangeB,
                    AlternativeBoundsA = selectedA,
                    AlternativeBoundsB = selectedB
                },
                    () => revision == Volatile.Read(ref _analysisRevision));
                return (Report: report, Comparison: comparison, RangeA: rangeA, RangeB: rangeB, AccelerationA: accelerationA,
                    AccelerationB: accelerationB, Workspace: workspace, Interval: selectedInterval);
            });
            if (revision != _analysisRevision || _disposed) return false;
            if (comparisonRun is not null) _runB = comparisonRun;
            if (openingRun is not null)
            {
                _runA = WithLatestSavedMetadata(openingRun); _runB = b is null ? null : WithLatestSavedMetadata(b); _interval = result.Interval;
                _sameSpeed = sameSpeed;
                ShowMetadata(_runA);
                SelectionStart = _interval?.StartSeconds ?? double.NaN;
                SelectionEnd = _interval?.EndSeconds ?? double.NaN;
                SelectionFrom = (_interval?.StartSeconds ?? 0).ToString("0.00", CultureInfo.CurrentCulture);
                SelectionTo = (_interval?.EndSeconds ?? 0).ToString("0.00", CultureInfo.CurrentCulture);
                OnChanged(nameof(SameSpeed));
                ComparisonChoice = Library.FirstOrDefault(item => item.Id == b?.Id) ??
                    Library.FirstOrDefault(item => item.Id == _comparisonChoice?.Id && item.Id != openingRun.Id) ??
                    Library.FirstOrDefault(item => item.Id != openingRun.Id);
            }
            _reportA = result.Report; _reportB = result.Comparison?.RunB;
            var changedAlignment = _matchedA != result.RangeA || _matchedB != result.RangeB;
            _matchedA = result.RangeA; _matchedB = result.RangeB;
            _offsetA = _matchedA?.StartSeconds ?? 0; _offsetB = _matchedB?.StartSeconds ?? 0;
            ComparisonNote = "";
            if (sameSpeed && result is { RangeA: not null, RangeB: not null, AccelerationA: { } accelerationA, AccelerationB: { } accelerationB })
            {
                ComparisonNote = $"{FromSpeed}–{ToSpeed} {SpeedUnitText}: A {accelerationA.DurationSeconds:0.00}s · B {accelerationB.DurationSeconds:0.00}s. Each chart starts at its own crossing and ends at the target speed. Times are estimates from telemetry; routes and conditions may differ.";
            }
            else if (sameSpeed)
            { _sameSpeed = false; OnChanged(nameof(SameSpeed)); ComparisonNote = "That range is unavailable in one or both runs. Matching is off; the report and charts use elapsed time. Choose a different range to try again."; }
            if (b is not null && !sameSpeed) ComparisonNote = "Compare selected sections; routes and starting conditions may differ. Acceleration times are estimates from telemetry.";
            if (removesDuplicateComparison) ComparisonNote = "The selected run is now A, so its duplicate B comparison was removed.";
            if (openingRun is not null)
            {
                var duration = ReviewDuration();
                if (previousView.Whole || previousView.Start >= duration) ResetView();
                else { ViewStart = previousView.Start; ViewEnd = Math.Min(previousView.End, duration); CursorSeconds = Math.Clamp(previousView.Cursor, ViewStart, ViewEnd); }
                if (interval != result.Interval)
                    ComparisonNote += (ComparisonNote.Length > 0 ? " " : "") + (result.Interval is null
                        ? hadMatchedSelection && result.RangeA is null
                            ? "The previous matched selection is no longer available; the report now covers the whole run."
                            : "The previous selection is outside this run; the report now covers the whole run."
                        : "The selected interval was shortened to fit the available run data.");
            }
            else if (changedAlignment) ResetView();
            QualityNote = result.Report.QualityNote + (result.Comparison is null || result.Comparison.RunB.QualityNote == result.Report.QualityNote ? "" : "  B: " + result.Comparison.RunB.QualityNote);
            SynchronizeItems(Metrics, RunPresentation.Metrics(_reportA, _reportB, _settings.SpeedUnit, _settings.TorqueUnit));
            RefreshStatistics();
            var preparedFindings = new List<RunFindingItem>();
            var findings = result.Comparison?.Findings ?? result.Report.Findings;
            _imageFindings = findings.Take(3).ToArray();
            foreach (var finding in findings.Take(3))
            {
                var captured = finding;
                preparedFindings.Add(new(finding.Title, finding.Detail, new RunUiCommand(() =>
                {
                    if (captured.Interval is { } range)
                    {
                        ChartGroup = captured.EvidenceView switch { RunEvidenceView.Inputs => RunChartGroup.Inputs, RunEvidenceView.Tires => RunChartGroup.Tires, _ => RunChartGroup.Speed };
                        GraphView = AvailableGraphViews[0];
                        EnsureWorkspaceGraph(ChartGroup, GraphView.Mode);
                        SelectInterval(range.StartSeconds - _offsetA, range.EndSeconds - _offsetA);
                        if (HasSelection) { ViewStart = SelectionStart; ViewEnd = SelectionEnd; FocusChartsRequested?.Invoke(this, EventArgs.Empty); }
                    }
                    return Task.CompletedTask;
                }, () => !IsBusy && !RecordingActive, () => Fail("That section could not be selected.")), finding.Interval is not null));
            }
            for (var index = 0; index < preparedFindings.Count; index++)
                if (index < Findings.Count) Findings[index].Update(preparedFindings[index]);
                else Findings.Add(preparedFindings[index]);
            while (Findings.Count > preparedFindings.Count) Findings.RemoveAt(Findings.Count - 1);
            Status = "Report ready."; Error = ""; NotifyRun();
            if (result.Workspace is { } preparedWorkspace)
            {
                ApplyWorkspacePrepared(preparedWorkspace);
                SynchronizeItems(Charts, GraphView.Mode == RunPlotMode.TimeSeries ? preparedWorkspace.TimeGroups.GetValueOrDefault(ChartGroup) ?? [] : []);
                SynchronizeItems(AlternativeCharts, GraphView.Mode == RunPlotMode.TimeSeries ? [] : preparedWorkspace.AlternativeModes.GetValueOrDefault(GraphView.Mode) ?? []);
                RefreshMarkers();
            }
            else await BuildChartsAsync(revision);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { if (revision == _analysisRevision) Fail("The report could not be prepared. Select the run again to retry."); }
        finally { if (revision == _analysisRevision && !_loadingComparison) IsBusy = false; }
        return false;
    }
    private void RequestCharts() { if (_pageVisible && _runA is not null && !_disposed) _ = BuildChartsAsync(_analysisRevision); }
    private async Task BuildChartsAsync(long revision)
    {
        if (!_pageVisible || _runA is null) return;
        if (UsesModularWorkspace && !IsGraphWorkspaceOpen) return;
        if (RecordingActive) { _pendingAnalysis = true; Status = "The changed charts will be prepared after recording is saved or canceled."; return; }
        var chartRevision = ++_chartRevision;
        _preparingCharts = true; OnChanged(nameof(IsPreparingCharts)); OnChanged(nameof(CanExportImage));
        var a = _runA; var b = _runB; var group = ChartGroup;
        var mode = GraphView.Mode; var fullThrottle = FullThrottleOnly; var gear = GearFilter.Gear;
        var speed = _settings.SpeedUnit; var temperature = _settings.TireTemperatureUnit;
        var torque = _settings.TorqueUnit; var boost = _settings.BoostPressureUnit;
        var offsetA = _offsetA; var offsetB = _offsetB;
        var boundsA = _matchedA; var boundsB = _matchedB;
        var alternativeBoundsA = ReportBounds(false); var alternativeBoundsB = ReportBounds(true);
        var workspaceRequest = CreateWorkspaceRequest();
        try
        {
            var prepared = await Task.Run(() =>
            {
                if (chartRevision != Volatile.Read(ref _chartRevision) || revision != Volatile.Read(ref _analysisRevision)) throw new OperationCanceledException();
                var workspace = workspaceRequest is null ? null : RunWorkspacePreparation.Prepare(workspaceRequest,
                    () => chartRevision == Volatile.Read(ref _chartRevision) && revision == Volatile.Read(ref _analysisRevision));
                var time = mode != RunPlotMode.TimeSeries ? [] : workspace is not null ? workspace.TimeGroups.GetValueOrDefault(group) ?? [] :
                    RunPresentation.Charts(a, b, group, speed, temperature, offsetA, offsetB, boundsA, boundsB, torque, boost);
                var alternative = mode == RunPlotMode.TimeSeries ? [] : workspace is not null ? workspace.AlternativeModes.GetValueOrDefault(mode) ?? [] :
                    RunAlternativePlots.Build(a, b, mode, temperature, torque, alternativeBoundsA, alternativeBoundsB, fullThrottle, gear);
                return (Time: time, Alternative: alternative, Workspace: workspace);
            });
            if (chartRevision != _chartRevision || revision != _analysisRevision || !_pageVisible || _disposed || RecordingActive ||
                (UsesModularWorkspace && !IsGraphWorkspaceOpen) || group != ChartGroup || !SameChartSource(a, _runA) || !SameChartSource(b, _runB)) return;
            SynchronizeItems(Charts, prepared.Time);
            SynchronizeItems(AlternativeCharts, prepared.Alternative);
            if (prepared.Workspace is { } workspace) ApplyWorkspacePrepared(workspace);
            RefreshMarkers();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (chartRevision == _chartRevision && revision == _analysisRevision && !_disposed) Fail("The chart could not be prepared. Choose the chart view again to retry."); }
        finally { if (chartRevision == _chartRevision) { _preparingCharts = false; OnChanged(nameof(IsPreparingCharts)); OnChanged(nameof(CanExportImage)); } }
    }
    public void SelectInterval(double from, double to)
    {
        if (RecordingActive || _runA is null || !double.IsFinite(from) || !double.IsFinite(to)) return;
        var end = _matchedA is { } a && _matchedB is { } b ? Math.Min(a.EndSeconds - a.StartSeconds, b.EndSeconds - b.StartSeconds)
            : Math.Max(0, (_runA.Samples.LastOrDefault()?.ElapsedSeconds ?? 0) - _offsetA);
        from = Math.Clamp(from, 0, end); to = Math.Clamp(to, 0, end);
        if (to <= from) { Fail("The selection must end after it starts."); return; }
        SelectionStart = from; SelectionEnd = to; SelectionFrom = from.ToString("0.00", CultureInfo.CurrentCulture); SelectionTo = to.ToString("0.00", CultureInfo.CurrentCulture);
        _interval = new(from, to); RequestAnalysis();
    }
    private void ApplyTypedInterval()
    {
        if (double.TryParse(SelectionFrom, out var from) && double.TryParse(SelectionTo, out var to)) SelectInterval(from, to);
        else Fail("Enter the start and end as seconds, for example 12.5 and 18.");
    }
    private void ResetView()
    {
        ViewStart = 0;
        ViewEnd = ReviewDuration();
        CursorSeconds = ViewStart;
    }
    private double ReviewDuration() => Math.Max(1, Math.Max(
        (_matchedA?.EndSeconds ?? _runA?.Samples.LastOrDefault()?.ElapsedSeconds ?? 0) - _offsetA,
        (_matchedB?.EndSeconds ?? _runB?.Samples.LastOrDefault()?.ElapsedSeconds ?? 0) - _offsetB));
    private static RunInterval? ClampInterval(RunInterval? interval, double duration) => interval is { } selected &&
        selected.StartSeconds < duration ? new(selected.StartSeconds, Math.Min(selected.EndSeconds, duration)) : null;
    public async Task DeleteSelectedAsync()
    {
        if (_runA is null || !CanManageRun) return;
        var id = _runA.Id;
        IsBusy = true;
        try
        {
            if (!await FlushMetadataAsync()) { Fail("Save the pending run details before removing this run. Your draft is kept."); return; }
            await StoreOperationAsync(() => _service.Store.DeleteAsync(id)); ++_analysisRevision; ++_selectionRevision; _runA = _runB = null; _selectedRun = null;
            SuspendDeletedMetadata([id]);
            Findings.Clear(); Metrics.Clear(); Charts.Clear(); AlternativeCharts.Clear(); ClearStatistics(); ClearWorkspaceCharts(); NotifyRun(); OnChanged(nameof(SelectedRun));
            await LoadLibraryAsync(); Status = "Run removed. You can undo this removal."; LastDeletedId = id; _lastDeletedBatch = null; OnChanged(nameof(CanUndoDelete));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail("The run could not be removed. Try again."); }
        finally { IsBusy = false; }
    }
    private Guid? LastDeletedId { get; set; }
    public bool CanUndoDelete => LastDeletedId is not null || _lastDeletedBatch is not null;
    public async Task UndoDeleteAsync()
    {
        if (!CanUndoDelete || !CanManageLibrary) return;
        IsBusy = true;
        try
        {
            if (_lastDeletedBatch is { } batch)
            {
                var count = await StoreOperationAsync(() => _service.Store.RestoreDeletedBatchAsync(batch.Id));
                _lastDeletedBatch = null; Status = $"Restored {count} saved runs.";
            }
            else if (LastDeletedId is Guid id)
            { await StoreOperationAsync(() => _service.Store.RestoreAsync(id)); LastDeletedId = null; Status = "Run restored."; }
            OnChanged(nameof(CanUndoDelete)); Error = ""; await LoadLibraryAsync(); RestoreDeletedMetadata();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail("The run could not be restored. Its recovery copy has been kept."); }
        finally { IsBusy = false; }
    }
    public async Task ExportSelectedAsync(string destination)
    {
        if (_runA is null || !CanManageRun) return;
        var id = _runA.Id;
        IsBusy = true;
        try
        {
            if (!await FlushMetadataAsync()) { Fail("Save the pending run details before exporting. Your draft is kept."); return; }
            if (_runA?.Id != id) return;
            var run = _runA;
            await StoreOperationAsync(() => System.IO.Path.GetExtension(destination).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? RunCsvExporter.WriteAsync(run, destination) : _service.Store.ExportAsync(run.Id, destination));
            Status = "Run exported. Nothing was uploaded.";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail("Export failed. Choose a new filename and try again."); }
        finally { IsBusy = false; }
    }
    public async Task ImportAsync(string source)
    {
        if (!CanManageLibrary) return;
        IsBusy = true;
        try
        {
            var summary = await StoreOperationAsync(() => _service.Store.ImportAsync(source)); await LoadLibraryAsync();
            if (Library.FirstOrDefault(item => item.Id == summary.Id) is { } imported)
            { _selectedRun = imported; OnChanged(nameof(SelectedRun)); await LoadSelectionAsync(imported); }
            Status = "Run imported.";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail("This file could not be imported. Choose a complete Wisp run file."); }
        finally { IsBusy = false; }
    }
    internal async Task ShowReviewAsync(RecordedRun a, RecordedRun? b = null, RunPurpose purpose = RunPurpose.General)
    {
        _reviewRuns.Clear(); _sameSpeed = false; _matchedA = _matchedB = _interval = null; _offsetA = _offsetB = 0;
        SelectionStart = SelectionEnd = double.NaN; OnChanged(nameof(SameSpeed));
        _reviewRuns[a.Id] = a; if (b is not null) _reviewRuns[b.Id] = b;
        Library.Clear(); Library.Add(new(Summary(a))); if (b is not null) Library.Add(new(Summary(b)));
        _selectedRun = Library[0]; _runA = a; _runB = b; _settings.RunPurpose = purpose;
        _pageVisible = true; ShowMetadata(a);
        ClearWorkspaceCharts(); ShowSummary();
        ResetView(); NotifyRun(); OnChanged(nameof(Purpose)); OnChanged(nameof(SelectedRun)); OnChanged(nameof(IsLibraryEmpty)); await AnalyzeAsync();
        ReportOpened?.Invoke(this, EventArgs.Empty);
    }
    private static RunSummary Summary(RecordedRun run) => new(run.Id, run.Name, run.Tune, run.Notes, run.StartedAtUtc,
        run.Samples.LastOrDefault()?.ElapsedSeconds ?? 0, run.Samples.Length, run.Samples.FirstOrDefault()?.State.CarOrdinal ?? 0, run.IsIncomplete, run.FinishReason);
    private void NotifyRun()
    {
        if (!HasRun) ShowSummary();
        NotifyNavigation();
        SelectedPointContext = "";
        foreach (var property in new[] { nameof(HasRun), nameof(HasComparison), nameof(CanManageRun), nameof(RunALabel), nameof(RunBLabel), nameof(RunDescription), nameof(CarIdentifier), nameof(IntervalLabel), nameof(CursorVehicleContext) }) OnChanged(property);
        RefreshMarkers();
        RaiseCommands();
    }
    private void Fail(string message) { Error = message; Status = "Needs attention"; }
    private async Task<T> StoreOperationAsync<T>(Func<Task<T>> operation)
    {
        _storageOperations++; NotifyStorage();
        try { return await operation(); }
        finally { _storageOperations--; NotifyStorage(); }
    }
    private Task StoreOperationAsync(Func<Task> operation) => StoreOperationAsync(async () => { await operation(); return true; });
    private void NotifyStorage()
    {
        OnChanged(nameof(CanToggleRecording)); OnChanged(nameof(RecordingStatus)); OnChanged(nameof(CanManageRun)); OnChanged(nameof(CanManageLibrary)); OnChanged(nameof(CanSelectRun)); OnChanged(nameof(CanManageAllRuns)); OnChanged(nameof(CanExportImage)); RaiseCommands();
    }
    private void OnUi(Action action) { if (_disposed || _dispatcher.HasShutdownStarted) return; if (_dispatcher.CheckAccess()) action(); else _ = _dispatcher.BeginInvoke(action); }
    private RunUiCommand Command(Func<Task> execute, Func<bool>? canExecute = null)
    {
        var command = new RunUiCommand(execute, canExecute ?? (() => true), () => Fail("The action could not finish. Try again."));
        _commands.Add(command); return command;
    }
    private void RaiseCommands()
    {
        NotifyWorkspaceAvailability();
        foreach (var command in _commands) command.RaiseCanExecuteChanged();
        foreach (var finding in Findings) if (finding.ShowCommand is RunUiCommand command) command.RaiseCanExecuteChanged();
        foreach (var marker in Markers) if (marker.JumpCommand is RunUiCommand command) command.RaiseCanExecuteChanged();
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnChanged(property); return true; }
    private static void SynchronizeItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        var items = source.ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            if (index >= target.Count) target.Add(items[index]);
            else if (!EqualityComparer<T>.Default.Equals(target[index], items[index])) target[index] = items[index];
        }
        while (target.Count > items.Length) target.RemoveAt(target.Count - 1);
    }
    private void OnChanged([CallerMemberName] string? property = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        if (property is nameof(IsBusy) or nameof(Status) or nameof(IsPreparingCharts) or nameof(IntervalLabel))
            PropertyChanged?.Invoke(this, new(nameof(WorkspaceReadout)));
    }
    public void Dispose() { DisposeMetadata(); Library.CollectionChanged -= LibraryChanged; CancelCountdown(); ShortcutCaptureActive = false; _pendingSavedRun = null; _disposed = true; _analysisRevision++; _chartRevision++; _selectionRevision++; _service.StateChanged -= ServiceStateChanged; _service.RunSaved -= ServiceRunSaved; }
}

internal sealed class RunUiCommand(Func<Task> execute, Func<bool> canExecute, Action failed) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && canExecute();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; RaiseCanExecuteChanged();
        try { await execute(); }
        catch (Exception error) when (error is not OutOfMemoryException) { failed(); }
        finally { _running = false; RaiseCanExecuteChanged(); }
    }
    internal void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
