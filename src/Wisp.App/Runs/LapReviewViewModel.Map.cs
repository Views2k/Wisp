using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed partial class LapReviewViewModel
{
    private bool _is3D, _showContacts = true;
    private LapReviewLap? _contactLap;
    private RunMarker[]? _contactMarkers;
    private LapReviewContact[] _contacts = [];
    private LapReviewPlotData? _legendData;
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
    public double MapHeight
    {
        get => _settings.RunWorkspace.LapMapHeight > 0 ? _settings.RunWorkspace.LapMapHeight : double.NaN;
        set
        {
            var height = double.IsFinite(value) ? Math.Clamp(value, 70, 1400) : 0;
            if (_settings.RunWorkspace.LapMapHeight == height) return;
            _settings.RunWorkspace.LapMapHeight = height;
            Changed(nameof(MapHeight));
            _save();
        }
    }

    private IReadOnlyList<LapReviewContact> VisibleContacts
    {
        get
        {
            EnsureContacts();
            return ShowContacts ? _contacts : Array.Empty<LapReviewContact>();
        }
    }

    private void EnsureContacts()
    {
        var markers = _run?.Markers;
        if (ReferenceEquals(_contactLap, Lap) && ReferenceEquals(_contactMarkers, markers)) return;
        _contactLap = Lap; _contactMarkers = markers;
        _contacts = Lap is null ? [] : CachedTelemetryContacts(Lap).Concat(
            LapReviewContacts.FromMarkers(Lap, markers ?? [])).OrderBy(c => c.PointIndex).ToArray();
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
            var elevationFactor = LapReviewPlot.ElevationFactor(_settings.SpeedUnit);
            var elevationUnit = _settings.SpeedUnit == SpeedUnit.MilesPerHour ? "ft" : "m";
            ElevationSummary = data.Lap is { Points.Length: > 0 } ?
                $"{(IsMapComparison ? "Both laps’ recorded elevation" : "Recorded elevation")}: {positions.Min(p => p.Position.Y) * elevationFactor:0.0}–{positions.Max(p => p.Position.Y) * elevationFactor:0.0} {elevationUnit} · true scale" : "";
            foreach (var name in new[] { nameof(LegendMinimum), nameof(LegendMaximum), nameof(LegendTitle), nameof(ElevationSummary), nameof(MapTitle), nameof(MapATitle), nameof(MapBTitle) }) Changed(name);
        }
        Changed(nameof(MapCursorValue)); Changed(nameof(MapBCursorValue)); Changed(nameof(MapPlotA)); Changed(nameof(MapPlotB));
        Changed(nameof(ComparisonMapPlot));
        Changed(nameof(IsMapComparison));
        Changed(nameof(CanUseSharedSpace));
        Changed(nameof(HasMapComparison)); Changed(nameof(MapComparisonHint)); Changed(nameof(HasMapReference));
        NotifyScrubState();
    }
}
