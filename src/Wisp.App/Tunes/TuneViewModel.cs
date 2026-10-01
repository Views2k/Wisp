using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Wisp.Core.Tunes;

namespace Wisp.App.Tunes;

public sealed class TuneViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly TuneStore _store;
    private readonly Func<CancellationToken, Task<TuneCaptureResult>> _capture;
    private readonly Func<TuneSnapshot, bool> _isCurrent;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<TuneCommand> _commands = [];
    private SavedTune[] _saved = [];
    private SavedTune[] _comparisonExtras = [];
    private CancellationTokenSource? _refreshCancellation;
    private TuneSnapshot? _current, _opened, _dialogSnapshot;
    private SavedTune? _selected, _a, _b;
    private PendingTuneView? _pendingView;
    private TuneWorkspace _workspace;
    private TuneCategoryOption _category = new(TuneCategory.Tires, "Tires");
    private TuneSortOption _sort = new(TuneSort.Newest, "Newest first");
    private bool _visible, _disposed, _busy, _refreshing, _currentValid, _initialized, _dialogOpen, _openedFromRun;
    private long _refreshRevision;
    private Guid? _editingId;
    private string _status = "Start Forza to read the current tune.", _error = "", _openedName = "", _openedDescription = "";
    private string _dialogName = "", _dialogDescription = "", _dialogError = "";

    public TuneViewModel(TuneStore store, Func<CancellationToken, Task<TuneCaptureResult>> capture,
        Func<TuneSnapshot, bool> isCurrent, Dispatcher dispatcher)
    {
        _store = store; _capture = capture; _isCurrent = isCurrent; _dispatcher = dispatcher;
        RefreshCommand = Command(RefreshAsync, () => IsCurrentMode && !_busy && !_refreshing && !_dialogOpen);
        SaveCommand = Command(() => { BeginSave(); return Task.CompletedTask; }, () => CanSave);
        LoadCommand = Command(LoadSelectedAsync, () => _selected is not null && !_busy && !_dialogOpen);
        EditCommand = Command(() => { BeginEdit(); return Task.CompletedTask; }, () => _selected is not null && !_busy && !_dialogOpen);
        ConfirmDialogCommand = Command(ConfirmDialogAsync, () => _dialogOpen && !_busy);
        CancelDialogCommand = Command(() => { CancelDialog(); return Task.CompletedTask; }, () => _dialogOpen && !_busy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SavedTune> Library { get; } = [];
    public ObservableCollection<SavedTune> ComparisonChoices { get; } = [];
    public ObservableCollection<TuneDisplayRow> Rows { get; } = [];
    public IReadOnlyList<TuneCategoryOption> Categories { get; } = Enum.GetValues<TuneCategory>().Select(value => new TuneCategoryOption(value, TunePresentation.Category(value))).ToArray();
    public IReadOnlyList<TuneSortOption> SortOptions { get; } = [new(TuneSort.Newest, "Newest first"), new(TuneSort.Oldest, "Oldest first"), new(TuneSort.NameAscending, "Name A–Z"), new(TuneSort.NameDescending, "Name Z–A")];
    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand ConfirmDialogCommand { get; }
    public ICommand CancelDialogCommand { get; }
    public bool IsCurrentMode => _workspace == TuneWorkspace.Current;
    public bool IsSavedMode => _workspace == TuneWorkspace.Saved;
    public bool IsCompareMode => _workspace == TuneWorkspace.Compare;
    public bool CanBrowse => !_disposed && !_busy && !_dialogOpen;
    public bool CanSave => !_disposed && !_busy && !_refreshing && !_dialogOpen && IsCurrentMode && _currentValid && _current is { IsComplete: true };
    public bool IsBusy => _busy;
    public bool CanEditDialog => !_busy;
    public bool IsRefreshing => _refreshing;
    public bool IsLibraryEmpty => _initialized && Library.Count == 0;
    public string EmptyLibraryText => IsLibraryEmpty ? "No saved tunes yet. Read your current tune and choose Save tune." : "";
    public string Status => _status;
    public string Error => _error;
    public bool HasError => _error.Length != 0;
    public bool HasSnapshot => IsCompareMode ? _a is not null || _b is not null : Displayed is not null;
    public bool IsDialogOpen => _dialogOpen;
    public string DialogTitle => _editingId is null ? "Save tune" : "Edit tune details";
    public string DialogName { get => _dialogName; set { _dialogName = value ?? ""; Changed(); } }
    public string DialogDescription { get => _dialogDescription; set { _dialogDescription = value ?? ""; Changed(); } }
    public string DialogError => _dialogError;
    public string Heading => IsCompareMode ? "Compare saved tunes" : IsCurrentMode ? _current is { } current ? TunePresentation.Car(current) : "Current car" : _openedName.Length > 0 ? _openedName : "Saved tunes";
    public string Description => IsSavedMode ? _openedDescription : "";
    public string Context => IsCurrentMode ? _current is { } current
        ? $"{(_currentValid ? "Current car" : "Earlier capture")} · {current.CapturedAtUtc.ToLocalTime():g}" : ""
        : IsSavedMode && _opened is { } opened ? $"{(_openedFromRun ? "Attached tune" : "Saved tune")} · {TunePresentation.Car(opened)} · captured {opened.CapturedAtUtc.ToLocalTime():g}" : "";
    public string CompareATitle => _a is null ? "Choose tune A" : $"A · {_a.Name}";
    public string CompareBTitle => _b is null ? "Choose tune B" : $"B · {_b.Name}";
    public string CompareACar => _a is null ? "" : TunePresentation.Car(_a.Snapshot);
    public string CompareBCar => _b is null ? "" : TunePresentation.Car(_b.Snapshot);
    private TuneSnapshot? Displayed => IsCurrentMode ? _current : _opened;

    public TuneCategoryOption SelectedCategory
    {
        get => _category;
        set { if (value is not null && _category != value) { _category = value; Changed(); RebuildRows(); } }
    }
    public TuneSortOption SelectedSort
    {
        get => _sort;
        set { if (value is not null && _sort != value) { _sort = value; Changed(); SortLibrary(); } }
    }
    public SavedTune? SelectedTune
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Changed(); NotifyCommands(); } }
    }
    public SavedTune? CompareA { get => _a; set { if (_a != value) { _a = value; Changed(); NotifyView(); } } }
    public SavedTune? CompareB { get => _b; set { if (_b != value) { _b = value; Changed(); NotifyView(); } } }

    public async Task InitializeAsync()
    {
        if (_initialized || _busy || _disposed) return;
        _busy = true; NotifyCommands();
        try
        {
            _saved = (await _store.ListAsync(_lifetime.Token)).ToArray();
            if (_disposed) return;
            _initialized = true; SortLibrary();
            if (_store.Warning is { } warning) SetError(warning);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { SetError("Saved tunes could not be read. Try opening Tune again."); }
        finally { _busy = false; OpenPendingView(); NotifyCommands(); Changed(nameof(EmptyLibraryText)); }
    }

    public void SetWorkspace(TuneWorkspace workspace)
    {
        if (_disposed || _dialogOpen || _busy || _workspace == workspace) return;
        _workspace = workspace;
        _status = workspace switch { TuneWorkspace.Saved => "Choose a saved tune to open it in Wisp.", TuneWorkspace.Compare => "Choose two saved tunes.", _ => "Refresh to read the current tune." };
        if (!IsCurrentMode) CancelRefresh();
        NotifyView();
        if (_visible && IsCurrentMode) _ = RefreshAsync();
    }

    public void SetPageVisible(bool visible)
    {
        _visible = visible;
        if (!visible) { CancelRefresh(); return; }
        _ = OpenPageAsync();
    }

    private async Task OpenPageAsync()
    {
        await InitializeAsync();
        if (!_disposed && _visible && IsCurrentMode && !_dialogOpen) await RefreshAsync();
    }

    public void InvalidateCurrent(bool invalidateRefresh = true)
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(new Action(() => InvalidateCurrent(invalidateRefresh))); return; }
        if (_disposed) return;
        if (!invalidateRefresh && _current is { } current && _isCurrent(current)) return;
        if (invalidateRefresh) CancelRefresh();
        _currentValid = false;
        if (IsCurrentMode) _status = "The current car changed. Refresh to read its tune.";
        NotifyView();
        if (_visible && IsCurrentMode && !_dialogOpen && !_busy && !_refreshing) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed || _busy || _refreshing || _dialogOpen || !IsCurrentMode) return;
        CancelRefresh();
        var revision = _refreshRevision;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _refreshCancellation = cancellation; _refreshing = true; _status = "Reading current tune…"; SetError(""); NotifyCommands(); Changed(nameof(Status));
        try
        {
            var result = await _capture(cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || revision != _refreshRevision || !IsCurrentMode) return;
            _current = result.Snapshot; _currentValid = result.Snapshot is { } snapshot && _isCurrent(snapshot);
            _status = result.Snapshot is null ? result.Message : TunePresentation.CaptureStatus(result.Snapshot);
            NotifyView();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (revision == _refreshRevision) { _currentValid = false; SetError("Tune data could not be read. Refresh to try again."); } }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation)) { _refreshCancellation = null; _refreshing = false; NotifyCommands(); }
        }
    }

    public async Task LoadSelectedAsync()
    {
        if (_selected is not { } choice || _busy || _dialogOpen || _disposed) return;
        _busy = true; SetError(""); NotifyCommands();
        try
        {
            var loaded = await _store.LoadAsync(choice.Id, _lifetime.Token);
            if (_disposed) return;
            OpenSaved(loaded); _status = "Saved tune opened in Wisp.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { SetError("That tune could not be opened. Its saved file has been kept."); }
        finally { _busy = false; OpenPendingView(); NotifyView(); }
    }

    public void OpenSnapshot(TuneSnapshot snapshot, string name, string description)
    {
        RequestExternalView(new(snapshot, name, description, Compare: false));
    }

    public void OpenComparisonSnapshots(TuneSnapshot a, string nameA, string descriptionA,
        TuneSnapshot? b = null, string nameB = "", string descriptionB = "")
    {
        RequestExternalView(new(a, nameA, descriptionA, Compare: true, b, nameB, descriptionB));
    }

    private void RequestExternalView(PendingTuneView request)
    {
        if (_disposed) return;
        _pendingView = request;
        CancelRefresh();
        OpenPendingView();
    }

    private void OpenPendingView()
    {
        if (_disposed || _busy || _dialogOpen || _pendingView is not { } request) return;
        _pendingView = null;
        if (!request.Compare)
        {
            _workspace = TuneWorkspace.Saved; _opened = request.A;
            _openedFromRun = true; _selected = null;
            _openedName = request.NameA; _openedDescription = request.DescriptionA;
            _status = "Tune attached to this run."; NotifyView(); return;
        }
        static SavedTune Choice(TuneSnapshot snapshot, string name, string description) => new()
        {
            Id = Guid.NewGuid(),
            Snapshot = snapshot,
            Name = name,
            Description = description,
            SavedAtUtc = snapshot.CapturedAtUtc,
            ModifiedAtUtc = snapshot.CapturedAtUtc
        };
        _comparisonExtras = request.B is null ? [Choice(request.A, request.NameA, request.DescriptionA)]
            : [Choice(request.A, request.NameA, request.DescriptionA), Choice(request.B, request.NameB, request.DescriptionB)];
        _a = _comparisonExtras[0]; _b = _comparisonExtras.Length > 1 ? _comparisonExtras[1] : null;
        _workspace = TuneWorkspace.Compare; _status = request.B is null ? "Choose tune B to compare with the attached tune." : "Comparing the tunes attached to these runs.";
        SortLibrary(); NotifyView();
    }

    private void OpenSaved(SavedTune saved)
    {
        CancelRefresh(); _workspace = TuneWorkspace.Saved; _opened = saved.Snapshot;
        _openedFromRun = false;
        _openedName = saved.Name; _openedDescription = saved.Description; _selected = saved; NotifyView();
    }

    public void BeginSave()
    {
        if (!CanSave || _current is null) return;
        if (!_isCurrent(_current)) { InvalidateCurrent(); return; }
        _editingId = null; _dialogSnapshot = _current; _dialogName = $"Car {_current.Identity.CarOrdinal} · {_current.CapturedAtUtc.ToLocalTime():MMM d HH:mm}";
        _dialogDescription = ""; ShowDialog();
    }

    public void BeginEdit()
    {
        if (_selected is null || _busy || _dialogOpen || _disposed) return;
        _editingId = _selected.Id; _dialogSnapshot = _selected.Snapshot; _dialogName = _selected.Name; _dialogDescription = _selected.Description; ShowDialog();
    }

    private void ShowDialog()
    {
        _dialogError = ""; _dialogOpen = true;
        foreach (var property in new[] { nameof(DialogTitle), nameof(DialogName), nameof(DialogDescription), nameof(DialogError), nameof(IsDialogOpen) }) Changed(property);
        NotifyCommands();
    }

    public void CancelDialog()
    {
        if (_busy || !_dialogOpen) return;
        _dialogOpen = false; _dialogSnapshot = null; _editingId = null; Changed(nameof(IsDialogOpen)); OpenPendingView(); NotifyCommands();
    }

    public async Task ConfirmDialogAsync()
    {
        if (!_dialogOpen || _busy || _disposed || _dialogSnapshot is not { } snapshot) return;
        if (string.IsNullOrWhiteSpace(_dialogName) || _dialogName.Trim().Length > 40) { DialogFail("Enter a name between 1 and 40 characters."); return; }
        if (_dialogDescription.Trim().Length > 2000) { DialogFail("Keep the description within 2,000 characters."); return; }
        _busy = true; _dialogError = ""; Changed(nameof(DialogError)); NotifyCommands();
        try
        {
            if (_editingId is null && !_isCurrent(snapshot))
            { DialogFail("The car or session changed. Cancel and refresh before saving."); return; }
            if (_editingId is null)
            {
                var latest = await _capture(_lifetime.Token);
                if (!_isCurrent(snapshot) || latest.Snapshot is not { } current || !TuneComparison.HaveSameSetupIdentity(snapshot, current))
                {
                    _currentValid = false;
                    DialogFail("The tune changed or could not be confirmed. Your text is kept; cancel and refresh before saving.");
                    return;
                }
            }
            var saved = _editingId is { } id
                ? await _store.UpdateMetadataAsync(id, _dialogName, _dialogDescription, _lifetime.Token)
                : await _store.SaveAsync(snapshot, _dialogName, _dialogDescription, _lifetime.Token);
            if (_disposed) return;
            _saved = [.. _saved.Where(item => item.Id != saved.Id), saved];
            if (_a?.Id == saved.Id) _a = saved;
            if (_b?.Id == saved.Id) _b = saved;
            SortLibrary(); OpenSaved(saved); _status = _editingId is null ? "Tune saved." : "Tune details saved.";
            _dialogOpen = false; _dialogSnapshot = null; _editingId = null; Changed(nameof(IsDialogOpen));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ArgumentException) { DialogFail("Check the name and description, then try again."); }
        catch (Exception error) when (error is not OutOfMemoryException) { DialogFail("The tune could not be saved. Check storage and try again; your text is kept here."); }
        finally { _busy = false; OpenPendingView(); NotifyView(); }
    }

    private void SortLibrary()
    {
        var selectedId = _selected?.Id;
        var aId = _a?.Id; var bId = _b?.Id;
        var ordered = _sort.Sort switch
        {
            TuneSort.Oldest => _saved.OrderBy(value => value.SavedAtUtc).ThenBy(value => value.Id),
            TuneSort.NameAscending => _saved.OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(value => value.Id),
            TuneSort.NameDescending => _saved.OrderByDescending(value => value.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(value => value.Id),
            _ => _saved.OrderByDescending(value => value.SavedAtUtc).ThenBy(value => value.Id)
        };
        var items = ordered.ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            if (index < Library.Count && Library[index] == items[index]) continue;
            var found = Library.IndexOf(items[index]);
            if (found >= 0) Library.Move(found, index);
            else Library.Insert(index, items[index]);
        }
        while (Library.Count > items.Length) Library.RemoveAt(Library.Count - 1);
        _selected = _saved.FirstOrDefault(value => value.Id == selectedId);
        var comparisons = items.Concat(_comparisonExtras).ToArray();
        ComparisonChoices.Clear(); foreach (var item in comparisons) ComparisonChoices.Add(item);
        _a = comparisons.FirstOrDefault(value => value.Id == aId); _b = comparisons.FirstOrDefault(value => value.Id == bId);
        Changed(nameof(SelectedTune)); Changed(nameof(IsLibraryEmpty)); Changed(nameof(EmptyLibraryText)); NotifyCommands();
        Changed(nameof(CompareA)); Changed(nameof(CompareB));
    }

    private void RebuildRows()
    {
        var left = IsCompareMode ? _a?.Snapshot : Displayed;
        var right = IsCompareMode ? _b?.Snapshot : null;
        Rows.Clear();
        if (left is null && right is null) return;
        foreach (var row in TuneComparison.Compare(left, right).Where(row => row.Category == _category.Category))
        {
            if (row.Id is >= TuneFieldId.Gear1 and <= TuneFieldId.Gear10 && row.A?.Status == TuneFieldStatus.NotApplicable && (!IsCompareMode || row.B?.Status == TuneFieldStatus.NotApplicable)) continue;
            Rows.Add(TunePresentation.Row(row.A, row.B, row.Id, IsCompareMode, row.DisplayDelta, row.RawEqual));
        }
    }

    private void NotifyView()
    {
        foreach (var property in new[] { nameof(IsCurrentMode), nameof(IsSavedMode), nameof(IsCompareMode), nameof(HasSnapshot), nameof(Heading), nameof(Description), nameof(Context), nameof(Status), nameof(SelectedTune), nameof(CompareA), nameof(CompareB), nameof(CompareATitle), nameof(CompareBTitle), nameof(CompareACar), nameof(CompareBCar) }) Changed(property);
        RebuildRows(); NotifyCommands();
    }

    private void CancelRefresh() { _refreshRevision++; _refreshCancellation?.Cancel(); _refreshCancellation = null; _refreshing = false; }
    private void SetError(string error) { _error = error; Changed(nameof(Error)); Changed(nameof(HasError)); }
    private void DialogFail(string error) { _dialogError = error; Changed(nameof(DialogError)); }
    private TuneCommand Command(Func<Task> action, Func<bool> canExecute)
    {
        var command = new TuneCommand(action, () => !_disposed && canExecute(), () => SetError("This action could not finish. Try again.")); _commands.Add(command); return command;
    }
    private void NotifyCommands()
    {
        foreach (var property in new[] { nameof(CanBrowse), nameof(CanSave), nameof(IsBusy), nameof(CanEditDialog), nameof(IsRefreshing) }) Changed(property);
        foreach (var command in _commands) command.RaiseCanExecuteChanged();
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose() { if (_disposed) return; _disposed = true; _pendingView = null; CancelRefresh(); _lifetime.Cancel(); _lifetime.Dispose(); }

    private sealed record PendingTuneView(TuneSnapshot A, string NameA, string DescriptionA, bool Compare,
        TuneSnapshot? B = null, string NameB = "", string DescriptionB = "");
}

internal sealed class TuneCommand(Func<Task> action, Func<bool> canExecute, Action failed) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && canExecute();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; RaiseCanExecuteChanged();
        try { await action(); }
        catch (Exception error) when (error is not OutOfMemoryException) { failed(); }
        finally { _running = false; RaiseCanExecuteChanged(); }
    }
    internal void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
