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

    internal static int Run(string source, string output, Func<ResourceDictionary> loadResources, bool withSyntheticAac = false, bool reportedClip = false)
    {
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(reportedClip ? 50 : 30), Timeout.InfiniteTimeSpan);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(reportedClip ? 40 : 20));
        var token = deadline.Token;
        var report = new Report { SyntheticAac = withSyntheticAac, ReportedClip = reportedClip };
        var expectedHash = reportedClip ? ActualHash : withSyntheticAac ? AacFixtureHash : FixtureHash;
        var expectedBytes = reportedClip ? ActualBytes : withSyntheticAac ? AacFixtureBytes : FixtureBytes;
        var expectedWidth = reportedClip ? 3840 : 1280; var expectedHeight = reportedClip ? 2160 : 720;
        var expectedDuration = reportedClip ? ActualDuration100ns : FixtureDuration100ns;
        var samples = new List<LosslessDecodedFrame>();
        var foreground = GetForegroundWindow();
        var priorContext = SynchronizationContext.Current;
        var checkedApps = Stopwatch.GetTimestamp();
        Application? application = null;
        Window? window = null;
        ClipsPage? page = null;
        ClipsViewModel? model = null;
        LosslessClipPlayer? observed = null;
        FileStream? held = null;
        using var bindings = new BindingTrace();
        using var stages = new StreamWriter(new FileStream(Path.Combine(output, "lossless-product-stages.txt"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
        try
        {
            Need(foreground != IntPtr.Zero && AppsClosed(), "apps-and-foreground-guard");
            Stage("verify-pinned-source");
            var full = Path.GetFullPath(source);
            var checkout = FindCheckout(output);
            Need(Path.IsPathFullyQualified(source) && Path.GetExtension(full).Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
                (reportedClip ? Path.GetFileName(full) == "29e58a5054f547e7b7fc7535c16252da.mp4" :
                full.StartsWith(Path.Combine(checkout, "work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "pinned-source-required");
            ClipLibrary.CheckPath(full);
            held = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            Need(held.Length == expectedBytes, "fixture-size");
            var final = new StringBuilder(32768);
            var count = GetFinalPathNameByHandle(held.SafeFileHandle, final, (uint)final.Capacity, 0);
            Need(count > 0 && count < final.Capacity && final.ToString().Equals(@"\\?\" + full, StringComparison.OrdinalIgnoreCase), "held-fixture-path");
            report.SourceSha256 = Convert.ToHexString(SHA256.HashData(held));
            Need(report.SourceSha256 == expectedHash, "fixture-hash");
            Need(new DriveInfo(Path.GetPathRoot(output)!).AvailableFreeSpace >= expectedBytes + 512L * 1024 * 1024, "isolated-copy-headroom");
            Stage("create-owned-test-library");
            var directory = Path.Combine(output, "isolated-library");
            var library = new ClipLibrary(directory);
            var reservation = library.ReserveSaveAsync(new(30, expectedHeight, 60, 100, LosslessVideo: true), token).GetAwaiter().GetResult();
            held.Position = 0;
            using (var target = new FileStream(reservation.MediaPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { held.CopyToAsync(target, token).GetAwaiter().GetResult(); target.Flush(); Need(target.Length == expectedBytes, "complete-copy"); }
            using (var copied = new FileStream(reservation.MediaPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Need(Convert.ToHexString(SHA256.HashData(copied)) == expectedHash, "isolated-copy-hash");
            library.CommitFinalizedAsync(reservation.Id, new(expectedBytes, expectedWidth, expectedHeight, 60, 0, expectedDuration,
                HasAudio: reportedClip || withSyntheticAac, LosslessVideo: true), token).GetAwaiter().GetResult();
            Guard(); Stage("create-passive-production-page");
            application = new ReviewApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = loadResources() };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var thumbnails = new RecorderThumbnailProvider(Path.Combine(AppContext.BaseDirectory, "Wisp.Recorder.exe"));
            model = new(new(), new InertRecorder(), Dispatcher.CurrentDispatcher, thumbnails, directory);
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
                if (page is not null) page.DataContext = null;
                if (window is not null) { window.Content = null; window.Close(); }
                model?.Dispose();
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
            // Two exact paused samples: first frame and the measured frame at 1.25s.
            report.DecodedSamplesMatch = report.DecodedRgbHashes.SequenceEqual(new[]
            {
                "EDC52B17B4A705B788C9A86532D45157E586FCDF9702F180ABD8C6A788D11218",
                "A73EDC08D5E6C167D9042F3F5C0AC91D5749E1DD992479B04A9D062F6F5C4240"
            });
        }
        report.Completed = report.Failure is null && report.Checks.Count == (withSyntheticAac || reportedClip ? 15 : 14) && report.Checks.All(item => item.Passed) &&
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
            Stage("lossless-vmem-thumbnail");
            await Until(() => current.IsLoaded && currentModel.Clips.Count == 1 && currentModel.Clips[0].HasThumbnail, 7);
            var thumbnail = currentModel.Clips[0].Thumbnail!;
            var pixels = new byte[ClipThumbnailWire.PixelBytes]; thumbnail.CopyPixels(pixels, ClipThumbnailWire.Stride, 0);
            Check(thumbnail.IsFrozen && thumbnail.PixelWidth == 320 && thumbnail.PixelHeight == 180, "production-lossless-thumbnail");
            Check(pixels.Where((value, index) => index % 4 != 3).Distinct().Count() > 16 &&
                pixels.Where((value, index) => index % 4 == 3).All(value => value == 255), "thumbnail-opaque-nonblank");
            report.ThumbnailSha256 = Convert.ToHexString(SHA256.HashData(pixels));
            current.UpdateLayout();
            var card = Descendants(current).OfType<Button>().Single(button => button.DataContext is ClipCardItem);
            var host = (ContentControl)current.FindName("PlayerHost");
            var play = (Button)current.FindName("PlayPauseButton");
            var position = (Slider)current.FindName("PlaybackPosition");
            var status = (TextBlock)current.FindName("PlaybackStatus");
            Stage("prepare-production-lossless-player"); Click(card);
            await Until(() => host.Content is LosslessVideoHost, 3);
            observed = (LosslessClipPlayer?)typeof(ClipsPage).GetField("_losslessPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current);
            Need(observed is not null, "lossless-backend-route");
            await Until(() => observed.Snapshot.Ready && play.IsEnabled && position.IsEnabled, 8);
            Check(host.Content is LosslessVideoHost && observed.Host.Ready.IsCompletedSuccessfully, "clipped-native-video-host");
            Check(Math.Abs(observed.Snapshot.Duration - expectedDuration / 10_000_000d) <= .02, "actual-container-duration");
            Check(status.Text == "Ready · press Play.", "explicit-play-required");
            Stage("hold-paused-at-start");
            await Hold(2, () => observed.Snapshot.Position <= .034 && !observed.Snapshot.Ended);
            if (reportedClip) samples.Add(await observed.ReadDiagnosticFrameAsync(token));
            Check(currentModel.SelectedClip?.Entry.ViewedAtUtc is null, "opening-does-not-mark-viewed");
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
            if (reportedClip) samples.Add(await observed.ReadDiagnosticFrameAsync(token));
            Check(Equals(play.Content, "Play"), "seek-does-not-resume");
            Stage("resume-to-end"); Click(play);
            await Until(() => observed.Snapshot.Ended && Equals(play.Content, "Play again"));
            Check(Math.Abs(position.Value - position.Maximum) <= .034, "end-updates-transport");
            await Until(() => currentModel.SelectedClip?.Entry.ViewedAtUtc is not null);
            Check(currentModel.SelectedClip!.Entry.ViewedAtUtc is not null, "actual-playback-marks-viewed");
            Stage("replay"); Click(play);
            await Until(() => !observed.Snapshot.Ended && observed.Snapshot.Position < 1);
            await Until(() => observed.Snapshot.Ended && Equals(play.Content, "Play again"));
            Check(observed.Snapshot.Ended, "replay-completes");
            Stage("close-production-player");
            Click(Descendants(current).OfType<Button>().Single(button => Equals(button.Content, "Close player")));
            Check(host.Content is null && !currentModel.HasSelection, "close-clears-selected-player");
            Check(await observed.CloseAsync(), "bounded-player-cleanup"); Guard();
        }
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
        public bool DecodedSamplesMatch { get; set; }
        public bool SourceModified => false;
        public bool GameplayCaptured => false;
        public bool? AudioPlayed => MutedThroughout ? false : null;
        public string Scope => "Pinned MP4 in a new isolated library. Actual ClipsPage lossless route, vmem poster presence, explicit transport, viewport and cleanup. " +
            (SyntheticAac || ReportedClip ? "AAC decoder format and advancing audio clock are checked while muted. " : "Silent fixture; no AAC check. ") +
            (ReportedClip ? "Two decoded video frames are compared with independent CPU RGB hashes; no image files written. " : "") +
            "No audible-output, displayed-pixel fidelity, physical A/V sync or gameplay performance claim.";
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
