using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using Wisp.App.Clips;

namespace Wisp.UiReview;

internal static class ClipsPlaybackUiReview
{
    private const string FixtureSha256 = "9AD9AFA9D8D30AA96EF2AB9F272BA7B7D7AE074FFE611FC3BACAC1F5E632C01C";
    private const double KnownDurationSeconds = 4.01665;
    private const double SeekToleranceSeconds = .35;

    internal static int Run(string source, string output, Func<ResourceDictionary> loadResources)
    {
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(26));
        using var bindings = new BindingTrace();
        var checks = new List<object>();
        var measurements = new Dictionary<string, double>();
        var phase = "source-validation";
        string? failure = null;
        object? failureSnapshot = null;
        object? settledFailureSnapshot = null;
        var foreground = GetForegroundWindow();
        var foregroundUnchanged = foreground != IntPtr.Zero;
        var elapsed = Stopwatch.StartNew();
        var previousContext = SynchronizationContext.Current;
        Application? application = null;
        Window? window = null;
        ClipsViewModel? model = null;
        ClipsPage? page = null;
        var ownedWindowClosed = true;
        try
        {
            Require(foreground != IntPtr.Zero && GameClosed(), "game-and-foreground-guard");
            using var fixture = OpenFixture(source, output);
            Require(Convert.ToHexString(SHA256.HashData(fixture)) == FixtureSha256, "generated-fixture-hash");
            fixture.Position = 0;
            application = new ReviewApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources = loadResources();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var libraryDirectory = Path.Combine(output, "playback-fixture-" + Guid.NewGuid().ToString("N"));
            var library = new ClipLibrary(libraryDirectory);
            for (var index = 0; index < ClipLibrary.PageSize; index++)
            {
                Guard();
                var target = Await(library.ReserveSaveAsync(new(30, 1080, 60, 75), cancellation.Token));
                fixture.Position = 0;
                using (var destination = new FileStream(target.MediaPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    fixture.CopyTo(destination);
                Await(library.CommitFinalizedAsync(target.Id, new(fixture.Length, 1920, 1080, 60, 0, 40_166_666, false), cancellation.Token));
            }
            model = new(new() { StorageDirectory = libraryDirectory }, new InertRecorder(), Dispatcher.CurrentDispatcher,
                libraryDirectory: libraryDirectory);
            page = new ClipsPage { DataContext = model };
            ((Slider)page.FindName("PlaybackVolume")).Value = 0;
            var activeWindow = new Window
            {
                Title = "Wisp synthetic clip playback check",
                Width = 980,
                Height = 750,
                ShowActivated = false,
                ShowInTaskbar = false,
                Focusable = false,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)application.FindResource("WindowBrush"),
                Content = page
            };
            window = activeWindow;
            activeWindow.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(activeWindow).Handle;
                const int extendedStyle = -20, nonactivating = 0x08000000, toolWindow = 0x80;
                _ = SetWindowLong(handle, extendedStyle, GetWindowLong(handle, extendedStyle) | nonactivating | toolWindow);
                Require((GetWindowLong(handle, extendedStyle) & nonactivating) != 0, "passive-window-style");
            };
            Await(model.InitializeAsync());
            phase = "show-muted-player";
            Guard(); ownedWindowClosed = false; window.Show();
            Await(Scenario());
        }
        catch (Exception error)
        {
            failure = phase + "/" + error.GetType().Name;
            try { failureSnapshot = CaptureFailureSnapshot(page, model, window); }
            catch { failureSnapshot = new { available = false }; }
            if (window?.IsVisible == true && GetForegroundWindow() == foreground)
            {
                try { Await(Settle(100)); settledFailureSnapshot = CaptureFailureSnapshot(page, model, window); }
                catch { settledFailureSnapshot = new { available = false }; }
            }
        }
        finally
        {
            try
            {
                if (window is not null) { window.Content = null; window.Close(); ownedWindowClosed = !window.IsVisible; }
                if (page is not null) page.DataContext = null;
                model?.Dispose();
            }
            catch (Exception error) { failure ??= "cleanup/" + error.GetType().Name; }
            foregroundUnchanged &= GetForegroundWindow() == foreground;
            SynchronizationContext.SetSynchronizationContext(previousContext);
            try { application?.Shutdown(); }
            catch (Exception error) { failure ??= "application-cleanup/" + error.GetType().Name; }
        }
        if (!foregroundUnchanged) failure ??= "foreground-changed";
        if (!ownedWindowClosed) failure ??= "window-not-closed";
        if (bindings.TotalCount != 0) failure ??= "binding-diagnostics";
        var report = new
        {
            completed = failure is null,
            failure,
            failureSnapshot,
            settledFailureSnapshot,
            elapsedSeconds = elapsed.Elapsed.TotalSeconds,
            sourceSha256 = FixtureSha256,
            knownDurationSeconds = KnownDurationSeconds,
            seekToleranceSeconds = SeekToleranceSeconds,
            foregroundUnchanged,
            ownedWindowClosed,
            generatedLibraryClips = ClipLibrary.PageSize,
            bindingDiagnosticCount = bindings.TotalCount,
            checks,
            measurements,
            audioPlayed = false,
            gameCaptureUsed = false,
            recorderHelperStarted = false,
            installedLibraryAccessed = false,
            limits = "Generated video only. Checks MediaElement position/state and actual page handlers, not frame-accurate seeking, decoded pixel fidelity or gameplay performance."
        };
        File.WriteAllText(Path.Combine(output, "clips-playback-review.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Clips playback review: {(failure is null ? "PASS" : "FAIL")}; {checks.Count} checks; foreground unchanged: {foregroundUnchanged}; own window closed: {ownedWindowClosed}.");
        return failure is null ? 0 : 2;

        void Guard()
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != foreground) { foregroundUnchanged = false; throw new InvalidOperationException("Foreground changed."); }
        }
        void Check(bool condition, string name)
        {
            checks.Add(new { name, passed = condition });
            Require(condition, name);
        }
        async Task Until(Func<bool> condition, int milliseconds)
        {
            var limit = Stopwatch.StartNew();
            while (!condition())
            {
                Guard();
                if (limit.ElapsedMilliseconds >= milliseconds) throw new TimeoutException();
                await Task.Delay(25, cancellation.Token);
            }
            Guard();
        }
        async Task Settle(int milliseconds)
        {
            var limit = Stopwatch.StartNew();
            while (limit.ElapsedMilliseconds < milliseconds) { Guard(); await Task.Delay(25, cancellation.Token); }
        }
        async Task Scenario()
        {
            var currentPage = page ?? throw new InvalidOperationException();
            var currentModel = model ?? throw new InvalidOperationException();
            var currentWindow = window ?? throw new InvalidOperationException();
            await Until(() => currentPage.IsLoaded && currentPage.IsVisible, 1500);
            currentPage.UpdateLayout();
            var cards = Descendants(currentPage).OfType<Button>().Where(item => item.DataContext is ClipCardItem).ToArray();
            Check(cards.Length == ClipLibrary.PageSize && currentModel.Clips.Count == ClipLibrary.PageSize, "full-generated-gallery");
            var selected = currentModel.Clips[^1];
            var playCard = cards.Single(item => item.DataContext is ClipCardItem card && card.Id == selected.Id);
            var scroll = (ScrollViewer)currentPage.FindName("ClipsScroll");
            var viewport = scroll.Template.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter
                ?? throw new InvalidOperationException("Scroll viewport is unavailable.");
            var host = (ContentControl)currentPage.FindName("PlayerHost");
            var playPause = (Button)currentPage.FindName("PlayPauseButton");
            var position = (Slider)currentPage.FindName("PlaybackPosition");
            var time = (TextBlock)currentPage.FindName("PlaybackTime");
            var playbackStatus = (TextBlock)currentPage.FindName("PlaybackStatus");
            var exportStatus = (TextBlock)currentPage.FindName("PreviewExportStatus");
            var timer = typeof(ClipsPage).GetField("_playbackTimer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(currentPage) as DispatcherTimer
                ?? throw new InvalidOperationException("Playback timer is unavailable.");
            var close = Descendants(currentPage).OfType<Button>().Single(item => Equals(item.Content, "Close player"));
            var export = Descendants(currentPage).OfType<Button>().Single(item => Equals(item.Content, "Export clip…"));
            var volume = (Slider)currentPage.FindName("PlaybackVolume");
            Check(!position.IsEnabled && !timer.IsEnabled && currentModel.NewClipCount == ClipLibrary.PageSize, "initial-unopened-and-unviewed");
            phase = "bottom-row-selection"; scroll.ScrollToBottom();
            await Until(() => scroll.VerticalOffset > 0 && FitsViewport(playCard), 1500);
            measurements["beforeSelectionVerticalOffset"] = scroll.VerticalOffset;
            Check(FitsViewport(playCard), "bottom-row-card-fully-visible-before-click");
            phase = "media-open-and-player-reveal"; Click(playCard);
            await Until(() => host.Content is MediaElement { IsMeasureValid: true, IsArrangeValid: true } &&
                host.IsMeasureValid && host.IsArrangeValid && playPause.IsEnabled && position.IsEnabled &&
                FitsViewport(host) && FitsViewport(playPause) && FitsViewport(position) && FitsViewport(time) &&
                FitsViewport(close) && FitsViewport(export) && FitsViewport(volume), 6500);
            measurements["afterSelectionVerticalOffset"] = scroll.VerticalOffset;
            Check(FitsViewport(host) && FitsViewport(playPause) && FitsViewport(position) && FitsViewport(time) &&
                FitsViewport(close) && FitsViewport(export) && FitsViewport(volume), "video-and-playback-controls-fully-visible-after-lower-card-click");
            var player = (MediaElement)host.Content;
            Check(player.Volume == 0 && player.NaturalVideoWidth == 1920 && player.NaturalVideoHeight == 1080, "muted-known-video");
            Check(player.LoadedBehavior == MediaState.Manual && player.UnloadedBehavior == MediaState.Close,
                "prepared-player-uses-manual-transport-and-close-on-unload");
            var fitScale = Math.Min(host.ActualWidth / player.NaturalVideoWidth, host.ActualHeight / player.NaturalVideoHeight);
            var fittedWidth = player.NaturalVideoWidth * fitScale;
            var fittedHeight = player.NaturalVideoHeight * fitScale;
            measurements["actualVideoWidth"] = player.ActualWidth; measurements["actualVideoHeight"] = player.ActualHeight;
            measurements["playerHostWidth"] = host.ActualWidth; measurements["playerHostHeight"] = host.ActualHeight;
            measurements["fittedVideoWidth"] = fittedWidth; measurements["fittedVideoHeight"] = fittedHeight;
            Check(host.ActualHeight > 400 && fittedWidth > 700 && fittedHeight > 400 &&
                player.ActualWidth >= fittedWidth - 1 && player.ActualHeight >= fittedHeight - 1 &&
                FitsViewport(player) && player.Stretch == Stretch.Uniform,
                "actual-video-fills-substantial-default-player-area");
            Check(host.HorizontalContentAlignment == HorizontalAlignment.Stretch && host.VerticalContentAlignment == VerticalAlignment.Stretch &&
                host.Background is SolidColorBrush { Color: var background } && background == Colors.Black &&
                CleanVisualChain(player), "player-is-opaque-without-ancestor-effects-or-fade");
            measurements["naturalDurationSeconds"] = player.NaturalDuration.TimeSpan.TotalSeconds;
            Check(Math.Abs(measurements["naturalDurationSeconds"] - KnownDurationSeconds) <= .1 && !timer.IsEnabled,
                "known-duration-prepared-without-running-timer");
            phase = "prepared-awaiting-explicit-play";
            await Settle(500);
            Check(Equals(playPause.Content, "Play") && playbackStatus.Text == "Ready · press Play." &&
                player.Position.TotalSeconds <= .05 && !timer.IsEnabled && currentModel.NewClipCount == ClipLibrary.PageSize,
                "prepared-clip-stays-at-start-and-unviewed-without-autoplay");
            player.RaiseEvent(new RoutedEventArgs(MediaElement.BufferingStartedEvent));
            Check(!playPause.IsEnabled && playbackStatus.Text == "Loading clip…" && timer.IsEnabled,
                "initial-buffering-disables-play-with-loading-status-and-watchdog");
            player.RaiseEvent(new RoutedEventArgs(MediaElement.BufferingEndedEvent));
            player.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent));
            await Settle(250);
            Check(playPause.IsEnabled && Equals(playPause.Content, "Play") && playbackStatus.Text == "Ready · press Play." &&
                player.Position.TotalSeconds <= .05 && !timer.IsEnabled && currentModel.NewClipCount == ClipLibrary.PageSize,
                "initial-buffering-end-and-duplicate-open-do-not-autoplay");
            phase = "explicit-play"; Click(playPause);
            await Until(() => player.Position.TotalSeconds >= .35 && currentModel.NewClipCount == ClipLibrary.PageSize - 1, 2500);
            var persisted = await new ClipLibrary(currentModel.StorageDirectory).GetPageAsync(0, cancellation.Token);
            Check(persisted.Clips.Single(item => item.Id == selected.Id).ViewedAtUtc is not null &&
                persisted.Clips.Count(item => item.ViewedAtUtc is not null) == 1, "only-played-clip-view-state-persisted");
            measurements["advancedPositionSeconds"] = player.Position.TotalSeconds;
            phase = "pause"; Click(playPause);
            await Settle(150);
            var paused = player.Position.TotalSeconds; await Settle(350);
            Check(!timer.IsEnabled && Equals(playPause.Content, "Play") && Math.Abs(player.Position.TotalSeconds - paused) <= .2, "paused-position-stable");
            phase = "buffering-while-user-paused";
            player.RaiseEvent(new RoutedEventArgs(MediaElement.BufferingStartedEvent));
            Check(playbackStatus.Text == "Paused · buffering clip…" && playbackStatus.Visibility == Visibility.Visible,
                "buffering-status-visible-with-user-pause");
            player.RaiseEvent(new RoutedEventArgs(MediaElement.BufferingEndedEvent));
            player.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent));
            await Settle(250);
            Check(playbackStatus.Text == "Paused" && !timer.IsEnabled && Equals(playPause.Content, "Play") &&
                Math.Abs(player.Position.TotalSeconds - paused) <= .2, "buffering-end-and-duplicate-open-do-not-resume-paused-player");
            phase = "export-from-preview";
            var exportDirectory = Path.Combine(output, "generated-preview-export");
            await currentModel.SetStorageDirectoryAsync(exportDirectory);
            Click(export);
            await Until(() => currentModel.HasPreviewExportStatus && !currentModel.IsBusy, 3000);
            Check(!export.IsEnabled && !currentModel.CanExport && exportStatus.Visibility == Visibility.Visible &&
                exportStatus.Text.StartsWith("Export successful.", StringComparison.Ordinal) &&
                Directory.GetFiles(exportDirectory, "*.mp4").Length == 1, "successful-export-is-visible-and-disabled-for-this-preview");
            currentPage.UpdateLayout();
            Check(FitsViewport(host) && FitsViewport(exportStatus) && FitsViewport(playPause) && FitsViewport(position),
                "export-status-and-controls-fit-with-video");
            phase = "seek-paused"; position.Value = 2.25;
            await Until(() => Math.Abs(player.Position.TotalSeconds - 2.25) <= SeekToleranceSeconds, 1500);
            Check(!timer.IsEnabled && Equals(playPause.Content, "Play"), "seek-preserves-pause");
            measurements["pausedSeekPositionSeconds"] = player.Position.TotalSeconds;
            phase = "seek-drag";
            position.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            var beforeDrag = player.Position.TotalSeconds; position.Value = .75;
            await Settle(300);
            Check(Math.Abs(player.Position.TotalSeconds - beforeDrag) <= .2 && position.Value == .75, "drag-does-not-seek-or-reset-before-commit");
            position.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await Until(() => Math.Abs(player.Position.TotalSeconds - .75) <= SeekToleranceSeconds, 1500);
            Check(!timer.IsEnabled && Equals(playPause.Content, "Play"), "drag-commit-preserves-pause");
            phase = "resume-and-end"; Click(playPause);
            await Until(() => player.Position.TotalSeconds > 1.05 && timer.IsEnabled, 1500);
            position.Value = position.Maximum - .4;
            await Until(() => Equals(playPause.Content, "Play again"), 2500);
            Check(!timer.IsEnabled && Math.Abs(position.Value - position.Maximum) <= .01, "ended-shows-total-and-stops-timer");
            phase = "replay"; Click(playPause);
            await Until(() => player.Position.TotalSeconds >= .2 && player.Position.TotalSeconds < 1.5 && timer.IsEnabled, 2000);
            Check(Equals(playPause.Content, "Pause") && currentModel.NewClipCount == ClipLibrary.PageSize - 1, "replay-advances-without-new-view-state");
            phase = "close"; Click(close); await Settle(300);
            Check(host.Content is null && player.Source is null && !position.IsEnabled && position.Value == 0 &&
                time.Text == "0:00 / 0:00" && !timer.IsEnabled && !currentModel.HasSelection, "close-clears-source-selection-timer-and-time");
            player.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent));
            player.RaiseEvent(new RoutedEventArgs(MediaElement.BufferingStartedEvent));
            player.RaiseEvent(new RoutedEventArgs(MediaElement.MediaEndedEvent));
            Check(host.Content is null && !playPause.IsEnabled && !timer.IsEnabled &&
                playbackStatus.Visibility == Visibility.Collapsed && exportStatus.Visibility == Visibility.Collapsed,
                "closed-player-callbacks-cannot-revive-status-or-transport");
            phase = "reopen-before-unload"; Click(playCard);
            await Until(() => host.Content is MediaElement && playPause.IsEnabled && position.IsEnabled, 5000);
            var secondPlayer = host.Content as MediaElement ?? throw new InvalidOperationException("The reopened player is unavailable.");
            Check(!ReferenceEquals(secondPlayer, player) && secondPlayer.Volume == 0 && export.IsEnabled &&
                !currentModel.HasPreviewExportStatus, "reopen-owns-new-muted-player-and-fresh-export-state");
            player.RaiseEvent(new RoutedEventArgs(MediaElement.BufferingStartedEvent));
            player.RaiseEvent(new RoutedEventArgs(MediaElement.MediaEndedEvent));
            Check(ReferenceEquals(host.Content, secondPlayer) && Equals(playPause.Content, "Play") && !timer.IsEnabled &&
                playbackStatus.Text == "Ready · press Play.",
                "replaced-player-callbacks-do-not-change-current-playback");
            phase = "unload"; currentWindow.Content = null;
            await Until(() => !currentPage.IsLoaded, 1000); await Settle(300);
            secondPlayer.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent));
            secondPlayer.RaiseEvent(new RoutedEventArgs(MediaElement.BufferingStartedEvent));
            Check(host.Content is null && secondPlayer.Source is null && !timer.IsEnabled && !position.IsEnabled &&
                time.Text == "0:00 / 0:00" && !currentModel.HasSelection && playbackStatus.Visibility == Visibility.Collapsed,
                "unload-clears-live-player-and-timer");
            Check(GameClosed(), "game-remained-closed");

            bool FitsViewport(FrameworkElement element)
            {
                if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
                var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
                return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= viewport.ActualWidth + 1 &&
                    bounds.Bottom <= viewport.ActualHeight + 1;
            }
            static bool CleanVisualChain(DependencyObject element)
            {
                for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
                    if (current is UIElement visual && (visual.Opacity != 1 || visual.OpacityMask is not null || visual.Effect is not null)) return false;
                return true;
            }
        }
    }

    private static object CaptureFailureSnapshot(ClipsPage? page, ClipsViewModel? model, Window? window)
    {
        var scroll = page?.FindName("ClipsScroll") as ScrollViewer;
        var viewport = scroll?.Template?.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter;
        var host = page?.FindName("PlayerHost") as ContentControl;
        var player = host?.Content as MediaElement;
        var position = page?.FindName("PlaybackPosition") as Slider;
        var playPause = page?.FindName("PlayPauseButton") as Button;
        var selected = model?.SelectedClip;
        var timer = page is null ? null : typeof(ClipsPage).GetField("_playbackTimer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(page) as DispatcherTimer;
        var elements = new Dictionary<string, object>();
        elements["video"] = ElementState(player, viewport);
        foreach (var name in new[] { "PlaybackSurface", "PlayerHost", "PlayPauseButton", "PlaybackPosition", "PlaybackTime", "PlaybackVolume" })
            elements[name] = ElementState(page?.FindName(name) as FrameworkElement, viewport);
        if (page is not null)
        {
            var buttons = Descendants(page).OfType<Button>().ToArray();
            elements["close"] = ElementState(buttons.FirstOrDefault(button => Equals(button.Content, "Close player")), viewport);
            elements["export"] = ElementState(buttons.FirstOrDefault(button => Equals(button.Content, "Export clip…")), viewport);
        }
        return new
        {
            available = page is not null,
            pageLoaded = page?.IsLoaded,
            pageVisible = page?.IsVisible,
            pageWidth = Finite(page?.ActualWidth),
            pageHeight = Finite(page?.ActualHeight),
            dpiScaleX = page is null ? null : Finite(VisualTreeHelper.GetDpi(page).DpiScaleX),
            dpiScaleY = page is null ? null : Finite(VisualTreeHelper.GetDpi(page).DpiScaleY),
            windowVisible = window?.IsVisible,
            windowState = window is null ? (int?)null : (int)window.WindowState,
            hasSelection = model?.HasSelection,
            pathCharacters = model is null || selected is null ? (int?)null : Path.Combine(model.StorageDirectory, $"{selected.Id:N}.mp4").Length,
            expectedFileBytes = selected?.Entry.Media.FileBytes,
            hasError = model?.HasError,
            busy = model?.IsBusy,
            newClipCount = model?.NewClipCount,
            hostHasContent = host?.Content is not null,
            playerState = PlayerState(player),
            playPauseEnabled = playPause?.IsEnabled,
            pauseCaption = Equals(playPause?.Content, "Pause"),
            playCaption = Equals(playPause?.Content, "Play"),
            replayCaption = Equals(playPause?.Content, "Play again"),
            positionEnabled = position?.IsEnabled,
            positionValue = Finite(position?.Value),
            positionMaximum = Finite(position?.Maximum),
            timerEnabled = timer?.IsEnabled,
            verticalOffset = Finite(scroll?.VerticalOffset),
            scrollableHeight = Finite(scroll?.ScrollableHeight),
            viewportWidth = Finite(viewport?.ActualWidth),
            viewportHeight = Finite(viewport?.ActualHeight),
            elements
        };
    }
    private static object PlayerState(MediaElement? player)
    {
        if (player is null) return new { present = false };
        try
        {
            return new
            {
                present = true,
                stateReadable = true,
                loaded = player.IsLoaded,
                visible = player.IsVisible,
                sourceAssigned = player.Source is not null,
                naturalWidth = player.NaturalVideoWidth,
                naturalHeight = player.NaturalVideoHeight,
                naturalDurationSeconds = player.NaturalDuration.HasTimeSpan ? Finite(player.NaturalDuration.TimeSpan.TotalSeconds) : null,
                positionSeconds = Finite(player.Position.TotalSeconds),
                volume = Finite(player.Volume),
                buffering = player.IsBuffering,
                bufferingProgress = Finite(player.BufferingProgress),
                hasVideo = player.HasVideo,
                hasAudio = player.HasAudio,
                stretch = (int)player.Stretch
            };
        }
        catch { return new { present = true, stateReadable = false }; }
    }
    private static object ElementState(FrameworkElement? element, ScrollContentPresenter? viewport)
    {
        if (element is null) return new { present = false };
        Rect? bounds = null;
        try
        {
            if (viewport is not null)
                bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        }
        catch (InvalidOperationException) { }
        return new
        {
            present = true,
            loaded = element.IsLoaded,
            visible = element.IsVisible,
            enabled = element.IsEnabled,
            measureValid = element.IsMeasureValid,
            arrangeValid = element.IsArrangeValid,
            width = Finite(element.ActualWidth),
            height = Finite(element.ActualHeight),
            boundsAvailable = bounds.HasValue,
            left = Finite(bounds?.Left),
            top = Finite(bounds?.Top),
            right = Finite(bounds?.Right),
            bottom = Finite(bounds?.Bottom),
            fitsViewport = bounds is { } rectangle && viewport is not null && element.IsVisible &&
                element.ActualWidth > 0 && element.ActualHeight > 0 && rectangle.Left >= -1 && rectangle.Top >= -1 &&
                rectangle.Right <= viewport.ActualWidth + 1 && rectangle.Bottom <= viewport.ActualHeight + 1
        };
    }
    private static double? Finite(double? value) => value.HasValue && double.IsFinite(value.Value) ? value.Value : null;

    private static FileStream OpenFixture(string source, string output)
    {
        var checkout = new DirectoryInfo(output);
        while (!File.Exists(Path.Combine(checkout.FullName, "Wisp.sln"))) checkout = checkout.Parent ?? throw new ArgumentException();
        var workspace = checkout.Parent?.Name == "work" ? checkout.Parent.Parent?.FullName : null;
        var full = Path.GetFullPath(source);
        var inCheckout = full.StartsWith(checkout.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var inOutputs = workspace is not null && full.StartsWith(Path.Combine(workspace, "outputs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        Require((inCheckout || inOutputs) && string.Equals(Path.GetExtension(full), ".mp4", StringComparison.OrdinalIgnoreCase), "fixture-location");
        ClipLibrary.CheckPath(full);
        var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Require(file.Length is > 0 and <= 32 * 1024 * 1024, "fixture-size");
            var path = new StringBuilder(32768);
            var count = GetFinalPathNameByHandle(file.SafeFileHandle, path, (uint)path.Capacity, 0);
            Require(count > 0 && count < path.Capacity && string.Equals(path.ToString(), @"\\?\" + full, StringComparison.OrdinalIgnoreCase), "fixture-resolved-path");
            return file;
        }
        catch { file.Dispose(); throw; }
    }
    private static bool GameClosed()
    {
        foreach (var name in new[] { "ForzaHorizon6", "ForzaHorizon5", "ForzaHorizon4", "ForzaMotorsport" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) return false; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return true;
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var pending = new Queue<DependencyObject>(); var seen = new HashSet<DependencyObject>(); pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            if (!seen.Add(current)) continue;
            Require(seen.Count <= 4096, "ui-tree-bound"); yield return current;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) pending.Enqueue(child);
            for (var index = 0; current is Visual && index < VisualTreeHelper.GetChildrenCount(current); index++) pending.Enqueue(VisualTreeHelper.GetChild(current, index));
        }
    }
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private sealed class ReviewApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
    private sealed class InertRecorder : IClipRecorder
    {
        public ClipRecorderSnapshot Snapshot => new(ClipRecorderState.Unavailable, false, false, false, "Synthetic playback check; recorder unavailable.");
        public event EventHandler? StateChanged { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, ClipRecordingSpec recording, CancellationToken token, bool showCaptureBorder = false) => throw new InvalidOperationException();
        public Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken token) => throw new InvalidOperationException();
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
