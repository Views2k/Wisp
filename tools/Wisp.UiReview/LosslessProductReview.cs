using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using Wisp.App.Clips;

namespace Wisp.UiReview;

internal static class LosslessProductReview
{
    // Eight stream-copy repetitions of the byte-verified Wisp synthetic MP4.
    // 128 silent frames, 1280x720, 60 fps, full-range GBR; no user media.
    private const string FixtureHash = "9F3E88FEC6355888BEA9A0B4F56F4DC9FC90948DB64FEAE31F65EEC5B4EA806A";
    private const long FixtureBytes = 120772903, FixtureDuration100ns = 21333333;
    private const string AacFixtureHash = "79A8A6AD75AA0251B6D2B412E4B6A644C07C85CA497386A85BCF1FCA7D10EE7A";
    private const long AacFixtureBytes = 120825849;
    private const string ActualHash = "48A33FB214FE9F4D4802FAF88E71E61280C2C8CC680F2DF4A0135A1416806ABD";
    private const long ActualBytes = 965569830, ActualDuration100ns = 24666500;
    private const string LatestActualHash = "0CF1D32049C3BC3145F3B832345F88B12F60CBC167DD7C38C4F67164D04BF383";
    private const string LatestOracleHash = "9712A851B8C90CEBE1F20FA927D77435A8820C87D2EF77D5D6333986097DEC31";
    private const long LatestActualBytes = 3291416230, LatestActualDuration100ns = 68000000;
    private static readonly int[] LatestSampleFrames = [0, 75, 195, 375];

    internal static int Run(string source, string output, Func<ResourceDictionary> loadResources, bool withSyntheticAac = false,
        bool reportedClip = false, bool latestReportedClip = false, string? oraclePath = null, string? oracleSha256 = null,
        bool immediateStart = false, bool freshSave = false)
    {
        reportedClip |= latestReportedClip;
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(latestReportedClip ? 60 : reportedClip ? 50 : 30), Timeout.InfiniteTimeSpan);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(latestReportedClip ? 50 : reportedClip ? 40 : 20));
        var token = deadline.Token;
        var report = new Report
        {
            SyntheticAac = withSyntheticAac,
            ReportedClip = reportedClip,
            LatestReportedClip = latestReportedClip,
            ImmediateStart = immediateStart,
            FreshSave = freshSave
        };
        var expectedHash = latestReportedClip ? LatestActualHash : reportedClip ? ActualHash : withSyntheticAac ? AacFixtureHash : FixtureHash;
        var expectedBytes = latestReportedClip ? LatestActualBytes : reportedClip ? ActualBytes : withSyntheticAac ? AacFixtureBytes : FixtureBytes;
        var expectedWidth = reportedClip ? 3840 : 1280; var expectedHeight = reportedClip ? 2160 : 720;
        var expectedDuration = latestReportedClip ? LatestActualDuration100ns : reportedClip ? ActualDuration100ns : FixtureDuration100ns;
        var samples = new List<LosslessDecodedFrame>();
        string[]? oracle = null;
        var foreground = GetForegroundWindow();
        var priorContext = SynchronizationContext.Current;
        var checkedApps = Stopwatch.GetTimestamp();
        Application? application = null;
        Window? window = null;
        ClipsPage? page = null;
        ClipsViewModel? model = null;
        LosslessClipPlayer? observed = null;
        FileStream? held = null;
        Task? freshSaveWork = null;
        DispatcherOperation? freshOpenOperation = null, immediatePlayOperation = null;
        PropertyChangedEventHandler? freshModelHandler = null, freshCardHandler = null;
        NotifyCollectionChangedEventHandler? freshCollectionHandler = null;
        DependencyPropertyChangedEventHandler? immediatePlayHandler = null;
        Button? immediatePlayButton = null;
        var watchedCards = new List<ClipCardItem>();
        long freshSaveStarted = 0;
        using var bindings = new BindingTrace();
        using var stages = new StreamWriter(new FileStream(Path.Combine(output, "lossless-product-stages.txt"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
        try
        {
            Need(foreground != IntPtr.Zero && AppsClosed(), "apps-and-foreground-guard");
            Need(!freshSave || immediateStart && latestReportedClip, "fresh-save-requires-latest-immediate-mode");
            Stage("verify-pinned-source");
            var full = Path.GetFullPath(source);
            var checkout = FindCheckout(output);
            Need(Path.IsPathFullyQualified(source) && Path.GetExtension(full).Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
                (reportedClip ? Path.GetFileName(full) == (latestReportedClip ? "f7cacb517fab4f35abfa388f2d4ec4db.mp4" : "29e58a5054f547e7b7fc7535c16252da.mp4") :
                full.StartsWith(Path.Combine(checkout, "work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "pinned-source-required");
            ClipLibrary.CheckPath(full);
            held = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            Need(held.Length == expectedBytes, "fixture-size");
            var final = new StringBuilder(32768);
            var count = GetFinalPathNameByHandle(held.SafeFileHandle, final, (uint)final.Capacity, 0);
            Need(count > 0 && count < final.Capacity && final.ToString().Equals(@"\\?\" + full, StringComparison.OrdinalIgnoreCase), "held-fixture-path");
            report.SourceSha256 = Convert.ToHexString(SHA256.HashData(held));
            Need(report.SourceSha256 == expectedHash, "fixture-hash");
            if (latestReportedClip)
            {
                Stage("verify-independent-rgb-oracle");
                oracle = ReadLatestOracle(oraclePath, oracleSha256, report);
            }
            Need(new DriveInfo(Path.GetPathRoot(output)!).AvailableFreeSpace >= expectedBytes + 512L * 1024 * 1024, "isolated-copy-headroom");
            Stage("create-owned-test-library");
            var directory = Path.Combine(output, "isolated-library");
            var library = new ClipLibrary(directory);
            var recording = new ClipRecordingSpec(30, expectedHeight, 60, 100, LosslessVideo: true);
            var media = new FinalizedClipMedia(expectedBytes, expectedWidth, expectedHeight, 60, 0, expectedDuration,
                HasAudio: reportedClip || withSyntheticAac, LosslessVideo: true);
            if (!freshSave)
            {
                var reservation = library.ReserveSaveAsync(recording, token).GetAwaiter().GetResult();
                held.Position = 0;
                using (var target = new FileStream(reservation.MediaPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { held.CopyToAsync(target, token).GetAwaiter().GetResult(); target.Flush(); Need(target.Length == expectedBytes, "complete-copy"); }
                using (var copied = new FileStream(reservation.MediaPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Need(Convert.ToHexString(SHA256.HashData(copied)) == expectedHash, "isolated-copy-hash");
                library.CommitFinalizedAsync(reservation.Id, media, token).GetAwaiter().GetResult();
            }
            Guard(); Stage("create-passive-production-page");
            application = new ReviewApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = loadResources() };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var thumbnails = new RecorderThumbnailProvider(Path.Combine(AppContext.BaseDirectory, "Wisp.Recorder.exe"));
            model = freshSave
                ? new(new()
                {
                    Enabled = true,
                    LengthSeconds = 30,
                    ResolutionHeight = expectedHeight,
                    FrameRate = 60,
                    Quality = 100,
                    LosslessVideo = true
                },
                    new FreshSaveRecorder(held, recording, media, directory, expectedHash, report, token),
                    Dispatcher.CurrentDispatcher, thumbnails, directory)
                : new(new(), new InertRecorder(), Dispatcher.CurrentDispatcher, thumbnails, directory);
            page = new ClipsPage { DataContext = model };
            ((Slider)page.FindName("PlaybackVolume")).Value = 0;
            window = new Window
            {
                Title = "Wisp lossless player check",
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
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                const int index = -20, styles = 0x08000080;
                _ = SetWindowLong(handle, index, GetWindowLong(handle, index) | styles);
                Need((GetWindowLong(handle, index) & styles) == styles, "passive-host-style");
            };
            Await(model.InitializeAsync()); Guard(); window.Show(); Await(Scenario());
        }
        catch (Exception error)
        {
            report.ThumbnailAtFailure = LosslessThumbnailDecoder.LastDiagnostic;
            report.FailureStage = report.Stage;
            report.PlayerFailure = observed?.Snapshot.Failure;
            report.Failure = error is CheckFailure check ? check.Code : "diagnostic-exception";
            report.ExceptionType = error.GetType().Name; report.ExceptionHResult = error.HResult;
        }
        finally
        {
            Stage("cleanup");
            try
            {
                freshOpenOperation?.Abort(); immediatePlayOperation?.Abort();
                if (immediatePlayButton is not null && immediatePlayHandler is not null)
                    immediatePlayButton.IsEnabledChanged -= immediatePlayHandler;
                if (model is not null && freshModelHandler is not null) model.PropertyChanged -= freshModelHandler;
                if (model is not null && freshCollectionHandler is not null) model.Clips.CollectionChanged -= freshCollectionHandler;
                if (freshCardHandler is not null)
                    foreach (var card in watchedCards) card.PropertyChanged -= freshCardHandler;
                if (page is not null) page.DataContext = null;
                if (window is not null) { window.Content = null; window.Close(); }
                model?.Dispose();
                if (freshSaveWork is not null) Await(freshSaveWork.WaitAsync(TimeSpan.FromSeconds(5)));
                if (observed is not null) report.PlayerCleanupConfirmed = AwaitResult(observed.CloseAsync());
                report.EngineCleanupConfirmed = AwaitResult(LosslessMpvRuntime.ShutdownAsync());
                report.CleanupStatus = LosslessMpvRuntime.CleanupStatus;
                report.RuntimeStage = LosslessMpvRuntime.RuntimeStage;
                report.ThumbnailEngineCleanupConfirmed = AwaitResult(LosslessVlcRuntime.ShutdownAsync());
                report.OwnedWindowClosed = window?.IsVisible != true;
                application?.Shutdown();
            }
            catch (Exception error)
            {
                report.Failure ??= "cleanup-failed";
                report.ExceptionType ??= error.GetType().Name; report.ExceptionHResult ??= error.HResult;
            }
            held?.Dispose(); SynchronizationContext.SetSynchronizationContext(priorContext);
            report.ForegroundUnchanged &= foreground != IntPtr.Zero && GetForegroundWindow() == foreground;
            try { report.AppsClosedAtEnd = AppsClosed(); } catch { report.AppsClosedAtEnd = false; }
            report.BindingDiagnosticCount = bindings.TotalCount;
            report.ThumbnailAfterCleanup = LosslessThumbnailDecoder.LastDiagnostic;
        }
        if (reportedClip && report.PlayerCleanupConfirmed)
        {
            foreach (var sample in samples)
            {
                var rgb = new byte[checked(sample.Width * sample.Height * 3)];
                for (int from = 0, to = 0; to < rgb.Length; from += 4, to += 3)
                { rgb[to] = sample.Bgra[from + 2]; rgb[to + 1] = sample.Bgra[from + 1]; rgb[to + 2] = sample.Bgra[from]; }
                report.DecodedRgbHashes.Add(Convert.ToHexString(SHA256.HashData(rgb)));
            }
            if (latestReportedClip)
            {
                for (var sampleIndex = 0; sampleIndex < report.DecodedRgbHashes.Count && sampleIndex < LatestSampleFrames.Length; sampleIndex++)
                {
                    var target = LatestSampleFrames[sampleIndex];
                    // Exact RGB identity; one frame either side accommodates seek timestamp rounding.
                    report.OracleFrameMatches.Add(Enumerable.Range(Math.Max(0, target - 1), target == 0 ? 2 : 3)
                        .Where(frame => oracle![frame].Equals(report.DecodedRgbHashes[sampleIndex], StringComparison.OrdinalIgnoreCase)).ToArray());
                }
                if (report.DecodedRgbHashes.Count == LatestSampleFrames.Length + 1)
                    report.UninterruptedOracleFrameMatches = Enumerable.Range(0, oracle!.Length)
                        .Where(frame => oracle[frame].Equals(report.DecodedRgbHashes[LatestSampleFrames.Length], StringComparison.OrdinalIgnoreCase)).ToArray();
                if (report.UninterruptedSamplePosition is { } samplePosition && double.IsFinite(samplePosition))
                {
                    var clockFrame = Math.Clamp(samplePosition * 60, 0, 407);
                    report.UninterruptedClockFrame = clockFrame;
                    report.UninterruptedSampleMatchesClock = report.UninterruptedOracleFrameMatches.Any(frame => Math.Abs(frame - clockFrame) <= 1);
                }
                report.DecodedSamplesMatch = report.DecodedRgbHashes.Count == LatestSampleFrames.Length + 1 &&
                    report.OracleFrameMatches.Count == LatestSampleFrames.Length && report.OracleFrameMatches.All(matches => matches.Length > 0) &&
                    report.UninterruptedSampleMatchesClock;
            }
            else
            {
                // Preserve the historical clip's first-frame and 1.25-second identities.
                report.DecodedSamplesMatch = report.DecodedRgbHashes.SequenceEqual(new[]
                {
                    "EDC52B17B4A705B788C9A86532D45157E586FCDF9702F180ABD8C6A788D11218",
                    "A73EDC08D5E6C167D9042F3F5C0AC91D5749E1DD992479B04A9D062F6F5C4240"
                });
            }
        }
        var expectedChecks = (latestReportedClip ? 16 : withSyntheticAac || reportedClip ? 15 : 14) +
            (immediateStart ? 2 : 0) + (freshSave ? 2 : 0);
        report.Completed = report.Failure is null && report.Checks.Count == expectedChecks && report.Checks.All(item => item.Passed) &&
            report.ForegroundUnchanged && report.AppsClosedAtEnd && report.OwnedWindowClosed && report.PlayerCleanupConfirmed &&
            report.EngineCleanupConfirmed && report.ThumbnailEngineCleanupConfirmed && report.BindingDiagnosticCount == 0 && report.MutedThroughout &&
            (!reportedClip || report.DecodedSamplesMatch);
        File.WriteAllText(Path.Combine(output, "lossless-product-review.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report.Completed ? "Production lossless player check passed." : "Production lossless player check failed; see scalar report.");
        return report.Completed ? 0 : 2;

        void Stage(string stage) { report.Stage = stage; stages.WriteLine(stage); }
        void Check(bool value, string name) { report.Checks.Add(new(name, value)); Need(value, name); }
        void Guard()
        {
            token.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != foreground) { report.ForegroundUnchanged = false; throw new CheckFailure("foreground-changed"); }
            if (Stopwatch.GetElapsedTime(checkedApps) >= TimeSpan.FromMilliseconds(250))
            { checkedApps = Stopwatch.GetTimestamp(); Need(AppsClosed(), "apps-started-during-check"); }
            if (page is not null && ((Slider)page.FindName("PlaybackVolume")).Value != 0)
            { report.MutedThroughout = false; throw new CheckFailure("volume-changed"); }
            Need(observed?.Snapshot.Failure is null, "product-player-failed");
        }
        async Task Until(Func<bool> condition, double maximumSeconds = 5)
        {
            var started = Stopwatch.GetTimestamp();
            while (!condition())
            {
                Guard(); Need(Stopwatch.GetElapsedTime(started).TotalSeconds < maximumSeconds, "phase-deadline");
                observed?.RequestPoll(); await Task.Delay(20, token);
            }
            Guard();
        }
        async Task Hold(double seconds, Func<bool> invariant)
        {
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < seconds)
            { Guard(); observed?.RequestPoll(); Need(invariant(), "paused-invariant"); await Task.Delay(20, token); }
        }
        async Task Scenario()
        {
            var current = page!; var currentModel = model!;
            if (freshSave)
            {
                Stage("empty-fresh-save-page");
                await Until(() => current.IsLoaded && current.IsVisible, 2);
                Check(currentModel.Clips.Count == 0 && !currentModel.HasSelection && currentModel.CanSave,
                    "fresh-save-starts-with-empty-library-and-save-ready-fixture");
            }
            else
            {
                Stage("lossless-vmem-thumbnail");
                await Until(() => current.IsLoaded && currentModel.Clips.Count == 1 && currentModel.Clips[0].HasThumbnail, 7);
                var thumbnail = currentModel.Clips[0].Thumbnail!;
                var pixels = new byte[ClipThumbnailWire.PixelBytes]; thumbnail.CopyPixels(pixels, ClipThumbnailWire.Stride, 0);
                Check(thumbnail.IsFrozen && thumbnail.PixelWidth == 320 && thumbnail.PixelHeight == 180, "production-lossless-thumbnail");
                Check(pixels.Where((value, index) => index % 4 != 3).Distinct().Count() > 16 &&
                    pixels.Where((value, index) => index % 4 == 3).All(value => value == 255), "thumbnail-opaque-nonblank");
                report.ThumbnailSha256 = Convert.ToHexString(SHA256.HashData(pixels));
            }
            current.UpdateLayout();
            var card = freshSave ? null : Descendants(current).OfType<Button>().Single(button => button.DataContext is ClipCardItem);
            var host = (ContentControl)current.FindName("PlayerHost");
            var play = (Button)current.FindName("PlayPauseButton");
            var position = (Slider)current.FindName("PlaybackPosition");
            var status = (TextBlock)current.FindName("PlaybackStatus");
            var spinner = (FrameworkElement)current.FindName("PlaybackLoadingIndicator");
            var immediateClick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clickQueued = false;
            if (immediateStart)
            {
                immediatePlayButton = play;
                immediatePlayHandler = (_, _) =>
                {
                    if (!play.IsEnabled || clickQueued || immediateClick.Task.IsCompleted) return;
                    clickQueued = true;
                    immediatePlayOperation = current.Dispatcher.InvokeAsync(() =>
                    {
                        clickQueued = false;
                        if (!play.IsEnabled || immediateClick.Task.IsCompleted) return;
                        try
                        {
                            Guard();
                            observed = (LosslessClipPlayer?)typeof(ClipsPage).GetField("_losslessPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current);
                            Need(observed is not null, "quick-start-backend");
                            Check(observed.Snapshot is { Ready: true, Buffering: false, Ended: false } &&
                                spinner.Visibility == Visibility.Collapsed && status.Text == "Ready · press Play." &&
                                observed.Snapshot.Position <= .034 && currentModel.NewClipCount == 1,
                                "immediate-first-play-requires-prepared-paused-player");
                            if (freshSave)
                            {
                                report.FirstPlayMilliseconds = Stopwatch.GetElapsedTime(freshSaveStarted).TotalMilliseconds;
                                report.ThumbnailAtFirstPlay = LosslessThumbnailDecoder.LastDiagnostic;
                                report.ThumbnailWorkCompletedAtPlay = currentModel.ThumbnailCompletion.IsCompleted;
                            }
                            Stage("immediate-first-enabled-play"); Click(play);
                            immediateClick.TrySetResult();
                        }
                        catch (Exception error) { immediateClick.TrySetException(error); }
                    }, DispatcherPriority.Input);
                };
                play.IsEnabledChanged += immediatePlayHandler;
            }
            if (freshSave)
            {
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                freshSaveStarted = Stopwatch.GetTimestamp();
                freshCardHandler = (sender, _) =>
                {
                    if (sender is ClipCardItem item && item.PreviewStatus == "Loading preview…")
                        report.ThumbnailLoadingObserved = true;
                };
                freshCollectionHandler = (_, args) =>
                {
                    if (args.NewItems is null) return;
                    foreach (ClipCardItem added in args.NewItems)
                    {
                        watchedCards.Add(added);
                        added.PropertyChanged += freshCardHandler;
                    }
                };
                freshModelHandler = (_, args) =>
                {
                    if (args.PropertyName == nameof(ClipsViewModel.HasSelection) && currentModel.HasSelection)
                    {
                        report.ThumbnailAfterSelection = LosslessThumbnailDecoder.LastDiagnostic;
                        report.ThumbnailWorkCompletedAfterSelection = currentModel.ThumbnailCompletion.IsCompleted;
                    }
                    if (args.PropertyName != nameof(ClipsViewModel.CanBrowse) || !currentModel.CanBrowse ||
                        currentModel.Clips.Count != 1 || freshOpenOperation is not null) return;
                    report.FirstBrowseEnabledMilliseconds = Stopwatch.GetElapsedTime(freshSaveStarted).TotalMilliseconds;
                    freshOpenOperation = current.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            Guard();
                            var freshCard = Descendants(current).OfType<Button>().Single(button => button.DataContext is ClipCardItem);
                            Check(currentModel.CanBrowse && !currentModel.HasError && currentModel.Clips.Count == 1 && freshCard.IsEnabled,
                                "fresh-saved-card-is-actionable-at-first-browse-input");
                            Check(report.ThumbnailLoadingObserved, "fresh-save-starts-production-thumbnail-work");
                            report.FreshCardClickMilliseconds = Stopwatch.GetElapsedTime(freshSaveStarted).TotalMilliseconds;
                            report.ThumbnailAtCardOpen = LosslessThumbnailDecoder.LastDiagnostic;
                            report.ThumbnailWorkCompletedAtOpen = currentModel.ThumbnailCompletion.IsCompleted;
                            Stage("open-just-saved-lossless-card"); Click(freshCard);
                            opened.TrySetResult();
                        }
                        catch (Exception error) { opened.TrySetException(error); }
                    }, DispatcherPriority.Input);
                };
                currentModel.Clips.CollectionChanged += freshCollectionHandler;
                currentModel.PropertyChanged += freshModelHandler;
                Stage("save-through-production-view-model");
                var saving = currentModel.SaveClipAsync();
                freshSaveWork = saving;
                await Until(() => opened.Task.IsCompleted || saving.IsCompleted && currentModel.HasError, 35);
                Check(!currentModel.HasError, "fresh-save-completed-without-error");
                await opened.Task.WaitAsync(token);
                await saving.WaitAsync(token);
                report.FreshSaveCompleted = true;
            }
            else { Stage("prepare-production-lossless-player"); Click(card!); }
            await Until(() => host.Content is LosslessVideoHost, 3);
            observed = (LosslessClipPlayer?)typeof(ClipsPage).GetField("_losslessPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current);
            Need(observed is not null, "lossless-backend-route");
            await Until(() => observed.Snapshot.Ready && play.IsEnabled && position.IsEnabled, 8);
            Check(host.Content is LosslessVideoHost && observed.Host.Ready.IsCompletedSuccessfully, "clipped-native-video-host");
            Check(Math.Abs(observed.Snapshot.Duration - expectedDuration / 10_000_000d) <= .02, "actual-container-duration");
            if (immediateStart)
            {
                await immediateClick.Task.WaitAsync(token);
                await Until(() => observed.Snapshot.Position >= .3 && position.Value > 0 && !observed.Snapshot.Ended);
                Click(play);
                await Hold(.2, () => !observed.Snapshot.Ended);
                var decoded = await observed.ReadDiagnosticFrameAsync(token);
                Check(decoded.Bgra.Where((value, index) => index % 4 != 3).Distinct().Count() > 16,
                    "immediate-first-play-decodes-nonblank-frame");
                Stage("reset-after-quick-start"); position.Value = 0;
                Check(!play.IsEnabled && !position.IsEnabled && spinner.Visibility == Visibility.Visible,
                    "accepted-paused-seek-immediately-shows-loading-and-disables-play");
                Click(play);
                await Until(() => !observed.Snapshot.Buffering && play.IsEnabled && position.IsEnabled && observed.Snapshot.Position <= .034);
                Check(Equals(play.Content, "Play") && spinner.Visibility == Visibility.Collapsed,
                    "early-click-during-seek-does-not-start-playback");
            }
            else
            {
                Check(status.Text == "Ready · press Play.", "explicit-play-required");
                Stage("hold-paused-at-start");
                await Hold(2, () => observed.Snapshot.Position <= .034 && !observed.Snapshot.Ended);
                Check(currentModel.SelectedClip?.Entry.ViewedAtUtc is null, "opening-does-not-mark-viewed");
            }
            if (reportedClip) await SamplePaused();
            Stage("explicit-play-and-pause"); Click(play);
            await Until(() => observed.Snapshot.Position >= .3 && !observed.Snapshot.Ended); Click(play);
            await Hold(.2, () => !observed.Snapshot.Ended);
            var paused = observed.Snapshot.Position;
            await Hold(.3, () => Math.Abs(observed.Snapshot.Position - paused) <= .034);
            Check(Equals(play.Content, "Play") && paused > 0 && paused < 1.8, "explicit-pause-holds-clock");
            if (withSyntheticAac || reportedClip)
            {
                Stage("verify-muted-aac-decode");
                var statisticsTask = observed.ReadAudioStatisticsAsync(token);
                await Until(() => statisticsTask.IsCompleted, 2);
                var statistics = await statisticsTask;
                report.AudioCodec = statistics.Codec;
                report.AudioPosition = statistics.Position;
                report.AudioChannels = statistics.Channels;
                report.NativeVolume = statistics.Volume;
                report.NativeMute = statistics.Mute;
                if (statistics.Volume != 0) report.MutedThroughout = false;
                Check(statistics.Codec == "aac" && statistics.Channels > 0 && statistics.Position > 0 && statistics.Volume == 0 && statistics.Mute == true,
                    "aac-clock-advanced-while-muted");
            }
            Stage("paused-seek-to-duration"); position.Value = position.Maximum;
            await Until(() => observed.Snapshot.Ended && Equals(play.Content, "Play again"));
            await Hold(.2, () => observed.Snapshot.Ended);
            Check(Math.Abs(observed.Snapshot.Position - observed.Snapshot.Duration) < .034, "paused-endpoint-seek-completes");
            var seekPosition = reportedClip ? 1.25 : 1;
            Stage("paused-seek"); position.Value = seekPosition;
            await Until(() => Math.Abs(observed.Snapshot.Position - seekPosition) <= .05);
            await Hold(.3, () => Math.Abs(observed.Snapshot.Position - seekPosition) <= .05);
            if (reportedClip) await SamplePaused();
            if (latestReportedClip)
            {
                foreach (var target in LatestSampleFrames.Skip(2))
                {
                    var seconds = target / 60d;
                    Stage("paused-oracle-seek-" + target); position.Value = seconds;
                    await Until(() => Math.Abs(observed.Snapshot.Position - seconds) <= .05);
                    await Hold(.3, () => Math.Abs(observed.Snapshot.Position - seconds) <= .05 && !observed.Snapshot.Ended);
                    await SamplePaused();
                }
            }
            Check(Equals(play.Content, "Play"), "seek-does-not-resume");
            Stage("resume-to-end"); Click(play);
            await Until(() => observed.Snapshot.Ended && Equals(play.Content, "Play again"), latestReportedClip ? 9 : 5);
            Check(Math.Abs(position.Value - position.Maximum) <= .034, "end-updates-transport");
            await Until(() => currentModel.SelectedClip?.Entry.ViewedAtUtc is not null);
            Check(currentModel.SelectedClip!.Entry.ViewedAtUtc is not null, "actual-playback-marks-viewed");
            Stage("replay"); Click(play);
            await Until(() => !observed.Snapshot.Ended && observed.Snapshot.Position < 1);
            if (latestReportedClip)
            {
                Stage("uninterrupted-replay-sample");
                await Until(() => observed.Snapshot.Position >= 3.25 && !observed.Snapshot.Ended, 6);
                Click(play);
                await Hold(.2, () => !observed.Snapshot.Ended);
                var samplePosition = observed.Snapshot.Position;
                await Hold(.2, () => Math.Abs(observed.Snapshot.Position - samplePosition) <= .034 && !observed.Snapshot.Ended);
                Check(Equals(play.Content, "Play") && samplePosition >= 3.25 && samplePosition < observed.Snapshot.Duration,
                    "uninterrupted-replay-paused-for-sample");
                await SamplePaused();
                report.UninterruptedSamplePosition = observed.Snapshot.Position;
                Click(play);
            }
            await Until(() => observed.Snapshot.Ended && Equals(play.Content, "Play again"), latestReportedClip ? 9 : 5);
            Check(observed.Snapshot.Ended, "replay-completes");
            Stage("close-production-player");
            Click(Descendants(current).OfType<Button>().Single(button => Equals(button.Content, "Close player")));
            Check(host.Content is null && !currentModel.HasSelection, "close-clears-selected-player");
            Check(await observed.CloseAsync(), "bounded-player-cleanup"); Guard();
        }
        async Task SamplePaused()
        {
            Guard();
            samples.Add(await observed!.ReadDiagnosticFrameAsync(token));
            report.DecodedSamplePositions.Add(observed.Snapshot.Position);
            Guard();
        }
    }

    private static string[] ReadLatestOracle(string? path, string? expectedSha256, Report report)
    {
        Need(path is not null && Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
            expectedSha256 is { Length: 64 } && expectedSha256.Equals(LatestOracleHash, StringComparison.OrdinalIgnoreCase), "explicit-pinned-oracle-required");
        var full = Path.GetFullPath(path);
        ClipLibrary.CheckPath(full);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        Need(stream.Length is > 0 and <= 128 * 1024, "oracle-size-bound");
        var final = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(stream.SafeFileHandle, final, (uint)final.Capacity, 0);
        Need(count > 0 && count < final.Capacity && final.ToString().Equals(@"\\?\" + full, StringComparison.OrdinalIgnoreCase), "held-oracle-path");
        report.OracleSha256 = Convert.ToHexString(SHA256.HashData(stream));
        Need(report.OracleSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase), "oracle-hash");
        stream.Position = 0;
        var hashes = new List<string>();
        var header = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#')) { header.Add(line.Trim()); continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',').Select(value => value.Trim()).ToArray();
            var index = hashes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Need(hashes.Count < 408 && parts.Length == 6 && parts[0] == "0" && parts[1] == index && parts[2] == index &&
                parts[3] == "1" && parts[4] == "24883200" && parts[5].Length == 64 && parts[5].All(Uri.IsHexDigit), "oracle-frame-format");
            hashes.Add(parts[5]);
        }
        Need(hashes.Count == 408 && header.Contains("#hash: SHA256") && header.Contains("#tb 0: 1/60") &&
            header.Contains("#media_type 0: video") && header.Contains("#codec_id 0: rawvideo") && header.Contains("#dimensions 0: 3840x2160"), "complete-rgb-oracle-required");
        report.OracleFrameCount = hashes.Count;
        return hashes.ToArray();
    }

    private static string FindCheckout(string output)
    {
        for (DirectoryInfo? directory = new(output); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln"))) return directory.FullName;
        throw new CheckFailure("checkout-required");
    }
    private static bool AppsClosed()
    {
        foreach (var name in new[] { "Wisp", "ForzaHorizon6", "ForzaHorizon5", "ForzaHorizon4", "ForzaMotorsport" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) return false; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return true;
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>(); var seen = new HashSet<DependencyObject>(); queue.Enqueue(root);
        while (queue.TryDequeue(out var item))
        {
            if (!seen.Add(item)) continue;
            Need(seen.Count < 4096, "bounded-ui-tree"); yield return item;
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) queue.Enqueue(child);
            for (var index = 0; item is Visual && index < VisualTreeHelper.GetChildrenCount(item); index++) queue.Enqueue(VisualTreeHelper.GetChild(item, index));
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
    private static T AwaitResult<T>(Task<T> task) { Await(task); return task.GetAwaiter().GetResult(); }
    private static void Need([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string code)
    { if (!value) throw new CheckFailure(code); }
    private sealed class CheckFailure(string code) : Exception { internal string Code { get; } = code; }
    private sealed class ReviewApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
    private sealed class InertRecorder : IClipRecorder
    {
        public ClipRecorderSnapshot Snapshot => new(ClipRecorderState.Unavailable, false, false, false, "Synthetic playback check; recording unavailable.");
        public event EventHandler? StateChanged { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, ClipRecordingSpec recording, CancellationToken token, bool showCaptureBorder = false) => throw new InvalidOperationException();
        public Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class FreshSaveRecorder(FileStream source, ClipRecordingSpec recording, FinalizedClipMedia media,
        string libraryDirectory, string expectedHash, Report report, CancellationToken budget) : IClipRecorder
    {
        private int _saveCount;
        public ClipRecorderSnapshot Snapshot => new(ClipRecorderState.Buffering, true, true, true, "Local fixture ready; no game capture.");
        public event EventHandler? StateChanged { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, ClipRecordingSpec settings, CancellationToken token, bool showCaptureBorder = false)
            => throw new InvalidOperationException();
        public async Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken token)
        {
            Need(Interlocked.Increment(ref _saveCount) == 1 && target.Recording == recording &&
                string.Equals(Path.GetFullPath(target.MediaPath), Path.Combine(libraryDirectory, target.Id.ToString("N") + ".mp4"),
                    StringComparison.OrdinalIgnoreCase), "single-owned-fresh-save-target");
            using var copying = CancellationTokenSource.CreateLinkedTokenSource(token, budget);
            copying.CancelAfter(TimeSpan.FromSeconds(30));
            source.Position = 0;
            await using (var copy = new FileStream(target.MediaPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(copy, copying.Token).ConfigureAwait(false);
                await copy.FlushAsync(copying.Token).ConfigureAwait(false);
                Need(copy.Length == media.FileBytes, "complete-fresh-save-copy");
            }
            using (var copied = new FileStream(target.MediaPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                report.FreshCopySha256 = Convert.ToHexString(await SHA256.HashDataAsync(copied, copying.Token).ConfigureAwait(false));
                Need(report.FreshCopySha256 == expectedHash, "fresh-save-copy-hash");
            }
            return media;
        }
    }
    private sealed record CheckResult(string Name, bool Passed);
    private sealed class Report
    {
        public string Stage { get; set; } = "guards";
        public bool Completed { get; set; }
        public string? Failure { get; set; }
        public string? FailureStage { get; set; }
        public string? PlayerFailure { get; set; }
        public string? ExceptionType { get; set; }
        public int? ExceptionHResult { get; set; }
        public string? SourceSha256 { get; set; }
        public string? ThumbnailSha256 { get; set; }
        public bool SyntheticAac { get; set; }
        public bool ReportedClip { get; set; }
        public bool LatestReportedClip { get; set; }
        public bool ImmediateStart { get; set; }
        public bool FreshSave { get; set; }
        public bool FreshSaveCompleted { get; set; }
        public string? FreshCopySha256 { get; set; }
        public double? FirstBrowseEnabledMilliseconds { get; set; }
        public double? FreshCardClickMilliseconds { get; set; }
        public double? FirstPlayMilliseconds { get; set; }
        public bool ThumbnailLoadingObserved { get; set; }
        public bool? ThumbnailWorkCompletedAtOpen { get; set; }
        public bool? ThumbnailWorkCompletedAfterSelection { get; set; }
        public bool? ThumbnailWorkCompletedAtPlay { get; set; }
        public LosslessThumbnailDecoder.ThumbnailDiagnostic? ThumbnailAtCardOpen { get; set; }
        public LosslessThumbnailDecoder.ThumbnailDiagnostic? ThumbnailAfterSelection { get; set; }
        public LosslessThumbnailDecoder.ThumbnailDiagnostic? ThumbnailAtFirstPlay { get; set; }
        public string? OracleSha256 { get; set; }
        public int OracleFrameCount { get; set; }
        public string? AudioCodec { get; set; }
        public double? AudioPosition { get; set; }
        public long? AudioChannels { get; set; }
        public double? NativeVolume { get; set; }
        public bool? NativeMute { get; set; }
        public LosslessThumbnailDecoder.ThumbnailDiagnostic? ThumbnailAtFailure { get; set; }
        public LosslessThumbnailDecoder.ThumbnailDiagnostic? ThumbnailAfterCleanup { get; set; }
        public bool ForegroundUnchanged { get; set; } = true;
        public bool MutedThroughout { get; set; } = true;
        public bool AppsClosedAtEnd { get; set; }
        public bool OwnedWindowClosed { get; set; }
        public bool PlayerCleanupConfirmed { get; set; }
        public bool EngineCleanupConfirmed { get; set; }
        public bool ThumbnailEngineCleanupConfirmed { get; set; }
        public string? CleanupStatus { get; set; }
        public string? RuntimeStage { get; set; }
        public int BindingDiagnosticCount { get; set; }
        public List<CheckResult> Checks { get; } = [];
        public List<string> DecodedRgbHashes { get; } = [];
        public List<double> DecodedSamplePositions { get; } = [];
        public List<int[]> OracleFrameMatches { get; } = [];
        public double? UninterruptedSamplePosition { get; set; }
        public int[] UninterruptedOracleFrameMatches { get; set; } = [];
        public double? UninterruptedClockFrame { get; set; }
        public bool UninterruptedSampleMatchesClock { get; set; }
        public bool DecodedSamplesMatch { get; set; }
        public bool SourceModified => false;
        public bool GameplayCaptured => false;
        public bool? AudioPlayed => MutedThroughout ? false : null;
        public string Scope => "Pinned MP4 in a new isolated library. Actual ClipsPage lossless route, explicit transport, viewport and cleanup. " +
            (FreshSave ? "Actual view-model save and library publication use a fixture recorder that copies already-finalized bytes; no native recording or finalizer is exercised. The card opens at first browse-ready input without waiting for its production thumbnail. Thumbnail task completion or cancellation is not native teardown; engine shutdown is checked separately. " : "Vmem poster presence is checked before opening. ") +
            (SyntheticAac || ReportedClip ? "AAC decoder format and advancing audio clock are checked while muted. " : "Silent fixture; no AAC check. ") +
            (LatestReportedClip ? "Four paused decoded video frames match exact RGB hashes within one frame of each seek target; another sample after uninterrupted replay past 3.25 seconds must match the independent 408-frame CPU oracle within one frame of its recorded paused clock. No image files written. " :
                ReportedClip ? "Two decoded video frames are compared with independent CPU RGB hashes; no image files written. " : "") +
            "No audible-output, displayed-pixel fidelity, physical A/V sync or gameplay performance claim.";
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
