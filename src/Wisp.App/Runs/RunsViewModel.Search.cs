using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using System.Windows.Threading;

namespace Wisp.App.Runs;

public sealed partial class RunsViewModel
{
    private string _librarySearch = "";
    private int _libraryMatchCount;
    private bool _searchRefreshPending;
    public ObservableCollection<SavedRunItem> FilteredLibrary { get; } = [];
    public ICommand ClearLibrarySearchCommand { get; private set; } = null!;
    public string LibrarySearch
    {
        get => _librarySearch;
        set { if (Set(ref _librarySearch, value ?? "")) RefreshLibrarySearch(); }
    }
    public bool HasLibrarySearch => !string.IsNullOrWhiteSpace(LibrarySearch);
    public string LibrarySearchSummary => !HasLibrarySearch ? $"All {Library.Count} saved runs" :
        $"{_libraryMatchCount} {(_libraryMatchCount == 1 ? "match" : "matches")}" +
        (_selectedRun is { } selected && !MatchesSearch(selected) ? " · Current run kept below." : "");

    private void InitializeLibrarySearch()
    {
        ClearLibrarySearchCommand = Command(() => { LibrarySearch = ""; return Task.CompletedTask; }, () => HasLibrarySearch);
        Library.CollectionChanged += LibraryChanged;
    }

    private void LibraryChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_searchRefreshPending || _disposed) return;
        _searchRefreshPending = true;
        _ = _dispatcher.InvokeAsync(() =>
        {
            _searchRefreshPending = false;
            if (!_disposed) RefreshLibrarySearch();
        }, DispatcherPriority.Background);
    }

    private bool MatchesSearch(SavedRunItem item)
    {
        var query = LibrarySearch.Trim();
        return query.Length == 0 || item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.Summary.Tune.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshLibrarySearch()
    {
        var matches = Library.Where(MatchesSearch).ToArray();
        _libraryMatchCount = matches.Length;
        // Keep the active A in the selector so filtering never clears its WPF
        // selection. B continues to use the unfiltered library.
        var items = Library.Where(item => item.Id == _selectedRun?.Id || MatchesSearch(item)).ToArray();
        SynchronizeItems(FilteredLibrary, items);
        OnChanged(nameof(SelectedRun));
        OnChanged(nameof(HasLibrarySearch)); OnChanged(nameof(LibrarySearchSummary));
        RaiseCommands();
    }
}
