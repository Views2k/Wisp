using System.Collections.ObjectModel;
using System.Windows.Input;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed partial class RunsViewModel
{
    public string WorkspaceReadout => IsBusy ? Status : IsPreparingCharts ? "Preparing graphs…" : IntervalLabel;
    private RunWorkspacePresetOption _selectedWorkspacePreset = RunWorkspaceCatalog.Presets[0];
    private RunWorkspaceComparisonOption _workspaceComparisonMode = RunWorkspaceCatalog.ComparisonModes[0];
    private readonly Dictionary<string, RunWorkspacePreparedModule> _workspacePrepared = new(StringComparer.Ordinal);
    private RunWorkspaceRequest? _workspacePreparedRequest;
    private bool _modularWorkspaceEnabled;
    private string _workspaceStatus = "Changes to this layout are saved automatically.";
    public ObservableCollection<RunWorkspaceModule> WorkspacePanels { get; } = [];
    public ObservableCollection<RunWorkspaceModule> AvailableWorkspaceModules { get; } = [];
    public RunWorkspacePresetOption[] WorkspacePresets { get; } = RunWorkspaceCatalog.Presets.ToArray();
    public RunWorkspaceComparisonOption[] WorkspaceComparisonModes { get; } = RunWorkspaceCatalog.ComparisonModes.ToArray();
    public ICommand SaveWorkspaceCommand { get; private set; } = null!;
    public ICommand ResetWorkspaceCommand { get; private set; } = null!;
    public bool WorkspaceCanEdit => !_disposed && !RecordingActive && !IsBusy;
    public bool WorkspaceHasPanels => WorkspacePanels.Count > 0;
    public bool WorkspaceHasRpmPlots => WorkspacePanels.Any(module => module.Id == "power-rpm");
    public bool WorkspaceIsSideBySide => HasComparison && WorkspaceComparisonMode.Id == RunWorkspaceComparisonMode.SideBySide;
    internal bool UsesModularWorkspace => _modularWorkspaceEnabled;
    public bool WorkspaceHasPreparedCharts => WorkspaceHasPanels && WorkspaceCacheIsCurrent() && WorkspacePanels.All(module => _workspacePrepared.ContainsKey(module.Id));
    public string WorkspaceStatus { get => _workspaceStatus; private set => Set(ref _workspaceStatus, value); }
    public bool HasCustomWorkspaceLayout => !AvailableWorkspaceModules.Select(module => (module.Id, module.Width, module.IsVisible))
        .SequenceEqual(RunWorkspaceCatalog.CreatePanels(SelectedWorkspacePreset.Id).Select(module => (module.Id, module.Width, module.IsVisible)));

    public RunWorkspacePresetOption SelectedWorkspacePreset
    {
        get => _selectedWorkspacePreset;
        set
        {
            if (!WorkspaceCanEdit || value is null || !WorkspacePresets.Contains(value) || value == _selectedWorkspacePreset) return;
            ApplyWorkspacePreset(value);
        }
    }
    public RunWorkspaceComparisonOption WorkspaceComparisonMode
    {
        get => _workspaceComparisonMode;
        set
        {
            if (!WorkspaceCanEdit || value is null || !WorkspaceComparisonModes.Contains(value) || !Set(ref _workspaceComparisonMode, value)) return;
            foreach (var module in WorkspacePanels) ApplyWorkspaceModulePlots(module);
            OnChanged(nameof(WorkspaceIsSideBySide));
            PersistWorkspace();
        }
    }

    private void InitializeWorkspace()
    {
        _settings.RunWorkspace ??= new RunWorkspaceSettings();
        _settings.RunWorkspace.Normalize();
        _selectedWorkspacePreset = WorkspacePresets.First(preset => preset.Id == _settings.RunWorkspace.Preset);
        _workspaceComparisonMode = WorkspaceComparisonModes.First(mode => mode.Id == _settings.RunWorkspace.ComparisonMode);
        LoadWorkspacePanels(_settings.RunWorkspace.Panels);
        SaveWorkspaceCommand = Command(() => { PersistWorkspace(); WorkspaceStatus = "Layout saved."; return Task.CompletedTask; }, () => WorkspaceCanEdit);
        ResetWorkspaceCommand = Command(() => { ApplyWorkspacePreset(SelectedWorkspacePreset); return Task.CompletedTask; }, () => WorkspaceCanEdit);
    }

    internal void SetModularWorkspaceEnabled(bool enabled)
    {
        if (_modularWorkspaceEnabled == enabled) return;
        _modularWorkspaceEnabled = enabled;
        if (!enabled) ClearWorkspaceCharts();
        else if (IsGraphWorkspaceOpen) RequestCharts();
    }

    private void LoadWorkspacePanels(IEnumerable<RunWorkspacePanelSettings> settings)
    {
        AvailableWorkspaceModules.Clear();
        foreach (var panel in settings)
        {
            if (RunWorkspaceCatalog.Find(panel.Id) is not { } definition) continue;
            AvailableWorkspaceModules.Add(new(definition, panel, () => WorkspaceCanEdit, WorkspaceModuleChanged, MoveWorkspaceModule, CanMoveWorkspaceModule,
                () => Fail("The graph layout could not be changed. Try again.")));
        }
        RefreshWorkspaceOrder();
        foreach (var module in WorkspacePanels) ApplyWorkspaceModulePlots(module);
    }

    private void ApplyWorkspacePreset(RunWorkspacePresetOption preset)
    {
        _selectedWorkspacePreset = preset;
        LoadWorkspacePanels(RunWorkspaceCatalog.CreatePanels(preset.Id));
        OnChanged(nameof(SelectedWorkspacePreset));
        PersistWorkspace();
        if (IsGraphWorkspaceOpen) RequestCharts();
    }

    private void WorkspaceModuleChanged(RunWorkspaceModule module, bool visibilityChanged)
    {
        if (visibilityChanged)
        {
            RefreshWorkspaceOrder();
            if (module.IsVisible) ApplyWorkspaceModulePlots(module);
            else module.Plots.Clear();
        }
        PersistWorkspace();
        // Width, order and comparison arrangement only rearrange already-prepared data.
        if (visibilityChanged && module.IsVisible && IsGraphWorkspaceOpen && (!WorkspaceCacheIsCurrent() || !_workspacePrepared.ContainsKey(module.Id))) RequestCharts();
    }

    private bool CanMoveWorkspaceModule(RunWorkspaceModule module, int delta)
    {
        var index = WorkspacePanels.IndexOf(module);
        return index >= 0 && index + delta >= 0 && index + delta < WorkspacePanels.Count;
    }
    private void MoveWorkspaceModule(RunWorkspaceModule module, int delta)
    {
        if (!WorkspaceCanEdit || !CanMoveWorkspaceModule(module, delta)) return;
        var next = WorkspacePanels[WorkspacePanels.IndexOf(module) + delta];
        AvailableWorkspaceModules.Move(AvailableWorkspaceModules.IndexOf(module), AvailableWorkspaceModules.IndexOf(next));
        RefreshWorkspaceOrder();
        PersistWorkspace();
    }

    private void RefreshWorkspaceOrder()
    {
        WorkspacePanels.Clear();
        foreach (var module in AvailableWorkspaceModules.Where(module => module.IsVisible)) WorkspacePanels.Add(module);
        foreach (var name in new[] { nameof(WorkspaceHasPanels), nameof(WorkspaceHasRpmPlots), nameof(HasCustomWorkspaceLayout), nameof(WorkspaceHasPreparedCharts), nameof(CanExportImage) }) OnChanged(name);
        NotifyWorkspaceAvailability();
    }

    private void PersistWorkspace()
    {
        var settings = new RunWorkspaceSettings
        {
            Preset = SelectedWorkspacePreset.Id,
            ComparisonMode = WorkspaceComparisonMode.Id,
            Panels = AvailableWorkspaceModules.Select(module => module.Snapshot()).ToList()
        };
        settings.Normalize();
        _settings.RunWorkspace = settings;
        WorkspaceStatus = "Layout saved automatically.";
        OnChanged(nameof(HasCustomWorkspaceLayout));
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyWorkspaceAvailability()
    {
        OnChanged(nameof(WorkspaceCanEdit));
        OnChanged(nameof(WorkspaceIsSideBySide));
        foreach (var module in AvailableWorkspaceModules) module.NotifyAvailability();
        if (SaveWorkspaceCommand is RunUiCommand save) save.RaiseCanExecuteChanged();
        if (ResetWorkspaceCommand is RunUiCommand reset) reset.RaiseCanExecuteChanged();
    }

    // Evidence links reveal their charts in the current layout instead of replacing it.
    private void EnsureWorkspaceGraph(RunChartGroup group, RunPlotMode mode)
    {
        if (!UsesModularWorkspace || !WorkspaceCanEdit) return;
        var ids = RunWorkspacePreparation.EvidenceModules(group, mode);
        foreach (var id in ids)
            AvailableWorkspaceModules.FirstOrDefault(module => module.Id == id)?.SetVisible(true);
        for (var index = ids.Length - 1; index >= 0; index--)
            if (AvailableWorkspaceModules.FirstOrDefault(module => module.Id == ids[index]) is { } module)
                AvailableWorkspaceModules.Move(AvailableWorkspaceModules.IndexOf(module), 0);
        RefreshWorkspaceOrder();
        foreach (var module in WorkspacePanels) ApplyWorkspaceModulePlots(module);
        PersistWorkspace();
    }

    private RunWorkspaceRequest? CreateWorkspaceRequest()
    {
        if (!UsesModularWorkspace || _runA is null || !IsGraphWorkspaceOpen) return null;
        return new(_runA, _runB, WorkspacePanels.Select(module => module.Id).ToArray(),
            _settings.SpeedUnit, _settings.TireTemperatureUnit, _settings.TorqueUnit, _settings.BoostPressureUnit,
            _offsetA, _offsetB, _matchedA, _matchedB, ReportBounds(false), ReportBounds(true), FullThrottleOnly, GearFilter.Gear);
    }

    private void ApplyWorkspacePrepared(RunWorkspacePrepared prepared)
    {
        _workspacePrepared.Clear();
        _workspacePreparedRequest = prepared.Request;
        foreach (var module in prepared.Modules) _workspacePrepared[module.Id] = module;
        foreach (var module in AvailableWorkspaceModules) ApplyWorkspaceModulePlots(module);
        OnChanged(nameof(WorkspaceIsSideBySide));
        OnChanged(nameof(WorkspaceHasPreparedCharts));
        OnChanged(nameof(CanExportImage));
    }

    private void ApplyWorkspaceModulePlots(RunWorkspaceModule module)
    {
        if (!module.IsVisible || !WorkspaceCacheIsCurrent() || !_workspacePrepared.TryGetValue(module.Id, out var prepared))
        { module.Plots.Clear(); return; }
        var plots = RunWorkspacePreparation.CreatePlots(prepared, HasComparison, WorkspaceComparisonMode.Id);
        for (var index = 0; index < plots.Length; index++)
        {
            var plot = plots[index];
            if (index >= module.Plots.Count) module.Plots.Add(plot);
            else if (module.Plots[index].Label == plot.Label && module.Plots[index].SourceGroup == plot.SourceGroup)
                module.Plots[index].UpdatePanels(plot);
            else module.Plots[index] = plot;
        }
        while (module.Plots.Count > plots.Length) module.Plots.RemoveAt(module.Plots.Count - 1);
    }

    private void ClearWorkspaceCharts()
    {
        _workspacePrepared.Clear();
        _workspacePreparedRequest = null;
        foreach (var module in AvailableWorkspaceModules) module.Plots.Clear();
        OnChanged(nameof(WorkspaceIsSideBySide));
        OnChanged(nameof(WorkspaceHasPreparedCharts));
        OnChanged(nameof(CanExportImage));
    }

    private bool WorkspaceCacheIsCurrent() => _workspacePreparedRequest is { } previous &&
        SameChartSource(previous.RunA, _runA) && SameChartSource(previous.RunB, _runB) &&
        previous.Speed == _settings.SpeedUnit && previous.Temperature == _settings.TireTemperatureUnit &&
        previous.Torque == _settings.TorqueUnit && previous.Boost == _settings.BoostPressureUnit &&
        previous.OffsetA == _offsetA && previous.OffsetB == _offsetB && previous.TimeBoundsA == _matchedA && previous.TimeBoundsB == _matchedB &&
        previous.AlternativeBoundsA == ReportBounds(false) && previous.AlternativeBoundsB == ReportBounds(true) &&
        previous.FullThrottleOnly == FullThrottleOnly && previous.Gear == GearFilter.Gear;

    // Saving a name or notes replaces the record without changing any plotted samples.
    internal static bool SameChartSource(RecordedRun? prepared, RecordedRun? current) =>
        prepared is null ? current is null : current is not null && prepared.Id == current.Id && ReferenceEquals(prepared.Samples, current.Samples);

    private (RunPlotPanel[] Time, RunAlternativePlotPanel[] Alternative) CreateWorkspaceExportCharts()
    {
        if (!WorkspaceHasPreparedCharts) return ([], []);
        var modules = WorkspacePanels.Select(module => _workspacePrepared[module.Id]).ToArray();
        return (modules.SelectMany(module => module.TimePanels).ToArray(), modules.SelectMany(module => module.AlternativePanels).ToArray());
    }
}
