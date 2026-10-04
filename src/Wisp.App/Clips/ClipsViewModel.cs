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
    public string Title => Entry.Name ?? SavedTitle;
    public string SavedTitle => Entry.SavedAtUtc.ToLocalTime().ToString("MMM d · h:mm tt");
    public bool HasName => Entry.Name is not null;
    public string Detail => $"{TimeSpan.FromSeconds(Entry.DurationSeconds):m\\:ss} · {Entry.Media.Height}p · {Entry.Media.FrameRate} fps" +
        (Entry.Media.LosslessVideo ? " · Lossless video" : "") + (Entry.Media.HdrVideo ? " · HDR" : "");
    public string ReviewState => Entry.ExportedAtUtc is not null ? "Exported" : Entry.ViewedAtUtc is not null ? "Viewed" : "New";
    public string PlayLabel => HasName ? $"Open clip {Title}, saved {SavedTitle}, {Detail}" : $"Open clip from {Title}, {Detail}";
    public BitmapSource? Thumbnail => _thumbnail;
    public bool HasThumbnail => _thumbnail is not null;
    public string PreviewStatus => _thumbnailLoading ? "Loading preview…" : "Preview unavailable";
    public bool IsSelected => _selected;
    internal void Select(bool selected) { _selected = selected; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    internal void Update(ClipEntry updated)
    {
        Entry = updated;
        foreach (var name in new[] { nameof(Title), nameof(SavedTitle), nameof(HasName), nameof(Detail), nameof(PlayLabel), nameof(ReviewState) })
            PropertyChanged?.Invoke(this, new(name));
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
    private readonly Queue<bool> _saveShortcuts = new();
    private int QueuedSaveCount => _saveShortcuts.Count;
    private string _saveQueueNotice = "";
    private Task? _initialization;
    private Func<bool, bool, OverlayHotkeyChord, string?>? _registerShortcut;
    private string _error = "", _notice = "", _shortcutStatus = "Shortcuts are off.";
    private string _libraryWarning = "";
    private string? _dismissedDashboardNotice;
    private string _previewExportStatus = "";
    private string _exportFailureDetails = "";
    private bool _previewExportSucceeded;
    private bool _preparingPlaybackCopy;
    private CancellationTokenSource? _exportWork;
    private bool _exportProgressKnown;
    private double _exportProgress;
    private int _pageIndex, _pageCount, _total, _pending, _pendingNotices, _newClips;
    private long _selectionRevision;
    private CancellationTokenSource? _thumbnailWork;
    private long _thumbnailRevision;
    private bool _galleryActive;
    private string _search = "";
    private DispatcherTimer? _searchTimer;
    private int _matching;
    private ClipCardItem? _managed;
    private bool _renaming, _confirmingDelete, _choosingExport;
    private string _renameText = "";
    internal const int MaximumSearchLength = 100;
    internal static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);
    internal Task ThumbnailCompletion { get; private set; } = Task.CompletedTask;

    public ClipsViewModel(ClipsSettings settings, IClipRecorder recorder, Dispatcher dispatcher, IClipThumbnailProvider? thumbnails = null,
        string? libraryDirectory = null, ICompatibleClipExporter? compatibleExporter = null)
    {
        _token = _lifetime.Token; _settings = settings.Clone(); _settings.Normalize();
        _recorder = recorder; _dispatcher = dispatcher; _thumbnails = thumbnails;
        _snapshot = recorder.Snapshot;
        _libraryDirectory = libraryDirectory ?? ClipsSettings.DefaultLibraryDirectory;
        if (_libraryDirectory.Length > 0) _library = new ClipLibrary(_libraryDirectory, compatibleExporter);
        SaveClipCommand = Command(SaveClipAsync, () => CanRequestSave);
        PreviousPageCommand = Command(() => LoadPageAsync(_pageIndex - 1), () => !IsBusy && _pageIndex > 0);
        NextPageCommand = Command(() => LoadPageAsync(_pageIndex + 1), () => !IsBusy && _pageIndex + 1 < _pageCount);
        RefreshCommand = Command(() => LoadPageAsync(_pageIndex), () => !IsBusy && _library is not null);
        RecoverPendingCommand = Command(RecoverPendingAsync, () => CanRecoverPending);
        DismissPendingCommand = Command(DismissPendingAsync, () => !IsBusy && _pendingNotices > 0);
        DismissDashboardNoticeCommand = Command(DismissDashboardNoticeAsync, () => !_disposed && HasDashboardNotice && (!PendingDashboardNotice || !IsBusy));
        DisableDashboardNoticesCommand = Command(() => { RemindersEnabled = false; return Task.CompletedTask; }, () => !_disposed && CanDisableDashboardNotice);
        ClearSearchCommand = Command(() => { SearchText = ""; return ApplySearchAsync(); }, () => !_disposed && _search.Length > 0);
        ConfirmRenameCommand = Command(ConfirmRenameAsync, () => CanManageClips && _renaming && _managed is not null);
        ConfirmDeleteCommand = Command(ConfirmDeleteAsync, () => CanManageClips && _confirmingDelete && _managed is not null);
        CancelManagementCommand = Command(() => { CancelManagement(); return Task.CompletedTask; }, () => !_disposed && HasManagement);
        _recorder.StateChanged += RecorderChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? PreferencesChanged;
    public event EventHandler? ReminderChanged;
    public event Action<ClipShortcutFeedbackKind>? ShortcutFeedbackRequested;
    public ObservableCollection<ClipCardItem> Clips { get; } = [];
    public ICommand SaveClipCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RecoverPendingCommand { get; }
    public ICommand DismissPendingCommand { get; }
    public ICommand DismissDashboardNoticeCommand { get; }
    public ICommand DisableDashboardNoticesCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ConfirmRenameCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelManagementCommand { get; }
    public ClipsSettings Preferences => _settings.Clone();
    public IReadOnlyList<int> LengthChoices => ClipsSettings.LengthChoices;
    public IReadOnlyList<int> ResolutionChoices => ClipsSettings.ResolutionChoices;
    public IReadOnlyList<int> FrameRateChoices => ClipsSettings.FrameRateChoices;
    public bool ShortcutCaptureActive { get; set; }
    public bool IsBusy => _busy;
    public bool CanEditSettings => !_disposed && !IsBusy && !_snapshot.Enabled;
    public bool CanEditCompressionQuality => CanEditSettings && !LosslessVideo;
    public bool CanToggle => !_disposed && _runtimeActive && !IsBusy && (_snapshot.Enabled || (_snapshot.CanEnable && _library is not null));
    public bool ClippingEnabled => _snapshot.Enabled;
    public bool IsRecording => _snapshot.State == ClipRecorderState.Buffering;
    public bool CanBrowse => !_disposed && !IsBusy;
    public bool CanSave => !_disposed && _runtimeActive && !IsBusy && _library is not null && _snapshot.CanSave;
    public bool CanRequestSave => !_disposed && _runtimeActive && _library is not null && _snapshot.Enabled &&
        (!IsBusy || _saveActive) && QueuedSaveCount + (_saveActive ? 1 : 0) < 2 &&
        (_snapshot.State is ClipRecorderState.Preparing or ClipRecorderState.Buffering or ClipRecorderState.Saving ||
         _snapshot.State == ClipRecorderState.Paused && _snapshot.CanSave);
    public bool HasQueuedSave => QueuedSaveCount > 0;
    public bool HasSaveQueueStatus => QueuedSaveCount > 0 || _saveQueueNotice.Length > 0;
    public string SaveQueueStatus => QueuedSaveCount == 0 ? _saveQueueNotice : _saveActive
        ? "Another clip save is queued. It will end when saving starts."
        : $"{QueuedSaveCount} clip save{(QueuedSaveCount == 1 ? "" : "s")} queued until recording is ready. Queued clips end when saving starts.";
    public string RecorderStatus => _snapshot.Status;
    public string FailureReport => _recorder.FailureReport;
    public bool HasFailureReport => FailureReport.Length > 0;
    public string Error => _libraryWarning.Length == 0 ? _error : _error.Length == 0 ? _libraryWarning : _libraryWarning + " " + _error;
    public bool HasError => _error.Length > 0 || _libraryWarning.Length > 0;
    public string Notice => _notice;
    public string StorageDirectory => _settings.StorageDirectory;
    public string SuggestedStorageDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Wisp Clips");
    public string StorageText => StorageDirectory.Length == 0 ? "No export folder selected. Saved clips stay in Wisp until you export them." : StorageDirectory;
    public string LongClipHint => LosslessVideo
        ? $"Lossless clips keep up to {LengthSeconds} seconds, within Wisp's size limit. Available history depends on the video and free space. Export can create a compatible copy or keep the original lossless video."
        : LengthSeconds == 300
        ? "Five-minute clips use more memory and storage and can take longer to save. Original exports keep recorded quality; compatible copies use H.264 for easier sharing."
        : "Longer clips use more memory and storage. Original exports keep recorded quality; compatible copies use H.264 for easier sharing.";
    public string PageText => HasActiveSearch
        ? _pageCount == 0 ? "No matching clips" : $"Page {_pageIndex + 1} of {_pageCount} · {_matching} of {_total} clips"
        : _pageCount == 0 ? "No saved clips" : $"Page {_pageIndex + 1} of {_pageCount} · {_total} clips";
    public string EmptyText => HasActiveSearch
        ? $"No saved clips match “{ActiveSearch}”. Try a name, a date like Oct 3, or 1080p, HDR or lossless."
        : "No clips saved yet. Enable clipping, then save a moment from your drive to watch it here.";
    public string SearchText
    {
        get => _search;
        set
        {
            var next = value ?? "";
            if (next.Length > MaximumSearchLength) next = next[..MaximumSearchLength];
            if (_search == next || _disposed) return;
            _search = next;
            OnChanged(); OnChanged(nameof(HasSearchText));
            ((ClipCommand)ClearSearchCommand).Raise();
            ScheduleSearch();
        }
    }
    public bool HasSearchText => _search.Length > 0;
    private string ActiveSearch => _search.Trim();
    private bool HasActiveSearch => ActiveSearch.Length > 0;
    public bool CanManageClips => !_disposed && !IsBusy && !IsExporting && _library is not null;
    public bool HasManagement => _renaming || _confirmingDelete;
    public bool IsRenaming => _renaming;
    public bool IsConfirmingDelete => _confirmingDelete;
    public string ManagementTitle => _managed is null ? "" : _confirmingDelete ? $"Delete “{_managed.Title}”?" : $"Rename “{_managed.Title}”";
    public string DeleteDetail => "The clip and its video file are removed from Wisp. Copies you exported are not affected. This can't be undone.";
    public string RenameText
    {
        get => _renameText;
        set { var next = value ?? ""; if (_renameText == next) return; _renameText = next; OnChanged(); }
    }
    public bool IsChoosingExportFormat => _choosingExport;
    public bool RequiresExportChoice => _selected?.Entry.Media.RequiresMpvPlayer == true;
    public string CompatibleExportDescription => _selected?.Entry.Media.HdrVideo == true
        ? "Standard H.264 MP4, converted to SDR. Plays in browsers, Discord, phones and Windows apps. Wisp converts it first, which can take a while for long clips."
        : "Standard H.264 MP4. Plays in browsers, Discord, phones and Windows apps. Wisp converts it first, which can take a while for long clips.";
    public string OriginalExportTitle => _selected?.Entry.Media is { HdrVideo: true, LosslessVideo: true } ? "Original lossless HDR recording"
        : _selected?.Entry.Media.LosslessVideo == true ? "Original lossless recording" : "Original HDR recording";
    public string OriginalExportDescription => _selected?.Entry.Media is { HdrVideo: true, LosslessVideo: true }
        ? "The exact HDR recording (HEVC 4:4:4, 10-bit). Largest file, for editing software that supports HDR. Most players can't open it."
        : _selected?.Entry.Media.LosslessVideo == true
        ? "The exact recording (H.264 4:4:4), the best quality for editing. Large file. VLC, Windows apps, browsers and Discord may not play it correctly."
        : "The HDR recording (HEVC 10-bit), for HDR screens and HDR editing. Many apps can't play it or show it washed out.";
    public string PendingText => _pending == 0 ? "" : $"{_pending} unfinished save(s) retained in Wisp's private clip storage. Your completed clips are kept.";
    public bool HasPendingSaves => _pending > 0;
    public bool CanRecoverPending => !_disposed && !IsBusy && !_snapshot.Enabled && _library is not null && HasPendingSaves;
    public string PendingRecoveryHint => _snapshot.Enabled ? "Turn clipping off before recovering unfinished saves. Dismissing this notice keeps the files."
        : "Recover finished video or clear empty saves. Dismissing this notice keeps the files.";
    public bool CanOpenClipFolder => !_disposed && !IsBusy && StorageDirectory.Length > 0;
    public bool IsEmpty => !IsBusy && Clips.Count == 0;
    // Hidden once the library has clips; the empty-state text covers the first save.
    public bool ShowsManagementHint => _total > 0 && !HasManagement;
    public bool HasSelection => _selected is not null;
    public bool CanExport => HasSelection && !IsBusy && !_previewExportSucceeded && !_preparingPlaybackCopy;
    public bool SelectedIsLossless => _selected?.Entry.Media.LosslessVideo == true;
    public bool HasPlaybackFormatNote => _selected?.Entry.Media.RequiresMpvPlayer == true;
    public string LosslessPlaybackNote => _selected?.Entry.Media.HdrVideo == true
        ? SelectedIsLossless
            ? "Playback uses a compressed HDR copy. The original lossless clip is unchanged and can be exported."
            : "Play HDR video in Wisp, export the original, or choose a compatible H.264 copy for sharing in SDR."
        : "Original lossless video requires a player that supports H.264 4:4:4. Play it in Wisp or mpv, or export a compatible MP4 copy. The original stays in Wisp.";
    public bool IsExporting => _exportWork is not null;
    public bool CanCancelExport => _exportWork is { IsCancellationRequested: false };
    public bool ExportProgressIndeterminate => !_exportProgressKnown;
    public double ExportProgress => _exportProgress;
    public string ExportProgressText => !CanCancelExport ? "Cancelling…" : _exportProgressKnown ? $"{_exportProgress:0}%" : "Copying…";
    public string PreviewExportStatus => _previewExportStatus;
    public bool HasPreviewExportStatus => _previewExportStatus.Length > 0;
    public string ExportFailureDetails => _exportFailureDetails;
    public bool HasExportFailureDetails => _exportFailureDetails.Length > 0;
    public ClipCardItem? SelectedClip => _selected;
    public string SelectedTitle => _selected?.Title ?? "Choose a clip to play";
    public int NewClipCount => _newClips;
    public bool HasReminder => _settings.RemindersEnabled && _newClips > 0;
    public string ReminderText => _newClips == 1 ? "You have a new clip to review or export." : $"You have {_newClips} new clips to review or export.";
    private bool RecorderNeedsAttention => _snapshot.State == ClipRecorderState.Error;
    private string? DashboardNoticeKey => HasError ? $"error:{Error}" : RecorderNeedsAttention ? $"recorder:{RecorderStatus}"
        : HasReminder ? $"new:{_newClips}" : _pendingNotices > 0 ? $"pending:{_pendingNotices}:{_pending}" : null;
    private bool PendingDashboardNotice => !HasError && !RecorderNeedsAttention && !HasReminder && _pendingNotices > 0;
    public bool HasDashboardNotice => DashboardNoticeKey is { } key && key != _dismissedDashboardNotice;
    public bool CanDisableDashboardNotice => HasDashboardNotice && !HasError && !RecorderNeedsAttention && HasReminder;
    public string DashboardNoticeTitle => HasError || RecorderNeedsAttention ? "Clips need attention" : HasReminder ? "New clips" : "Unfinished clip saves";
    public string DashboardNoticeText => HasError ? Error : RecorderNeedsAttention ? RecorderStatus : HasReminder ? ReminderText : PendingText;
    public string ShortcutStatus => _shortcutStatus;
    public string ToggleShortcutText => _settings.ToggleShortcut.ToString();
    public string SaveShortcutText => _settings.SaveShortcut.ToString();
    public bool ToggleShortcutEnabled { get => _settings.ToggleShortcutEnabled; set => ConfigureShortcut(false, value, _settings.ToggleShortcut); }
    public bool SaveShortcutEnabled { get => _settings.SaveShortcutEnabled; set => ConfigureShortcut(true, value, _settings.SaveShortcut); }
    public bool ShortcutSoundsEnabled
    {
        get => _settings.ShortcutSoundsEnabled;
        set { if (!_disposed && value != _settings.ShortcutSoundsEnabled) { _settings.ShortcutSoundsEnabled = value; Changed(); } }
    }
    public int LengthSeconds { get => _settings.LengthSeconds; set { if (ChangeChoice(value, LengthChoices, _settings.LengthSeconds)) { _settings.LengthSeconds = value; Changed(); OnChanged(nameof(LongClipHint)); } } }
    public int ResolutionHeight { get => _settings.ResolutionHeight; set { if (ChangeChoice(value, ResolutionChoices, _settings.ResolutionHeight)) { _settings.ResolutionHeight = value; Changed(); } } }
    public int FrameRate { get => _settings.FrameRate; set { if (ChangeChoice(value, FrameRateChoices, _settings.FrameRate)) { _settings.FrameRate = value; Changed(); } } }
    public bool CaptureSystemAudio
    {
        get => _settings.CaptureSystemAudio;
        set
        {
            if (!CanEditSettings || value == _settings.CaptureSystemAudio) return;
            _settings.CaptureSystemAudio = value;
            Changed();
        }
    }
    public int Quality
    {
        get => _settings.Quality;
        set { if (CanEditCompressionQuality && value is >= 10 and <= 100 && value != _settings.Quality) { _settings.Quality = value; _qualityPending = true; OnChanged(); } }
    }
    public bool LosslessVideo
    {
        get => _settings.LosslessVideo;
        set
        {
            if (!CanEditSettings || value == _settings.LosslessVideo) return;
            CommitQuality();
            _settings.LosslessVideo = value;
            Changed();
            OnChanged(nameof(CanEditCompressionQuality));
            OnChanged(nameof(LongClipHint));
        }
    }
    public bool PreserveHdrRecording
    {
        get => _settings.PreserveHdrRecording;
        set
        {
            if (!CanEditSettings || value == _settings.PreserveHdrRecording) return;
            _settings.PreserveHdrRecording = value;
            Changed();
        }
    }
    public bool RemindersEnabled
    {
        get => _settings.RemindersEnabled;
        set
        {
            if (_disposed || _settings.RemindersEnabled == value) return;
            _settings.RemindersEnabled = value;
            if (_dismissedDashboardNotice?.StartsWith("new:", StringComparison.Ordinal) == true) _dismissedDashboardNotice = null;
            Changed(); OnChanged(nameof(HasReminder)); NotifyDashboardNotice(); ReminderChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal Task DismissDashboardNoticeAsync()
    {
        if (_disposed || !HasDashboardNotice) return Task.CompletedTask;
        if (PendingDashboardNotice)
            return Operation(async () =>
            {
                await _library!.DismissPendingNoticesAsync(_token);
                await RefreshReminderAsync();
            }, "The notice could not be dismissed. Existing files are kept.", !IsBusy && _library is not null);
        _dismissedDashboardNotice = DashboardNoticeKey;
        NotifyDashboardNotice();
        ReminderChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
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
                NoticeText(imported > 0 ? $"{imported} existing clip(s) added to Wisp. Original files are kept." : "");
                if (result.PendingLegacySaves > 0)
                    LibraryWarningText("The previous clip folder contains unfinished saves. Those files have been kept.");
                else
                {
                    _settings.LegacyLibraryDirectory = "";
                    PreferencesChanged?.Invoke(this, EventArgs.Empty);
                }
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

    public Task ToggleAsync() => Operation(async () =>
    {
        var next = !_snapshot.Enabled;
        if (next && !ClipsSettings.CanRecordToDirectory(_libraryDirectory))
        { ErrorText(ClipsSettings.RecordingPathTooLongMessage); return; }
        if (next && await IsNetworkStorageAsync())
        { ErrorText("Clip recording needs a local folder. Network drives are not supported."); return; }
        if (_disposed || !_runtimeActive) return;
        await _recorder.SetEnabledAsync(next, Recording(), _token);
        RefreshRecorder();
        if (_snapshot.Enabled != next) { ErrorText("Clipping did not change state. Check the recorder status."); return; }
        if (!next) PersistEnabled(false);
    }, "Clipping could not change state. Check the recorder status.", CanToggle);

    public async Task ToggleFromShortcutAsync()
    {
        if (_disposed || !_runtimeActive) return;
        if (!CanToggle) { NotifyShortcutFeedback(ClipShortcutFeedbackKind.Failed); return; }
        var next = !_snapshot.Enabled;
        await ToggleAsync();
        NotifyShortcutFeedback(_snapshot.Enabled != next ? ClipShortcutFeedbackKind.Failed :
            next ? ClipShortcutFeedbackKind.Enabled : ClipShortcutFeedbackKind.Disabled);
    }

    internal void SetRuntimeActive(bool active)
    {
        _runtimeActive = active;
        if (!active) CancelQueuedSaves("Queued saves cancelled because clipping is paused.");
        NotifyState();
    }
    internal async Task RestoreEnabledPreferenceAsync()
    {
        if (!_settings.Enabled || _snapshot.Enabled) return;
        await ToggleAsync();
        if (!_snapshot.Enabled) PersistEnabled(false);
    }

    private void PersistEnabled(bool enabled)
    {
        if (_settings.Enabled == enabled) return;
        _settings.Enabled = enabled;
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }
    private Task<bool> IsNetworkStorageAsync() => Task.Run(() =>
        _libraryDirectory.StartsWith(@"\\", StringComparison.Ordinal) ||
        new DriveInfo(Path.GetPathRoot(_libraryDirectory)!).DriveType == DriveType.Network, _token);

    public Task SaveClipAsync() => RequestSaveAsync(false);
    public Task SaveClipFromShortcutAsync() => RequestSaveAsync(true);

    private Task RequestSaveAsync(bool fromShortcut)
    {
        if (_disposed || !_runtimeActive) return Task.CompletedTask;
        if (!CanRequestSave)
        {
            if (_snapshot.State is ClipRecorderState.Error or ClipRecorderState.Unavailable) ErrorText(RecorderStatus);
            else if (QueuedSaveCount + (_saveActive ? 1 : 0) >= 2) NoticeText("Two clip saves are already pending. Wait for them to finish before saving another.");
            else if (_snapshot.State == ClipRecorderState.Saving) NoticeText(ClipRecorderService.ReasonText("save_in_progress"));
            else if (IsBusy) NoticeText("Wisp is busy with another clip action. Wait for it to finish, then save again.");
            else if (_library is null) ErrorText("Wisp's clip library is unavailable. Check the storage status.");
            else if (!_snapshot.Enabled) ErrorText("Clipping is off. Enable clipping before saving a clip.");
            else ErrorText("No clip is ready yet. " + RecorderStatus);
            if (fromShortcut) NotifyShortcutFeedback(ClipShortcutFeedbackKind.Failed);
            return Task.CompletedTask;
        }
        _saveQueueNotice = ""; _saveShortcuts.Enqueue(fromShortcut);
        NotifyState();
        return DrainSaveQueueAsync();
    }

    private async Task DrainSaveQueueAsync()
    {
        if (_drainingSaves || QueuedSaveCount == 0 || !CanSave) return;
        _drainingSaves = true;
        try
        {
            while (QueuedSaveCount > 0 && CanSave)
            {
                var fromShortcut = _saveShortcuts.Dequeue(); _saveActive = true;
                NotifyState();
                var saved = await SaveOneClipAsync();
                _saveActive = false;
                if (fromShortcut) NotifyShortcutFeedback(saved ? ClipShortcutFeedbackKind.Saved : ClipShortcutFeedbackKind.Failed);
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
            catch (Exception error)
            {
                if (error is RecorderClientException { SaveCompletedWithoutMedia: true })
                {
                    try { await library.DropEmptyReservationAsync(target.Id, CancellationToken.None); }
                    catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException) { }
                }
                if (!_disposed) await ReadPageAsync(0);
                throw;
            }
            // The completed card appears before optional disk reconciliation/poster work.
            var duration = TimeSpan.FromTicks(media.ActualEnd100ns - media.ActualStart100ns).TotalSeconds;
            NoticeText($"Clip saved · {duration:0.0} s. " + (media.SizeLimited ? "Shortened by the lossless size limit. " : "") + (media.HasAudio
                ? "Select it below to watch or export."
                : "Game audio was unavailable. Select it below to watch or export."));
            try
            {
                var page = await library.GetPageAsync(0, ActiveSearch, _token);
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
        // While searching, the refresh that follows decides whether the new clip matches.
        if (!HasActiveSearch) Clips.Insert(0, new(entry));
        while (Clips.Count > ClipLibrary.PageSize) Clips.RemoveAt(Clips.Count - 1);
        _total++; _pageCount = (_total + ClipLibrary.PageSize - 1) / ClipLibrary.PageSize;
        _newClips++;
        OnChanged(nameof(NewClipCount)); OnChanged(nameof(HasReminder)); OnChanged(nameof(ReminderText));
        NotifyState(); ReminderChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelQueuedSaves(string message)
    {
        if (QueuedSaveCount == 0) return;
        var hadShortcut = _saveShortcuts.Contains(true);
        _saveShortcuts.Clear();
        _saveQueueNotice = message;
        if (hadShortcut) NotifyShortcutFeedback(ClipShortcutFeedbackKind.Failed);
    }

    private void NotifyShortcutFeedback(ClipShortcutFeedbackKind outcome)
    {
        if (!_disposed && _runtimeActive && ShortcutSoundsEnabled) ShortcutFeedbackRequested?.Invoke(outcome);
    }

    private string? SaveFailureText(Exception error)
    {
        var current = _recorder.Snapshot;
        if (current.State == ClipRecorderState.Error) return current.Status;
        return error is RecorderClientException recorder ? ClipRecorderService.ReasonText(recorder.Reason) : null;
    }

    public Task LoadPageAsync(int pageIndex) => Operation(() => ReadPageAsync(Math.Max(0, pageIndex)),
        "The clip library could not be read. Its files have been kept.", !IsBusy && !_disposed);

    private void ScheduleSearch()
    {
        if (_searchTimer is null)
        {
            _searchTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = SearchDelay };
            _searchTimer.Tick += async (_, _) =>
            {
                // Retry on the next tick while another clip action is running.
                if (_disposed || IsBusy) return;
                _searchTimer?.Stop();
                await ApplySearchAsync();
            };
        }
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    internal Task ApplySearchAsync()
    {
        _searchTimer?.Stop();
        return Operation(() => ReadPageAsync(0), "The clip library could not be searched. Its files have been kept.",
            !_disposed && _library is not null && !IsBusy);
    }

    public void BeginRename(ClipCardItem item)
    {
        if (!CanManageClips || !Clips.Contains(item)) return;
        _managed = item; _renaming = true; _confirmingDelete = false;
        _renameText = item.Entry.Name ?? "";
        NotifyManagement(); OnChanged(nameof(RenameText));
    }

    public void BeginDelete(ClipCardItem item)
    {
        if (!CanManageClips || !Clips.Contains(item)) return;
        _managed = item; _renaming = false; _confirmingDelete = true;
        NotifyManagement();
    }

    public void CancelManagement()
    {
        if (!HasManagement && _managed is null) return;
        _managed = null; _renaming = false; _confirmingDelete = false;
        NotifyManagement();
    }

    internal Task ConfirmRenameAsync()
    {
        var item = _managed;
        var text = _renameText;
        return Operation(async () =>
        {
            var updated = await _library!.RenameAsync(item!.Id, text, _token);
            if (_disposed) return;
            item.Update(updated);
            if (ReferenceEquals(_selected, item)) OnChanged(nameof(SelectedTitle));
            CancelManagement();
            NoticeText(updated.Name is null ? "Clip name cleared. It shows its saved date and time again." : "Clip renamed.");
            if (HasActiveSearch) await ReadPageAsync(_pageIndex);
        }, "The clip could not be renamed. Its file has been kept.", CanManageClips && _renaming && item is not null,
        error => error is ArgumentException ? $"Use a name of up to {ClipLibrary.MaximumNameLength} characters on one line." : null);
    }

    internal Task ConfirmDeleteAsync()
    {
        var item = _managed;
        return Operation(async () =>
        {
            var wasSelected = ReferenceEquals(_selected, item);
            // Closing the player and stopping preview work releases the file before deletion.
            if (wasSelected) ClearSelection();
            CancelThumbnails();
            await DeleteReleasedAsync(item!.Id);
            if (_disposed) return;
            CancelManagement();
            await ReadPageAsync(_pageIndex);
            NoticeText("Clip deleted.");
        }, "The clip could not be deleted. Its file has been kept.", CanManageClips && _confirmingDelete && item is not null,
        error => ClipLibraryFiles.IsInUse(error)
            ? "This clip is still in use, possibly by another program. Close anything playing it and try again. Its file has been kept."
            : null);
    }

    // The player and thumbnail decoder close asynchronously; allow them a moment to release the file.
    private async Task DeleteReleasedAsync(Guid id)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await _library!.DeleteAsync(id, _token); return; }
            catch (IOException error) when (ClipLibraryFiles.IsInUse(error) && attempt < 5)
            { await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), _token); }
        }
    }

    public void BeginExportChoice()
    {
        if (!CanExport || !RequiresExportChoice) return;
        _choosingExport = true; NotifyExportChoice();
    }

    public void CancelExportChoice()
    {
        if (!_choosingExport) return;
        _choosingExport = false; NotifyExportChoice();
    }

    private void NotifyExportChoice()
    {
        foreach (var name in new[] { nameof(IsChoosingExportFormat), nameof(RequiresExportChoice), nameof(CompatibleExportDescription),
            nameof(OriginalExportTitle), nameof(OriginalExportDescription) }) OnChanged(name);
    }

    private void NotifyManagement()
    {
        foreach (var name in new[] { nameof(HasManagement), nameof(IsRenaming), nameof(IsConfirmingDelete), nameof(ManagementTitle),
            nameof(ShowsManagementHint) }) OnChanged(name);
        foreach (var command in new[] { ConfirmRenameCommand, ConfirmDeleteCommand, CancelManagementCommand }) ((ClipCommand)command).Raise();
    }

    internal Task RefreshGalleryOnActivationAsync() => Operation(() => ReadPageAsync(_pageIndex),
        "The clip library could not be read. Its files have been kept.",
        _initialization is { IsCompletedSuccessfully: true } && _galleryActive && !HasSelection && !IsBusy && _library is not null);

    public Task RecoverPendingAsync() => Operation(async () =>
    {
        var result = await _library!.ReconcilePendingAsync(_token);
        await ReadPageAsync(0);
        NoticeText($"Recovered {result.Recovered} clip(s); cleared {result.RemovedEmpty} empty save(s). " +
            (result.Remaining == 0 ? "No unfinished saves remain." : $"{result.Remaining} save(s) remain. You can try recovery again; existing files are kept."));
    }, "Recovery could not finish. Existing files are kept; check free space and try again.", CanRecoverPending);

    public Task DismissPendingAsync() => Operation(async () =>
    {
        await _library!.DismissPendingNoticesAsync(_token);
        await ReadPageAsync(_pageIndex);
        NoticeText("Unfinished-save notice dismissed. The files are kept and recovery is still available.");
    }, "The notice could not be dismissed. Existing files are kept.", !_disposed && !IsBusy && _library is not null && _pendingNotices > 0);

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
            CancelExportChoice();
            SetPreparingPlaybackCopy(false);
            SetPreviewExportStatus("", false);
            foreach (var name in new[] { nameof(SelectedClip), nameof(SelectedTitle), nameof(HasSelection), nameof(CanExport), nameof(SelectedIsLossless), nameof(HasPlaybackFormatNote), nameof(LosslessPlaybackNote) }) OnChanged(name);
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
        ? "Details copied. You can include them when reporting this problem."
        : "The clipboard is busy. Try copying the details again.");
    public void ClosePlayback() => ClearSelection();

    public Task ExportSelectedToFolderAsync()
    {
        if (!CanExport) return Task.CompletedTask;
        if (StorageDirectory.Length == 0)
        {
            const string message = "Export failed. Choose an export folder at the bottom of Clips, then try again.";
            SetPreviewExportStatus(message, false); ErrorText(message);
            return Task.CompletedTask;
        }
        return ExportSelectedAsync(StorageDirectory, useDirectory: true);
    }
    public Task ExportSelectedAsync(string destination, ClipExportFormat format = ClipExportFormat.Original) => ExportSelectedAsync(destination, useDirectory: false, format);
    public void CancelExport()
    {
        if (!CanCancelExport) return;
        _exportWork!.Cancel(); NotifyExport();
    }
    private Task ExportSelectedAsync(string destination, bool useDirectory, ClipExportFormat format = ClipExportFormat.Original)
    {
        var selected = _selected;
        var revision = _selectionRevision;
        var failure = format == ClipExportFormat.Compatible
            ? "Export failed. The compatible copy could not be created. Try again, or export the original video. Existing files are kept."
            : "Export failed. Check folder access and free space, then try again. Existing files are never overwritten.";
        CancelExportChoice();
        return Operation(async () =>
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_token);
            _exportWork = operation; _exportProgress = 0; _exportProgressKnown = format == ClipExportFormat.Compatible;
            NotifyExport();
            try
            {
                if (revision == _selectionRevision && !_disposed) SetPreviewExportStatus("Exporting clip…", false);
                var library = _library!;
                var progress = new Progress<double>(value =>
                {
                    if (_disposed || !ReferenceEquals(_exportWork, operation) || !double.IsFinite(value)) return;
                    _exportProgress = Math.Max(_exportProgress, Math.Clamp(value, 0, 100)); NotifyExport();
                });
                var result = format == ClipExportFormat.Compatible
                    ? await library.ExportCompatibleAsync(selected!.Id, destination, progress, operation.Token)
                    : useDirectory ? await library.ExportToDirectoryAsync(selected!.Id, destination, operation.Token)
                    : await library.ExportAsync(selected!.Id, destination, operation.Token);
                var message = !result.ExportStateSaved ? "Export successful. The file is ready, but Wisp could not update its saved status."
                    : result.FileCreated ? "Export successful." : "Export successful. This clip is already in your export folder; no duplicate was created.";
                if (revision == _selectionRevision && !_disposed) SetPreviewExportStatus(message, true);
                NoticeText(message);
                if (result.ExportStateSaved)
                {
                    try
                    {
                        var page = await library.GetPageAsync(_pageIndex, ActiveSearch, _token);
                        var updated = page.Clips.FirstOrDefault(item => item.Id == selected.Id);
                        if (updated is not null) selected.Update(updated);
                        SetReminder(page);
                    }
                    catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        const string saved = "Export successful. The file is ready, but Wisp could not refresh its saved status.";
                        if (revision == _selectionRevision && !_disposed) SetPreviewExportStatus(saved, true);
                        NoticeText(saved);
                    }
                }
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested && !_token.IsCancellationRequested)
            {
                const string cancelled = "Export cancelled. The original clip is kept.";
                if (revision == _selectionRevision && !_disposed) SetPreviewExportStatus(cancelled, false);
                if (!_disposed) NoticeText(cancelled);
            }
            finally { _exportWork = null; if (!_disposed) NotifyExport(); }
        }, failure, CanExport, error =>
        {
            if (revision != _selectionRevision || _disposed || selected is null) return "";
            var report = ClipExportFailureReport.Create(error, selected.Entry, format);
            SetPreviewExportStatus(report.Message, false);
            SetExportFailureDetails(report.Details);
            return report.Message;
        });
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
        var page = await _library.GetPageAsync(pageIndex, ActiveSearch, _token);
        if (_disposed) return;
        ClearSelection(); ApplyPage(page);
        StartThumbnails();
    }
    private void ApplyPage(ClipLibraryPage page)
    {
        _pageIndex = page.PageIndex; _pageCount = page.PageCount; _total = page.TotalClips; _pending = page.PendingSaves;
        _matching = page.MatchingClips;
        Clips.Clear();
        foreach (var entry in page.Clips)
        {
            var item = _selected?.Id == entry.Id ? _selected : _managed?.Id == entry.Id ? _managed : new ClipCardItem(entry);
            item.Update(entry);
            Clips.Add(item);
        }
        // A rename or delete stays open only while its clip is still listed.
        if (_managed is not null && !Clips.Contains(_managed)) CancelManagement();
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
        _pendingNotices = page.PendingNotices;
        _newClips = page.NewClips;
        OnChanged(nameof(NewClipCount)); OnChanged(nameof(HasReminder)); OnChanged(nameof(ReminderText));
        NotifyDashboardNotice();
        ReminderChanged?.Invoke(this, EventArgs.Empty);
    }
    private ClipRecordingSpec Recording() => new(LengthSeconds, ResolutionHeight, FrameRate, Quality, CaptureSystemAudio, LosslessVideo, PreserveHdrRecording);
    private bool ChangeChoice(int next, IReadOnlyList<int> choices, int current) => CanEditSettings && next != current && choices.Contains(next);
    private void Changed([CallerMemberName] string? name = null) { OnChanged(name); PreferencesChanged?.Invoke(this, EventArgs.Empty); }
    private void ClearSelection()
    {
        ++_selectionRevision; _selected?.Select(false); _selected = null;
        CancelExportChoice();
        SetPreparingPlaybackCopy(false);
        SetPreviewExportStatus("", false);
        foreach (var name in new[] { nameof(SelectedClip), nameof(SelectedTitle), nameof(HasSelection), nameof(CanExport), nameof(SelectedIsLossless), nameof(HasPlaybackFormatNote), nameof(LosslessPlaybackNote) }) OnChanged(name);
    }
    internal void SetPreparingPlaybackCopy(bool preparing)
    {
        if (_preparingPlaybackCopy == preparing) return;
        _preparingPlaybackCopy = preparing;
        OnChanged(nameof(CanExport));
    }
    private void NotifyExport()
    {
        foreach (var name in new[] { nameof(IsExporting), nameof(CanCancelExport), nameof(ExportProgressIndeterminate), nameof(ExportProgress), nameof(ExportProgressText), nameof(CanManageClips) }) OnChanged(name);
        foreach (var command in new[] { ConfirmRenameCommand, ConfirmDeleteCommand }) ((ClipCommand)command).Raise();
    }
    private void SetPreviewExportStatus(string message, bool succeeded)
    {
        SetExportFailureDetails("");
        _previewExportStatus = message;
        _previewExportSucceeded = succeeded;
        OnChanged(nameof(PreviewExportStatus)); OnChanged(nameof(HasPreviewExportStatus)); OnChanged(nameof(CanExport));
    }
    private void SetExportFailureDetails(string details)
    {
        if (_exportFailureDetails == details) return;
        _exportFailureDetails = details;
        OnChanged(nameof(ExportFailureDetails)); OnChanged(nameof(HasExportFailureDetails));
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
        if (_snapshot.Enabled && _snapshot.State is ClipRecorderState.Buffering or ClipRecorderState.Saving)
            PersistEnabled(true);
        else if (!_snapshot.Enabled && _snapshot.State is ClipRecorderState.Error or ClipRecorderState.Unavailable)
            PersistEnabled(false);
        if (_snapshot.State is ClipRecorderState.Disabled or ClipRecorderState.Error or ClipRecorderState.Unavailable or
            ClipRecorderState.WaitingForGame or ClipRecorderState.Reconnecting or ClipRecorderState.Stopping ||
            _snapshot.State == ClipRecorderState.Paused && !_snapshot.CanSave)
            CancelQueuedSaves("Queued saves cancelled because recording stopped or the game became unavailable.");
        OnChanged(nameof(RecorderStatus)); OnChanged(nameof(IsRecording));
        OnChanged(nameof(FailureReport)); OnChanged(nameof(HasFailureReport));
        NotifyState();
        if (QueuedSaveCount > 0 && CanSave) _ = DrainSaveQueueAsync();
    }
    private void NotifyState()
    {
        OnChanged(nameof(HasPendingSaves)); OnChanged(nameof(CanRecoverPending)); OnChanged(nameof(PendingRecoveryHint));
        foreach (var name in new[] { nameof(IsBusy), nameof(IsEmpty), nameof(CanEditSettings), nameof(CanEditCompressionQuality), nameof(CanToggle), nameof(ClippingEnabled), nameof(CanSave), nameof(CanRequestSave), nameof(HasQueuedSave), nameof(HasSaveQueueStatus), nameof(SaveQueueStatus), nameof(CanBrowse), nameof(CanExport), nameof(PageText), nameof(EmptyText), nameof(PendingText), nameof(CanOpenClipFolder), nameof(CanManageClips), nameof(ShowsManagementHint) }) OnChanged(name);
        NotifyDashboardNotice();
        foreach (var command in _commands) command.Raise();
    }
    private void ErrorText(string text) { _error = text; OnChanged(nameof(Error)); OnChanged(nameof(HasError)); NotifyDashboardNotice(); }
    private void NotifyDashboardNotice()
    {
        if (DashboardNoticeKey != _dismissedDashboardNotice) _dismissedDashboardNotice = null;
        OnChanged(nameof(HasDashboardNotice)); OnChanged(nameof(DashboardNoticeTitle)); OnChanged(nameof(DashboardNoticeText));
        OnChanged(nameof(CanDisableDashboardNotice));
        ((ClipCommand)DismissDashboardNoticeCommand).Raise();
        ((ClipCommand)DisableDashboardNoticesCommand).Raise();
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
        CommitQuality(); CancelThumbnails(); _saveShortcuts.Clear(); _disposed = true; ShortcutCaptureActive = false;
        _searchTimer?.Stop();
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
