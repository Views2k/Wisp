using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App.Clips;

namespace Wisp.UiReview;

// A presentation test, not a decoded-frame oracle. All native readbacks are
// bounded interiors of this diagnostic's own video and adjacent SDR patches.
internal static class PlaybackColorReview
{
    private const string FixtureSha = "639D45DA77A90BEB698BEAE0DE33966C236A2AFDA40F683AAE2FE83E00CFD61D";
    private static readonly byte[][] Palette =
    [
        [0, 0, 0], [255, 255, 255], [255, 0, 0], [0, 255, 0],
        [0, 0, 255], [0, 255, 255], [255, 0, 255], [255, 255, 0],
        [10, 10, 10], [11, 11, 11], [32, 32, 32], [64, 64, 64],
        [128, 128, 128], [192, 192, 192], [217, 109, 23], [13, 71, 201]
    ];
    private static readonly float[][] HdrPalette =
    [
        [0, 0, 0], [1, 1, 1], [2.5375f, 2.5375f, 2.5375f], [3.5f, 3.5f, 3.5f],
        [7.5f, 7.5f, 7.5f], [12.5f, 12.5f, 12.5f], [.18f, .18f, .18f], [.25f, .25f, .25f],
        [1, 0, 0], [0, 1, 0], [0, 0, 1], [0, 1, 1],
        [1, 0, 1], [1, 1, 0], [4, .25f, .125f], [.125f, .5f, 2]
    ];

    internal static int CheckVersion(string helperPath, string output)
    {
        var full = Path.GetFullPath(helperPath);
        Need(full.StartsWith(Checkout(output) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(full) == "Wisp.PlaybackColorTiles.dll", "owned-helper-path");
        ClipLibrary.CheckPath(full);
        using var held = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        var module = LoadLibraryEx(full, 0, 0x00000100 | 0x00000800);
        Need(module != 0, "tile-helper-load");
        try
        {
            var query = Marshal.GetDelegateForFunctionPointer<Windows10Gate>(NativeLibrary.GetExport(module, "WispPlaybackWindows10Gate"));
            var result = query();
            Need(result <= 1, "version-gate-result");
            using var file = new FileStream(Path.Combine(output, "playback-version-gate.json"), FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(file, new
            {
                IsWindows10OrGreater = result == 1,
                WindowsOpened = false,
                CaptureStarted = false,
                HelperSha256 = Convert.ToHexString(SHA256.HashData(held))
            }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine($"Same-process Windows 10 compatibility gate: {result}.");
            return 0;
        }
        finally { NativeLibrary.Free(module); }
    }

    internal static int Run(string source, string helperPath, string output, string? hdrFixtureSha = null)
    {
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(40), Timeout.InfiniteTimeSpan);
        var hdr = hdrFixtureSha is not null;
        var report = new Report { HdrFixture = hdr };
        var foreground = GetForegroundWindow();
        var started = Stopwatch.GetTimestamp();
        var checkedApps = 0L;
        var priorContext = SynchronizationContext.Current;
        Application? application = null;
        Window? window = null;
        LosslessVideoHost? host = null;
        ProbePlayer? player = null;
        DispatcherTimer? pulse = null;
        Task<int>? readback = null;
        nint module = 0;
        FileStream? heldSource = null, heldHelper = null;
        try
        {
            Guard();
            var sourceFull = Path.GetFullPath(source);
            var helperFull = Path.GetFullPath(helperPath);
            var checkout = Checkout(output);
            Need(sourceFull.StartsWith(Path.Combine(checkout, "work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                helperFull.StartsWith(checkout + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(sourceFull) == "fixture.mp4" && Path.GetFileName(helperFull) == "Wisp.PlaybackColorTiles.dll", "owned-fixture-paths");
            ClipLibrary.CheckPath(sourceFull); ClipLibrary.CheckPath(helperFull);
            heldSource = new(sourceFull, FileMode.Open, FileAccess.Read, FileShare.Read);
            heldHelper = new(helperFull, FileMode.Open, FileAccess.Read, FileShare.Read);
            Need(heldSource.Length > 0 && heldSource.Length < (hdr ? 256L : 32L) * 1024 * 1024 &&
                heldHelper.Length is > 0 and < 16 * 1024 * 1024, "fixture-size");
            report.FixtureSha256 = Convert.ToHexString(SHA256.HashData(heldSource));
            report.HelperSha256 = Convert.ToHexString(SHA256.HashData(heldHelper));
            Need(!hdr || Regex.IsMatch(hdrFixtureSha!, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant), "fixture-sha-required");
            Need(string.Equals(report.FixtureSha256, hdrFixtureSha ?? FixtureSha, StringComparison.OrdinalIgnoreCase), "generated-fixture-hash");
            module = LoadLibraryEx(helperFull, 0, 0x00000100 | 0x00000800);
            Need(module != 0, "tile-helper-load");
            var versionGate = Marshal.GetDelegateForFunctionPointer<Windows10Gate>(NativeLibrary.GetExport(module, "WispPlaybackWindows10Gate"));
            report.Windows10CompatibilityGate = versionGate() == 1;
            var find = Marshal.GetDelegateForFunctionPointer<FindMonitor>(NativeLibrary.GetExport(module, "WispFindPlaybackHdrMonitor"));
            var sample = Marshal.GetDelegateForFunctionPointer<ReadTiles>(NativeLibrary.GetExport(module, "WispReadPlaybackTiles"));
            Need(find(foreground, out var monitor) == 0, "hdr-monitor-required");
            Need(monitor.Right - monitor.Left >= 900 && monitor.Bottom - monitor.Top >= 500, "monitor-too-small");
            application = new ReviewApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var video = new Grid { Background = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            var reference = new UniformGrid { Columns = 4, Rows = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            foreach (var color in Palette) reference.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(color[0], color[1], color[2])) });
            var sides = new Grid { Margin = new Thickness(12) };
            sides.ColumnDefinitions.Add(new()); sides.ColumnDefinitions.Add(new());
            Grid.SetColumn(reference, 1); sides.Children.Add(video); sides.Children.Add(reference);
            sides.SizeChanged += (_, _) =>
            {
                var width = Math.Floor((sides.ActualWidth - 24) / 2);
                if (width <= 0) return;
                video.Width = reference.Width = width;
                video.Height = reference.Height = width * 9 / 16;
            };
            var beacon = new Border { Width = 8, Height = 8, Background = Brushes.Black, Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Left };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Wisp playback color check · generated video / SDR reference", Foreground = Brushes.White, Margin = new Thickness(12) });
            panel.Children.Add(sides); panel.Children.Add(beacon);
            window = new Window
            {
                Title = "Wisp generated playback color check",
                Width = 1000,
                Height = 560,
                ShowActivated = false,
                ShowInTaskbar = false,
                Focusable = false,
                IsHitTestVisible = false,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Black,
                Content = panel,
                Topmost = true
            };
            window.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                const int ex = -20, styles = 0x08000080;
                _ = SetWindowLong(hwnd, ex, GetWindowLong(hwnd, ex) | styles);
                Need((GetWindowLong(hwnd, ex) & styles) == styles, "passive-window-style");
                HwndSource.FromHwnd(hwnd)?.AddHook(BlockActivation);
            };
            window.Show();
            var owner = new WindowInteropHelper(window).Handle;
            Need(SetWindowPos(owner, new nint(-1), monitor.Left + 20, monitor.Top + 20,
                Math.Min(1100, monitor.Right - monitor.Left - 40), Math.Min(650, monitor.Bottom - monitor.Top - 40), 0x0010), "passive-placement");
            var lit = false;
            pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            pulse.Tick += (_, _) => { lit = !lit; beacon.Background = lit ? Brushes.White : Brushes.Black; };
            pulse.Start();
            Await(Scenario());
            report.Completed = report.Arms.Count == (hdr ? 1 : 2);

            async Task Scenario()
            {
                foreach (var outputSpace in hdr ? new[] { "auto" } : new[] { "auto", "srgb" })
                {
                    Guard();
                    var arm = new Arm { RequestedOutput = outputSpace };
                    report.Arms.Add(arm);
                    host = new LosslessVideoHost(video);
                    video.Children.Add(host);
                    while (!host.Ready.IsCompleted) await Tick();
                    var hwnd = await host.Ready;
                    player = new ProbePlayer(hwnd, outputSpace);
                    player.Native.Run("loadfile", sourceFull, "replace");
                    while (!player.Restarted || player.Native.Integer("video-params/w") != 1920) { player.Drain(); await Tick(); }
                    var pixelFormat = player.Native.Text("video-dec-params/pixelformat");
                    arm.DecodedPixelFormat = pixelFormat;
                    arm.Transfer = player.Native.Text("video-params/gamma");
                    arm.Primaries = player.Native.Text("video-params/primaries");
                    arm.VideoHeight = player.Native.Integer("video-params/h");
                    arm.Paused = player.Native.Flag("pause");
                    arm.PositionSeconds = player.Native.Number("time-pos");
                    Need(player.Native.Integer("video-params/h") == 1080 &&
                        (hdr ? pixelFormat is "yuv420p10" or "yuv420p10le" or "gbrp10" or "gbrp10le" : pixelFormat == "gbrp") &&
                        arm.Transfer == (hdr ? "pq" : "srgb") && arm.Paused == true &&
                        arm.PositionSeconds is >= 0 and < .01, "paused-first-frame");
                    Need(!hdr || arm.Primaries == "bt.2020", "hdr-primaries-required");
                    for (var i = 0; i < 15; i++) { player.Drain(); await Tick(); }
                    arm.ConfiguredColorSpace = player.ConfiguredColorSpace;
                    if (report.Windows10CompatibilityGate == true)
                    {
                        Need(arm.ConfiguredColorSpace is 0 or 12, "swapchain-confirmation-missing");
                        Need(outputSpace != "srgb" || arm.ConfiguredColorSpace == 0, "srgb-not-selected");
                        Need(!hdr || arm.ConfiguredColorSpace == 12, "hdr-output-not-selected");
                    }
                    else
                    {
                        // Measure the unchanged, unmanifested player too. The
                        // bundled helper skips SetColorSpace1 in this case;
                        // null records that fact instead of inventing a CSP.
                        Need(arm.ConfiguredColorSpace is null, "unexpected-color-space-configuration");
                    }
                    var points = new NativePoint[32];
                    for (var i = 0; i < 16; i++)
                    {
                        points[i] = Center(video, i);
                        points[16 + i] = Center(reference, i);
                    }
                    var means = new float[96]; var metadata = new uint[3];
                    readback = Task.Run(() => sample(owner, foreground, points, 32, means, metadata));
                    while (!readback.IsCompleted) { player.Drain(); await Tick(); }
                    arm.ReadbackCode = await readback; readback = null;
                    Need(arm.ReadbackCode == 0 && metadata[0] == 10 && metadata[1] == 12 && metadata[2] == 3, "owned-fp16-readback");
                    arm.Samples = metadata[2];
                    var white = means[17 * 3];
                    Need(white > .2f && white < 20 && means.All(float.IsFinite), "reference-white-invalid");
                    arm.ReferenceWhiteNits = white * 80;
                    arm.VideoWhiteNits = means[3] * 80;
                    arm.ExpectedVideoWhiteNits = hdr ? 80 : arm.ReferenceWhiteNits;
                    for (var i = 0; i < 16; i++)
                    {
                        var actual = means.Skip(i * 3).Take(3).ToArray();
                        var expected = hdr ? HdrPalette[i] : means.Skip((16 + i) * 3).Take(3).ToArray();
                        var error = actual.Zip(expected, (a, b) => Math.Abs(a - b) / white).Max();
                        arm.Patches.Add(new(i, actual, expected, error));
                    }
                    arm.MaximumRelativeToReferenceWhite = arm.Patches.Max(p => p.MaximumDifference);
                    // A 1.5% displayed-light bound exceeds FP16/10-bit quantization;
                    // it is a diagnostic verdict, never a claim about game capture.
                    arm.MatchesReference = arm.MaximumRelativeToReferenceWhite <= (hdr ? .03 : .015);
                    var closing = player;
                    player = null;
                    await Task.Run(closing.Native.Close);
                    video.Children.Remove(host); host.Dispose(); host = null;
                    await Tick();
                }
            }
        }
        catch (Exception error)
        {
            report.Failure = error is ProbeFailure f ? f.Code : "diagnostic-exception";
            report.ExceptionType = error.GetType().Name;
            report.ExceptionHResult = error.HResult;
        }
        finally
        {
            // Never unload the helper or destroy its owner while native readback
            // still runs. The helper itself has a five-second bounded lifetime.
            if (readback is not null) { try { Await(readback); } catch { report.Failure ??= "readback-cleanup"; } }
            if (player is not null) { try { Await(Task.Run(player.Native.Close)); } catch { report.Failure ??= "player-cleanup"; } }
            pulse?.Stop();
            try { host?.Dispose(); window?.Close(); application?.Shutdown(); }
            catch { report.Failure ??= "window-cleanup"; }
            if (module != 0) NativeLibrary.Free(module);
            heldSource?.Dispose(); heldHelper?.Dispose();
            SynchronizationContext.SetSynchronizationContext(priorContext);
            report.ForegroundPreserved = foreground != 0 && GetForegroundWindow() == foreground;
            report.OwnedWindowClosed = window?.IsVisible != true;
            report.Completed &= report.Failure is null && report.ForegroundPreserved && report.OwnedWindowClosed;
            using var file = new FileStream(Path.Combine(output, "playback-color-review.json"), FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(file, report, new JsonSerializerOptions { WriteIndented = true });
        }
        Console.WriteLine(report.Completed ? "Owned-window playback color A/B completed; see scalar report." : "Owned-window playback color A/B incomplete; see scalar report.");
        return report.Completed ? 0 : 2;

        void Guard()
        {
            Need(foreground != 0 && GetForegroundWindow() == foreground, "foreground-changed");
            Need(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(32), "diagnostic-deadline");
            if (checkedApps != 0 && Stopwatch.GetElapsedTime(checkedApps) < TimeSpan.FromMilliseconds(250)) return;
            checkedApps = Stopwatch.GetTimestamp();
            string[] excluded = ["ForzaHorizon6", "ForzaHorizon5", "ForzaHorizon4", "ForzaMotorsport", "Wisp", "Wisp.Recorder"];
            foreach (var process in Process.GetProcesses())
                using (process)
                    Need(!excluded.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase), "apps-must-be-closed");
        }
        async Task Tick() { Guard(); await Task.Delay(20); }
    }

    private static NativePoint Center(FrameworkElement element, int index)
    {
        var point = element.PointToScreen(new Point(element.ActualWidth * (2 * (index % 4) + 1) / 8,
            element.ActualHeight * (2 * (index / 4) + 1) / 8));
        return new((int)Math.Round(point.X), (int)Math.Round(point.Y));
    }
    private static string Checkout(string output)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(output));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new ProbeFailure("workspace-output-required");
    }
    private static void Await(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static void Need(bool value, string code) { if (!value) throw new ProbeFailure(code); }
    private sealed class ProbeFailure(string code) : Exception { internal string Code { get; } = code; }
    private sealed class ReviewApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
    private sealed class ProbePlayer
    {
        internal LosslessMpvNative Native { get; } = new();
        internal bool Restarted { get; private set; }
        internal int? ConfiguredColorSpace { get; private set; }
        private readonly nint _handle;
        private readonly Delegate _wait;
        internal ProbePlayer(nint window, string colorSpace)
        {
            try
            {
                // Diagnostic-only access avoids adding a product option/test hook.
                // Apply the actual product option table, then change just output CSP.
                var type = typeof(LosslessMpvNative);
                object Field(string name) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(Native)
                    ?? throw new ProbeFailure("native-probe-contract-changed");
                _handle = (nint)Field("_handle"); _wait = (Delegate)Field("_wait");
                var option = (Delegate)Field("_option");
                void Option(string name, string value) => Need((int)option.DynamicInvoke(_handle, name, value)! >= 0, "probe-option-rejected");
                foreach (var (name, value) in LosslessMpvNative.Options) Option(name, value);
                Option("d3d11-output-csp", colorSpace);
                Option("wid", window.ToInt64().ToString(CultureInfo.InvariantCulture));
                Need((int)((Delegate)Field("_logs")).DynamicInvoke(_handle, "v")! >= 0, "probe-logs-rejected");
                Need((int)((Delegate)Field("_initialize")).DynamicInvoke(_handle)! >= 0, "probe-initialize-rejected");
            }
            catch { Native.Close(); throw; }
        }
        internal void Drain()
        {
            for (var i = 0; i < 1024; i++)
            {
                var pointer = (nint)_wait.DynamicInvoke(_handle, 0d)!;
                var item = Marshal.PtrToStructure<MpvEvent>(pointer);
                if (item.Id == 0) return;
                Need(item.Id != 24 && item.Error >= 0, "mpv-event-error");
                if (item.Id == 21) Restarted = true;
                if (item.Id != 2 || item.Data == 0) continue;
                var log = Marshal.PtrToStructure<MpvLog>(item.Data);
                Need(log.NumericLevel > 20, "mpv-render-error");
                var length = 0;
                while (length < 4096 && Marshal.ReadByte(log.Text, length) != 0) length++;
                if (length == 4096) continue;
                var text = Marshal.PtrToStringUTF8(log.Text, length) ?? "";
                var match = Regex.Match(text, @"^Swapchain successfully configured to color space [A-Z0-9_]+ \(([0-9]{1,2})\)!\s*$", RegexOptions.CultureInvariant);
                if (match.Success) ConfiguredColorSpace = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                // All other native log text is discarded, never persisted.
            }
            throw new ProbeFailure("mpv-event-flood");
        }
    }
    private sealed class Report
    {
        public bool HdrFixture { get; init; }
        public string Method => HdrFixture
            ? "Pinned generated HDR/PQ clip with known first-frame scRGB patch inputs, played through production D3D11 options and automatic HDR output. Three fresh HDR-desktop frames, 32 owned 4x4 FP16 tiles; only scalar means persist. Expected patches are independent source values; adjacent SDR references measure Windows white. No game capture, activation or display changes."
            : "Same pinned generated SDR/sRGB clip and production D3D11 mpv options; only output CSP changes. Adjacent WPF SDR references. Three fresh HDR-desktop frames per arm, 32 owned 4x4 FP16 tiles per frame; only scalar RGB means persist. No game capture, activation, display changes or software screenshot oracle.";
        public string Limit => "This measures compositor output on the current HDR display, not physical panel light or game capture conversion. Completing the diagnostic does not mean both arms match.";
        public string? FixtureSha256 { get; set; }
        public string? HelperSha256 { get; set; }
        public bool Completed { get; set; }
        public bool ForegroundPreserved { get; set; }
        public bool OwnedWindowClosed { get; set; }
        public bool? Windows10CompatibilityGate { get; set; }
        public string? Failure { get; set; }
        public string? ExceptionType { get; set; }
        public int? ExceptionHResult { get; set; }
        public List<Arm> Arms { get; } = [];
    }
    private sealed class Arm
    {
        public required string RequestedOutput { get; init; }
        public string? DecodedPixelFormat { get; set; }
        public string? Transfer { get; set; }
        public string? Primaries { get; set; }
        public long? VideoHeight { get; set; }
        public bool? Paused { get; set; }
        public double? PositionSeconds { get; set; }
        public int? ConfiguredColorSpace { get; set; }
        public int ReadbackCode { get; set; } = -1;
        public uint Samples { get; set; }
        public double ReferenceWhiteNits { get; set; }
        public double VideoWhiteNits { get; set; }
        public double ExpectedVideoWhiteNits { get; set; }
        public double MaximumRelativeToReferenceWhite { get; set; }
        public bool MatchesReference { get; set; }
        public List<Patch> Patches { get; } = [];
    }
    private sealed record Patch(int Index, float[] VideoScRgb, float[] ReferenceScRgb, float MaximumDifference);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MpvEvent { public int Id, Error; public ulong Reply; public nint Data; }
    [StructLayout(LayoutKind.Sequential)] private struct MpvLog { public nint Prefix, Level, Text; public int NumericLevel; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindMonitor(nint foreground, out NativeRect bounds);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Windows10Gate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadTiles(nint owner, nint foreground, [In] NativePoint[] centers, uint count, [Out] float[] means, [Out] uint[] metadata);
    private static nint BlockActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    { if (message != 0x0021) return 0; handled = true; return 4; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW", SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
}
