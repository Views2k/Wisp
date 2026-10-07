using System.Windows.Input;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed record LapContactChoice(int PointIndex, string Label);

public sealed partial class LapReviewViewModel
{
    private bool _is3D, _showContacts = true, _savingContact, _contactClosing;
    private Task _contactSaveTask = Task.CompletedTask;
    private LapReviewLap? _contactLap;
    private RunMarker[]? _contactMarkers;
    private LapReviewContact[] _contacts = [];
    private LapReviewPlotData? _legendData;
    private LapContactChoice? _selectedContact;
    private string _mapStatus = "";
    public bool Is3D { get => _is3D; set { if (_is3D != value) ChangeScrubContext(() => { Set(ref _is3D, value); Changed(nameof(Is2D)); RefreshMapMode(); }); } }
    public bool Is2D { get => !Is3D; set { if (value) Is3D = false; } }
    public bool ShowContacts { get => _showContacts; set { if (Set(ref _showContacts, value)) Changed(nameof(Plot)); } }
    public string MapStatus { get => _mapStatus; set => Set(ref _mapStatus, value); }
    public string LegendMinimum { get; private set; } = "—";
    public string LegendMaximum { get; private set; } = "—";
    public string LegendTitle { get; private set; } = "Speed";
    public string MapTitle => Lap is { } lap ? $"{lap.RunName} · {lap.Label}" : "Recorded lap";
    public string MapCursorValue => Lap is { Points.Length: > 0 } lap ?
        $"{Channel.Label}: {RunPresentation.Number(LapReviewPlot.Value(lap.Points[Cursor], Plot, Cursor), " " + LapReviewPlot.Unit(Plot))}" : "";
    public string ElevationSummary { get; private set; } = "";
    public bool HasMapReference => !IsMapComparison && Plot.HasDistinctReference;
    public string ContactSummary => _contacts.Length == 0 ? "No contact markers" : $"{_contacts.Length} contact marker{(_contacts.Length == 1 ? "" : "s")}";
    public IReadOnlyList<LapContactChoice> ContactChoices { get; private set; } = [];
    public LapContactChoice? SelectedContact
    {
        get => _selectedContact;
        set { if (Set(ref _selectedContact, value) && value is not null) Cursor = value.PointIndex; }
    }
    public bool CanMarkContact => HasLap && !_disposed && !_savingContact && !_contactClosing && !IsBusy;
    public bool CanRemoveContact => CanMarkContact && _contacts.Any(c => c.PointIndex == Cursor && c.Kind == LapReviewContactKind.UserMarkedContact);
    public ICommand MarkContactCommand { get; private set; } = null!;
    public ICommand RemoveContactCommand { get; private set; } = null!;
    public event Action<Guid, RunMarker[]>? ContactMarkersChanged;

    private IReadOnlyList<LapReviewContact> VisibleContacts
    {
        get
        {
            EnsureContacts();
            return ShowContacts ? _contacts : Array.Empty<LapReviewContact>();
        }
    }

    private void InitializeMap()
    {
        MarkContactCommand = new RunUiCommand(() => SetContactAsync(true), () => CanMarkContact,
            () => MapStatus = "The contact marker could not be saved. Try again.");
        RemoveContactCommand = new RunUiCommand(() => SetContactAsync(false), () => CanRemoveContact,
            () => MapStatus = "The contact marker could not be removed. Try again.");
        _commands.Add((RunUiCommand)MarkContactCommand); _commands.Add((RunUiCommand)RemoveContactCommand);
    }

    private void EnsureContacts()
    {
        var markers = _run?.Markers;
        if (ReferenceEquals(_contactLap, Lap) && ReferenceEquals(_contactMarkers, markers)) return;
        _contactLap = Lap; _contactMarkers = markers;
        _contacts = Lap is null ? [] : CachedTelemetryContacts(Lap).Concat(
            LapReviewContacts.FromMarkers(Lap, markers ?? [])).OrderBy(c => c.PointIndex).ToArray();
        ContactChoices = _contacts.Select(c => new LapContactChoice(c.PointIndex,
            $"{c.Label} · {c.DistanceMeters:0} m · {Lap!.Points[c.PointIndex].LapSeconds:0.000} s")).ToArray();
        _selectedContact = null;
        Changed(nameof(ContactChoices)); Changed(nameof(SelectedContact)); Changed(nameof(ContactSummary));
    }

    private Task SetContactAsync(bool marked)
    {
        if (!CanMarkContact || Lap is not { } lap || _run is not { } run) return Task.CompletedTask;
        var seconds = marked ? lap.Points[Cursor].RunSeconds :
            _contacts.First(c => c.PointIndex == Cursor && c.Kind == LapReviewContactKind.UserMarkedContact).RunSeconds;
        _savingContact = true;
        _contactSaveTask = SaveContactCoreAsync(run, seconds, marked);
        NotifyContactAvailability();
        return _contactSaveTask;
    }

    private async Task SaveContactCoreAsync(RecordedRun run, double seconds, bool marked)
    {
        // Establish the owning task before notifications can reenter close preparation.
        await Task.Yield();
        MapStatus = "Saving contact marker…";
        try
        {
            var markers = await _store.SetContactMarkerAsync(run.Id, seconds, marked);
            if (_disposed) return;
            if (_run?.Id == run.Id) _run = _run with { Markers = markers };
            ContactMarkersChanged?.Invoke(run.Id, markers);
            MapStatus = marked ? "Contact marker saved with the run." : "Contact marker removed.";
            Changed(nameof(Plot));
        }
        finally { _savingContact = false; if (!_disposed) NotifyContactAvailability(); }
    }

    internal Task PrepareToCloseContactsAsync()
    {
        _contactClosing = true;
        NotifyContactAvailability();
        // An earlier reported failure is not a pending edit. A failure during
        // this close attempt propagates so the normal shutdown guard keeps Wisp open.
        return _savingContact ? _contactSaveTask : Task.CompletedTask;
    }

    internal void CancelContactClosePreparation()
    {
        _contactClosing = false;
        if (!_disposed) NotifyContactAvailability();
    }

    private void NotifyContactAvailability()
    {
        RaiseCommands(); Changed(nameof(CanMarkContact)); Changed(nameof(CanRemoveContact));
    }

    private void RefreshMapDetails()
    {
        var data = Plot;
        EnsureContacts();
        if (_legendData is not { } old || !ReferenceEquals(old.Lap, data.Lap) || !ReferenceEquals(old.Reference, data.Reference) ||
            !ReferenceEquals(old.Comparison, data.Comparison) ||
            old.Channel != data.Channel || old.Wheel != data.Wheel || old.SpeedUnit != data.SpeedUnit ||
            old.TemperatureUnit != data.TemperatureUnit || old.TorqueUnit != data.TorqueUnit)
        {
            _legendData = data;
            var range = SharedMapRange(data);
            _sharedMapRange = range;
            LegendTitle = Channel.Label + (IsWheelChannel ? " · " + Wheels[SelectedWheel] : "");
            LegendMinimum = range.HasValues ? $"{range.Minimum:0.##} {LapReviewPlot.Unit(data)}" : "Unavailable";
            LegendMaximum = range.HasValues ? $"{range.Maximum:0.##} {LapReviewPlot.Unit(data)}" : "Unavailable";
            var positions = IsMapComparison && Reference is { } reference
                ? data.Lap!.Points.Concat(reference.Points) : data.Lap?.Points ?? [];
            ElevationSummary = data.Lap is { Points.Length: > 0 } ?
                $"{(IsMapComparison ? "Both laps’ recorded elevation" : "Recorded elevation")}: {positions.Min(p => p.Position.Y):0.0}–{positions.Max(p => p.Position.Y):0.0} m · true scale" : "";
            foreach (var name in new[] { nameof(LegendMinimum), nameof(LegendMaximum), nameof(LegendTitle), nameof(ElevationSummary), nameof(MapTitle), nameof(MapATitle), nameof(MapBTitle) }) Changed(name);
        }
        Changed(nameof(MapCursorValue)); Changed(nameof(MapBCursorValue)); Changed(nameof(MapPlotA)); Changed(nameof(MapPlotB));
        Changed(nameof(ComparisonMapPlot));
        Changed(nameof(IsMapComparison));
        Changed(nameof(CanUseSharedSpace));
        Changed(nameof(HasMapComparison)); Changed(nameof(MapComparisonHint)); Changed(nameof(HasMapReference)); Changed(nameof(CanMarkContact)); Changed(nameof(CanRemoveContact));
        NotifyScrubState();
        if (MarkContactCommand is RunUiCommand mark) mark.RaiseCanExecuteChanged();
        if (RemoveContactCommand is RunUiCommand remove) remove.RaiseCanExecuteChanged();
    }
}
