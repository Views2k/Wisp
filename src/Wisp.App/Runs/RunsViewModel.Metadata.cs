using System.Windows.Input;
using System.Windows.Threading;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed partial class RunsViewModel
{
    private readonly Dictionary<Guid, MetadataEdit> _metadataEdits = [];
    private DispatcherTimer? _metadataTimer;
    private Task _metadataSaveTask = Task.CompletedTask;
    private Task _metadataRestoreTask = Task.CompletedTask;
    private Task<bool>? _metadataCloseTask;
    private long _metadataCloseRevision;
    private bool _metadataClosing;
    private bool _restoringMetadata;

    public ICommand RetryMetadataSaveCommand { get; private set; } = null!;
    public bool HasPendingMetadata => _metadataEdits.Values.Any(edit => !edit.Deleted && edit.Revision != edit.SavedRevision);
    public bool HasMetadataSaveError => _metadataEdits.Values.Any(edit => !edit.Deleted && edit.Failed && edit.Revision != edit.SavedRevision);
    public bool HasUnprotectedMetadata => _metadataEdits.Values.Any(edit => !edit.Deleted && edit.Revision != edit.SavedRevision && !edit.DraftPreserved);
    public string MetadataSaveStatus
    {
        get
        {
            var failed = _metadataEdits.Values.FirstOrDefault(edit => !edit.Deleted && edit.Failed && edit.Revision != edit.SavedRevision);
            if (failed is not null)
            {
                if (_runA?.Id == failed.Draft.Id) return failed.NeedsName ? "Enter a run name." : "Changes haven't been saved.";
                var name = string.IsNullOrWhiteSpace(failed.Draft.Name)
                    ? Library.FirstOrDefault(item => item.Id == failed.Draft.Id)?.Summary.Name ?? "Untitled run" : failed.Draft.Name;
                return $"Details for “{name}” haven't been saved.";
            }
            return HasPendingMetadata ? "Saving…" : HasRun ? "Saved" : "";
        }
    }
    public string MetadataSaveHelp => !HasMetadataSaveError ? "Run names, tune labels and notes save automatically." :
        _metadataEdits.Values.Any(edit => !edit.Deleted && edit.Failed && !edit.DraftPreserved)
            ? "Changes are still kept in Wisp. Check available storage and retry before closing the app."
            : "Your draft is kept on this PC. Retry, or open the run details to finish editing.";

    private void InitializeMetadata()
    {
        _metadataTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(750) };
        _metadataTimer.Tick += MetadataTimerTick;
        RetryMetadataSaveCommand = Command(async () =>
        {
            foreach (var edit in _metadataEdits.Values.Where(edit => edit.Failed)) edit.AttemptedRevision = -1;
            await FlushMetadataAsync();
        }, () => HasMetadataSaveError && !_disposed && !RecordingActive);
    }

    private void MetadataChanged()
    {
        if (_restoringMetadata || _disposed || _runA is null) return;
        if (!_metadataEdits.TryGetValue(_runA.Id, out var edit))
            _metadataEdits[_runA.Id] = edit = new MetadataEdit(MetadataOf(_runA));
        edit.Draft = new(_runA.Id, _name, _tune, _notes);
        edit.Revision++;
        edit.Failed = false; edit.NeedsName = false; edit.DraftPreserved = false;
        NotifyMetadata();
        ScheduleMetadataSave();
    }

    private void ScheduleMetadataSave()
    {
        if (_disposed || _metadataTimer is null) return;
        _metadataTimer.Stop(); _metadataTimer.Start();
    }

    private async void MetadataTimerTick(object? sender, EventArgs args)
    {
        _metadataTimer?.Stop();
        if (!_disposed) await SaveMetadataBatchAsync();
    }

    /// <summary>Flushes pending details without blocking navigation or discarding failed drafts.</summary>
    public async Task<bool> FlushMetadataAsync()
    {
        _metadataTimer?.Stop();
        while (!_disposed)
        {
            await _metadataSaveTask;
            if (!HasUnattemptedMetadata()) break;
            await SaveMetadataBatchAsync();
        }
        return !HasPendingMetadata;
    }

    public Task<bool> PrepareToCloseMetadataAsync()
    {
        if (_metadataCloseTask is { IsCompleted: false }) return _metadataCloseTask;
        _metadataClosing = true;
        CancelCountdown("Countdown canceled because Wisp is closing.");
        var revision = ++_metadataCloseRevision;
        _metadataCloseTask = PrepareAsync();
        NotifyMetadataCloseAvailability();
        return _metadataCloseTask;

        async Task<bool> PrepareAsync()
        {
            // The caller's Closing event must return before it requests Close again.
            // Reading an earlier recovery draft also finishes before shutdown approval.
            await Task.Yield();
            try
            {
                await _metadataRestoreTask;
                foreach (var edit in _metadataEdits.Values.Where(edit => edit.Failed && !edit.Deleted)) edit.AttemptedRevision = -1;
                await FlushMetadataAsync();
                if (revision != _metadataCloseRevision) return false;
                if (!HasUnprotectedMetadata) return true;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Fail("Run details could not be saved. Wisp has stayed open; check local storage and retry."); }
            CancelMetadataClosePreparation();
            return false;
        }
    }

    public void CancelMetadataClosePreparation()
    {
        _metadataCloseRevision++;
        _metadataClosing = false;
        NotifyMetadataCloseAvailability();
        RefreshStatus();
    }

    private void NotifyMetadataCloseAvailability()
    {
        foreach (var property in new[] { nameof(CanManageRun), nameof(CanManageLibrary), nameof(CanSelectRun), nameof(CanManageAllRuns), nameof(CanToggleRecording) }) OnChanged(property);
        RaiseCommands();
    }

    private bool HasUnattemptedMetadata() => _metadataEdits.Values.Any(edit =>
        !edit.Deleted && edit.Revision != edit.SavedRevision && edit.AttemptedRevision != edit.Revision);

    private Task SaveMetadataBatchAsync()
    {
        if (!_metadataSaveTask.IsCompleted) return _metadataSaveTask;
        var pending = _metadataEdits.Values.Where(edit => !edit.Deleted && edit.Revision != edit.SavedRevision && edit.AttemptedRevision != edit.Revision)
            .Select(edit => (Edit: edit, Draft: edit.Draft, Revision: edit.Revision)).ToArray();
        if (pending.Length == 0 || _disposed) return Task.CompletedTask;
        _metadataSaveTask = SaveBatchAsync();
        return _metadataSaveTask;

        async Task SaveBatchAsync()
        {
            // Yield before notifications so reentrant flush requests observe the task
            // that owns this batch rather than starting a second writer.
            await Task.Yield();
            foreach (var (edit, draft, revision) in pending)
            {
                edit.AttemptedRevision = revision;
                try
                {
                    var result = await _service.Store.SaveMetadataDraftAsync(draft);
                    if (result.Summary is { } summary)
                    {
                        edit.SavedRevision = revision; edit.LastSaved = summary;
                        if (!_disposed) ApplySavedMetadata(summary);
                    }
                    if (edit.Revision == revision)
                    {
                        edit.Failed = result.Summary is null;
                        edit.NeedsName = result.NeedsName;
                        edit.DraftPreserved = result.DraftPreserved;
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (edit.Revision == revision) { edit.Failed = true; edit.DraftPreserved = false; }
                }
                if (!_disposed) NotifyMetadata();
            }
            if (!_disposed && HasUnattemptedMetadata()) ScheduleMetadataSave();
        }
    }

    private async Task RestoreMetadataDraftAsync(RecordedRun run)
    {
        if (_metadataEdits.ContainsKey(run.Id)) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataRestoreTask = Task.WhenAll(_metadataRestoreTask, completion.Task);
        var edit = new MetadataEdit(MetadataOf(run));
        try
        {
            var draft = await _service.Store.ReadMetadataDraftAsync(run.Id);
            if (draft is not null && draft != edit.Draft)
            {
                edit.Draft = draft; edit.Revision = 1; edit.DraftPreserved = true;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The saved run remains usable even if a recovery draft is unreadable.
            Error = "A details draft could not be read. Its file has been kept.";
        }
        finally
        {
            if (_metadataEdits.TryAdd(run.Id, edit) && edit.Revision != 0) ScheduleMetadataSave();
            completion.TrySetResult();
        }
    }

    private async Task<bool> RestoreLibraryMetadataDraftsAsync()
    {
        try
        {
            var drafts = await _service.Store.ReadMetadataDraftsAsync(Library.Select(item => item.Id));
            foreach (var draft in drafts)
            {
                if (_metadataEdits.ContainsKey(draft.Id)) continue;
                var saved = Library.FirstOrDefault(item => item.Id == draft.Id)?.Summary;
                if (saved is null) continue;
                var original = new RunMetadataDraft(saved.Id, saved.Name, saved.Tune, saved.Notes);
                _metadataEdits.Add(draft.Id, new MetadataEdit(draft)
                { Revision = draft == original ? 0 : 1, DraftPreserved = true });
            }
            NotifyMetadata();
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Fail("A saved details draft could not be read. Its file has been kept; check local storage before exporting or removing the library.");
            return false;
        }
    }

    private void ShowMetadata(RecordedRun run)
    {
        var draft = _metadataEdits.TryGetValue(run.Id, out var edit) ? edit.Draft : MetadataOf(run);
        _restoringMetadata = true;
        try { Name = draft.Name; Tune = draft.Tune; Notes = draft.Notes; }
        finally { _restoringMetadata = false; }
        NotifyMetadata();
    }

    private RecordedRun WithLatestSavedMetadata(RecordedRun run) =>
        _metadataEdits.TryGetValue(run.Id, out var edit) && edit.LastSaved is { } saved
            ? run with { Name = saved.Name, Tune = saved.Tune, Notes = saved.Notes } : run;

    private void ApplySavedMetadata(RunSummary saved)
    {
        if (_runA?.Id == saved.Id) _runA = _runA with { Name = saved.Name, Tune = saved.Tune, Notes = saved.Notes };
        if (_runB?.Id == saved.Id) _runB = _runB with { Name = saved.Name, Tune = saved.Tune, Notes = saved.Notes };
        if (_reviewRuns.TryGetValue(saved.Id, out var review)) _reviewRuns[saved.Id] = review with { Name = saved.Name, Tune = saved.Tune, Notes = saved.Notes };
        var index = Library.ToList().FindIndex(item => item.Id == saved.Id);
        if (index >= 0)
        {
            var selectedId = _selectedRun?.Id;
            var comparisonId = _comparisonChoice?.Id;
            var item = new SavedRunItem(saved);
            Library[index] = item;
            if (selectedId == saved.Id) { _selectedRun = item; OnChanged(nameof(SelectedRun)); }
            if (comparisonId == saved.Id) { _comparisonChoice = item; OnChanged(nameof(ComparisonChoice)); }
        }
        OnChanged(nameof(RunALabel)); OnChanged(nameof(RunBLabel));
        RefreshLibrarySearch();
    }

    private void NotifyMetadata()
    {
        foreach (var name in new[] { nameof(HasPendingMetadata), nameof(HasMetadataSaveError), nameof(HasUnprotectedMetadata), nameof(MetadataSaveStatus), nameof(MetadataSaveHelp) }) OnChanged(name);
        RaiseCommands();
    }

    private void DisposeMetadata()
    {
        if (_metadataTimer is not null) { _metadataTimer.Stop(); _metadataTimer.Tick -= MetadataTimerTick; }
    }

    private void SuspendDeletedMetadata(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
            if (_metadataEdits.TryGetValue(id, out var edit)) edit.Deleted = true;
        NotifyMetadata();
    }

    private void RestoreDeletedMetadata()
    {
        foreach (var item in Library)
            if (_metadataEdits.TryGetValue(item.Id, out var edit) && edit.Deleted)
            {
                edit.Deleted = false; edit.AttemptedRevision = -1;
            }
        if (HasUnattemptedMetadata()) ScheduleMetadataSave();
        NotifyMetadata();
    }

    private static RunMetadataDraft MetadataOf(RecordedRun run) => new(run.Id, run.Name, run.Tune, run.Notes);
    private sealed class MetadataEdit(RunMetadataDraft draft)
    {
        internal RunMetadataDraft Draft = draft;
        internal long Revision, SavedRevision, AttemptedRevision = -1;
        internal RunSummary? LastSaved;
        internal bool Failed, DraftPreserved, NeedsName, Deleted;
    }
}
