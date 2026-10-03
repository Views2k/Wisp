using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using Wisp.App.Clips;

namespace Wisp.UiReview;

internal static class ClipsLargePlaybackReview
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = true
    };

    internal static int Run(string source, string metadata, string output, Func<ResourceDictionary> loadResources,
        bool immediateStart = false, bool freshSave = false, bool baselineStartup = false)
    {
        using var watchdog = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var token = budget.Token;
        var report = new ReviewReport { ImmediateStart = immediateStart, FreshSave = freshSave, BaselineStartup = baselineStartup };
        var foreground = GetForegroundWindow();
        var priorContext = SynchronizationContext.Current;
        FileStream? original = null;
        Application? application = null;
        Window? window = null;
        ClipsViewModel? model = null;
        ClipsPage? page = null;
        MediaElement? observedPlayer = null;
        Button? quickStartButton = null;
        FrameworkElement? loadingIndicator = null;
        DependencyPropertyChangedEventHandler? quickStartHandler = null, loadingHandler = null;
        DispatcherOperation? quickStartOperation = null;
        DispatcherOperation? freshOpenOperation = null;
        PropertyChangedEventHandler? freshModelHandler = null, freshCardHandler = null;
        NotifyCollectionChangedEventHandler? freshCollectionHandler = null;
        var watchedCards = new List<ClipCardItem>();
        Task? freshSaveWork = null;
        long freshSaveStarted = 0;
        var appsCheckedAt = Stopwatch.GetTimestamp();
        using var bindings = new BindingTrace();
        try
        {
            Require(foreground != IntPtr.Zero && AppsClosed(), "apps-and-foreground-guard");
            Require(!baselineStartup || freshSave && typeof(ClipsPlaybackState).GetProperty("Loading") is null,
                "baseline-requires-original-playback-state");
            report.Phase = "validate-explicit-source";
            using var description = OpenReadOnly(metadata, ".json", 16 * 1024);
            var fixture = JsonSerializer.Deserialize<FixtureMetadata>(description, JsonOptions)
                ?? throw new InvalidDataException();
            Require(fixture.Recording is not null && fixture.Media is not null, "fixture-metadata");
            Require(!freshSave || immediateStart && !fixture.Recording.LosslessVideo && !fixture.Media.LosslessVideo,
                "fresh-save-requires-immediate-compressed-playback");
            original = OpenReadOnly(source, ".mp4", 4L * 1024 * 1024 * 1024);
            // A requested 30-second clip can finalize at 29.x seconds; this scenario needs only 20.
            Require(original.Length == fixture.Media.FileBytes &&
                fixture.Media.ActualEnd100ns - fixture.Media.ActualStart100ns >= TimeSpan.FromSeconds(20).Ticks,
                "large-clip-metadata");
            report.Recording = fixture.Recording;
            report.FileBytes = original.Length;
            var drive = new DriveInfo(Path.GetPathRoot(output)!);
            Require(drive.AvailableFreeSpace >= original.Length + 512L * 1024 * 1024, "copy-disk-headroom");
            report.Phase = "isolated-copy";
            var libraryDirectory = Path.Combine(output, "isolated-library");
            var library = new ClipLibrary(libraryDirectory);
            if (!freshSave)
            {
                var target = library.ReserveSaveAsync(fixture.Recording, token).GetAwaiter().GetResult();
                using var copying = CancellationTokenSource.CreateLinkedTokenSource(token);
                copying.CancelAfter(TimeSpan.FromSeconds(30));
                report.SourceSha256 = CopyOwnedAsync(original, target.MediaPath, copying.Token).GetAwaiter().GetResult();
                report.PrivateVideoCopyCreated = true;
                library.CommitFinalizedAsync(target.Id, fixture.Media, token).GetAwaiter().GetResult();
            }
            report.Phase = "create-passive-player";
            Guard(); Require(AppsClosed(), "apps-remained-closed");
            application = new ReviewApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources = loadResources();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            if (freshSave)
            {
                var helper = Path.Combine(AppContext.BaseDirectory, "Wisp.Recorder.exe");
                ClipLibrary.CheckPath(helper);
                Require(File.Exists(helper), "production-thumbnail-helper-required");
                var settings = new ClipsSettings
                {
                    Enabled = true,
                    LengthSeconds = fixture.Recording.LengthSeconds,
                    ResolutionHeight = fixture.Recording.ResolutionHeight,
                    FrameRate = fixture.Recording.FrameRate,
                    Quality = fixture.Recording.Quality,
                    CaptureSystemAudio = fixture.Recording.CaptureSystemAudio
                };
                model = new(settings, new FreshSaveRecorder(original, fixture, libraryDirectory, report, token),
                    Dispatcher.CurrentDispatcher, new RecorderThumbnailProvider(helper), libraryDirectory: libraryDirectory);
            }
            else model = new(new(), new InertRecorder(), Dispatcher.CurrentDispatcher, libraryDirectory: libraryDirectory);
            page = new ClipsPage { DataContext = model };
            ((Slider)page.FindName("PlaybackVolume")).Value = 0;
            var currentWindow = new Window
            {
                Title = "Wisp local clip playback check",
                Width = 980,
                Height = 750,
                ShowActivated = false,
                ShowInTaskbar = false,
                Focusable = false,
                IsHitTestVisible = false,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)application.FindResource("WindowBrush"),
                Content = page
            };
            window = currentWindow;
            currentWindow.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(currentWindow).Handle;
                const int index = -20, styles = 0x08000080;
                _ = SetWindowLong(handle, index, GetWindowLong(handle, index) | styles);
                Require((GetWindowLong(handle, index) & styles) == styles, "passive-window-style");
            };
            Await(model.InitializeAsync());
            Guard(); window.Show();
            Await(Scenario(fixture));
        }
        catch (Exception error)
        {
            report.FailurePhase = report.Phase;
            if (error is CheckFailure failure) report.FailureCode = failure.Code;
            report.ExceptionType = error.GetType().Name;
            report.ExceptionHResult = error.HResult;
        }
        finally
        {
            try
            {
                if (quickStartButton is not null && quickStartHandler is not null) quickStartButton.IsEnabledChanged -= quickStartHandler;
                if (loadingIndicator is not null && loadingHandler is not null) loadingIndicator.IsVisibleChanged -= loadingHandler;
                quickStartOperation?.Abort();
                freshOpenOperation?.Abort();
                if (model is not null && freshModelHandler is not null) model.PropertyChanged -= freshModelHandler;
                if (model is not null && freshCollectionHandler is not null) model.Clips.CollectionChanged -= freshCollectionHandler;
                if (freshCardHandler is not null)
                    foreach (var card in watchedCards) card.PropertyChanged -= freshCardHandler;
                if (window is not null) { window.Content = null; window.Close(); }
                if (page is not null) page.DataContext = null;
                var thumbnailWork = model?.ThumbnailCompletion;
                model?.Dispose();
                if (freshSaveWork is not null) Await(freshSaveWork.WaitAsync(TimeSpan.FromSeconds(5)));
                if (freshSave && thumbnailWork is not null)
                {
                    Await(thumbnailWork.WaitAsync(TimeSpan.FromSeconds(5)));
                    report.ThumbnailWorkSettledAfterCleanup = thumbnailWork.IsCompleted;
                }
                report.OwnedWindowClosed = window?.IsVisible != true;
            }
            catch (Exception error)
            {
                report.FailurePhase ??= "cleanup";
                report.ExceptionType ??= error.GetType().Name;
                report.ExceptionHResult ??= error.HResult;
            }
            original?.Dispose();
            report.ForegroundUnchanged &= foreground != IntPtr.Zero && GetForegroundWindow() == foreground;
            report.AppsClosedAtEnd = AppsClosed();
            report.BindingDiagnosticCount = bindings.TotalCount;
            SynchronizationContext.SetSynchronizationContext(priorContext);
            try { application?.Shutdown(); }
            catch (Exception error)
            {
                report.FailurePhase ??= "application-cleanup";
                report.ExceptionType ??= error.GetType().Name;
                report.ExceptionHResult ??= error.HResult;
            }
        }
        report.Completed = report.FailurePhase is null && report.OwnedWindowClosed && report.ForegroundUnchanged &&
            report.AppsClosedAtEnd && report.BindingDiagnosticCount == 0;
        report.Phase = "complete";
        File.WriteAllText(Path.Combine(output, "clips-large-playback-review.json"), JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"Large clip playback check: {(report.Completed ? "PASS" : "FAIL")}; {report.Checks.Count} checks.");
        return report.Completed ? 0 : 2;

        void Guard()
        {
            token.ThrowIfCancellationRequested();
            if (observedPlayer is not null && observedPlayer.Volume != 0)
            {
                report.MutedThroughoutObservedChecks = false;
                throw new CheckFailure("player-unmuted");
            }
            if (Stopwatch.GetElapsedTime(appsCheckedAt) >= TimeSpan.FromSeconds(1))
            {
                appsCheckedAt = Stopwatch.GetTimestamp();
                Require(AppsClosed(), "apps-started-during-check");
            }
            if (GetForegroundWindow() != foreground)
            {
                report.ForegroundUnchanged = false;
                throw new InvalidOperationException();
            }
        }
        void Check(bool condition, string name)
        {
            report.Checks.Add(new(name, condition));
            Require(condition, name);
        }
        void ObserveFreshSave(string name)
        {
            if (!freshSave || model is null || freshSaveStarted == 0) return;
            var card = model.Clips.FirstOrDefault();
            var loading = card?.PreviewStatus == "Loading preview…";
            report.ThumbnailLoadingObserved |= loading;
            report.ThumbnailReadyObserved |= card?.HasThumbnail == true;
            if (report.FreshSaveEvents.Count < 64)
                report.FreshSaveEvents.Add(new(name, Stopwatch.GetElapsedTime(freshSaveStarted).TotalMilliseconds,
                    model.IsBusy, model.CanBrowse, model.HasSelection, loading, card?.HasThumbnail == true));
        }
        async Task Until(Func<bool> condition, TimeSpan timeout)
        {
            var start = Stopwatch.GetTimestamp();
            while (!condition())
            {
                Guard();
                if (Stopwatch.GetElapsedTime(start) >= timeout) throw new TimeoutException();
                await Task.Delay(25, token);
            }
            Guard();
        }
        async Task Hold(TimeSpan duration)
        {
            var start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start) < duration) { Guard(); await Task.Delay(25, token); }
        }
        async Task Scenario(FixtureMetadata fixture)
        {
            var currentPage = page!;
            var currentModel = model!;
            await Until(() => currentPage.IsLoaded && currentPage.IsVisible, TimeSpan.FromSeconds(2));
            currentPage.UpdateLayout();
            var card = freshSave ? null : Descendants(currentPage).OfType<Button>().Single(item => item.DataContext is ClipCardItem);
            var host = (ContentControl)currentPage.FindName("PlayerHost");
            var transport = (Button)currentPage.FindName("PlayPauseButton");
            var position = (Slider)currentPage.FindName("PlaybackPosition");
            var status = (TextBlock)currentPage.FindName("PlaybackStatus");
            report.Phase = "prepare-production-player";
            var opening = Stopwatch.GetTimestamp();
            var quickStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long explicitPlayAt = 0;
            void RecordEvent(string name)
            {
                if (report.Events.Count < 64)
                    report.Events.Add(new(name, Stopwatch.GetElapsedTime(opening).TotalMilliseconds));
            }
            void ObservePlayer(MediaElement value)
            {
                if (ReferenceEquals(observedPlayer, value)) return;
                Require(observedPlayer is null, "single-production-player");
                observedPlayer = value;
                value.MediaOpened += (_, _) => RecordEvent("opened");
                value.BufferingStarted += (_, _) => RecordEvent("buffering-started");
                value.BufferingEnded += (_, _) => RecordEvent("buffering-ended");
                value.MediaEnded += (_, _) => RecordEvent("ended");
                value.MediaFailed += (_, _) => RecordEvent("failed");
            }
            if (immediateStart)
            {
                var indicator = currentPage.FindName("PlaybackLoadingIndicator") as FrameworkElement;
                Require(indicator is not null || baselineStartup, "production-loading-indicator");
                indicator ??= new FrameworkElement { Visibility = Visibility.Collapsed };
                loadingIndicator = indicator;
                loadingHandler = (_, args) => { if (args.NewValue is true) report.LoadingIndicatorObserved = true; };
                loadingIndicator.IsVisibleChanged += loadingHandler;
                quickStartButton = transport;
                quickStartHandler = (_, args) =>
                {
                    if (args.NewValue is not true || quickStartOperation is not null) return;
                    report.FirstEnabledMilliseconds = Stopwatch.GetElapsedTime(opening).TotalMilliseconds;
                    // Queue actual input priority: never reenter the IsEnabled setter, wait for layout,
                    // add a warm-up delay, or request a frame before this first Play attempt.
                    quickStartOperation = currentPage.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            Guard();
                            Require(host.Content is MediaElement, "quick-start-production-player");
                            var quickPlayer = (MediaElement)host.Content;
                            ObservePlayer(quickPlayer);
                            report.Phase = "immediate-first-enabled-play";
                            var readiness = new FirstClickReadiness(
                                Stopwatch.GetElapsedTime(opening).TotalMilliseconds,
                                quickPlayer.HasVideo, quickPlayer.IsBuffering, quickPlayer.NaturalVideoWidth, quickPlayer.NaturalVideoHeight,
                                quickPlayer.NaturalDuration.HasTimeSpan ? quickPlayer.NaturalDuration.TimeSpan.TotalSeconds : 0,
                                quickPlayer.Position.TotalSeconds, quickPlayer.IsLoaded, quickPlayer.IsMeasureValid, quickPlayer.IsArrangeValid,
                                host.ActualWidth, host.ActualHeight, host.IsMeasureValid, host.IsArrangeValid,
                                transport.IsEnabled, position.IsEnabled, indicator.Visibility == Visibility.Visible, status.Text);
                            report.FirstClick = readiness;
                            if (freshSave)
                            {
                                ObserveFreshSave("first-enabled-play");
                                report.ThumbnailWorkCompletedAtPlay = currentModel.ThumbnailCompletion.IsCompleted;
                            }
                            Check(readiness.PlayEnabled && readiness.SeekEnabled && !readiness.LoadingIndicatorVisible &&
                                readiness.HasVideo && !readiness.Buffering && quickPlayer.Volume == 0,
                                "first-enabled-click-has-ready-controls-and-no-loading");
                            Check(currentModel.NewClipCount == 1 && quickPlayer.Position.TotalSeconds <= .05 && Equals(transport.Content, "Play"),
                                "first-enabled-click-has-no-prior-playback");
                            explicitPlayAt = Stopwatch.GetTimestamp();
                            Click(transport);
                            report.ImmediateClickIssued = true;
                            Check(Equals(transport.Content, "Pause"), "immediate-click-commits-play-intent");
                            quickStarted.TrySetResult();
                        }
                        catch (Exception error) { quickStarted.TrySetException(error); }
                    }, DispatcherPriority.Input);
                };
                transport.IsEnabledChanged += quickStartHandler;
            }
            if (freshSave)
            {
                Check(currentModel.Clips.Count == 0 && !currentModel.HasSelection && currentModel.CanSave,
                    "fresh-save-starts-with-empty-library-and-save-ready-fixture");
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                freshSaveStarted = Stopwatch.GetTimestamp();
                freshCardHandler = (_, args) =>
                {
                    if (args.PropertyName is nameof(ClipCardItem.PreviewStatus) or nameof(ClipCardItem.HasThumbnail))
                        ObserveFreshSave(args.PropertyName);
                };
                freshCollectionHandler = (_, args) =>
                {
                    if (args.NewItems is not null)
                        foreach (ClipCardItem added in args.NewItems)
                        {
                            watchedCards.Add(added);
                            added.PropertyChanged += freshCardHandler;
                        }
                    ObserveFreshSave("gallery-" + args.Action);
                };
                freshModelHandler = (_, args) =>
                {
                    if (args.PropertyName is nameof(ClipsViewModel.IsBusy) or nameof(ClipsViewModel.HasSelection))
                        ObserveFreshSave(args.PropertyName);
                    if (args.PropertyName != nameof(ClipsViewModel.CanBrowse) || !currentModel.CanBrowse ||
                        currentModel.Clips.Count != 1 || freshOpenOperation is not null) return;
                    report.FirstBrowseEnabledMilliseconds = Stopwatch.GetElapsedTime(freshSaveStarted).TotalMilliseconds;
                    freshOpenOperation = currentPage.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            Guard();
                            Check(currentModel.CanBrowse && !currentModel.HasError && currentModel.Clips.Count == 1,
                                "fresh-saved-card-is-actionable-after-publication");
                            var freshCard = Descendants(currentPage).OfType<Button>().Single(item => item.DataContext is ClipCardItem);
                            Check(freshCard.IsEnabled, "fresh-saved-card-enabled-at-first-browse-input");
                            Check(report.ThumbnailLoadingObserved, "fresh-save-starts-production-thumbnail-work");
                            report.FreshCardClickMilliseconds = Stopwatch.GetElapsedTime(freshSaveStarted).TotalMilliseconds;
                            report.ThumbnailWorkCompletedAtOpen = currentModel.ThumbnailCompletion.IsCompleted;
                            ObserveFreshSave("first-browse-card-open");
                            opening = Stopwatch.GetTimestamp();
                            report.Phase = "open-just-saved-card";
                            Click(freshCard);
                            opened.TrySetResult();
                        }
                        catch (Exception error) { opened.TrySetException(error); }
                    }, DispatcherPriority.Input);
                };
                currentModel.Clips.CollectionChanged += freshCollectionHandler;
                currentModel.PropertyChanged += freshModelHandler;
                report.Phase = "save-through-production-view-model";
                var saving = currentModel.SaveClipAsync();
                freshSaveWork = saving;
                await Until(() => opened.Task.IsCompleted || saving.IsCompleted && currentModel.HasError,
                    TimeSpan.FromSeconds(35));
                Check(!currentModel.HasError, "fresh-save-completed-without-error");
                await opened.Task.WaitAsync(token);
                await saving.WaitAsync(token);
                report.FreshSaveCompleted = true;
            }
            else Click(card!);
            await Until(() => host.Content is MediaElement, TimeSpan.FromSeconds(3));
            var player = (MediaElement)host.Content;
            ObservePlayer(player);
            if (immediateStart)
            {
                if (!transport.IsEnabled && !report.ImmediateClickIssued && loadingIndicator!.Visibility == Visibility.Visible)
                {
                    report.EarlyDisabledClickAttempted = true;
                    Check(!position.IsEnabled && Equals(transport.Content, "Play"), "loading-disables-play-and-seek");
                    var before = player.Position.TotalSeconds;
                    Click(transport); // Deliberately routed while disabled; production's handler must refuse it.
                    report.EarlyDisabledClickRefused = !transport.IsEnabled && !position.IsEnabled && Equals(transport.Content, "Play") &&
                        player.Position.TotalSeconds == before && currentModel.NewClipCount == 1;
                    Check(report.EarlyDisabledClickRefused, "disabled-early-click-does-not-start-playback");
                }
                await Until(() => quickStarted.Task.IsCompleted, TimeSpan.FromSeconds(16));
                await quickStarted.Task;
                if (!baselineStartup) Check(report.LoadingIndicatorObserved, "preparation-indicator-was-observed");
                report.ReadyObservedMilliseconds = report.FirstEnabledMilliseconds;
            }
            else
            {
                await Until(() => transport.IsEnabled && position.IsEnabled && player.IsMeasureValid && player.IsArrangeValid,
                    TimeSpan.FromSeconds(16));
                report.ReadyObservedMilliseconds = Stopwatch.GetElapsedTime(opening).TotalMilliseconds;
            }
            report.ActualWidth = player.NaturalVideoWidth; report.ActualHeight = player.NaturalVideoHeight;
            report.ActualHasAudio = player.HasAudio;
            report.ActualDurationSeconds = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0;
            report.RecordedDurationSeconds = (fixture.Media.ActualEnd100ns - fixture.Media.ActualStart100ns) / 10_000_000d;
            report.DurationDifferenceSeconds = report.ActualDurationSeconds - report.RecordedDurationSeconds;
            report.DurationAgreesWithinHalfSecond = Math.Abs(report.DurationDifferenceSeconds) <= .5;
            // Encoded duration is verified separately. Windows' reported duration is retained without assuming its precision.
            Check(player.HasVideo && player.CanPause && player.Volume == 0 && report.ActualDurationSeconds >= 20,
                "actual-media-is-playable");
            Check(player.HasAudio == fixture.Media.HasAudio && player.NaturalVideoWidth == fixture.Media.Width &&
                player.NaturalVideoHeight == fixture.Media.Height,
                "actual-media-matches-selected-metadata");
            if (!immediateStart)
            {
                report.Phase = "prepared-two-second-hold";
                await Hold(TimeSpan.FromSeconds(2));
                Check(player.Position.TotalSeconds <= .05 && Equals(transport.Content, "Play") &&
                    status.Text == "Ready · press Play." && currentModel.NewClipCount == 1,
                    "prepared-at-start-without-autoplay-or-view-write");
            }
            report.Phase = "explicit-play-fifteen-seconds";
            if (!immediateStart) { Click(transport); explicitPlayAt = Stopwatch.GetTimestamp(); }
            var playing = explicitPlayAt;
            var lastAdvance = playing;
            var lastPosition = player.Position.TotalSeconds;
            while (Stopwatch.GetElapsedTime(playing) < TimeSpan.FromSeconds(15))
            {
                Guard();
                var seconds = player.Position.TotalSeconds;
                report.Samples.Add(new(Stopwatch.GetElapsedTime(playing).TotalMilliseconds, seconds, player.IsBuffering));
                if (freshSave && (report.VideoSamples.Count == 0 && seconds >= .2 ||
                    report.VideoSamples.Count == 1 && seconds >= 1 || report.VideoSamples.Count == 2 && seconds >= 5))
                    ObserveVideo(player);
                if (seconds > lastPosition + .001) { lastAdvance = Stopwatch.GetTimestamp(); lastPosition = seconds; }
                report.MaximumClockPlateauMilliseconds = Math.Max(report.MaximumClockPlateauMilliseconds,
                    Stopwatch.GetElapsedTime(lastAdvance).TotalMilliseconds);
                await Task.Delay(100, token);
            }
            report.PlayedPositionSeconds = player.Position.TotalSeconds;
            if (freshSave)
            {
                ObserveFreshSave("fifteen-second-playback-complete");
                report.ThumbnailWorkCompletedAfterPlayback = currentModel.ThumbnailCompletion.IsCompleted;
            }
            Check(report.PlayedPositionSeconds >= 13 && report.MaximumClockPlateauMilliseconds < 1500 &&
                currentModel.NewClipCount == 0 && report.Events.All(item => item.Name != "failed"),
                "media-clock-advances-without-observed-long-pause");
            report.Phase = "pause-seek-resume";
            if (immediateStart && !baselineStartup)
            {
                // Only checks the buffering controls; this synthetic event is after the real first-play timing check.
                report.SyntheticBufferingCheck = true;
                player.RaiseEvent(new RoutedEventArgs(MediaElement.BufferingStartedEvent));
                Check(loadingIndicator!.Visibility == Visibility.Visible && !position.IsEnabled && transport.IsEnabled &&
                    Equals(transport.Content, "Pause"), "buffering-shows-indicator-disables-seek-and-allows-pause");
            }
            Click(transport); await Hold(TimeSpan.FromMilliseconds(150));
            var paused = player.Position.TotalSeconds;
            if (immediateStart && !baselineStartup)
            {
                player.RaiseEvent(new RoutedEventArgs(MediaElement.BufferingEndedEvent));
                Check(loadingIndicator!.Visibility == Visibility.Collapsed && position.IsEnabled && transport.IsEnabled &&
                    Equals(transport.Content, "Play"), "buffering-ended-retains-explicit-pause");
            }
            await Hold(TimeSpan.FromMilliseconds(350));
            Check(Equals(transport.Content, "Play") && Math.Abs(player.Position.TotalSeconds - paused) <= .2,
                "explicit-pause-is-stable");
            var target = report.ActualDurationSeconds / 2;
            position.Value = target;
            await Until(() => Math.Abs(player.Position.TotalSeconds - target) <= .35, TimeSpan.FromSeconds(2));
            Check(Equals(transport.Content, "Play"), "seek-does-not-start-playback");
            Click(transport);
            await Until(() => player.Position.TotalSeconds >= target + .5, TimeSpan.FromSeconds(2));
            Check(Equals(transport.Content, "Pause"), "explicit-resume-advances");
            if (freshSave) ObserveVideo(player);
            report.Phase = "close-production-player";
            Click(Descendants(currentPage).OfType<Button>().Single(item => Equals(item.Content, "Close player")));
            Check(host.Content is null && player.Source is null && !currentModel.HasSelection, "close-clears-source");
        }
        void ObserveVideo(MediaElement player)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawRectangle(new VisualBrush(player) { Stretch = Stretch.Fill }, null, new Rect(0, 0, 320, 180));
            var frame = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
            frame.Render(visual);
            var pixels = new byte[320 * 180 * 4]; frame.CopyPixels(pixels, 320 * 4, 0);
            var nonblack = 0;
            for (var index = 0; index < pixels.Length; index += 4)
                if (Math.Max(pixels[index], Math.Max(pixels[index + 1], pixels[index + 2])) > 12) nonblack++;
            report.VideoSamples.Add(new(player.Position.TotalSeconds, nonblack,
                Convert.ToHexString(SHA256.HashData(pixels))));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
            using var file = new FileStream(Path.Combine(output, $"player-visual-{report.VideoSamples.Count}.png"), FileMode.CreateNew);
            encoder.Save(file);
        }
    }

    private static async Task<string> CopyOwnedAsync(FileStream source, string path, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var copy = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[65536];
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            await copy.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        await copy.FlushAsync(token).ConfigureAwait(false);
        Require(copy.Length == source.Length, "complete-isolated-copy");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static FileStream OpenReadOnly(string requested, string extension, long maximumBytes)
    {
        Require(Path.IsPathFullyQualified(requested) && !requested.StartsWith(@"\\", StringComparison.Ordinal) &&
            string.Equals(Path.GetExtension(requested), extension, StringComparison.OrdinalIgnoreCase), "explicit-local-source");
        var full = Path.GetFullPath(requested);
        ClipLibrary.CheckPath(full);
        var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        try
        {
            Require(file.Length > 0 && file.Length <= maximumBytes, "source-size-bound");
            var path = new StringBuilder(32768);
            var count = GetFinalPathNameByHandle(file.SafeFileHandle, path, (uint)path.Capacity, 0);
            Require(count > 0 && count < path.Capacity &&
                string.Equals(path.ToString(), @"\\?\" + full, StringComparison.OrdinalIgnoreCase), "held-source-path");
            return file;
        }
        catch { file.Dispose(); throw; }
    }
    private static bool AppsClosed()
    {
        foreach (var name in new[] { "Wisp", "ForzaHorizon6", "ForzaHorizon5", "ForzaHorizon4", "ForzaMotorsport" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) return false; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return true;
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string reason)
    { if (!condition) throw new CheckFailure(reason); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var pending = new Queue<DependencyObject>(); var seen = new HashSet<DependencyObject>(); pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            if (!seen.Add(current)) continue;
            Require(seen.Count <= 4096, "bounded-ui-tree"); yield return current;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) pending.Enqueue(child);
            for (var index = 0; current is Visual && index < VisualTreeHelper.GetChildrenCount(current); index++)
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
        }
    }
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private sealed class ReviewApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
    private sealed class InertRecorder : IClipRecorder
    {
        public ClipRecorderSnapshot Snapshot => new(ClipRecorderState.Unavailable, false, false, false, "Local playback check; recording unavailable.");
        public event EventHandler? StateChanged { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, ClipRecordingSpec recording, CancellationToken token, bool showCaptureBorder = false) => throw new InvalidOperationException();
        public Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class FreshSaveRecorder(FileStream source, FixtureMetadata fixture, string libraryDirectory,
        ReviewReport report, CancellationToken budget) : IClipRecorder
    {
        private int _saveCount;
        public ClipRecorderSnapshot Snapshot => new(ClipRecorderState.Buffering, true, true, true, "Local fixture ready; no game capture.");
        public event EventHandler? StateChanged { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, ClipRecordingSpec recording, CancellationToken token, bool showCaptureBorder = false)
            => throw new InvalidOperationException();
        public async Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken token)
        {
            Require(Interlocked.Increment(ref _saveCount) == 1 && target.Recording == fixture.Recording &&
                string.Equals(Path.GetFullPath(target.MediaPath), Path.Combine(libraryDirectory, target.Id.ToString("N") + ".mp4"),
                    StringComparison.OrdinalIgnoreCase), "single-owned-fresh-save-target");
            using var copying = CancellationTokenSource.CreateLinkedTokenSource(token, budget);
            copying.CancelAfter(TimeSpan.FromSeconds(30));
            report.SourceSha256 = await CopyOwnedAsync(source, target.MediaPath, copying.Token).ConfigureAwait(false);
            report.PrivateVideoCopyCreated = true;
            return fixture.Media;
        }
    }
    private sealed record FixtureMetadata(ClipRecordingSpec Recording, FinalizedClipMedia Media);
    private sealed record CheckResult(string Name, bool Passed);
    private sealed record PlaybackEvent(string Name, double ElapsedMilliseconds);
    private sealed record PositionSample(double ElapsedMilliseconds, double PositionSeconds, bool Buffering);
    private sealed record VideoSample(double PositionSeconds, int NonblackPixels, string PixelSha256);
    private sealed record FreshSaveEvent(string Name, double ElapsedMilliseconds, bool Busy, bool CanBrowse,
        bool HasSelection, bool ThumbnailLoading, bool HasThumbnail);
    private sealed record FirstClickReadiness(double ElapsedMilliseconds, bool HasVideo, bool Buffering, int VideoWidth, int VideoHeight,
        double DurationSeconds, double PositionSeconds, bool PlayerLoaded, bool PlayerMeasureValid, bool PlayerArrangeValid,
        double HostWidth, double HostHeight, bool HostMeasureValid, bool HostArrangeValid, bool PlayEnabled, bool SeekEnabled,
        bool LoadingIndicatorVisible, string Status);
    private sealed class CheckFailure(string code) : Exception { public string Code { get; } = code; }
    private sealed class ReviewReport
    {
        public bool Completed { get; set; }
        public bool ImmediateStart { get; set; }
        public bool BaselineStartup { get; set; }
        public bool FreshSave { get; set; }
        public bool FreshSaveCompleted { get; set; }
        public double? FirstBrowseEnabledMilliseconds { get; set; }
        public double? FreshCardClickMilliseconds { get; set; }
        public bool ThumbnailLoadingObserved { get; set; }
        public bool ThumbnailReadyObserved { get; set; }
        public bool? ThumbnailWorkCompletedAtOpen { get; set; }
        public bool? ThumbnailWorkCompletedAtPlay { get; set; }
        public bool? ThumbnailWorkCompletedAfterPlayback { get; set; }
        public bool? ThumbnailWorkSettledAfterCleanup { get; set; }
        public List<FreshSaveEvent> FreshSaveEvents { get; } = [];
        public bool LoadingIndicatorObserved { get; set; }
        public bool EarlyDisabledClickAttempted { get; set; }
        public bool EarlyDisabledClickRefused { get; set; }
        public bool ImmediateClickIssued { get; set; }
        public bool SyntheticBufferingCheck { get; set; }
        public double FirstEnabledMilliseconds { get; set; }
        public FirstClickReadiness? FirstClick { get; set; }
        public string Phase { get; set; } = "guards";
        public string? FailurePhase { get; set; }
        public string? FailureCode { get; set; }
        public string? ExceptionType { get; set; }
        public int? ExceptionHResult { get; set; }
        public bool ForegroundUnchanged { get; set; } = true;
        public bool OwnedWindowClosed { get; set; }
        public bool AppsClosedAtEnd { get; set; }
        public int BindingDiagnosticCount { get; set; }
        public long FileBytes { get; set; }
        public string? SourceSha256 { get; set; }
        public ClipRecordingSpec? Recording { get; set; }
        public int ActualWidth { get; set; }
        public int ActualHeight { get; set; }
        public bool ActualHasAudio { get; set; }
        public bool PrivateVideoCopyCreated { get; set; }
        public double ActualDurationSeconds { get; set; }
        public double RecordedDurationSeconds { get; set; }
        public double DurationDifferenceSeconds { get; set; }
        public bool DurationAgreesWithinHalfSecond { get; set; }
        public double ReadyObservedMilliseconds { get; set; }
        public double MaximumClockPlateauMilliseconds { get; set; }
        public double PlayedPositionSeconds { get; set; }
        public List<CheckResult> Checks { get; } = [];
        public List<PlaybackEvent> Events { get; } = [];
        public List<PositionSample> Samples { get; } = [];
        public List<VideoSample> VideoSamples { get; } = [];
        public bool SourceWrites => false;
        public bool InstalledLibraryMetadataAccessed => false;
        public bool MutedThroughoutObservedChecks { get; set; } = true;
        public bool? AudioPlayed => MutedThroughoutObservedChecks ? false : null;
        public bool GameCaptureUsed => false;
        public bool PlayerVisualSamplesSaved => FreshSave;
        public bool ExtractedAudioSaved => false;
        public string Scope => "One explicitly selected local clip copied into an isolated library. " +
            (FreshSave ? "Actual view-model save, library publication and production thumbnail provider; a fixture recorder copies already-finalized bytes. The card opens at first browse-ready input without waiting for its thumbnail. No native recording or finalizer is exercised. " : "") +
            "Production page preparation, transport and media-clock observations only. No full predecode, displayed-frame smoothness, audible A/V sync or gameplay performance claim. The private fixture copy is retained in this output folder.";
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
