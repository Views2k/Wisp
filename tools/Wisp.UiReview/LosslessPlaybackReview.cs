using System.Diagnostics;
using System.IO;
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

internal static class LosslessPlaybackReview
{
    internal static int Run(string source, string expectedSha256, string output)
    {
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
        var report = new Report();
        var foreground = GetForegroundWindow();
        var priorContext = SynchronizationContext.Current;
        FileStream? held = null;
        Application? application = null;
        Window? window = null;
        MediaElement? player = null;
        var checkedApps = Stopwatch.GetTimestamp();
        try
        {
            Need(foreground != IntPtr.Zero && AppsClosed(), "apps-and-foreground-guard");
            report.Stage = "verify-generated-fixture";
            var full = Path.GetFullPath(source);
            var checkout = FindCheckout(output);
            Need(Path.IsPathFullyQualified(source) && !full.StartsWith(@"\\", StringComparison.Ordinal) &&
                full.StartsWith(checkout + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                full.Length < 260 && Path.GetFileName(full) == "fixture.mp4" &&
                expectedSha256.Length == 64 && expectedSha256.All(Uri.IsHexDigit), "explicit-generated-fixture-required");
            ClipLibrary.CheckPath(full);
            held = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            Need(held.Length is > 0 and <= 128L * 1024 * 1024, "fixture-size-bound");
            var finalPath = new StringBuilder(32768);
            var pathLength = GetFinalPathNameByHandle(held.SafeFileHandle, finalPath, (uint)finalPath.Capacity, 0);
            Need(pathLength > 0 && pathLength < finalPath.Capacity &&
                string.Equals(finalPath.ToString(), @"\\?\" + full, StringComparison.OrdinalIgnoreCase), "held-fixture-path");
            report.SourceSha256 = Convert.ToHexString(SHA256.HashData(held));
            report.FileBytes = held.Length;
            Need(report.SourceSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase), "fixture-hash-mismatch");
            application = new ReviewApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            // Match the production ClipsPage decoder settings; no custom codec or capture path.
            player = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Close,
                Stretch = Stretch.Uniform,
                ScrubbingEnabled = true,
                Volume = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            player.MediaOpened += (_, _) => report.Opened = true;
            player.MediaEnded += (_, _) => report.Ended = true;
            player.MediaFailed += (_, args) =>
            {
                report.MediaFailureType = args.ErrorException.GetType().Name;
                report.MediaFailureHResult = args.ErrorException.HResult;
            };
            window = new Window
            {
                Title = "Wisp synthetic decoder compatibility check",
                Width = 660,
                Height = 400,
                ShowActivated = false,
                ShowInTaskbar = false,
                Focusable = false,
                IsHitTestVisible = false,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Black,
                Content = player
            };
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window!).Handle;
                const int index = -20, styles = 0x08000080;
                _ = SetWindowLong(handle, index, GetWindowLong(handle, index) | styles);
                Need((GetWindowLong(handle, index) & styles) == styles, "passive-window-style");
            };
            Guard();
            window.Show();
            Await(Observe());
            report.Compatible = true;
        }
        catch (CheckFailure error) { report.Failure = error.Code; }
        catch (Exception error)
        {
            report.Failure = "diagnostic-exception";
            report.ExceptionType = error.GetType().Name;
            report.ExceptionHResult = error.HResult;
        }
        finally
        {
            try
            {
                player?.Close();
                if (window is not null) { window.Content = null; window.Close(); }
                report.OwnedWindowClosed = window?.IsVisible != true;
                application?.Shutdown();
            }
            catch (Exception error)
            {
                report.Failure ??= "cleanup-failed";
                report.ExceptionType ??= error.GetType().Name;
                report.ExceptionHResult ??= error.HResult;
            }
            held?.Dispose();
            SynchronizationContext.SetSynchronizationContext(priorContext);
            report.ForegroundUnchanged &= foreground != IntPtr.Zero && GetForegroundWindow() == foreground;
            try { report.AppsClosedAtEnd = AppsClosed(); }
            catch { report.AppsClosedAtEnd = false; }
        }
        report.Completed = report.Compatible && report.Failure is null && report.OwnedWindowClosed &&
            report.ForegroundUnchanged && report.AppsClosedAtEnd && report.MutedThroughoutObservedChecks;
        File.WriteAllText(Path.Combine(output, "lossless-playback-review.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report.Completed ? "Synthetic WPF decoder compatibility passed."
            : "Synthetic WPF decoder compatibility did not pass; see scalar report.");
        return report.Completed ? 0 : 2;

        void Guard()
        {
            if (Stopwatch.GetElapsedTime(checkedApps) >= TimeSpan.FromMilliseconds(250))
            { checkedApps = Stopwatch.GetTimestamp(); Need(AppsClosed(), "apps-started-during-check"); }
            if (GetForegroundWindow() != foreground)
            { report.ForegroundUnchanged = false; throw new CheckFailure("foreground-changed"); }
            if (player is not null && player.Volume != 0)
            { report.MutedThroughoutObservedChecks = false; throw new CheckFailure("player-unmuted"); }
        }
        async Task Observe()
        {
            var current = player!;
            var started = Stopwatch.GetTimestamp();
            report.Stage = "open-current-wpf-decoder";
            current.Source = new Uri(Path.GetFullPath(source));
            current.Pause();
            while (!report.Opened && report.MediaFailureType is null) await Tick();
            Need(report.MediaFailureType is null, "media-open-failed");
            report.OpenedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            report.Width = current.NaturalVideoWidth; report.Height = current.NaturalVideoHeight;
            report.HasAudio = current.HasAudio;
            report.NaturalDurationSeconds = current.NaturalDuration.HasTimeSpan ? current.NaturalDuration.TimeSpan.TotalSeconds : null;
            Need(current.HasVideo && current.CanPause && !current.HasAudio && report.Width == 1280 && report.Height == 720,
                "opened-fixture-metadata-mismatch");
            current.Position = TimeSpan.Zero;
            report.Stage = "short-muted-transport";
            current.Play();
            while (!report.Ended && report.MaximumPositionSeconds < .1 && report.MediaFailureType is null)
            {
                await Tick();
                report.MaximumPositionSeconds = Math.Max(report.MaximumPositionSeconds, current.Position.TotalSeconds);
            }
            Need(report.MediaFailureType is null && (report.Ended || report.MaximumPositionSeconds >= .1), "media-transport-failed");
            current.Pause();
            Guard();
            report.ElapsedDecoderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            async Task Tick()
            {
                Guard();
                Need(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10), "decoder-deadline");
                await Task.Delay(16);
            }
        }
    }

    private static string FindCheckout(string output)
    {
        for (DirectoryInfo? directory = new(output); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln")) &&
                File.Exists(Path.Combine(directory.FullName, "src", "Wisp.App", "Wisp.App.csproj"))) return directory.FullName;
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
    private static void Need(bool condition, string code) { if (!condition) throw new CheckFailure(code); }
    private sealed class CheckFailure(string code) : Exception { internal string Code { get; } = code; }
    private sealed class ReviewApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
    private sealed class Report
    {
        public string Stage { get; set; } = "guards";
        public bool Completed { get; set; }
        public bool Compatible { get; set; }
        public string? Failure { get; set; }
        public string? ExceptionType { get; set; }
        public int? ExceptionHResult { get; set; }
        public string? SourceSha256 { get; set; }
        public long FileBytes { get; set; }
        public bool Opened { get; set; }
        public bool Ended { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool HasAudio { get; set; }
        public double? NaturalDurationSeconds { get; set; }
        public string? MediaFailureType { get; set; }
        public int? MediaFailureHResult { get; set; }
        public double OpenedMilliseconds { get; set; }
        public double MaximumPositionSeconds { get; set; }
        public double ElapsedDecoderMilliseconds { get; set; }
        public bool ForegroundUnchanged { get; set; } = true;
        public bool AppsClosedAtEnd { get; set; }
        public bool OwnedWindowClosed { get; set; }
        public bool MutedThroughoutObservedChecks { get; set; } = true;
        public bool SourceWrites => false;
        public bool CapturedPixels => false;
        public bool ScreenshotsSaved => false;
        public string Scope => "Explicit generated 16-frame 1280x720 at 60 fps fixture, independently byte-verified before invocation. Current WPF MediaElement opening and short muted transport only; no decoded-pixel, displayed-frame, A/V sync or performance claim.";
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
