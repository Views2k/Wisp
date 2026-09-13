using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Wisp.App.Runs;

public sealed class RunWorkspaceModule : INotifyPropertyChanged
{
    private readonly Func<bool> _canEdit;
    private readonly Action<RunWorkspaceModule, bool> _changed;
    private bool _visible;
    private RunWorkspaceWidth _width;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public ObservableCollection<RunWorkspacePlot> Plots { get; } = [];
    public RunWorkspaceWidth[] WidthOptions { get; } = Enum.GetValues<RunWorkspaceWidth>();
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand HideCommand { get; }

    internal RunWorkspaceModule(RunWorkspaceDefinition definition, RunWorkspacePanelSettings settings,
        Func<bool> canEdit, Action<RunWorkspaceModule, bool> changed, Action<RunWorkspaceModule, int> move,
        Func<RunWorkspaceModule, int, bool> canMove, Action failed)
    {
        Id = definition.Id; Title = definition.Title; Description = definition.Description;
        _visible = settings.IsVisible; _width = settings.Width; _canEdit = canEdit; _changed = changed;
        MoveUpCommand = Command(() => move(this, -1), () => canEdit() && canMove(this, -1), failed);
        MoveDownCommand = Command(() => move(this, 1), () => canEdit() && canMove(this, 1), failed);
        HideCommand = Command(() => IsVisible = false, () => canEdit() && IsVisible, failed);
    }

    public bool IsVisible
    {
        get => _visible;
        set
        {
            if (value == _visible) return;
            if (!_canEdit()) { Changed(); return; }
            _visible = value; Changed(); _changed(this, true);
        }
    }
    public RunWorkspaceWidth Width
    {
        get => _width;
        set
        {
            if (value == _width) return;
            if (!_canEdit() || !Enum.IsDefined(value)) { Changed(); return; }
            _width = value; Changed(); _changed(this, false);
        }
    }

    internal void SetVisible(bool value) { if (_visible != value) { _visible = value; Changed(nameof(IsVisible)); } }
    internal void NotifyAvailability()
    {
        foreach (var command in new[] { MoveUpCommand, MoveDownCommand, HideCommand })
            ((RunUiCommand)command).RaiseCanExecuteChanged();
    }
    internal RunWorkspacePanelSettings Snapshot() => new() { Id = Id, Width = Width, IsVisible = IsVisible };
    private static RunUiCommand Command(Action action, Func<bool> canExecute, Action failed) =>
        new(() => { action(); return Task.CompletedTask; }, canExecute, failed);
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
