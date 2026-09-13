using System.Collections.ObjectModel;

namespace Wisp.App.Runs;

public sealed partial class RunsViewModel
{
    private RunStatistic[] _allStatistics = [];
    private string _selectedStatisticGroup = RunStatisticsPresentation.Overview;

    public ObservableCollection<RunStatistic> Statistics { get; } = [];
    public string[] StatisticGroups { get; } = [.. RunStatisticsPresentation.Groups];
    public RunStatisticsView[] StatisticsViews { get; } = Enum.GetValues<RunStatisticsView>();
    public string StatisticsDifferenceLabel => "Difference (B − A)";
    public string StatisticsComparisonNote => "Differences are B minus A. A larger value is not automatically better; compare similar routes and conditions.";

    public string SelectedStatisticGroup
    {
        get => _selectedStatisticGroup;
        set
        {
            if (_disposed || !StatisticGroups.Contains(value) || !Set(ref _selectedStatisticGroup, value)) return;
            FilterStatistics();
        }
    }

    public RunStatisticsView StatisticsView
    {
        get => _settings.RunStatisticsView;
        set
        {
            if (_disposed || !Enum.IsDefined(value) || value == _settings.RunStatisticsView) return;
            _settings.RunStatisticsView = value;
            OnChanged();
            OnChanged(nameof(IsStatisticsCards));
            OnChanged(nameof(IsStatisticsTable));
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsStatisticsCards => StatisticsView == RunStatisticsView.Cards;
    public bool IsStatisticsTable => StatisticsView == RunStatisticsView.Table;

    internal void RefreshStatistics()
    {
        var prepared = _reportA is null ? [] : RunStatisticsPresentation.Create(
            _reportA.Statistics, _reportB?.Statistics, _settings.SpeedUnit, _settings.TorqueUnit,
            _settings.TireTemperatureUnit, _settings.BoostPressureUnit);
        var existing = _allStatistics.ToDictionary(row => row.Key);
        _allStatistics = prepared.Select(next =>
        {
            if (!existing.TryGetValue(next.Key, out var current)) return next;
            current.UpdateValues(next);
            return current;
        }).ToArray();
        FilterStatistics();
    }

    internal void ClearStatistics()
    {
        _allStatistics = [];
        Statistics.Clear();
    }

    private void FilterStatistics()
    {
        SynchronizeItems(Statistics, _allStatistics.Where(item =>
                     _selectedStatisticGroup == RunStatisticsPresentation.AllStatistics ||
                     (_selectedStatisticGroup == RunStatisticsPresentation.Overview ? item.IsKey : item.Group == _selectedStatisticGroup)));
    }
}
