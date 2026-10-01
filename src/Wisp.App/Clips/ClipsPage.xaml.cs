using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wisp.App.Clips;

public partial class ClipsPage : UserControl
{
    public static readonly DependencyProperty UseLegacyStyleProperty = DependencyProperty.Register(nameof(UseLegacyStyle),
        typeof(bool), typeof(ClipsPage), new PropertyMetadata(false, (owner, _) => ((ClipsPage)owner).ApplyCornerStyle()));
    private static readonly DependencyPropertyKey GalleryColumnsPropertyKey = DependencyProperty.RegisterReadOnly(nameof(GalleryColumns),
        typeof(int), typeof(ClipsPage), new PropertyMetadata(1));
    public static readonly DependencyProperty GalleryColumnsProperty = GalleryColumnsPropertyKey.DependencyProperty;
    public bool UseLegacyStyle { get => (bool)GetValue(UseLegacyStyleProperty); set => SetValue(UseLegacyStyleProperty, value); }
    public int GalleryColumns => (int)GetValue(GalleryColumnsProperty);
    private ClipsViewModel? Model => DataContext as ClipsViewModel;
    private Button? _shortcutButton;
    private MediaElement? _player;
    private LosslessClipPlayer? _losslessPlayer;
    private bool HasPlayer => _player is not null || _losslessPlayer is not null;
    private Window? _hostWindow;
    private readonly ClipsPlaybackState _playback = new();
    private readonly Stopwatch _preparationElapsed = new();
    private Guid? _playingClipId;
    private readonly DispatcherTimer _playbackTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private MediaElement? _seekPlayer;
    private LosslessClipPlayer? _seekLosslessPlayer;
    private long _seekRevision;
    private bool _updatingTimeline, _seeking;
    private string _playbackFailureDetails = "";
    private LosslessClipPlayer? _playbackFailureOwner;

    public ClipsPage()
    {
        InitializeComponent();
        _playbackTimer.Tick += (_, _) => RefreshPlaybackPosition();
        DataContextChanged += ContextChanged;
        Loaded += PageLoaded;
        Unloaded += PageUnloaded;
        IsVisibleChanged += (_, _) => { if (!IsVisible) LeavePage(); else UpdateGalleryVisibility(); };
    }

    private void ApplyCornerStyle() => Resources["ClipControlRadius"] = new CornerRadius(UseLegacyStyle ? 7 : 16);
    private void ContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        StopPlayer(); FinishShortcut();
        if (e.OldValue is ClipsViewModel old) { old.PropertyChanged -= ModelChanged; old.ShortcutCaptureActive = false; old.SetGalleryActive(false); }
        if (IsLoaded && e.NewValue is ClipsViewModel current)
        {
            current.PropertyChanged += ModelChanged;
            UpdateGalleryVisibility();
            _ = current.InitializeAsync();
        }
    }
    private async void PageLoaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null) { _hostWindow.StateChanged -= HostStateChanged; _hostWindow.Deactivated -= HostDeactivated; }
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null) { _hostWindow.StateChanged += HostStateChanged; _hostWindow.Deactivated += HostDeactivated; }
        if (Model is { } model)
        {
            model.PropertyChanged -= ModelChanged;
            model.PropertyChanged += ModelChanged;
            UpdateGalleryVisibility();
            await model.InitializeAsync();
        }
    }
    private void PageUnloaded(object sender, RoutedEventArgs e)
    {
        LeavePage();
        if (Model is { } model) model.PropertyChanged -= ModelChanged;
        if (_hostWindow is not null) { _hostWindow.StateChanged -= HostStateChanged; _hostWindow.Deactivated -= HostDeactivated; }
        _hostWindow = null;
    }
    private void HostStateChanged(object? sender, EventArgs e) { if (_hostWindow?.WindowState == WindowState.Minimized) LeavePage(); else UpdateGalleryVisibility(); }
    private void HostDeactivated(object? sender, EventArgs e) => PausePreviewForSystemCapture();
    private void PausePreviewForSystemCapture()
    {
        if (Model is not { CaptureSystemAudio: true, ClippingEnabled: true } ||
            !HasPlayer || !_playback.Ready || _playback.Paused) return;
        try
        {
            _player?.Pause(); _losslessPlayer?.SetPaused(true); _playback.SetPaused(true);
            UpdatePlaybackStatus(); UpdatePlaybackTimer();
        }
        catch (InvalidOperationException) { FailPlayback(); }
    }
    private void UpdateGalleryVisibility() => Model?.SetGalleryActive(IsLoaded && IsVisible &&
        _hostWindow?.WindowState != WindowState.Minimized && Model?.SelectedClip?.Entry.Media.LosslessVideo != true);
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_hostWindow?.IsActive != true && e.PropertyName is nameof(ClipsViewModel.ClippingEnabled) or nameof(ClipsViewModel.CaptureSystemAudio))
            PausePreviewForSystemCapture();
        if (e.PropertyName == nameof(ClipsViewModel.HasSelection))
        {
            UpdateGalleryVisibility();
            UpdatePlaybackFade();
            if (Model?.HasSelection != true) StopPlayer();
        }
    }
    private void LeavePage() { StopPlayer(); FinishShortcut(); Model?.SetGalleryActive(false); Model?.CommitQuality(); Model?.ClosePlayback(); }
    private void Gallery_SizeChanged(object sender, SizeChangedEventArgs e) =>
        SetValue(GalleryColumnsPropertyKey, Math.Clamp((int)(Math.Max(0, e.NewSize.Width) / 176), 1, 5));
    private void ClipsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0) UpdatePlayerSize();
    }
    private void PlaybackLayout_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePlayerSize();
    private void UpdatePlayerSize()
    {
        UpdatePlaybackFade();
        if (PlayerHost is null || PlaybackControls is null || PlaybackSurface.Visibility != Visibility.Visible) return;
        var width = PlayerHost.ActualWidth;
        var viewportHeight = ClipsScroll.ViewportHeight;
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(viewportHeight) || viewportHeight <= 0) return;
        var media = Model?.SelectedClip?.Entry.Media;
        var aspect = _player is { NaturalVideoWidth: > 0, NaturalVideoHeight: > 0 } player
            ? (double)player.NaturalVideoWidth / player.NaturalVideoHeight
            : media is { Width: > 0, Height: > 0 } ? (double)media.Width / media.Height : 16d / 9;
        static double OuterHeight(FrameworkElement element) => element.Visibility == Visibility.Collapsed ? 0 :
            element.ActualHeight + element.Margin.Top + element.Margin.Bottom;
        var chrome = OuterHeight(PlaybackHeader) + OuterHeight(PlaybackTimeline) + OuterHeight(PlaybackControls) +
            OuterHeight(PreviewExportStatus) +
            PlaybackSurface.Padding.Top + PlaybackSurface.Padding.Bottom + PlaybackSurface.BorderThickness.Top +
            PlaybackSurface.BorderThickness.Bottom + PlaybackSurface.Margin.Top + PlaybackSurface.Margin.Bottom;
        var height = Math.Max(0, Math.Min(width / aspect, viewportHeight - chrome));
        if (!double.IsFinite(PlayerHost.Height) || Math.Abs(PlayerHost.Height - height) > .5) PlayerHost.Height = height;
    }
    private void UpdatePlaybackFade()
    {
        if (ClipsScroll?.Template?.FindName("PART_ScrollContentPresenter", ClipsScroll) is not ScrollContentPresenter viewport) return;
        if (Model?.HasSelection == true) ScrollEdgeFade.SetIsEnabled(viewport, false);
        else viewport.ClearValue(ScrollEdgeFade.IsEnabledProperty);
    }
    private async void ClippingToggle_Click(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ToggleAsync(); }
    private void Quality_Commit(object sender, RoutedEventArgs e) => Model?.CommitQuality();

    private void CopyErrorDetails_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { HasFailureReport: true } model) return;
        try { Clipboard.SetText(model.FailureReport); model.ReportCopyCompleted(true); }
        catch (ExternalException) { model.ReportCopyCompleted(false); }
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { IsBusy: false } model) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose an export folder on a local drive",
            Multiselect = false,
            InitialDirectory = Directory.Exists(model.StorageDirectory) ? model.StorageDirectory :
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await model.SetStorageDirectoryAsync(dialog.FolderName);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var folder = await model.GetClipFolderForOpenAsync();
        if (folder is null || !ReferenceEquals(Model, model) || !IsVisible) return;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            { UseShellExecute = false };
            start.ArgumentList.Add(folder);
            using var process = Process.Start(start);
            if (process is null) model.ClipFolderOpenFailed();
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        { model.ClipFolderOpenFailed(); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { CanExport: true, SelectedClip: { } selected } model) return;
        var revision = _playback.Revision;
        var dialog = CreateExportDialog(model.StorageDirectory, selected.Entry);
        dialog.FileOk += (_, cancel) =>
        {
            var error = ValidateExportDestination(dialog.FileName);
            if (error is null) return;
            cancel.Cancel = true;
            MessageBox.Show(Window.GetWindow(this), error, "Choose an export file", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || !ReferenceEquals(Model, model) ||
            !ReferenceEquals(model.SelectedClip, selected) || revision != _playback.Revision || !model.CanExport) return;
        await model.ExportSelectedAsync(dialog.FileName);
    }

    internal static Microsoft.Win32.SaveFileDialog CreateExportDialog(string exportDirectory, ClipEntry clip) => new()
    {
        Title = "Export clip",
        Filter = "MP4 video (*.mp4)|*.mp4",
        FileName = $"Wisp-{clip.SavedAtUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{clip.Id:N}.mp4",
        DefaultExt = ".mp4",
        AddExtension = true,
        CheckPathExists = true,
        OverwritePrompt = false,
        InitialDirectory = Directory.Exists(exportDirectory) ? exportDirectory :
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
    };

    internal static string? ValidateExportDestination(string destination)
    {
        if (!Path.GetExtension(destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            return "Use .mp4 for the exported video.";
        return File.Exists(destination) || Directory.Exists(destination)
            ? "This name is already in use. Choose a different filename, or Cancel to keep the existing file."
            : null;
    }

    private async void PlayClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipCardItem item } || Model is not { IsBusy: false } model) return;
        StopPlayer();
        var revision = _playback.Revision;
        var path = await model.SelectForPlaybackAsync(item);
        if (path is null || revision != _playback.Revision || !IsLoaded || !IsVisible || !ReferenceEquals(Model, model)) return;
        _playback.Prepare(); revision = _playback.Revision;
        _playingClipId = item.Id;
        if (item.Entry.Media.LosslessVideo)
        {
            OpenLosslessPlayer(item.Entry, path, model, revision);
            return;
        }
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Volume = PlaybackVolume.Value,
            ScrubbingEnabled = true
        };
        _player = player;
        bool Current() => ReferenceEquals(_player, player) && revision == _playback.Revision &&
            IsLoaded && IsVisible && ReferenceEquals(Model, model);
        player.MediaOpened += (_, _) =>
        {
            if (!Current() || !_playback.Preparing) return;
            try
            {
                var seconds = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0;
                if (!_playback.TryOpen(revision, player.HasVideo, player.NaturalVideoWidth, player.NaturalVideoHeight, seconds))
                { FailPlayback("This clip has no playable video or duration. Choose another clip."); return; }
                // Opening the decoder must not advance the clip before the user's Play action.
                player.Pause(); player.Position = TimeSpan.Zero;
                _playback.BufferingChanged(revision, player.IsBuffering);
                if (!_playback.WaitingForInitialBuffer) _preparationElapsed.Stop();
                if (!Current()) return;
                _updatingTimeline = true;
                try { PlaybackPosition.Maximum = _playback.DurationSeconds; PlaybackPosition.IsEnabled = true; }
                finally { _updatingTimeline = false; }
                SetTimelinePosition(0); UpdatePlaybackStatus(); UpdatePlayerSize(); UpdatePlaybackTimer();
                if (_hostWindow?.IsActive != true) PausePreviewForSystemCapture();
            }
            catch (InvalidOperationException) { FailPlayback(); }
        };
        player.BufferingStarted += (_, _) =>
        {
            if (!Current()) return;
            var waiting = _playback.WaitingForInitialBuffer;
            _playback.BufferingChanged(revision, true);
            if (!waiting && _playback.WaitingForInitialBuffer) _preparationElapsed.Restart();
            UpdatePlaybackStatus(); UpdatePlaybackTimer();
        };
        player.BufferingEnded += (_, _) =>
        {
            if (!Current()) return;
            _playback.BufferingChanged(revision, false);
            if (!_playback.Preparing) _preparationElapsed.Stop();
            UpdatePlaybackStatus(); UpdatePlaybackTimer();
        };
        player.MediaFailed += (_, _) => { if (Current()) FailPlayback(); };
        player.MediaEnded += (_, _) =>
        {
            if (!Current() || !_playback.Ready) return;
            try
            {
                player.Pause(); _playback.ReachEnd(revision);
                _playbackTimer.Stop();
                if (!_seeking) SetTimelinePosition(_playback.DurationSeconds);
                UpdatePlaybackStatus();
            }
            catch (InvalidOperationException) { FailPlayback(); }
        };
        PlayerHost.Content = player;
        _preparationElapsed.Restart(); UpdatePlaybackStatus(); UpdatePlaybackTimer();
        try { player.Source = new Uri(path, UriKind.Absolute); player.Pause(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
        { FailPlayback(); return; }
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!Current() || !PlaybackSurface.IsVisible || ClipsScroll.Content is not FrameworkElement content) return;
            var offset = PlaybackSurface.TranslatePoint(new Point(), content).Y;
            if (double.IsFinite(offset)) ClipsScroll.ScrollToVerticalOffset(offset);
        }, DispatcherPriority.Loaded);
    }

    private void OpenLosslessPlayer(ClipEntry clip, string path, ClipsViewModel model, long revision)
    {
        if (ClipsScroll.Template?.FindName("PART_ScrollContentPresenter", ClipsScroll) is not FrameworkElement viewport)
        { FailPlayback("The video viewport is not ready. Choose the clip again to retry."); return; }
        var host = new LosslessVideoHost(viewport) { Focusable = false, IsHitTestVisible = false };
        var player = new LosslessClipPlayer(host, PlaybackVolume.Value);
        _losslessPlayer = player;
        bool Current() => ReferenceEquals(_losslessPlayer, player) && revision == _playback.Revision &&
            IsLoaded && IsVisible && ReferenceEquals(Model, model);
        player.Changed += (_, _) =>
        {
            if (!Current()) return;
            var state = player.Snapshot;
            if (state.Failure is { } failure) { FailPlayback(failure); return; }
            if (!state.Ready) return;
            if (_playback.Preparing)
            {
                if (!_playback.TryOpen(revision, true, clip.Media.Width, clip.Media.Height, state.Duration))
                { FailPlayback("This lossless clip has no playable video or duration."); return; }
                _preparationElapsed.Stop();
                _updatingTimeline = true;
                try { PlaybackPosition.Maximum = state.Duration; PlaybackPosition.IsEnabled = true; }
                finally { _updatingTimeline = false; }
                SetTimelinePosition(0); UpdatePlayerSize();
            }
            _playback.BufferingChanged(revision, state.Buffering);
            if (state.Ended)
            {
                _playback.ReachEnd(revision);
                if (!_seeking) SetTimelinePosition(_playback.DurationSeconds);
            }
            UpdatePlaybackStatus(); UpdatePlaybackTimer();
            if (_hostWindow?.IsActive != true) PausePreviewForSystemCapture();
        };
        PlayerHost.Content = host;
        _preparationElapsed.Restart(); UpdatePlaybackStatus(); UpdatePlaybackTimer();
        player.Open(clip, path);
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!Current() || !PlaybackSurface.IsVisible || ClipsScroll.Content is not FrameworkElement content) return;
            var offset = PlaybackSurface.TranslatePoint(new Point(), content).Y;
            if (double.IsFinite(offset)) ClipsScroll.ScrollToVerticalOffset(offset);
        }, DispatcherPriority.Loaded);
    }

    private void StopPlayer()
    {
        _playbackFailureDetails = "";
        _playbackFailureOwner = null;
        if (CopyPlaybackDetails is not null) CopyPlaybackDetails.Visibility = Visibility.Collapsed;
        _playback.Reset(); _preparationElapsed.Reset(); _playingClipId = null;
        _playbackTimer.Stop(); _seekPlayer = null; _seekLosslessPlayer = null; _seeking = false;
        var lossless = _losslessPlayer; _losslessPlayer = null;
        if (lossless is not null) _ = lossless.CloseAsync();
        var player = _player; _player = null;
        if (player is not null)
        {
            try { player.Stop(); player.Close(); player.Source = null; }
            catch (InvalidOperationException) { }
        }
        if (PlayerHost is not null) PlayerHost.Content = null;
        if (PlayPauseButton is not null) PlayPauseButton.IsEnabled = false;
        if (PlaybackPosition is not null)
        {
            _updatingTimeline = true;
            try { PlaybackPosition.Value = 0; PlaybackPosition.Maximum = 1; PlaybackPosition.IsEnabled = false; }
            finally { _updatingTimeline = false; }
        }
        if (PlaybackTime is not null) PlaybackTime.Text = "0:00 / 0:00";
        UpdatePlaybackStatus();
    }
    private void FailPlayback(string message = "This clip could not be played. Choose the clip again to retry.")
    {
        var lossless = _losslessPlayer;
        var failureStage = lossless?.DiagnosticStage;
        StopPlayer(); _playback.Fail(message); UpdatePlaybackStatus(); Model?.PlaybackFailed();
        if (lossless is not null)
        {
            _playbackFailureDetails = $"{message}\nFailure stage: {failureStage}";
            _playbackFailureOwner = lossless;
            CopyPlaybackDetails.Visibility = Visibility.Visible;
        }
    }
    private void CopyPlaybackDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackFailureDetails.Length == 0) return;
        try
        {
            Clipboard.SetText($"Wisp lossless playback (mpv)\n{_playbackFailureDetails}\nCleanup: {_playbackFailureOwner?.CleanupStatus ?? "not-recorded"}");
            Model?.ReportCopyCompleted(true);
        }
        catch (ExternalException) { Model?.ReportCopyCompleted(false); }
    }
    private void UpdatePlaybackStatus()
    {
        if (PlaybackStatus is null) return;
        PlaybackStatus.Text = _playback.Status;
        PlaybackStatus.Visibility = PlaybackStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PlayPauseButton.Content = _playback.Ready && !_playback.Paused ? "Pause" : _playback.Ended ? "Play again" : "Play";
        PlayPauseButton.IsEnabled = _playback.CanTogglePlayback;
    }
    private void ClosePlayer_Click(object sender, RoutedEventArgs e) { StopPlayer(); Model?.ClosePlayback(); }
    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!HasPlayer || !PlayPauseButton.IsEnabled) return;
        try
        {
            var play = _playback.Paused;
            var restart = play && _playback.Ended;
            // Commit user intent first: Play can raise a buffering callback immediately.
            _playback.SetPaused(!play);
            if (_losslessPlayer is { } lossless)
            {
                if (restart) SetTimelinePosition(0);
                lossless.SetPaused(!play);
                UpdatePlaybackStatus(); UpdatePlaybackTimer();
                return;
            }
            if (play)
            {
                if (restart) { _player!.Position = TimeSpan.Zero; SetTimelinePosition(0); }
                _player!.Play();
            }
            else _player!.Pause();
            UpdatePlaybackStatus(); UpdatePlaybackTimer();
        }
        catch (InvalidOperationException) { FailPlayback(); }
    }
    private void PlaybackVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null) _player.Volume = e.NewValue;
        _losslessPlayer?.SetVolume(e.NewValue);
    }

    private bool CanTrackPlayback => HasPlayer && _playback.Ready &&
        IsLoaded && IsVisible && _hostWindow?.WindowState != WindowState.Minimized;
    private void UpdatePlaybackTimer()
    {
        if (HasPlayer && IsLoaded && IsVisible && _hostWindow?.WindowState != WindowState.Minimized &&
            (_playback.Preparing || _playback.WaitingForInitialBuffer || CanTrackPlayback && !_playback.Paused && !_seeking)) _playbackTimer.Start();
        else _playbackTimer.Stop();
    }
    private void RefreshPlaybackPosition()
    {
        if (_playback.Preparing || _playback.WaitingForInitialBuffer)
        {
            if (_playback.OpeningTimedOut(_preparationElapsed.Elapsed))
                FailPlayback("This clip is taking too long to prepare. Choose it again to retry.");
            return;
        }
        if (!CanTrackPlayback || _playback.Paused || _seeking) { _playbackTimer.Stop(); return; }
        try
        {
            _losslessPlayer?.RequestPoll();
            var seconds = _losslessPlayer?.Snapshot.Position ?? _player!.Position.TotalSeconds;
            SetTimelinePosition(seconds);
            if (_playback.ObservePosition(_playback.Revision, seconds) && _playingClipId is { } id && Model is { } model)
                _ = model.PlaybackOpenedAsync(id);
        }
        catch (InvalidOperationException) { FailPlayback(); }
    }
    private void SetTimelinePosition(double seconds)
    {
        if (!double.IsFinite(seconds)) seconds = 0;
        seconds = Math.Clamp(seconds, 0, _playback.DurationSeconds);
        _updatingTimeline = true;
        try { PlaybackPosition.Value = seconds; }
        finally { _updatingTimeline = false; }
        UpdateTimeLabel(seconds);
    }
    private void UpdateTimeLabel(double seconds)
    {
        static string Format(double value) => $"{(long)value / 60}:{(long)value % 60:00}";
        PlaybackTime.Text = $"{Format(seconds)} / {Format(_playback.DurationSeconds)}";
    }
    private void PlaybackPosition_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTimeline || PlaybackTime is null || _playback.DurationSeconds <= 0) return;
        UpdateTimeLabel(Math.Clamp(e.NewValue, 0, _playback.DurationSeconds));
        if (!_seeking) SeekPlayback(_player, _losslessPlayer, _playback.Revision, e.NewValue);
    }
    private void PlaybackSeek_Begin(object sender, DragStartedEventArgs e)
    {
        if (!CanTrackPlayback || !PlaybackPosition.IsEnabled) return;
        _seekPlayer = _player; _seekLosslessPlayer = _losslessPlayer; _seekRevision = _playback.Revision; _seeking = true; _playbackTimer.Stop();
    }
    private void PlaybackSeek_End(object sender, DragCompletedEventArgs e)
    {
        if (!_seeking) return;
        var player = _seekPlayer; var lossless = _seekLosslessPlayer; var revision = _seekRevision;
        _seekPlayer = null; _seekLosslessPlayer = null; _seeking = false;
        SeekPlayback(player, lossless, revision, PlaybackPosition.Value);
        UpdatePlaybackTimer();
    }
    private void SeekPlayback(MediaElement? player, LosslessClipPlayer? lossless, long revision, double seconds)
    {
        if ((player is null && lossless is null) || !ReferenceEquals(player, _player) || !ReferenceEquals(lossless, _losslessPlayer) ||
            revision != _playback.Revision || !CanTrackPlayback || !double.IsFinite(seconds)) return;
        try
        {
            seconds = Math.Clamp(seconds, 0, _playback.DurationSeconds);
            if (lossless is not null) lossless.Seek(seconds);
            else player!.Position = TimeSpan.FromSeconds(seconds);
            _playback.SeekTo(seconds);
            SetTimelinePosition(seconds); UpdatePlaybackStatus();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { FailPlayback(); }
    }

    private void CaptureShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Model is not { CanEditSettings: true } model) return;
        FinishShortcut(); _shortcutButton = button; button.Focus(); model.ShortcutCaptureActive = true;
        button.SetCurrentValue(ContentProperty, "Press shortcut…");
    }
    private void CaptureShortcut_KeyDown(object sender, KeyEventArgs e)
    {
        if (_shortcutButton is null || Model is not { } model) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { FinishShortcut(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = OverlayHotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= OverlayHotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= OverlayHotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= OverlayHotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= OverlayHotkeyModifiers.Windows;
        var save = ReferenceEquals(_shortcutButton, SaveShortcutButton);
        if (model.ConfigureShortcut(save, save ? model.SaveShortcutEnabled : model.ToggleShortcutEnabled, new(modifiers, key))) FinishShortcut();
    }
    private void CaptureShortcut_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishShortcut();
    private void FinishShortcut()
    {
        if (Model is { } model) model.ShortcutCaptureActive = false;
        _shortcutButton?.GetBindingExpression(ContentProperty)?.UpdateTarget(); _shortcutButton = null;
    }
}
