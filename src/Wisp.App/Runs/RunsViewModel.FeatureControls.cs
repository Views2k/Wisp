namespace Wisp.App.Runs;

public sealed partial class RunsViewModel
{
    private bool _libraryFeatureAvailable = true;
    private string _libraryFeatureStatus = "";
    private long _libraryFeatureRevision;
    private TaskCompletionSource? _libraryIdle;
    private CancellationTokenSource _libraryFeatureLifetime = new();
    private sealed class LibraryPausedException : OperationCanceledException { }
    public bool LibraryFeatureAvailable => _libraryFeatureAvailable;
    public string LibraryFeatureStatus => _libraryFeatureStatus;

    internal async Task SetLibraryFeatureAvailableAsync(bool available, string? reason)
    {
        _libraryFeatureStatus = available ? "" : reason ?? "Saved runs are temporarily paused.";
        OnChanged(nameof(LibraryFeatureStatus));
        if (_libraryFeatureAvailable == available) return;
        _libraryFeatureAvailable = available;
        if (!available) _libraryFeatureLifetime.Cancel();
        else
        {
            _libraryFeatureLifetime.Dispose();
            _libraryFeatureLifetime = new();
        }
        var revision = ++_libraryFeatureRevision;
        if (!available && _loadingSelection) _pendingSelection = _selectedRun;
        ++_selectionRevision; ++_analysisRevision; ++_chartRevision;
        _loadingSelection = _loadingComparison = _preparingCharts = false;
        IsBusy = false;
        OnChanged(nameof(LibraryFeatureAvailable)); OnChanged(nameof(IsPreparingCharts)); NotifyNavigation();
        OnChanged(nameof(WorkspaceCanEdit)); OnChanged(nameof(Status)); OnChanged(nameof(CanUndoDelete)); NotifyStorage();
        if (!available)
        {
            // Finish already accepted file writes and protect text entered before the pause.
            await Task.WhenAll(FlushMetadataAsync(), _libraryIdle?.Task ?? Task.CompletedTask);
            return;
        }
        await LoadLibraryAsync();
        if (revision != _libraryFeatureRevision || !LibraryFeatureAvailable || _disposed) return;
        var pendingSelection = _pendingSelection is not null || _pendingSavedRun is not null;
        RefreshStatus();
        if (!pendingSelection) RequestAnalysis();
    }
}
