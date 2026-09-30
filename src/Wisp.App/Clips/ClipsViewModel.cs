using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Wisp.App.Clips;

public enum ClipRecorderState { Unavailable, Disabled, WaitingForGame, Buffering, Saving, Stopping, Error, Preparing, Reconnecting, Paused }
public enum ClipBorderlessAccessResult { Allowed, Denied, Unavailable }
public sealed record ClipRecorderSnapshot(ClipRecorderState State, bool Enabled, bool CanEnable, bool CanSave, string Status);

public interface IClipRecorder
{
    // Status is safe user-facing copy, never raw native diagnostics or paths.
    ClipRecorderSnapshot Snapshot { get; }
    // A fixed, privacy-safe report; never raw stderr or exception messages.
    string FailureReport => "";
    event EventHandler? StateChanged;
    Task<ClipBorderlessAccessResult> RequestBorderlessAccessAsync(CancellationToken cancellationToken)
        => Task.FromResult(ClipBorderlessAccessResult.Unavailable);
    Task<ClipBorderlessAccessResult> CheckBorderlessAccessAsync(CancellationToken cancellationToken)
        => Task.FromResult(ClipBorderlessAccessResult.Unavailable);
    Task SetEnabledAsync(bool enabled, ClipRecordingSpec recording, CancellationToken cancellationToken, bool showCaptureBorder = false);
    // Write only this reserved path, with create-new semantics. Return only after
    // successful finalization/close; failure must not report a saved clip.
    Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken cancellationToken);
}

public interface IClipThumbnailProvider
{
    // Optional static poster, already frozen and at most 512px per dimension.
    // Returning null means no preview; do not open a video player for each card.
    Task<BitmapSource?> LoadAsync(ClipEntry clip, string validatedMediaPath, CancellationToken cancellationToken);
}

public sealed class ClipCardItem(ClipEntry entry) : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _selected;
    private bool _thumbnailLoading;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ClipEntry Entry { get; private set; } = entry;
    public Guid Id => Entry.Id;
    public string Title => Entry.SavedAtUtc.ToLocalTime().ToString("MMM d · h:mm tt");
    public string Detail => $"{TimeSpan.FromSeconds(Entry.DurationSeconds):m\\:ss} · {Entry.Media.Height}p · {Entry.Media.FrameRate} fps";
    public string ReviewState => Entry.ExportedAtUtc is not null ? "Exported" : Entry.ViewedAtUtc is not null ? "Viewed" : "New";
    public string PlayLabel => $"Play clip from {Title}, {Detail}";
    public BitmapSource? Thumbnail => _thumbnail;
    public bool HasThumbnail => _thumbnail is not null;
    public string PreviewStatus => _thumbnailLoading ? "Loading preview…" : "Preview unavailable";
    public bool IsSelected => _selected;
    internal void Select(bool selected) { _selected = selected; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    internal void Update(ClipEntry updated)
    {
        Entry = updated;
        PropertyChanged?.Invoke(this, new(nameof(ReviewState)));
    }
    internal void SetThumbnail(BitmapSource? image)
    {
        if (image is not null && (!image.IsFrozen || image.PixelWidth > 512 || image.PixelHeight > 512)) return;
        _thumbnail = image;
        PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
        PropertyChanged?.Invoke(this, new(nameof(HasThumbnail)));
    }
    internal void SetThumbnailLoading(bool loading)
    {
        if (_thumbnailLoading == loading) return;
        _thumbnailLoading = loading; PropertyChanged?.Invoke(this, new(nameof(PreviewStatus)));
    }
}

public sealed class ClipsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IClipRecorder _recorder;
    private readonly IClipThumbnailProvider? _thumbnails;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly List<ClipCommand> _commands = [];
    private readonly ClipsSettings _settings;
    private readonly string _libraryDirectory;
    private ClipLibrary? _library;
    private ClipRecorderSnapshot _snapshot;
    private ClipCardItem? _selected;
    private bool _busy, _disposed, _qualityPending, _runtimeActive = true;
    private bool _saveActive, _drainingSaves;
    private int _queuedSaves;
    private string _saveQueueNotice = "";
    private string _capturePermissionHint = "";
    private Task? _initialization;
    private Func<bool, bool, OverlayHotkeyChord, string?>? _registerShortcut;
    private string _error = "", _notice = "", _shortcutStatus = "Shortcuts are off.";
    private string _libraryWarning = "";
    private int _pageIndex, _pageCount, _total, _pending, _newClips;
    private long _selectionRevision;
    private CancellationTokenSource? _thumbnailWork;
    private long _thumbnailRevision;
    private bool _galleryActive;
    internal Task ThumbnailCompletion { get; private set; } = Task.CompletedTask;

    public ClipsViewModel(ClipsSettings settings, IClipRecorder recorder, Dispatcher dispatcher, IClipThumbnailProvider? thumbnails = null, string? libraryDirectory = null)
    {
        _token = _lifetime.Token; _settings = settings.Clone(); _settings.Normalize();
        _recorder = recorder; _dispatcher = dispatcher; _thumbnails = thumbnails;
        _snapshot = recorder.Snapshot;
        _libraryDirectory = libraryDirectory ?? ClipsSettings.DefaultLibraryDirectory;
        if (_libraryDirectory.Length > 0) _library = new ClipLibrary(_libraryDirectory);
        SaveClipCommand = Command(SaveClipAsync, () => CanRequestSave);
        PreviousPageCommand = Command(() => LoadPageAsync(_pageIndex - 1), () => !IsBusy && _pageIndex > 0);
        NextPageCommand = Command(() => LoadPageAsync(_pageIndex + 1), () => !IsBusy && _pageIndex + 1 < _pageCount);
        RefreshCommand = Command(() => LoadPageAsync(_pageIndex), () => !IsBusy && _library is not null);
        _recorder.StateChanged += RecorderChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? PreferencesChanged;
    public event EventHandler? ReminderChanged;
    public ObservableCollection<ClipCardItem> Clips { get; } = [];
    public ICommand SaveClipCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand RefreshCommand { get; }
    public ClipsSettings Preferences => _settings.Clone();
    public IReadOnlyList<int> LengthChoices => ClipsSettings.LengthChoices;
    public IReadOnlyList<int> ResolutionChoices => ClipsSettings.ResolutionChoices;
    public IReadOnlyList<int> FrameRateChoices => ClipsSettings.FrameRateChoices;
    public bool ShortcutCaptureActive { get; set; }
    public bool IsBusy => _busy;
    public bool CanEditSettings => !_disposed && !IsBusy && !_snapshot.Enabled;
    public bool CanToggle => !_disposed && _runtimeActive && !IsBusy && (_snapshot.Enabled || (_snapshot.CanEnable && _library is not null));
    public bool ClippingEnabled => _snapshot.Enabled;
    public bool IsRecording => _snapshot.State == ClipRecorderState.Buffering;
    public bool CanBrowse => !_disposed && !IsBusy;
    public bool CanSave => !_disposed && _runtimeActive && !IsBusy && _library is not null && _snapshot.CanSave;
    public bool CanRequestSave => !_disposed && _runtimeActive && _library is not null && _snapshot.Enabled &&
        (!IsBusy || _saveActive) && _queuedSaves + (_saveActive ? 1 : 0) < 2 &&
        _snapshot.State is ClipRecorderState.Preparing or ClipRecorderState.Buffering or ClipRecorderState.Saving;
    public bool HasQueuedSave => _queuedSaves > 0;
    public bool HasSaveQueueStatus => _queuedSaves > 0 || _saveQueueNotice.Length > 0;
    public string SaveQueueStatus => _queuedSaves == 0 ? _saveQueueNotice : _saveActive
        ? "Another clip save is queued. It will end when saving starts."
        : $"{_queuedSaves} clip save{(_queuedSaves == 1 ? "" : "s")} queued until recording is ready. Queued clips end when saving starts.";
    public string RecorderStatus => _snapshot.Status;
    public string CapturePermissionHint => _capturePermissionHint;
    public bool HasCapturePermissionHint => _capturePermissionHint.Length > 0;
    public string FailureReport => _recorder.FailureReport;
    public bool HasFailureReport => FailureReport.Length > 0;
    public string Error => _libraryWarning.Length == 0 ? _error : _error.Length == 0 ? _libraryWarning : _libraryWarning + " " + _error;
    public bool HasError => _error.Length > 0 || _libraryWarning.Length > 0;
    public string Notice => _notice;
    public string StorageDirectory => _settings.StorageDirectory;
    public string SuggestedStorageDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Wisp Clips");
    public string StorageText => StorageDirectory.Length == 0 ? "No export folder selected. Saved clips stay in Wisp until you export them." : StorageDirectory;
    public string LongClipHint => LengthSeconds == 300
        ? "Five-minute clips use more memory and storage and can take longer to save. Export keeps the recorded resolution and quality."
        : "Longer clips use more memory and storage. Export keeps the recorded resolution and quality.";
    public string PageText => _pageCount == 0 ? "No saved clips" : $"Page {_pageIndex + 1} of {_pageCount} · {_total} clips";
    public string PendingText => _pending == 0 ? "" : $"{_pending} unfinished save(s) retained in Wisp's private clip storage. Your completed clips are kept.";
    public bool CanOpenClipFolder => !_disposed && !IsBusy && StorageDirectory.Length > 0;
    public bool IsEmpty => !IsBusy && Clips.Count == 0;
    public bool HasSelection => _selected is not null;
    public bool CanExport => HasSelection && !IsBusy;
    public ClipCardItem? SelectedClip => _selected;
    public string SelectedTitle => _selected?.Title ?? "Choose a clip to play";
    public int NewClipCount => _newClips;
    public bool HasReminder => _settings.RemindersEnabled && _newClips > 0;
    public string ReminderText => _newClips == 1 ? "You have a new clip to review or export." : $"You have {_newClips} new clips to review or export.";
    private bool RecorderNeedsAttention => _snapshot.State == ClipRecorderState.Error;
    public bool HasDashboardNotice => HasError || RecorderNeedsAttention || _pending > 0 || HasReminder;
    public string DashboardNoticeTitle => HasError || RecorderNeedsAttention ? "Clips need attention" : _pending > 0 ? "Unfinished clip saves" : "New clips";
    public string DashboardNoticeText => HasError ? Error : RecorderNeedsAttention ? RecorderStatus : _pending > 0 ? PendingText : ReminderText;
    public string ShortcutStatus => _shortcutStatus;
    public string ToggleShortcutText => _settings.ToggleShortcut.ToString();
    public string SaveShortcutText => _settings.SaveShortcut.ToString();
    public bool ToggleShortcutEnabled { get => _settings.ToggleShortcutEnabled; set => ConfigureShortcut(false, value, _settings.ToggleShortcut); }
    public bool SaveShortcutEnabled { get => _settings.SaveShortcutEnabled; set => ConfigureShortcut(true, value, _settings.SaveShortcut); }
    public int LengthSeconds { get => _settings.LengthSeconds; set { if (ChangeChoice(value, LengthChoices, _settings.LengthSeconds)) { _settings.LengthSeconds = value; Changed(); OnChanged(nameof(LongClipHint)); } } }
    public int ResolutionHeight { get => _settings.ResolutionHeight; set { if (ChangeChoice(value, ResolutionChoices, _settings.ResolutionHeight)) { _settings.ResolutionHeight = value; Changed(); } } }
    public int FrameRate { get => _settings.FrameRate; set { if (ChangeChoice(value, FrameRateChoices, _settings.FrameRate)) { _settings.FrameRate = value; Changed(); } } }
    public bool ShowCaptureBorder
    {
        get => _settings.ShowCaptureBorder;
        set
        {
            if (!CanEditSettings || value == _settings.ShowCaptureBorder) return;
            _settings.ShowCaptureBorder = value;
            _capturePermissionHint = "";
            Changed(); OnChanged(nameof(CapturePermissionHint)); OnChanged(nameof(HasCapturePermissionHint));
        }
    }
    public int Quality
    {
        get => _settings.Quality;
        set { if (CanEditSettings && value is >= 10 and <= 100 && value != _settings.Quality) { _settings.Quality = value; _qualityPending = true; OnChanged(); } }
    }
    public bool RemindersEnabled
    {
        get => _settings.RemindersEnabled;
        set { if (_settings.RemindersEnabled != value) { _settings.RemindersEnabled = value; Changed(); OnChanged(nameof(HasReminder)); NotifyDashboardNotice(); ReminderChanged?.Invoke(this, EventArgs.Empty); } }
    }

    public Task InitializeAsync() => _initialization ??= Operation(async () =>
    {
        if (_library is null) return;
        if (_settings.LegacyLibraryDirectory.Length > 0 && !string.Equals(_settings.LegacyLibraryDirectory, _libraryDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var imported = 0;
            try
            {
                ClipImportResult result;
                do
                {
                    NoticeText($"Bringing existing clips into Wisp… {imported} imported. Original files are kept.");
                    result = await _library!.ImportLegacyAsync(_settings.LegacyLibraryDirectory, _token);
                    imported += result.Imported;
                    if (result.Remaining > 0 && result.Imported == 0)
                        throw new IOException("The existing clip import made no progress.");
                } while (result.Remaining > 0);
                _settings.LegacyLibraryDirectory = "";
                PreferencesChanged?.Invoke(this, EventArgs.Empty);
                NoticeText(imported > 0 ? $"{imported} existing clip(s) added to Wisp. Original files are kept." : "");
                if (result.PendingLegacySaves > 0)
                    LibraryWarningText("The previous clip folder contains unfinished saves. Those files have been kept.");
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { LibraryWarningText("Some existing clips could not be imported. Original files are kept; Wisp will try the import again on its next start."); }
        }
        else if (_settings.LegacyLibraryDirectory.Length > 0)
        { _settings.LegacyLibraryDirectory = ""; PreferencesChanged?.Invoke(this, EventArgs.Empty); }
        await ReadPageAsync(0);
    }, "The clip library could not be read. Its files have been kept.", !_disposed);
    private void LibraryWarningText(string value)
    {
        _libraryWarning = value;
        OnChanged(nameof(Error)); OnChanged(nameof(HasError)); NotifyDashboardNotice();
    }

    public void CommitQuality()
    {
        if (!_qualityPending) return;
        _qualityPending = false;
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task ToggleAsync() => ToggleAsync(requestAccess: true);
    private Task ToggleAsync(bool requestAccess) => Operation(async () =>
    {
        var next = !_snapshot.Enabled;
        if (next && !ClipsSettings.CanRecordToDirectory(_libraryDirectory))
        { ErrorText(ClipsSettings.RecordingPathTooLongMessage); return; }
        if (next && await IsNetworkStorageAsync())
        { ErrorText("Clip recording needs a local folder. Network drives are not supported."); return; }
        if (next && !ShowCaptureBorder)
        {
            var access = requestAccess ? await _recorder.RequestBorderlessAccessAsync(_token) : await _recorder.CheckBorderlessAccessAsync(_token);
            if (_disposed || !_runtimeActive) return;
            _capturePermissionHint = access switch
            {
                ClipBorderlessAccessResult.Allowed => "",
                ClipBorderlessAccessResult.Denied => "Windows did not allow borderless capture. Clipping can still work with the Windows capture border visible.",
                _ => "Borderless capture is unavailable. Windows may show a capture border while clipping is on."
            };
            OnChanged(nameof(CapturePermissionHint)); OnChanged(nameof(HasCapturePermissionHint));
        }
        else if (next)
        { _capturePermissionHint = ""; OnChanged(nameof(CapturePermissionHint)); OnChanged(nameof(HasCapturePermissionHint)); }
        if (_disposed || !_runtimeActive) return;
        await _recorder.SetEnabledAsync(next, Recording(), _token, ShowCaptureBorder);
        RefreshRecorder();
        if (_snapshot.Enabled != next) { ErrorText("Clipping did not change state. Check the recorder status."); return; }
        _settings.Enabled = next;
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }, "Clipping could not change state. Check the recorder status.", CanToggle);

    internal void SetRuntimeActive(bool active)
    {
        _runtimeActive = active;
        if (!active) CancelQueuedSaves("Queued saves cancelled because clipping is paused.");
        NotifyState();
    }
    internal Task RestoreEnabledPreferenceAsync() => _settings.Enabled && !_snapshot.Enabled ? ToggleAsync(requestAccess: false) : Task.CompletedTask;
    private Task<bool> IsNetworkStorageAsync() => Task.Run(() =>
        _libraryDirectory.StartsWith(@"\\", StringComparison.Ordinal) ||
        new DriveInfo(Path.GetPathRoot(_libraryDirectory)!).DriveType == DriveType.Network, _token);

    public Task SaveClipAsync()
    {
        if (_disposed || !_runtimeActive) return Task.CompletedTask;
        if (!CanRequestSave)
        {
            if (_snapshot.State is ClipRecorderState.Error or ClipRecorderState.Unavailable) ErrorText(RecorderStatus);
            else if (_queuedSaves + (_saveActive ? 1 : 0) >= 2) NoticeText("Two clip saves are already pending. Wait for them to finish before saving another.");
            else if (_snapshot.State == ClipRecorderState.Saving) NoticeText(ClipRecorderService.ReasonText("save_in_progress"));
            else if (IsBusy) NoticeText("Wisp is busy with another clip action. Wait for it to finish, then save again.");
            else if (_library is null) ErrorText("Wisp's clip library is unavailable. Check the storage status.");
            else if (!_snapshot.Enabled) ErrorText("Clipping is off. Enable clipping before saving a clip.");
            else ErrorText("No clip is ready yet. " + RecorderStatus);
            return Task.CompletedTask;
        }
        _saveQueueNotice = ""; _queuedSaves++;
        NotifyState();
        return DrainSaveQueueAsync();
    }

    private async Task DrainSaveQueueAsync()
    {
        if (_drainingSaves || _queuedSaves == 0 || !CanSave) return;
        _drainingSaves = true;
        try
        {
            while (_queuedSaves > 0 && CanSave)
            {
                _queuedSaves--; _saveActive = true;
                NotifyState();
                var saved = await SaveOneClipAsync();
                _saveActive = false;
                if (!saved) CancelQueuedSaves("Queued saves cancelled because the previous clip could not be saved.");
                NotifyState();
            }
        }
        finally { _saveActive = false; _drainingSaves = false; if (!_disposed) NotifyState(); }
    }

    private async Task<bool> SaveOneClipAsync()
    {
        var saved = false;
        await Operation(async () =>
        {
            var library = _library!;
            var target = await library.ReserveSaveAsync(Recording(), _token);
            FinalizedClipMedia media;
            try
            {
                media = await _recorder.SaveAsync(target, _token);
                var entry = await library.CommitFinalizedAsync(target.Id, media, _token);
                saved = true;
                if (_disposed || !ReferenceEquals(_library, library)) return;
                InsertSavedClip(entry);
            }
            catch
            {
                if (!_disposed) await ReadPageAsync(0);
                throw;
            }
            // The completed card appears before optional disk reconciliation/poster work.
            var duration = TimeSpan.FromTicks(media.ActualEnd100ns - media.ActualStart100ns).TotalSeconds;
            NoticeText($"Clip saved · {duration:0.0} s. " + (media.HasAudio
                ? "Select it below to watch or export."
                : "Game audio was unavailable. Select it below to watch or export."));
            try
            {
                var page = await library.GetPageAsync(0, _token);
                if (_disposed || !ReferenceEquals(_library, library)) return;
                ApplyPage(page); StartThumbnails();
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { ErrorText("The clip was saved, but the full list could not be refreshed. Its card is shown below."); }
        }, "The clip could not be saved. Any unfinished save and its file have been kept.", CanSave, SaveFailureText);
        return saved;
    }

    private void InsertSavedClip(ClipEntry entry)
    {
        CancelThumbnails();
        if (_pageIndex != 0) Clips.Clear();
        _pageIndex = 0;
        Clips.Insert(0, new(entry));
        while (Clips.Count > ClipLibrary.PageSize) Clips.RemoveAt(Clips.Count - 1);
        _total++; _pageCount = (_total + ClipLibrary.PageSize - 1) / ClipLibrary.PageSize;
        _newClips++;
        OnChanged(nameof(NewClipCount)); OnChanged(nameof(HasReminder)); OnChanged(nameof(ReminderText));
        NotifyState(); ReminderChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelQueuedSaves(string message)
    {
        if (_queuedSaves == 0) return;
        _queuedSaves = 0;
        _saveQueueNotice = message;
    }

    private string? SaveFailureText(Exception error)
    {
        var current = _recorder.Snapshot;
        if (current.State == ClipRecorderState.Error) return current.Status;
        return error is RecorderClientException recorder ? ClipRecorderService.ReasonText(recorder.Reason) : null;
    }

    public Task LoadPageAsync(int pageIndex) => Operation(() => ReadPageAsync(Math.Max(0, pageIndex)),
        "The clip library could not be read. Its files have been kept.", !IsBusy && !_disposed);

    public Task SetStorageDirectoryAsync(string directory) => Operation(async () =>
    {
        if (!ClipsSettings.TryNormalizeStorageDirectory(directory, out var full))
        { ErrorText("Choose an export folder on a local drive."); return; }
        await Task.Run(() => { _token.ThrowIfCancellationRequested(); ClipLibrary.CheckPath(full); }, _token);
        _settings.StorageDirectory = full;
        foreach (var name in new[] { nameof(StorageDirectory), nameof(StorageText), nameof(CanOpenClipFolder) }) OnChanged(name);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
        NoticeText("Export folder selected. Saved clips stay in Wisp until you choose Export.");
    }, "That export folder could not be selected. The previous folder is unchanged.", CanBrowse);

    public async Task<string?> GetClipFolderForOpenAsync()
    {
        if (!CanOpenClipFolder) return null;
        var library = _library;
        var directory = StorageDirectory;
        try
        {
            await Task.Run(() =>
            {
                _token.ThrowIfCancellationRequested();
                if (!ClipsSettings.TryNormalizeStorageDirectory(directory, out var full) ||
                    new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
                    throw new IOException();
                ClipLibrary.CheckPath(full);
                if (!Directory.Exists(full)) throw new DirectoryNotFoundException();
            }, _token);
            return !_disposed && ReferenceEquals(library, _library) ? directory : null;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_disposed) ClipFolderOpenFailed(); return null; }
    }
    public void ClipFolderOpenFailed() => ErrorText("The export folder could not be opened. Check that the drive is available.");

    public async Task<string?> SelectForPlaybackAsync(ClipCardItem item)
    {
        if (IsBusy || _disposed || _library is null || !Clips.Contains(item)) return null;
        var revision = ++_selectionRevision;
        string? path = null;
        await Operation(async () =>
        {
            var candidate = await _library.GetMediaPathAsync(item.Id, _token);
            if (revision != _selectionRevision || _disposed) return;
            _selected?.Select(false); _selected = item; item.Select(true);
            foreach (var name in new[] { nameof(SelectedClip), nameof(SelectedTitle), nameof(HasSelection), nameof(CanExport) }) OnChanged(name);
            path = candidate;
        }, "This clip could not be opened. Its file has been kept.", true);
        return path;
    }

    public async Task PlaybackOpenedAsync(Guid id)
    {
        if (_disposed || _library is null || _selected?.Id != id) return;
        var library = _library;
        try
        {
            var updated = await library.MarkViewedAsync(id, _token);
            if (_disposed || !ReferenceEquals(_library, library)) return;
            Clips.FirstOrDefault(item => item.Id == id)?.Update(updated);
            await RefreshReminderAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ErrorText("The clip opened, but its viewed state could not be saved."); }
    }

    public void PlaybackFailed() => ErrorText("This clip could not be played. Its file has been kept.");
    public void ReportCopyCompleted(bool copied) => NoticeText(copied
        ? "Error details copied. You can include them when reporting this problem."
        : "The clipboard is busy. Try Copy error details again.");
    public void ClosePlayback() => ClearSelection();

    public Task ExportSelectedToFolderAsync()
    {
        if (StorageDirectory.Length == 0)
        { ErrorText("Choose an export folder above Saved clips, then choose Export again."); return Task.CompletedTask; }
        return ExportSelectedAsync(StorageDirectory, useDirectory: true);
    }
    public Task ExportSelectedAsync(string destination) => ExportSelectedAsync(destination, useDirectory: false);
    private Task ExportSelectedAsync(string destination, bool useDirectory)
    {
        var selected = _selected;
        return Operation(async () =>
        {
            var library = _library!;
            var result = useDirectory ? await library.ExportToDirectoryAsync(selected!.Id, destination, _token) : await library.ExportAsync(selected!.Id, destination, _token);
            if (result.ExportStateSaved)
            {
                var page = await library.GetPageAsync(_pageIndex, _token);
                var updated = page.Clips.FirstOrDefault(item => item.Id == selected.Id);
                if (updated is not null) selected.Update(updated);
                SetReminder(page);
            }
            NoticeText(!result.ExportStateSaved ? "The export exists, but its exported state could not be saved." : result.FileCreated ? "Clip exported." : "This clip is already in your export folder. No duplicate was created.");
        }, "The clip could not be exported. Check folder access and free space. An existing file is never overwritten.", CanExport);
    }

    public bool ConfigureShortcut(bool save, bool enabled, OverlayHotkeyChord chord)
    {
        if (!CanEditSettings) return false;
        if (!OverlayHotkeyChord.TryCreate(chord.Modifiers, chord.Key, out chord, out var error)) { ErrorText(error); return false; }
        if (enabled && (save ? _settings.ToggleShortcutEnabled && chord == _settings.ToggleShortcut : _settings.SaveShortcutEnabled && chord == _settings.SaveShortcut))
        { ErrorText("Use different shortcuts for toggling clipping and saving a clip."); return false; }
        var registrationError = _registerShortcut?.Invoke(save, enabled, chord);
        if (registrationError is not null)
        {
            _shortcutStatus = $"{(save ? "Save a clip" : "Toggle clipping")} ({chord}) unavailable: {registrationError}. Choose another shortcut in Recording settings.";
            OnChanged(nameof(ShortcutStatus));
            ErrorText($"Shortcut unchanged. {_shortcutStatus}");
            foreach (var name in new[] { nameof(ToggleShortcutEnabled), nameof(SaveShortcutEnabled), nameof(ToggleShortcutText), nameof(SaveShortcutText) }) OnChanged(name);
            return false;
        }
        if (save) { _settings.SaveShortcutEnabled = enabled; _settings.SaveShortcutModifiers = chord.Modifiers; _settings.SaveShortcutKey = chord.Key; }
        else { _settings.ToggleShortcutEnabled = enabled; _settings.ToggleShortcutModifiers = chord.Modifiers; _settings.ToggleShortcutKey = chord.Key; }
        foreach (var name in new[] { nameof(ToggleShortcutEnabled), nameof(SaveShortcutEnabled), nameof(ToggleShortcutText), nameof(SaveShortcutText) }) OnChanged(name);
        _shortcutStatus = enabled ? _registerShortcut is null ? "Shortcut configured; waiting for registration." : "Shortcut is ready." : "Shortcut disabled.";
        OnChanged(nameof(ShortcutStatus));
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SetShortcutStatus(string safeStatus) { _shortcutStatus = safeStatus; OnChanged(nameof(ShortcutStatus)); }
    internal void SetShortcutRegistration(Func<bool, bool, OverlayHotkeyChord, string?> registration) => _registerShortcut = registration;

    private async Task ReadPageAsync(int pageIndex)
    {
        if (_library is null) { Clips.Clear(); NotifyState(); return; }
        var page = await _library.GetPageAsync(pageIndex, _token);
        if (_disposed) return;
        ClearSelection(); ApplyPage(page);
        StartThumbnails();
    }
    private void ApplyPage(ClipLibraryPage page)
    {
        _pageIndex = page.PageIndex; _pageCount = page.PageCount; _total = page.TotalClips; _pending = page.PendingSaves;
        Clips.Clear();
        foreach (var entry in page.Clips)
        {
            var item = _selected?.Id == entry.Id ? _selected : new ClipCardItem(entry);
            item.Update(entry);
            Clips.Add(item);
        }
        SetReminder(page); NotifyState();
    }
    internal void SetGalleryActive(bool active)
    {
        if (_disposed || _galleryActive == active) return;
        _galleryActive = active;
        if (active) StartThumbnails(); else CancelThumbnails();
    }
    private void CancelThumbnails()
    {
        ++_thumbnailRevision;
        var work = _thumbnailWork; _thumbnailWork = null;
        work?.Cancel();
        foreach (var item in Clips) item.SetThumbnailLoading(false);
    }
    private void StartThumbnails()
    {
        CancelThumbnails();
        if (_disposed || !_galleryActive || _thumbnails is null || _library is null || Clips.Count == 0) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_token);
        _thumbnailWork = cancellation;
        ThumbnailCompletion = LoadThumbnailsAsync(_library, Clips.ToArray(), _thumbnailRevision, cancellation);
    }
    private async Task LoadThumbnailsAsync(ClipLibrary library, ClipCardItem[] cards, long revision, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        bool Current() => !_disposed && _galleryActive && revision == _thumbnailRevision && ReferenceEquals(library, _library);
        try
        {
            // Metadata loading and page navigation do not wait for this loop.
            // Cancellation reaches the one decoder; old results cannot populate a new page.
            foreach (var item in cards)
            {
                if (!Current() || token.IsCancellationRequested) return;
                if (item.HasThumbnail) continue;
                item.SetThumbnailLoading(true);
                try
                {
                    var path = await library.GetMediaPathAsync(item.Id, token);
                    var image = await _thumbnails!.LoadAsync(item.Entry, path, token);
                    if (!Current() || token.IsCancellationRequested || !Clips.Contains(item)) return;
                    item.SetThumbnail(image);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) when (error is not OutOfMemoryException) { /* Keep the accessible fallback. */ }
                finally { if (Current()) item.SetThumbnailLoading(false); }
            }
        }
        finally
        {
            if (ReferenceEquals(_thumbnailWork, cancellation)) _thumbnailWork = null;
            cancellation.Dispose();
        }
    }
    private async Task RefreshReminderAsync()
    {
        var library = _library;
        if (_disposed || library is null) return;
        var page = await library.GetPageAsync(_pageIndex, _token);
        if (!_disposed && ReferenceEquals(_library, library)) SetReminder(page);
    }
    private void SetReminder(ClipLibraryPage page)
    {
        _newClips = page.NewClips;
        OnChanged(nameof(NewClipCount)); OnChanged(nameof(HasReminder)); OnChanged(nameof(ReminderText));
        NotifyDashboardNotice();
        ReminderChanged?.Invoke(this, EventArgs.Empty);
    }
    private ClipRecordingSpec Recording() => new(LengthSeconds, ResolutionHeight, FrameRate, Quality);
    private bool ChangeChoice(int next, IReadOnlyList<int> choices, int current) => CanEditSettings && next != current && choices.Contains(next);
    private void Changed([CallerMemberName] string? name = null) { OnChanged(name); PreferencesChanged?.Invoke(this, EventArgs.Empty); }
    private void ClearSelection()
    {
        ++_selectionRevision; _selected?.Select(false); _selected = null;
        foreach (var name in new[] { nameof(SelectedClip), nameof(SelectedTitle), nameof(HasSelection), nameof(CanExport) }) OnChanged(name);
    }
    private async Task Operation(Func<Task> action, string errorText, bool allowed, Func<Exception, string?>? failureText = null)
    {
        if (!allowed || _busy || _disposed) return;
        _busy = true; ErrorText(""); NoticeText(""); NotifyState();
        try { CommitQuality(); await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { ErrorText(failureText?.Invoke(error) ?? errorText); }
        finally { _busy = false; if (!_disposed) { RefreshRecorder(); NotifyState(); } }
    }
    private void RecorderChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) RefreshRecorder();
        else _ = _dispatcher.InvokeAsync(() => { if (!_disposed) RefreshRecorder(); });
    }
    private void RefreshRecorder()
    {
        _snapshot = _recorder.Snapshot;
        if (_snapshot.State is ClipRecorderState.Disabled or ClipRecorderState.Error or ClipRecorderState.Unavailable or
            ClipRecorderState.WaitingForGame or ClipRecorderState.Paused or ClipRecorderState.Reconnecting or ClipRecorderState.Stopping)
            CancelQueuedSaves("Queued saves cancelled because recording stopped or the game became unavailable.");
        OnChanged(nameof(RecorderStatus)); OnChanged(nameof(IsRecording));
        OnChanged(nameof(FailureReport)); OnChanged(nameof(HasFailureReport));
        NotifyState();
        if (_queuedSaves > 0 && CanSave) _ = DrainSaveQueueAsync();
    }
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(IsEmpty), nameof(CanEditSettings), nameof(CanToggle), nameof(ClippingEnabled), nameof(CanSave), nameof(CanRequestSave), nameof(HasQueuedSave), nameof(HasSaveQueueStatus), nameof(SaveQueueStatus), nameof(CanBrowse), nameof(CanExport), nameof(PageText), nameof(PendingText), nameof(CanOpenClipFolder) }) OnChanged(name);
        NotifyDashboardNotice();
        foreach (var command in _commands) command.Raise();
    }
    private void ErrorText(string text) { _error = text; OnChanged(nameof(Error)); OnChanged(nameof(HasError)); NotifyDashboardNotice(); }
    private void NotifyDashboardNotice()
    {
        OnChanged(nameof(HasDashboardNotice)); OnChanged(nameof(DashboardNoticeTitle)); OnChanged(nameof(DashboardNoticeText));
    }
    private void NoticeText(string text) { _notice = text; OnChanged(nameof(Notice)); }
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private ICommand Command(Func<Task> execute, Func<bool> canExecute)
    {
        var command = new ClipCommand(execute, canExecute); _commands.Add(command); return command;
    }
    public void Dispose()
    {
        if (_disposed) return;
        CommitQuality(); CancelThumbnails(); _queuedSaves = 0; _disposed = true; ShortcutCaptureActive = false;
        _recorder.StateChanged -= RecorderChanged; _lifetime.Cancel(); _lifetime.Dispose(); ClearSelection();
        if (_thumbnails is IDisposable ownedThumbnails) ownedThumbnails.Dispose();
        // Operations retain the captured token, never access a disposed token source.
    }
    private sealed class ClipCommand(Func<Task> action, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) { if (CanExecute(parameter)) await action(); }
        public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
