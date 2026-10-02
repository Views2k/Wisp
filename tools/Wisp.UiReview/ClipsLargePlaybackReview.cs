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

    internal static int Run(string source, string metadata, string output, Func<ResourceDictionary> loadResources)
    {
        using var watchdog = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var token = budget.Token;
        var report = new ReviewReport();
        var foreground = GetForegroundWindow();
        var priorContext = SynchronizationContext.Current;
        FileStream? original = null;
        Application? application = null;
        Window? window = null;
        ClipsViewModel? model = null;
        ClipsPage? page = null;
        MediaElement? observedPlayer = null;
        var appsCheckedAt = Stopwatch.GetTimestamp();
        using var bindings = new BindingTrace();
        try
        {
            Require(foreground != IntPtr.Zero && AppsClosed(), "apps-and-foreground-guard");
            report.Phase = "validate-explicit-source";
            using var description = OpenReadOnly(metadata, ".json", 16 * 1024);
            var fixture = JsonSerializer.Deserialize<FixtureMetadata>(description, JsonOptions)
                ?? throw new InvalidDataException();
            Require(fixture.Recording is not null && fixture.Media is not null, "fixture-metadata");
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
            var target = library.ReserveSaveAsync(fixture.Recording, token).GetAwaiter().GetResult();
            using (var copying = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                copying.CancelAfter(TimeSpan.FromSeconds(30));
                report.SourceSha256 = CopyOwnedAsync(original, target.MediaPath, copying.Token).GetAwaiter().GetResult();
                report.PrivateVideoCopyCreated = true;
            }
            library.CommitFinalizedAsync(target.Id, fixture.Media, token).GetAwaiter().GetResult();
            report.Phase = "create-passive-player";
            Guard(); Require(AppsClosed(), "apps-remained-closed");
            application = new ReviewApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources = loadResources();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            model = new(new(), new InertRecorder(), Dispatcher.CurrentDispatcher, libraryDirectory: libraryDirectory);
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
                if (window is not null) { window.Content = null; window.Close(); }
                if (page is not null) page.DataContext = null;
                model?.Dispose();
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
            var card = Descendants(currentPage).OfType<Button>().Single(item => item.DataContext is ClipCardItem);
            var host = (ContentControl)currentPage.FindName("PlayerHost");
            var transport = (Button)currentPage.FindName("PlayPauseButton");
            var position = (Slider)currentPage.FindName("PlaybackPosition");
            var status = (TextBlock)currentPage.FindName("PlaybackStatus");
            report.Phase = "prepare-production-player";
            var opening = Stopwatch.GetTimestamp();
            Click(card);
            await Until(() => host.Content is MediaElement, TimeSpan.FromSeconds(3));
            var player = (MediaElement)host.Content;
            observedPlayer = player;
            void RecordEvent(string name)
            {
                if (report.Events.Count < 64)
                    report.Events.Add(new(name, Stopwatch.GetElapsedTime(opening).TotalMilliseconds));
            }
            player.MediaOpened += (_, _) => RecordEvent("opened");
            player.BufferingStarted += (_, _) => RecordEvent("buffering-started");
            player.BufferingEnded += (_, _) => RecordEvent("buffering-ended");
            player.MediaEnded += (_, _) => RecordEvent("ended");
            player.MediaFailed += (_, _) => RecordEvent("failed");
            await Until(() => transport.IsEnabled && position.IsEnabled && player.IsMeasureValid && player.IsArrangeValid,
                TimeSpan.FromSeconds(16));
            report.ReadyObservedMilliseconds = Stopwatch.GetElapsedTime(opening).TotalMilliseconds;
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
            report.Phase = "prepared-two-second-hold";
            await Hold(TimeSpan.FromSeconds(2));
            Check(player.Position.TotalSeconds <= .05 && Equals(transport.Content, "Play") &&
                status.Text == "Ready · press Play." && currentModel.NewClipCount == 1,
                "prepared-at-start-without-autoplay-or-view-write");
            report.Phase = "explicit-play-fifteen-seconds";
            Click(transport);
            var playing = Stopwatch.GetTimestamp();
            var lastAdvance = playing;
            var lastPosition = player.Position.TotalSeconds;
            while (Stopwatch.GetElapsedTime(playing) < TimeSpan.FromSeconds(15))
            {
                Guard();
                var seconds = player.Position.TotalSeconds;
                report.Samples.Add(new(Stopwatch.GetElapsedTime(playing).TotalMilliseconds, seconds, player.IsBuffering));
                if (seconds > lastPosition + .001) { lastAdvance = Stopwatch.GetTimestamp(); lastPosition = seconds; }
                report.MaximumClockPlateauMilliseconds = Math.Max(report.MaximumClockPlateauMilliseconds,
                    Stopwatch.GetElapsedTime(lastAdvance).TotalMilliseconds);
                await Task.Delay(100, token);
            }
            report.PlayedPositionSeconds = player.Position.TotalSeconds;
            Check(report.PlayedPositionSeconds >= 13 && report.MaximumClockPlateauMilliseconds < 1500 &&
                currentModel.NewClipCount == 0 && report.Events.All(item => item.Name != "failed"),
                "media-clock-advances-without-observed-long-pause");
            report.Phase = "pause-seek-resume";
            Click(transport); await Hold(TimeSpan.FromMilliseconds(150));
            var paused = player.Position.TotalSeconds;
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
            report.Phase = "close-production-player";
            Click(Descendants(currentPage).OfType<Button>().Single(item => Equals(item.Content, "Close player")));
            Check(host.Content is null && player.Source is null && !currentModel.HasSelection, "close-clears-source");
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
    private sealed record FixtureMetadata(ClipRecordingSpec Recording, FinalizedClipMedia Media);
    private sealed record CheckResult(string Name, bool Passed);
    private sealed record PlaybackEvent(string Name, double ElapsedMilliseconds);
    private sealed record PositionSample(double ElapsedMilliseconds, double PositionSeconds, bool Buffering);
    private sealed class CheckFailure(string code) : Exception { public string Code { get; } = code; }
    private sealed class ReviewReport
    {
        public bool Completed { get; set; }
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
        public bool SourceWrites => false;
        public bool InstalledLibraryMetadataAccessed => false;
        public bool MutedThroughoutObservedChecks { get; set; } = true;
        public bool? AudioPlayed => MutedThroughoutObservedChecks ? false : null;
        public bool GameCaptureUsed => false;
        public bool ScreenshotsOrExtractedAudioSaved => false;
        public string Scope => "One explicitly selected local clip copied into an isolated library. Production page preparation, transport and media-clock observations only. No full predecode, displayed-frame smoothness, audible A/V sync or gameplay performance claim. The private fixture copy is retained in this output folder.";
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
