using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using static Wisp.LosslessPlaybackProbe.ProbeFiles;

namespace Wisp.LosslessPlaybackProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--source" || args[2] != "--output")
        {
            Console.WriteLine("Synthetic-only probe: --source <absolute fixture.mp4> --output <new absolute checkout work directory>");
            return 1;
        }
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
        using var files = new ProbeFiles();
        var report = new Report();
        var started = Stopwatch.GetTimestamp();
        var foreground = GetForegroundWindow();
        var checkedApps = started;
        var previousContext = SynchronizationContext.Current;
        Application? application = null;
        Window? window = null;
        PassiveVideoHost? host = null;
        LibVLC? engine = null;
        Media? media = null;
        MediaPlayer? player = null;
        Task<bool>? pendingSnapshot = null;
        ProbeCheckpoints? checkpoints = null;
        var events = new Events();
        var outputCreated = false;
        try
        {
            Need(foreground != IntPtr.Zero && AppsClosed(), "apps-and-foreground-guard");
            files.Prepare(args[1], args[3]);
            outputCreated = true;
            checkpoints = new ProbeCheckpoints(files.Output, started, events.Snapshot);
            Mark("load-local-libvlc");
            var native = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
            Need(File.Exists(Path.Combine(native, "libvlc.dll")) && File.Exists(Path.Combine(native, "libvlccore.dll")) &&
                Directory.Exists(Path.Combine(native, "plugins")), "app-local-native-package-required");
            report.NativeDllSha256 = Hash(Path.Combine(native, "libvlc.dll"));
            report.NativeCoreSha256 = Hash(Path.Combine(native, "libvlccore.dll"));
            report.ManagedDllSha256 = Hash(typeof(LibVLC).Assembly.Location);
            Mark("before-core-initialize");
            Core.Initialize(native);
            Mark("after-core-initialize");
            Mark("before-engine-create");
            engine = new LibVLC("--ignore-config", "--quiet", "--no-audio", "--no-osd",
                "--no-video-title-show", "--no-snapshot-preview");
            Mark("after-engine-create");
            Mark("before-player-create");
            player = new MediaPlayer(engine);
            Mark("after-player-create");
            Mark("before-player-input-and-audio-options");
            player.Mute = true; player.Volume = 0; player.EnableKeyInput = false; player.EnableMouseInput = false;
            Mark("after-player-input-and-audio-options");
            // Callback threads only store bounded scalars. No LibVLC call, UI access, logging, or blocking here.
            Mark("before-event-subscriptions");
            player.Opening += (_, _) => Interlocked.Increment(ref events.Opening);
            player.Paused += (_, _) => Interlocked.Increment(ref events.Paused);
            player.Playing += (_, _) => Interlocked.Increment(ref events.Playing);
            player.EndReached += (_, _) => Interlocked.Exchange(ref events.Ended, 1);
            player.EncounteredError += (_, _) => Interlocked.Exchange(ref events.Failed, 1);
            player.Buffering += (_, value) => Volatile.Write(ref events.Buffering, value.Cache);
            player.SnapshotTaken += (_, _) => Interlocked.Increment(ref events.Snapshots);
            Mark("after-event-subscriptions");
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var viewport = new Border { Width = 640, Height = 320, Background = Brushes.Black, ClipToBounds = true };
            host = new PassiveVideoHost(viewport)
            {
                Width = 640, Height = 360, VerticalAlignment = VerticalAlignment.Top,
                Focusable = false, IsHitTestVisible = false
            };
            viewport.Child = host;
            window = new Window
            {
                Title = "Wisp synthetic lossless decoder check", Width = 680, Height = 380,
                ShowActivated = false, ShowInTaskbar = false, Focusable = false, IsHitTestVisible = false,
                WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Black, Content = viewport
            };
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                const int index = -20, styles = 0x08000080;
                _ = SetWindowLong(handle, index, GetWindowLong(handle, index) | styles);
                Need((GetWindowLong(handle, index) & styles) == styles, "passive-window-style");
                HwndSource.FromHwnd(handle)?.AddHook(PassiveHook);
            };
            Guard();
            Mark("before-passive-window-show");
            window.Show();
            Mark("after-passive-window-show");
            window.UpdateLayout();
            Mark("after-passive-window-layout");
            Await(Observe());
            Mark("observe-completed");
        }
        catch (CheckFailure failure) { report.Failure = failure.Code; RecordFailure(); }
        catch (Exception error)
        {
            report.Failure = "probe-exception";
            report.ExceptionType = error.GetType().Name;
            report.ExceptionHResult = error.HResult;
            RecordFailure();
        }
        finally
        {
            try
            {
                report.HostBeforeCleanup = host?.GetStatus();
                Volatile.Write(ref events.Host, report.HostBeforeCleanup);
                Mark("cleanup-start");
                // Stop can synchronously join VLC workers; never execute it in a VLC callback.
                if (player is not null)
                {
                    if (pendingSnapshot is not null)
                    {
                        Mark("before-pending-snapshot-join");
                        Await(pendingSnapshot);
                        Mark("after-pending-snapshot-join");
                    }
                    Mark("before-player-stop");
                    Await(Task.Run(player.Stop));
                    Mark("after-player-stop");
                    Mark("before-player-hwnd-clear");
                    player.Hwnd = IntPtr.Zero;
                    Mark("after-player-hwnd-clear");
                    Mark("before-player-dispose");
                    player.Dispose();
                    Mark("after-player-dispose");
                }
                Mark("before-media-dispose");
                media?.Dispose();
                Mark("after-media-dispose");
                Mark("before-engine-dispose");
                engine?.Dispose();
                Mark("after-engine-dispose");
                Mark("before-host-dispose");
                host?.Dispose();
                Mark("after-host-dispose");
                Mark("before-window-close");
                if (window is not null) { window.Content = null; window.Close(); }
                Mark("after-window-close");
                Mark("before-application-shutdown");
                application?.Shutdown();
                Mark("after-application-shutdown");
                report.Disposed = true;
            }
            catch (Exception error)
            {
                report.Failure ??= "cleanup-failed";
                report.ExceptionType ??= error.GetType().Name;
                report.ExceptionHResult ??= error.HResult;
                RecordFailure();
            }
            SynchronizationContext.SetSynchronizationContext(previousContext);
            report.ForegroundUnchanged &= foreground != IntPtr.Zero && foreground == GetForegroundWindow();
            try { report.AppsClosedAtEnd = AppsClosed(); }
            catch { report.AppsClosedAtEnd = false; }
        }
        report.OpeningEvents = Volatile.Read(ref events.Opening);
        report.PausedEvents = Volatile.Read(ref events.Paused);
        report.PlayingEvents = Volatile.Read(ref events.Playing);
        report.SnapshotEvents = Volatile.Read(ref events.Snapshots);
        report.ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        report.CheckpointSamplerHealthy = checkpoints?.SamplerHealthy == true;
        report.Completed = report.Failure is null && report.PreparedPaused && report.HeldPaused && report.Snapshot?.DifferingSamples == 0 &&
            report.ExplicitPlayCompleted && report.ViewportClipVerified && report.ForegroundUnchanged && report.AppsClosedAtEnd && report.Disposed &&
            report.CheckpointSamplerHealthy && !report.CheckpointWriteFailed;
        if (outputCreated)
        {
            using var output = new FileStream(Path.Combine(files.Output, "libvlc-probe.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
        }
        checkpoints?.Dispose();
        Console.WriteLine(report.Completed ? "Synthetic LibVLC preparation and exact snapshot check passed."
            : "Synthetic LibVLC check did not pass; scalar report identifies the boundary.");
        return report.Completed ? 0 : 2;

        void Mark(string stage)
        {
            report.Stage = stage;
            try { checkpoints?.Mark(stage); }
            catch { report.CheckpointWriteFailed = true; }
        }

        void RecordFailure()
        {
            Volatile.Write(ref events.FailureRecorded, 1);
            Volatile.Write(ref events.FailureCode, report.Failure ?? "unexpected-failure");
            try { checkpoints?.Mark("caught-failure"); }
            catch { /* A checkpoint I/O failure must not suppress native cleanup. */ }
        }

        T Native<T>(string call, Func<T> action)
        {
            checkpoints?.NativeCall(call);
            try { return action(); }
            finally { checkpoints?.NativeCall("none"); }
        }

        void Guard()
        {
            Need(!report.CheckpointWriteFailed, "checkpoint-write-failed");
            Need(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(16), "probe-soft-deadline");
            if (Stopwatch.GetElapsedTime(checkedApps) >= TimeSpan.FromMilliseconds(250))
            { checkedApps = Stopwatch.GetTimestamp(); Need(AppsClosed(), "apps-started-during-check"); }
            if (GetForegroundWindow() != foreground)
            { report.ForegroundUnchanged = false; throw new CheckFailure("foreground-changed"); }
            Need(Volatile.Read(ref events.Failed) == 0, "libvlc-media-error");
            if (player is not null)
            {
                var volume = Native("get-volume", () => player.Volume);
                report.MaximumReportedVolume = Math.Max(report.MaximumReportedVolume, volume);
                Need(volume <= 0, "player-unmuted");
            }
        }

        async Task Observe()
        {
            var current = player!;
            var videoHost = host!;
            Mark("wait-for-loaded-viewport");
            while (videoHost.VideoHandle == IntPtr.Zero || !videoHost.ClipVerified) await Tick();
            report.ViewportClipVerified = true;
            Mark("loaded-viewport-ready");
            Mark("before-player-hwnd-attach");
            current.Hwnd = videoHost.VideoHandle;
            Mark("after-player-hwnd-attach");
            Mark("before-media-create");
            media = new Media(engine!, files.Source, FromType.FromPath, ":start-paused");
            Mark("after-media-create");
            Mark("before-player-media-attach");
            current.Media = media;
            Mark("after-player-media-attach");
            Mark("before-start-paused-play");
            var opened = Stopwatch.GetTimestamp();
            Need(current.Play(), "libvlc-start-paused-rejected");
            Mark("after-start-paused-play");
            while (State() != VLCState.Paused) await Tick();
            Mark("paused-input-ready");
            report.TimeBeforePreparationSeek = Time();
            Need(Native("get-seekable", () => current.IsSeekable), "generated-file-must-be-seekable");
            // VLC3 ignores NextFrame while buffering. A paused seek flushes the decoder
            // and grants one preview frame without resuming its playback clock.
            Mark("before-paused-seek-zero");
            current.Time = 0;
            report.PausedSeekToStartRequested = true;
            Mark("after-paused-seek-zero");
            while (State() != VLCState.Paused || Vout() == 0) await Tick();
            Mark("paused-video-output-ready");
            report.OpenedMilliseconds = Stopwatch.GetElapsedTime(opened).TotalMilliseconds;
            report.InitialTimeMilliseconds = Time();
            uint width = 0, height = 0;
            Mark("before-prepared-metadata");
            Need(current.Size(0, ref width, ref height) && width == SyntheticRgb.Width && height == SyntheticRgb.Height &&
                current.CanPause && Time() is >= 0 and <= 17, "prepared-metadata-or-position-mismatch");
            Mark("after-prepared-metadata");
            report.Width = width; report.Height = height;
            report.PreparedPaused = true;
            Mark("hold-first-frame-paused");
            var hold = Stopwatch.GetTimestamp();
            var initialTime = Time();
            while (Stopwatch.GetElapsedTime(hold) < TimeSpan.FromSeconds(2))
            {
                await Tick();
                Need(State() == VLCState.Paused && Time() == initialTime && Volatile.Read(ref events.Ended) == 0,
                    "unrequested-playback-during-preparation");
            }
            report.HeldPaused = true;
            report.PlayingEventsBeforeExplicitPlay = Volatile.Read(ref events.Playing);
            report.BufferPercentAtPrepared = Volatile.Read(ref events.Buffering);
            Mark("hold-first-frame-paused-complete");
            Mark("exact-synthetic-snapshot");
            var snapshot = Path.Combine(files.Output, "first-frame.png");
            Need(!File.Exists(snapshot), "snapshot-already-exists");
            Mark("before-take-snapshot");
            pendingSnapshot = Task.Run(() => current.TakeSnapshot(0, snapshot, 0, 0));
            while (!pendingSnapshot.IsCompleted) await Tick();
            Need(await pendingSnapshot, "snapshot-request-failed");
            Mark("after-take-snapshot");
            while (Volatile.Read(ref events.Snapshots) == 0) await Tick();
            Mark("snapshot-event-received");
            report.Snapshot = SyntheticRgb.CompareFirstFrame(snapshot);
            report.SnapshotExact = report.Snapshot.DifferingSamples == 0;
            Need(report.SnapshotExact, "synthetic-first-frame-rgb-mismatch");
            Need(State() == VLCState.Paused && Time() == initialTime, "snapshot-changed-playback-state");
            Mark("before-explicit-resume");
            current.SetPause(false);
            Mark("after-explicit-resume");
            while (Volatile.Read(ref events.Ended) == 0)
            {
                await Tick();
                report.MaximumPlaybackTimeMilliseconds = Math.Max(report.MaximumPlaybackTimeMilliseconds, Time());
            }
            report.ExplicitPlayCompleted = true;
            Mark("explicit-play-ended");
            Guard();
            async Task Tick()
            {
                Volatile.Write(ref events.Host, videoHost.GetStatus());
                Guard();
                await Task.Delay(15);
            }
            VLCState State()
            {
                var value = Native("get-player-state", () => current.State);
                Volatile.Write(ref events.LastState, (int)value);
                return value;
            }
            long Time()
            {
                var value = Native("get-player-time", () => current.Time);
                Interlocked.Exchange(ref events.LastTime, value);
                return value;
            }
            uint Vout()
            {
                var value = Native("get-vout-count", () => current.VoutCount);
                Volatile.Write(ref events.LastVout, checked((int)value));
                return value;
            }
        }
    }

    private static string Hash(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }

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

    private static IntPtr PassiveHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    { if (message == 0x0021) { handled = true; return new IntPtr(3); } return IntPtr.Zero; }

    private sealed class Events
    {
        internal int Opening, Paused, Playing, Ended, Failed, Snapshots, FailureRecorded;
        internal int LastState = -1;
        internal int LastVout = -1;
        internal long LastTime = -1;
        internal string FailureCode = "none";
        internal PassiveVideoHost.Status? Host;
        internal float Buffering;
        internal object Snapshot() => new
        {
            Opening = Volatile.Read(ref Opening), Paused = Volatile.Read(ref Paused), Playing = Volatile.Read(ref Playing),
            Ended = Volatile.Read(ref Ended), Failed = Volatile.Read(ref Failed), Snapshots = Volatile.Read(ref Snapshots),
            Buffering = Volatile.Read(ref Buffering), LastState = Volatile.Read(ref LastState), LastTime = Interlocked.Read(ref LastTime),
            LastVout = Volatile.Read(ref LastVout),
            FailureRecorded = Volatile.Read(ref FailureRecorded), FailureCode = Volatile.Read(ref FailureCode),
            Host = Volatile.Read(ref Host)
        };
    }

    private sealed class Report
    {
        public string Stage { get; set; } = "guards";
        public bool Completed { get; set; }
        public string? Failure { get; set; }
        public string? ExceptionType { get; set; }
        public int? ExceptionHResult { get; set; }
        public string ManagedPackage => "LibVLCSharp 3.10.1";
        public string NativePackage => "VideoLAN.LibVLC.Windows 3.0.24";
        public string SourceSha256 => ExpectedSha256;
        public string? NativeDllSha256 { get; set; }
        public string? NativeCoreSha256 { get; set; }
        public string? ManagedDllSha256 { get; set; }
        public bool PreparedPaused { get; set; }
        public bool PausedSeekToStartRequested { get; set; }
        public long TimeBeforePreparationSeek { get; set; } = -1;
        public bool HeldPaused { get; set; }
        public bool SnapshotExact { get; set; }
        public SyntheticRgb.Comparison? Snapshot { get; set; }
        public bool ExplicitPlayCompleted { get; set; }
        public uint Width { get; set; }
        public uint Height { get; set; }
        public long InitialTimeMilliseconds { get; set; }
        public long MaximumPlaybackTimeMilliseconds { get; set; }
        public float BufferPercentAtPrepared { get; set; }
        public int OpeningEvents { get; set; }
        public int PausedEvents { get; set; }
        public int PlayingEvents { get; set; }
        public int PlayingEventsBeforeExplicitPlay { get; set; }
        public int SnapshotEvents { get; set; }
        public double OpenedMilliseconds { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public bool ViewportClipVerified { get; set; }
        public PassiveVideoHost.Status? HostBeforeCleanup { get; set; }
        public bool ForegroundUnchanged { get; set; } = true;
        public bool AppsClosedAtEnd { get; set; }
        public bool Disposed { get; set; }
        public bool CheckpointSamplerHealthy { get; set; }
        public bool CheckpointWriteFailed { get; set; }
        public int MaximumReportedVolume { get; set; } = -1;
        public bool EngineAudioDisabled => true;
        public bool CapturePerformed => false;
        public string Scope => "Only the pinned synthetic 16-frame fixture. Exact first decoded snapshot samples and paused preparation, plus short explicit transport. The PNG is generated media, not screen capture. No whole-file buffering, display-color fidelity, A/V sync, gameplay-performance, or product integration claim.";
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
}
