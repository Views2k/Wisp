using System.ComponentModel;
using System.Diagnostics;
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
    private Window? _hostWindow;
    private bool _paused;
    private long _playbackRevision;
    private readonly DispatcherTimer _playbackTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private MediaElement? _seekPlayer;
    private long _seekRevision;
    private double _durationSeconds;
    private bool _updatingTimeline, _seeking, _ended;

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
        if (_hostWindow is not null) _hostWindow.StateChanged -= HostStateChanged;
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null) _hostWindow.StateChanged += HostStateChanged;
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
        if (_hostWindow is not null) _hostWindow.StateChanged -= HostStateChanged;
        _hostWindow = null;
    }
    private void HostStateChanged(object? sender, EventArgs e) { if (_hostWindow?.WindowState == WindowState.Minimized) LeavePage(); else UpdateGalleryVisibility(); }
    private void UpdateGalleryVisibility() => Model?.SetGalleryActive(IsLoaded && IsVisible && _hostWindow?.WindowState != WindowState.Minimized);
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipsViewModel.HasSelection))
        {
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
        static double OuterHeight(FrameworkElement element) => element.ActualHeight + element.Margin.Top + element.Margin.Bottom;
        var chrome = OuterHeight(PlaybackHeader) + OuterHeight(PlaybackTimeline) + OuterHeight(PlaybackControls) +
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
        if (Model is { CanExport: true } model) await model.ExportSelectedToFolderAsync();
    }

    private async void PlayClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipCardItem item } || Model is not { IsBusy: false } model) return;
        StopPlayer();
        var revision = _playbackRevision;
        var path = await model.SelectForPlaybackAsync(item);
        if (path is null || revision != _playbackRevision || !IsVisible || !ReferenceEquals(Model, model)) return;
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
        player.MediaOpened += async (_, _) =>
        {
            if (!ReferenceEquals(_player, player) || revision != _playbackRevision || !IsVisible) return;
            try
            {
                player.Play(); _paused = false; _ended = false; PlayPauseButton.Content = "Pause"; PlayPauseButton.IsEnabled = true;
                _durationSeconds = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0;
                if (!double.IsFinite(_durationSeconds) || _durationSeconds <= 0) _durationSeconds = 0;
                _updatingTimeline = true;
                try { PlaybackPosition.Maximum = _durationSeconds > 0 ? _durationSeconds : 1; PlaybackPosition.IsEnabled = _durationSeconds > 0; }
                finally { _updatingTimeline = false; }
                SetTimelinePosition(0); UpdatePlayerSize(); UpdatePlaybackTimer();
            }
            catch (InvalidOperationException) { StopPlayer(); model.PlaybackFailed(); return; }
            await model.PlaybackOpenedAsync(item.Id);
        };
        player.MediaFailed += (_, _) => { if (ReferenceEquals(_player, player)) { StopPlayer(); model.PlaybackFailed(); } };
        player.MediaEnded += (_, _) =>
        {
            if (!ReferenceEquals(_player, player) || revision != _playbackRevision) return;
            try
            {
                player.Pause(); _paused = true; _ended = true; PlayPauseButton.Content = "Play again";
                _playbackTimer.Stop();
                if (!_seeking) SetTimelinePosition(_durationSeconds);
            }
            catch (InvalidOperationException) { StopPlayer(); model.PlaybackFailed(); }
        };
        PlayerHost.Content = player;
        try { player.Source = new Uri(path, UriKind.Absolute); player.Play(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
        { StopPlayer(); model.PlaybackFailed(); return; }
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_player, player) || revision != _playbackRevision || !ReferenceEquals(Model, model) ||
                !IsLoaded || !IsVisible || !PlaybackSurface.IsVisible || ClipsScroll.Content is not FrameworkElement content) return;
            var offset = PlaybackSurface.TranslatePoint(new Point(), content).Y;
            if (double.IsFinite(offset)) ClipsScroll.ScrollToVerticalOffset(offset);
        }, DispatcherPriority.Loaded);
    }

    private void StopPlayer()
    {
        ++_playbackRevision;
        _playbackTimer.Stop(); _seekPlayer = null; _seeking = false; _ended = false; _paused = false; _durationSeconds = 0;
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
    }
    private void ClosePlayer_Click(object sender, RoutedEventArgs e) { StopPlayer(); Model?.ClosePlayback(); }
    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null || !PlayPauseButton.IsEnabled) return;
        try
        {
            if (_paused)
            {
                if (_ended) { _player.Position = TimeSpan.Zero; _ended = false; SetTimelinePosition(0); }
                _player.Play();
            }
            else _player.Pause();
            _paused = !_paused; PlayPauseButton.Content = _paused ? "Play" : "Pause";
            UpdatePlaybackTimer();
        }
        catch (InvalidOperationException) { StopPlayer(); Model?.PlaybackFailed(); }
    }
    private void PlaybackVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_player is not null) _player.Volume = e.NewValue; }

    private bool CanTrackPlayback => _player is not null && PlayPauseButton.IsEnabled && _durationSeconds > 0 &&
        IsLoaded && IsVisible && _hostWindow?.WindowState != WindowState.Minimized;
    private void UpdatePlaybackTimer()
    {
        if (CanTrackPlayback && !_paused && !_seeking) _playbackTimer.Start();
        else _playbackTimer.Stop();
    }
    private void RefreshPlaybackPosition()
    {
        if (!CanTrackPlayback || _paused || _seeking) { _playbackTimer.Stop(); return; }
        try { SetTimelinePosition(_player!.Position.TotalSeconds); }
        catch (InvalidOperationException) { StopPlayer(); Model?.PlaybackFailed(); }
    }
    private void SetTimelinePosition(double seconds)
    {
        if (!double.IsFinite(seconds)) seconds = 0;
        seconds = Math.Clamp(seconds, 0, _durationSeconds);
        _updatingTimeline = true;
        try { PlaybackPosition.Value = seconds; }
        finally { _updatingTimeline = false; }
        UpdateTimeLabel(seconds);
    }
    private void UpdateTimeLabel(double seconds)
    {
        static string Format(double value) => $"{(long)value / 60}:{(long)value % 60:00}";
        PlaybackTime.Text = $"{Format(seconds)} / {Format(_durationSeconds)}";
    }
    private void PlaybackPosition_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTimeline || PlaybackTime is null || _durationSeconds <= 0) return;
        UpdateTimeLabel(Math.Clamp(e.NewValue, 0, _durationSeconds));
        if (!_seeking) SeekPlayback(_player, _playbackRevision, e.NewValue);
    }
    private void PlaybackSeek_Begin(object sender, DragStartedEventArgs e)
    {
        if (!CanTrackPlayback || !PlaybackPosition.IsEnabled) return;
        _seekPlayer = _player; _seekRevision = _playbackRevision; _seeking = true; _playbackTimer.Stop();
    }
    private void PlaybackSeek_End(object sender, DragCompletedEventArgs e)
    {
        if (!_seeking) return;
        var player = _seekPlayer; var revision = _seekRevision;
        _seekPlayer = null; _seeking = false;
        SeekPlayback(player, revision, PlaybackPosition.Value);
        UpdatePlaybackTimer();
    }
    private void SeekPlayback(MediaElement? player, long revision, double seconds)
    {
        if (player is null || !ReferenceEquals(player, _player) || revision != _playbackRevision || !CanTrackPlayback || !double.IsFinite(seconds)) return;
        try
        {
            seconds = Math.Clamp(seconds, 0, _durationSeconds);
            player.Position = TimeSpan.FromSeconds(seconds);
            _ended = seconds >= _durationSeconds;
            PlayPauseButton.Content = _paused ? _ended ? "Play again" : "Play" : "Pause";
            SetTimelinePosition(seconds);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { StopPlayer(); Model?.PlaybackFailed(); }
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
