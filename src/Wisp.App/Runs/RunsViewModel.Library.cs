namespace Wisp.App.Runs;

public sealed partial class RunsViewModel
{
    private RunDeletionBatch? _lastDeletedBatch;

    public async Task ExportAllAsync(string destination)
    {
        if (!CanManageAllRuns) return;
        var permission = _libraryFeatureLifetime.Token;
        IsBusy = true; Error = ""; Status = "Exporting saved runs…";
        try
        {
            if (!await RestoreLibraryMetadataDraftsAsync()) return;
            if (!await FlushMetadataAsync()) { Fail("Save the pending run details before exporting the library. Your drafts are kept."); return; }
            var count = await StoreOperationAsync(token => _service.Store.ExportAllAsync(destination, token), permission);
            Status = $"Exported {count} saved runs. Import the ZIP in Wisp to restore or compare them.";
        }
        catch (LibraryPausedException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Fail("Export failed. Your saved runs are unchanged. Choose a new filename and check available storage."); }
        finally { IsBusy = false; }
    }

    public async Task ImportManyAsync(IEnumerable<string> sources)
    {
        if (!CanManageLibrary) return;
        IsBusy = true; Error = ""; Status = "Checking and importing runs…";
        try
        {
            var result = await StoreOperationAsync(token => _service.Store.ImportManyAsync(sources, token));
            ForgetDeletedMetadata(result.Imported.Select(run => run.Id));
            if (LastDeletedId is { } deleted && result.Imported.Any(run => run.Id == deleted))
            {
                // The removed run is back in the library; there is nothing left to undo.
                LastDeletedId = null; OnChanged(nameof(CanUndoDelete));
            }
            await LoadLibraryAsync();
            if (!HasRun && result.Imported.FirstOrDefault() is { } first && Library.FirstOrDefault(item => item.Id == first.Id) is { } item)
            {
                _selectedRun = item; OnChanged(nameof(SelectedRun)); await LoadSelectionAsync(item);
            }
            Status = result.Imported.Count == 0 ? result.SkippedDuplicates > 0 ? "These runs are already in your library." : "No runs were found to import." :
                $"Imported {result.Imported.Count} {(result.Imported.Count == 1 ? "run" : "runs")}.";
            if (result.SkippedDuplicates > 0) Status += $" Skipped {result.SkippedDuplicates} already saved.";
        }
        catch (RunLibraryFullException)
        { Fail("There is not enough room in the run library. Export and remove saved runs, or import fewer runs."); }
        catch (LibraryPausedException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Fail("Import failed. Choose Wisp run files or a Wisp library ZIP. Unsupported, damaged or conflicting runs are not overwritten."); }
        finally { IsBusy = false; }
    }

    public async Task DeleteAllAsync()
    {
        if (!CanManageAllRuns) return;
        var permission = _libraryFeatureLifetime.Token;
        IsBusy = true; Error = ""; Status = "Removing saved runs…";
        try
        {
            if (!await RestoreLibraryMetadataDraftsAsync()) return;
            if (!await FlushMetadataAsync()) { Fail("Save the pending run details before removing the library. Your drafts are kept."); return; }
            var batch = await StoreOperationAsync(token => _service.Store.DeleteAllAsync(token), permission);
            if (batch.Count > 0)
            {
                _lastDeletedBatch = batch; LastDeletedId = null;
                ++_selectionRevision; ++_analysisRevision; ++_chartRevision;
                _runA = _runB = null; _reportA = _reportB = null; _selectedRun = _comparisonChoice = null;
                Findings.Clear(); Metrics.Clear(); Charts.Clear(); AlternativeCharts.Clear(); ClearStatistics(); ClearWorkspaceCharts();
                NotifyRun(); OnChanged(nameof(SelectedRun)); OnChanged(nameof(ComparisonChoice)); OnChanged(nameof(CanUndoDelete));
            }
            await LoadLibraryAsync();
            SuspendDeletedMetadata(_metadataEdits.Keys.Where(id => Library.All(item => item.Id != id)).ToArray());
            Status = $"Removed {batch.Count} saved runs. You can undo this removal.";
            if (batch.SkippedUnreadable > 0) Status += $" Kept {batch.SkippedUnreadable} unreadable files.";
        }
        catch (LibraryPausedException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Fail("The runs could not all be removed. Refresh the library to check its state; recovery copies are kept."); }
        finally { IsBusy = false; }
    }
}
