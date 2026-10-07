using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed record LapReviewMetric(string Label, string Value, string Reference);
public sealed record LapTimingChoice(string Label, LapTimingMode Mode);
public sealed record LapChannelChoice(string Label, LapReviewChannel Channel);
public sealed record LapReviewWheelReadout(string Label, string FrontLeft, string FrontRight, string RearLeft, string RearRight);
public sealed record LapReviewCursorReadout(string Position, string Speed, string Powertrain, string Throttle, string Brake,
    string Steering, string LateralG, string LongitudinalG, string Delta, LapReviewWheelReadout Temperatures,
    LapReviewWheelReadout SlipRatio, LapReviewWheelReadout SlipAngle, LapReviewWheelReadout Suspension);
public enum LapReviewChannel { Speed, Delta, Throttle, Brake, Steering, LateralG, LongitudinalG, CombinedG, Rpm, Gear, TireTemperature, SlipRatio, SlipAngle, Suspension, Power, Torque, Elevation }

public sealed partial class LapReviewViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettings _settings;
    private readonly RunStore _store;
    private readonly Action _save;
    private RecordedRun? _run, _comparisonRun, _referenceRun;
    private CancellationTokenSource? _loadCancellation, _analysisCancellation;
    private readonly List<RunUiCommand> _commands = [];
    private LapReviewLap? _lap, _reference;
    private LapReviewComparison? _comparison, _reverseComparison;
    private LapTimingChoice _timing;
    private LapChannelChoice _channel;
    private int _cursor, _sectionStart, _sectionEnd;
    private int _wheel;
    public string[] Wheels { get; } = ["Front left", "Front right", "Rear left", "Rear right"];
    public int SelectedWheel { get => _wheel; set { if (Set(ref _wheel, Math.Clamp(value, 0, 3))) Changed(nameof(Plot)); } }
    public bool IsWheelChannel => Channel.Channel is LapReviewChannel.TireTemperature or LapReviewChannel.SlipRatio or LapReviewChannel.SlipAngle or LapReviewChannel.Suspension;
    private string _status = "Record a lap with position telemetry to review its driven line.", _captureStatus = "", _referenceStatus = "";
    private bool _disposed, _busy, _applying;
    private LapSectionStatistics? _sectionStatistics, _referenceStatistics;
    private double? _referenceSectionSeconds;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<double>? CursorMoved;
    public event Action<double, double>? SectionChosen;
    public ObservableCollection<LapReviewLap> Laps { get; } = [];
    public ObservableCollection<LapReviewLap> ReferenceLaps { get; } = [];
    public ObservableCollection<LapReviewMetric> Metrics { get; } = [];
    public ObservableCollection<string> Events { get; } = [];
    public LapTimingChoice[] TimingChoices { get; } = [new("Race / Rivals laps", LapTimingMode.GameLaps), new("Time Attack", LapTimingMode.TimeAttack)];
    public LapChannelChoice[] Channels { get; } = [new("Speed", LapReviewChannel.Speed), new("Time delta", LapReviewChannel.Delta), new("Throttle", LapReviewChannel.Throttle), new("Brake", LapReviewChannel.Brake), new("Steering input", LapReviewChannel.Steering), new("Lateral G", LapReviewChannel.LateralG), new("Longitudinal G", LapReviewChannel.LongitudinalG), new("Combined G", LapReviewChannel.CombinedG), new("Engine RPM", LapReviewChannel.Rpm), new("Gear", LapReviewChannel.Gear), new("Tire temperature", LapReviewChannel.TireTemperature), new("Tire slip ratio", LapReviewChannel.SlipRatio), new("Tire slip angle (raw)", LapReviewChannel.SlipAngle), new("Suspension travel", LapReviewChannel.Suspension), new("Horsepower", LapReviewChannel.Power), new("Torque", LapReviewChannel.Torque), new("Elevation", LapReviewChannel.Elevation)];
    public ICommand SectionStartCommand { get; }
    public ICommand SectionEndCommand { get; }
    public ICommand WholeLapCommand { get; }
    public ICommand PinCommand { get; }
    public ICommand UnpinCommand { get; }
    public ICommand ShowGraphsCommand { get; }
    public LapReviewViewModel(AppSettings settings, RunStore store, Action save)
    {
        _settings = settings; _store = store; _save = save;
        _timing = TimingChoices.First(choice => choice.Mode == settings.LapTimingMode);
        _channel = Channels[0];
        SectionStartCommand = Command(() => { _sectionStart = Cursor; if (_sectionEnd < Cursor) _sectionEnd = Cursor; RefreshSection(); });
        SectionEndCommand = Command(() => { _sectionEnd = Cursor; if (_sectionStart > Cursor) _sectionStart = Cursor; RefreshSection(); });
        WholeLapCommand = Command(() => { _sectionStart = 0; _sectionEnd = Math.Max(0, MaximumCursor); RefreshSection(); });
        PinCommand = Command(() => { if (Lap is null || !Lap.IsComplete || Lap.Points.Length == 0) return; _settings.LapReviewBenchmarkRunId = Lap.RunId; _settings.LapReviewBenchmarkLapNumber = Lap.Number; _settings.LapReviewBenchmarkSampleIndex = Lap.Points[0].SampleIndex; _settings.LapReviewBenchmarkTimingMode = Lap.TimingMode; _save(); _ = LoadAsync(); }, () => CanPin);
        UnpinCommand = Command(() => { _settings.LapReviewBenchmarkRunId = null; _save(); _ = LoadAsync(); }, () => _settings.LapReviewBenchmarkRunId is not null);
        ShowGraphsCommand = Command(() => { if (Lap is { Points.Length: > 0 }) SectionChosen?.Invoke(Lap.Points[_sectionStart].RunSeconds, Lap.Points[_sectionEnd].RunSeconds); },
            () => HasLap && _sectionEnd > _sectionStart && Lap!.Points[_sectionEnd].RunSeconds > Lap.Points[_sectionStart].RunSeconds);
    }
    private RunUiCommand Command(Action action, Func<bool>? canExecute = null)
    {
        var command = new RunUiCommand(() => { action(); return Task.CompletedTask; },
            () => !_disposed && !_busy && (canExecute?.Invoke() ?? HasLap), () => { Status = "That lap action could not be completed."; });
        _commands.Add(command);
        return command;
    }
    private void RaiseCommands() { foreach (var command in _commands) command.RaiseCanExecuteChanged(); }
    private void RefreshBusy()
    {
        _busy = _loadCancellation is not null || _analysisCancellation is not null;
        Changed(nameof(IsBusy)); Changed(nameof(CanPin)); RaiseCommands();
    }
    public LapTimingChoice Timing { get => _timing; set { if (_disposed || value is null || !Set(ref _timing, value)) return; _ = LoadAsync(); } }
    public LapChannelChoice Channel { get => _channel; set { if (!_disposed && value is not null && Set(ref _channel, value)) { Changed(nameof(Plot)); Changed(nameof(IsWheelChannel)); } } }
    public LapReviewLap? Lap
    {
        get => _lap;
        set
        {
            if (_disposed || Equals(_lap, value)) return;
            ChangeScrubContext(() =>
            {
                Set(ref _lap, value);
                _cursor = _sectionStart = 0; _sectionEnd = Math.Max(0, MaximumCursor);
                Changed(nameof(MaximumCursor)); Changed(nameof(Cursor)); Changed(nameof(HasLap)); Changed(nameof(CanPin));
                RaiseCommands();
                if (_applying) RefreshCursor();
                else if (_comparisonRun is null && _settings.LapReviewBenchmarkRunId is not null) _ = LoadAsync();
                else RefreshComparison();
            });
        }
    }
    public LapReviewLap? Reference
    {
        get => _reference;
        set
        {
            if (_disposed || Equals(_reference, value)) return;
            ChangeScrubContext(() =>
            {
                Set(ref _reference, value);
                _referenceCursor = 0;
                _referenceMapContacts = [];
                if (!_applying) RefreshComparison();
            });
        }
    }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ReferenceStatus { get => _referenceStatus; private set => Set(ref _referenceStatus, value); }
    public string CaptureStatus { get => _captureStatus; set => Set(ref _captureStatus, value); }
    public bool AutomaticRecording { get => _settings.LapReviewRecordingEnabled; set { if (_disposed || _settings.LapReviewRecordingEnabled == value) return; _settings.LapReviewRecordingEnabled = value; Changed(); _save(); } }
    public bool HasLap => Lap is { Points.Length: > 1 };
    public bool CanPin => !_disposed && !_busy && UsableBenchmark(Lap);
    public bool IsBusy => _busy;
    public int MaximumCursor => Math.Max(0, (Lap?.Points.Length ?? 1) - 1);
    public int Cursor
    {
        get => _cursor;
        set
        {
            if (_disposed) return;
            value = Math.Clamp(value, 0, MaximumCursor);
            // Every primary cursor entry point shares Both-mode synchronization:
            // map/graph picking, keyboard navigation and the lap-position slider.
            var referenceChanged = SynchronizeRelativeReferenceCursor(value);
            if (Set(ref _cursor, value))
            {
                RefreshCursor(); Changed(nameof(Plot));
                if (Lap is { Points.Length: > 0 }) CursorMoved?.Invoke(Lap.Points[Cursor].RunSeconds);
            }
            else if (referenceChanged) NotifyReferenceCursor();
        }
    }
    public LapReviewCursorReadout? CursorDetails { get; private set; }
    public string CursorText => CursorDetails is { } details ? $"{details.Position} · {details.Speed} · {details.Powertrain}" : "";
    public string SectionText { get; private set; } = "";
    public LapReviewPlotData Plot => new(Lap, Reference, _comparison, Channel.Channel, _settings.SpeedUnit, Cursor, _sectionStart, _sectionEnd, SelectedWheel, _settings.TireTemperatureUnit, _settings.TorqueUnit, VisibleContacts);
    public void RefreshSettings() { if (_disposed) return; Changed(nameof(AutomaticRecording)); RefreshCursor(); FormatSection(); Changed(nameof(Plot)); RaiseCommands(); }
    public void SetRuns(RecordedRun? run, RecordedRun? comparison)
    {
        if (_disposed || ReferenceEquals(run, _run) && ReferenceEquals(comparison, _comparisonRun)) return;
        _run = run; _comparisonRun = comparison;
        if (run?.LapTimingMode is { } mode) { _timing = TimingChoices.First(choice => choice.Mode == mode); Changed(nameof(Timing)); }
        _ = LoadAsync();
    }
    private async Task LoadAsync()
    {
        if (_disposed) return;
        _loadCancellation?.Cancel();
        _analysisCancellation?.Cancel();
        var cancel = new CancellationTokenSource(); _loadCancellation = cancel;
        var selected = Lap;
        var selection = (Cursor: _cursor, Start: _sectionStart, End: _sectionEnd);
        var run = _run; var comparisonRun = _comparisonRun; var timing = Timing.Mode;
        var pin = _settings.LapReviewBenchmarkRunId; var pinNumber = _settings.LapReviewBenchmarkLapNumber;
        var pinSample = _settings.LapReviewBenchmarkSampleIndex;
        var pinMode = _settings.LapReviewBenchmarkTimingMode;
        ClearReview(); RefreshBusy();
        try
        {
            RecordedRun? pinned = null;
            var pinMissing = false;
            if (comparisonRun is null && pin is { } id)
            {
                try { pinned = await _store.LoadAsync(id); }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { pinMissing = true; }
            }
            var result = await Task.Run(() =>
            {
                var a = run is null ? new LapReviewResult([], "Record a lap, then open it here.") : LapReviewAnalysis.Build(run, timing, cancel.Token);
                var current = selected is null ? null : a.Laps.FirstOrDefault(lap => lap.RunId == selected.RunId && lap.TimingMode == selected.TimingMode &&
                    lap.Points.FirstOrDefault()?.SampleIndex == selected.Points.FirstOrDefault()?.SampleIndex);
                current ??= a.Laps.LastOrDefault(lap => lap.IsComplete) ?? a.Laps.LastOrDefault();
                var b = comparisonRun is null ? a : LapReviewAnalysis.Build(comparisonRun, comparisonRun.LapTimingMode ?? timing, cancel.Token);
                var pinnedLap = pinned is null ? null : LapReviewAnalysis.Build(pinned, pinMode, cancel.Token).Laps
                    .FirstOrDefault(lap => UsableBenchmark(lap) && lap.Number == pinNumber && lap.Points.FirstOrDefault()?.SampleIndex == pinSample);
                var pinMatch = current is null || pinnedLap is null ? null : LapReviewAnalysis.Compare(current, pinnedLap, cancel.Token);
                var usePin = pinMatch?.CanCompare == true;
                if (usePin) b = new([pinnedLap!], "");
                return (a, b, current, usePin, pinMatch);
            }, cancel.Token);
            if (_disposed || cancel.IsCancellationRequested) return;
            _referenceRun = result.usePin ? pinned : comparisonRun ?? run;
            _applying = true;
            try
            {
                Laps.Clear(); foreach (var lap in result.a.Laps) Laps.Add(lap);
                ReferenceLaps.Clear(); foreach (var lap in result.b.Laps) ReferenceLaps.Add(lap);
                Reference = ReferenceLaps.Where(UsableBenchmark).MinBy(lap => lap.DurationSeconds) ??
                    (comparisonRun is null ? null : ReferenceLaps.LastOrDefault(lap => lap.IsComplete && lap.Points.Length > 1) ??
                        ReferenceLaps.LastOrDefault(lap => lap.Points.Length > 1));
                Lap = result.current;
                if (Lap is not null && selected is not null && Lap.RunId == selected.RunId &&
                    Lap.TimingMode == selected.TimingMode && Lap.Points.FirstOrDefault()?.SampleIndex == selected.Points.FirstOrDefault()?.SampleIndex)
                {
                    _cursor = Math.Clamp(selection.Cursor, 0, MaximumCursor);
                    _sectionStart = Math.Clamp(selection.Start, 0, MaximumCursor);
                    _sectionEnd = Math.Clamp(selection.End, _sectionStart, MaximumCursor);
                    Changed(nameof(Cursor));
                }
            }
            finally { _applying = false; }
            ReferenceStatus = comparisonRun is not null ? $"Run B: {comparisonRun.Name}." + (pin is not null ? " Pinned benchmark is not used while Run B is selected." : "")
                : result.usePin ? $"Pinned: {pinned!.Name} · lap {pinNumber}"
                : pin is null ? "Reference laps from this run. Choose Run B above to compare another saved run."
                : pinMissing || result.pinMatch is null ? "Pinned benchmark is unavailable. Reference laps are from this run; the pin is kept."
                : $"Pinned benchmark is not used: {result.pinMatch.Message} Reference laps are from this run; the pin is kept.";
            Status = result.a.Message;
            RefreshComparison();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!_disposed && !cancel.IsCancellationRequested) { ClearReview(); Status = "Lap review could not be prepared. Reopen the run to retry."; } }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancel)) { _loadCancellation = null; if (!_disposed) RefreshBusy(); }
            cancel.Dispose();
        }
    }
    private void RefreshComparison()
    {
        _comparison = _reverseComparison = null;
        SynchronizeRelativeReferenceCursor(Cursor);
        RefreshCursor(); Changed(nameof(Plot));
        _ = AnalyzeAsync(compare: true);
    }
    private void RefreshCursor()
    {
        if (Lap is not { Points.Length: > 0 }) { CursorDetails = null; Changed(nameof(CursorDetails)); Changed(nameof(CursorText)); return; }
        var point = Lap.Points[Math.Clamp(Cursor, 0, Lap.Points.Length - 1)]; var state = point.Sample.State;
        var delta = _comparison?.Points.ElementAtOrDefault(Cursor)?.DeltaSeconds;
        var gear = state.Gear switch { TransmissionGear.Reverse => "R", TransmissionGear.Neutral => "N", TransmissionGear.Unknown => "—", _ => ((int)state.Gear).ToString() };
        CursorDetails = new($"{point.LapSeconds:0.000} s · {point.DistanceMeters:0} m", Speed(state.GroundSpeedMetersPerSecond),
            $"{gear} / {Number(state.EngineRpm, "", "0")}", $"{state.Accelerator / 2.55:0}%", $"{state.Brake / 2.55:0}%",
            $"{Math.Clamp(state.Steering / 127d * 100, -100, 100):0}%", Number(state.LateralAccelerationMetersPerSecondSquared / 9.80665, " g", "0.00"),
            Number(state.LongitudinalAccelerationMetersPerSecondSquared / 9.80665, " g", "0.00"),
            delta is { } d ? d.ToString("+0.000;-0.000;0.000") + " s" : Reference is null ? "No reference" : "No match",
            WheelReadout("Temperature", state.TireTemperatureFahrenheit, Temperature), WheelReadout("Slip ratio", state.TireSlipRatio),
            WheelReadout("Slip angle (raw)", state.TireSlipAngle), WheelReadout("Suspension", state.NormalizedSuspensionTravel));
        Changed(nameof(CursorDetails)); Changed(nameof(CursorText));
    }
    private void ClearReview()
    {
        ScrubContextChanging?.Invoke();
        _applying = true;
        try
        {
            _lap = _reference = null; _comparison = _reverseComparison = null;
            _referenceRun = null; _referenceMapContacts = [];
            ClearTelemetryContacts();
            _cursor = _referenceCursor = _sectionStart = _sectionEnd = 0;
            Laps.Clear(); ReferenceLaps.Clear();
            _sectionStatistics = _referenceStatistics = null; _referenceSectionSeconds = null;
            Metrics.Clear(); Events.Clear(); CursorDetails = null; SectionText = "";
            foreach (var property in new[] { nameof(Lap), nameof(Reference), nameof(Cursor), nameof(MaximumCursor), nameof(HasLap),
                nameof(CanPin), nameof(CursorDetails), nameof(CursorText), nameof(SectionText), nameof(Plot) }) Changed(property);
            RaiseCommands();
        }
        finally { _applying = false; NotifyScrubState(); ScrubContextChanged?.Invoke(); }
    }

    private void RefreshSection() => _ = AnalyzeAsync(compare: false);

    private async Task AnalyzeAsync(bool compare)
    {
        if (_disposed || _applying) return;
        _analysisCancellation?.Cancel();
        var cancel = new CancellationTokenSource(); _analysisCancellation = cancel;
        var lap = Lap; var reference = Reference; var comparison = _comparison; var reverse = _reverseComparison;
        _sectionStatistics = _referenceStatistics = null; _referenceSectionSeconds = null;
        Metrics.Clear(); Events.Clear();
        SectionText = lap is { Points.Length: > 1 } ? "Preparing the selected section…" : "";
        Changed(nameof(SectionText)); Changed(nameof(Plot)); RefreshBusy();
        try
        {
            if (lap is not { Points.Length: > 1 }) return;
            _sectionStart = Math.Clamp(_sectionStart, 0, MaximumCursor); _sectionEnd = Math.Clamp(_sectionEnd, _sectionStart, MaximumCursor);
            var first = _sectionStart; var last = _sectionEnd;
            var cachedContacts = PreparedTelemetryContacts(lap);
            var referenceMarkers = _referenceRun?.Markers ?? [];
            var cachedReferenceContacts = _referenceMapContacts;
            var result = await Task.Run(() =>
            {
                var contacts = cachedContacts ?? LapReviewContacts.Find(lap, cancel.Token);
                var referenceContacts = compare && reference is not null
                    ? LapReviewContacts.Find(reference, cancel.Token).Concat(LapReviewContacts.FromMarkers(reference, referenceMarkers)).OrderBy(contact => contact.PointIndex).ToArray()
                    : cachedReferenceContacts;
                if (compare)
                {
                    comparison = reference is null ? null : LapReviewAnalysis.Compare(lap, reference, cancel.Token);
                    reverse = comparison?.CanCompare == true && reference is not null &&
                        new LapReviewPlotData(lap, reference, comparison, LapReviewChannel.Speed, default, 0, 0, 0).HasDistinctReference
                        ? LapReviewAnalysis.Compare(reference, lap, cancel.Token) : null;
                }
                var stats = LapReviewAnalysis.AnalyzeSection(lap, first, last, cancel.Token);
                LapSectionStatistics? other = null;
                double? referenceSeconds = null;
                var from = comparison?.Points.ElementAtOrDefault(first);
                var to = comparison?.Points.ElementAtOrDefault(last);
                if (comparison?.CanCompare == true && reference is not null && from?.ReferencePointIndex is { } a &&
                    to?.ReferencePointIndex is { } b && b >= a)
                {
                    other = LapReviewAnalysis.AnalyzeSection(reference, a, b, cancel.Token);
                    if (from.ReferenceLapSeconds is { } start && to.ReferenceLapSeconds is { } end && end >= start)
                        referenceSeconds = end - start;
                }
                return (comparison, reverse, stats, other, referenceSeconds, contacts, referenceContacts);
            }, cancel.Token);
            if (_disposed || cancel.IsCancellationRequested || !ReferenceEquals(_analysisCancellation, cancel) ||
                !ReferenceEquals(lap, Lap) || !ReferenceEquals(reference, Reference)) return;
            _comparison = result.comparison; _reverseComparison = result.reverse; _sectionStatistics = result.stats;
            _referenceStatistics = result.other; _referenceSectionSeconds = result.referenceSeconds;
            AdoptTelemetryContacts(lap, result.contacts);
            _referenceMapContacts = result.referenceContacts;
            FormatSection(); RefreshCursor(); Changed(nameof(Plot));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_disposed && !cancel.IsCancellationRequested)
            {
                _comparison = _reverseComparison = null;
                SectionText = "This section could not be prepared. Select the lap again to retry.";
                Changed(nameof(SectionText)); RefreshCursor(); Changed(nameof(Plot));
            }
        }
        finally
        {
            if (ReferenceEquals(_analysisCancellation, cancel)) { _analysisCancellation = null; if (!_disposed) RefreshBusy(); }
            cancel.Dispose();
        }
    }

    private void FormatSection()
    {
        Metrics.Clear(); Events.Clear();
        if (Lap is not { Points.Length: > 1 } || _sectionStatistics is not { } stats) return;
        var other = _referenceStatistics;
        void Add(string label, Func<LapSectionStatistics, string> value) => Metrics.Add(new(label, value(stats), other is null ? "—" : value(other)));
        Metrics.Add(new("Time between selected positions", Number(stats.DurationSeconds, " s", "0.000"), Number(_referenceSectionSeconds, " s", "0.000")));
        Add("Recorded sample span", s => Number(s.DurationSeconds, " s", "0.000"));
        Add("Recorded / section time", s => $"{s.RecordedSeconds:0.000} / {s.DurationSeconds:0.000} s");
        Add("Driven distance", s => Number(s.DistanceMeters, " m", "0"));
        Add("Entry / minimum / exit speed", s => $"{Speed(s.EntrySpeedMetersPerSecond)} / {Speed(s.MinimumSpeedMetersPerSecond)} / {Speed(s.ExitSpeedMetersPerSecond)}");
        Add("Average speed", s => Speed(s.AverageSpeedMetersPerSecond));
        Add("Full throttle / braking / coasting", s => $"{Number(s.FullThrottleFraction * 100, "%", "0")} / {Number(s.BrakingFraction * 100, "%", "0")} / {Number(s.CoastingFraction * 100, "%", "0")}");
        Add("Peak brake input", s => Number(s.BrakePercent.Maximum, "%", "0"));
        Add("Steering range (raw input)", s => $"{s.SteeringRaw.Minimum:0} to {s.SteeringRaw.Maximum:0}");
        Add("Lateral G range", s => $"{s.LateralG.Minimum:0.00} to {s.LateralG.Maximum:0.00}");
        Add("Longitudinal G range", s => $"{s.LongitudinalG.Minimum:0.00} to {s.LongitudinalG.Maximum:0.00}");
        Add("Peak combined G", s => Number(s.CombinedG.Maximum, " g", "0.00"));
        Add("RPM range", s => $"{s.EngineRpm.Minimum:0} to {s.EngineRpm.Maximum:0}");
        Add("Shifts", s => s.Events.Count(e => e.Kind is LapReviewEventKind.Upshift or LapReviewEventKind.Downshift).ToString());
        Add("Mean tire temperatures · FL / FR / RL / RR", s => Summaries(s.TireTemperatureFahrenheit, Temperature));
        Add("Mean slip ratio · FL / FR / RL / RR", s => Summaries(s.TireSlipRatio, n => Number(n, "", "0.000")));
        Add("Mean slip angle (raw) · FL / FR / RL / RR", s => Summaries(s.TireSlipAngle, n => Number(n, "", "0.000")));
        Add("Mean suspension travel · FL / FR / RL / RR", s => Summaries(s.NormalizedSuspensionTravel, n => Number(n, "", "0.000")));
        foreach (var item in stats.Events.Take(100)) Events.Add($"{EventLabel(item.Kind)} · {item.DistanceMeters:0} m · {EventSeconds(Lap, item):0.000} s" + (item.ToGear is { } gear ? $" · gear {(int)gear}" : ""));
        if (stats.Events.Length > 100) Events.Add($"Showing the first 100 of {stats.Events.Length} input and shift events. Select a shorter section to inspect later events.");
        SectionText = $"{Lap.Points[_sectionStart].DistanceMeters:0}–{Lap.Points[_sectionEnd].DistanceMeters:0} m · {stats.QualityNote}\n" + (_comparison?.Message ?? "Select a reference lap for comparison.") +
            (other is null ? "" : "\nReference section time uses interpolated matching positions. Other reference statistics use nearby recorded samples at or before those positions.") +
            "\nTiming validity is not an official clean-lap rating. Brake and steering are game inputs, not pressure or steering-wheel angle.";
        Changed(nameof(SectionText));
    }

    private static double EventSeconds(LapReviewLap lap, LapReviewEvent item)
    {
        var point = lap.Points[item.PointIndex];
        if (item.PointIndex == 0 || item.Kind is LapReviewEventKind.Upshift or LapReviewEventKind.Downshift) return point.LapSeconds;
        var previous = lap.Points[item.PointIndex - 1];
        var before = item.Kind == LapReviewEventKind.ThrottlePickup ? previous.Sample.State.Accelerator : previous.Sample.State.Brake;
        var after = item.Kind == LapReviewEventKind.ThrottlePickup ? point.Sample.State.Accelerator : point.Sample.State.Brake;
        var fraction = after == before ? 1 : Math.Clamp((LapReviewAnalysis.InputActiveMinimum - before) / (double)(after - before), 0, 1);
        return previous.LapSeconds + (point.LapSeconds - previous.LapSeconds) * fraction;
    }
    private static string EventLabel(LapReviewEventKind kind) => kind switch { LapReviewEventKind.BrakeStart => "Brake applied", LapReviewEventKind.BrakeEnd => "Brake released", LapReviewEventKind.ThrottlePickup => "Throttle picked up", LapReviewEventKind.Upshift => "Upshift", _ => "Downshift" };
    private static bool UsableBenchmark(LapReviewLap? lap) => lap is { IsComplete: true, Points.Length: > 1 } &&
        (lap.Quality & (LapReviewQuality.TelemetryGap | LapReviewQuality.Rewind | LapReviewQuality.Discontinuity |
            LapReviewQuality.MissingPosition | LapReviewQuality.MissingTiming)) == 0;
    private string Speed(double? value) => Number(value * RunPresentation.SpeedFactor(_settings.SpeedUnit), " " + RunPresentation.SpeedLabel(_settings.SpeedUnit));
    private string Temperature(double? value) => _settings.TireTemperatureUnit == TireTemperatureUnit.Celsius ? Number((value - 32) * 5 / 9, " °C") : Number(value, " °F");
    private static string Number(double? value, string suffix = "", string format = "0.0") => RunPresentation.Number(value, suffix, format);
    private static LapReviewWheelReadout WheelReadout(string label, WheelValues wheels, Func<double?, string>? format = null)
    {
        format ??= n => Number(n, "", "0.000");
        return new(label, format(wheels.FrontLeft), format(wheels.FrontRight), format(wheels.RearLeft), format(wheels.RearRight));
    }
    private static string Summaries(LapWheelSummaries wheels, Func<double?, string> format) => string.Join(" / ", new[] { wheels.FrontLeft, wheels.FrontRight, wheels.RearLeft, wheels.RearRight }.Select(n => format(n.Mean)));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
    private void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new(name));
        if (name == nameof(Plot)) RefreshMapDetails();
    }
    public void Dispose() { _disposed = true; _loadCancellation?.Cancel(); _analysisCancellation?.Cancel(); RaiseCommands(); }
}
