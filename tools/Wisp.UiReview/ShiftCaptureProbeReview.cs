using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App;
using Wisp.App.ShiftCapture;

namespace Wisp.UiReview;

internal static class ShiftCaptureProbeReview
{
    // This method shows and samples only its own opaque test window. No controller,
    // game lookup, game activation, telemetry receiver or screenshot file is used.
    internal static int Run(string output)
    {
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(12), Timeout.InfiniteTimeSpan);
        var report = new Report();
        var elapsed = Stopwatch.StartNew();
        Application? application = null;
        Window? window = null;
        ShiftCapturePresentationProbe? probe = null;
        DispatcherTimer? timer = null;
        var dispatcherFrame = new DispatcherFrame();
        nint priorDpiContext = 0;
        nint windowHandle = 0;
        var initialForeground = GetForegroundWindow();
        try
        {
            if (Application.Current is not null) throw new InvalidOperationException("Review requires its own application.");
            priorDpiContext = SetThreadDpiAwarenessContext(new nint(-4));
            if (priorDpiContext == 0) throw new InvalidOperationException("Physical pixel context unavailable.");
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            const uint color = 0xFFFF5364;
            var ring = new ShiftCueVisual { Width = 96, Height = 96, IsHitTestVisible = false };
            var surface = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(13, 16, 24)),
                Padding = new Thickness(22),
                Child = ring,
                IsHitTestVisible = false
            };
            window = new Window
            {
                Title = "Wisp pixel readback check - synthetic",
                Width = 140,
                Height = 140,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false,
                ShowActivated = false,
                ShowInTaskbar = false,
                Focusable = false,
                Topmost = true,
                Left = SystemParameters.WorkArea.Left + 16,
                Top = SystemParameters.WorkArea.Top + 16,
                Background = surface.Background,
                Content = surface
            };
            window.SourceInitialized += (_, _) =>
            {
                windowHandle = new WindowInteropHelper(window).Handle;
                const int extendedStyle = -20;
                const int nonactivating = 0x08000000;
                const int toolWindow = 0x00000080;
                SetWindowLong(windowHandle, extendedStyle, GetWindowLong(windowHandle, extendedStyle) | nonactivating | toolWindow);
                if ((GetWindowLong(windowHandle, extendedStyle) & nonactivating) == 0)
                    throw new InvalidOperationException("Nonactivating style unavailable.");
                HwndSource.FromHwnd(windowHandle)?.AddHook(BlockActivation);
            };
            window.Closed += (_, _) => dispatcherFrame.Continue = false;
            var rendered = false;
            window.ContentRendered += (_, _) => rendered = true;
            window.Show();
            window.UpdateLayout();
            probe = new ShiftCapturePresentationProbe(windowHandle);
            var point = ring.PointToScreen(new Point(ring.ActualWidth * .5, ring.ActualHeight * .14));
            var region = new ShiftCaptureRect((int)Math.Floor(point.X) - 12, (int)Math.Floor(point.Y) - 12, 24, 24);
            report.RegionWidth = region.Width;
            report.RegionHeight = region.Height;
            report.DpiScale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            string[] phaseNames = ["off-1", "on-1", "off-2", "on-2", "off-3"];
            var phaseIndex = 0;
            var phaseStarted = Stopwatch.GetTimestamp();
            var consecutiveMatches = 0;
            var phaseRows = new List<ShiftCapturePresentationSample>();
            ring.Update(default);
            timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (elapsed.Elapsed.TotalSeconds > 9) { Stop("deadline"); return; }
                    if (window.IsActive || GetForegroundWindow() == windowHandle) { Stop("test-window-activated"); return; }
                    if (GetForegroundWindow() != initialForeground) { Stop("foreground-changed"); return; }
                    if (!rendered) return;
                    if (!OwnVisibleRegion(windowHandle, region)) { Stop("test-region-occluded-or-moved"); return; }
                    var phaseAge = (double)(Stopwatch.GetTimestamp() - phaseStarted) * 1000 / Stopwatch.Frequency;
                    if (phaseAge < 150) return;
                    if (phaseAge > 1500) { Stop("phase-no-confirmed-pixels"); return; }
                    var sample = probe.TrySample(region, color, 1);
                    if (!OwnVisibleRegion(windowHandle, region)) { Stop("test-region-changed-during-readback"); return; }
                    if (sample is null) return;
                    phaseRows.Add(sample);
                    if (sample.Status != ShiftCapturePresentationStatus.Observed) { Stop("capture-" + sample.Status); return; }
                    var on = phaseIndex is 1 or 3;
                    var expected = on ? sample.Pixels.CompatibleFraction >= .02 : sample.Pixels.CompatiblePixelCount == 0;
                    consecutiveMatches = expected ? consecutiveMatches + 1 : 0;
                    if (consecutiveMatches < 3) return;
                    report.Phases.Add(new(phaseNames[phaseIndex], on, phaseStarted, true, phaseRows.ToArray()));
                    if (phaseIndex == 2)
                    {
                        // Only this synthetic WPF surface is qualified. This probe
                        // is disposed here and must never be reused for a game HUD.
                        report.SyntheticWpfQualified = probe.SetQualified(true);
                        if (!report.SyntheticWpfQualified) { Stop("synthetic-qualification-refused"); return; }
                    }
                    phaseIndex++;
                    if (phaseIndex == phaseNames.Length)
                    {
                        report.Completed = true;
                        Stop(null);
                        return;
                    }
                    consecutiveMatches = 0;
                    phaseRows.Clear();
                    ring.Update(phaseIndex is 1 or 3 ? new ShiftCueVisualState(true, 3, color, true, 6700) : default);
                    phaseStarted = Stopwatch.GetTimestamp();
                }
                catch (Exception error) { Stop("exception-" + error.GetType().Name); }
            };
            timer.Start();
            Dispatcher.PushFrame(dispatcherFrame);

            void Stop(string? failure)
            {
                if (failure is not null)
                {
                    report.Failures.Add(failure);
                    if (phaseIndex < phaseNames.Length)
                        report.Phases.Add(new(phaseNames[phaseIndex], phaseIndex is 1 or 3, phaseStarted, false, phaseRows.ToArray()));
                }
                timer?.Stop();
                window.Close();
                dispatcherFrame.Continue = false;
            }
        }
        catch (Exception error) { report.Failures.Add("initialize-" + error.GetType().Name); }
        finally
        {
            timer?.Stop();
            probe?.Dispose();
            window?.Close();
            application?.Shutdown();
            if (priorDpiContext != 0) SetThreadDpiAwarenessContext(priorDpiContext);
            report.DurationMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            report.ForegroundPreserved = GetForegroundWindow() == initialForeground;
        }
        if (!report.ForegroundPreserved) report.Failures.Add("foreground-not-preserved");
        var costs = report.Phases.SelectMany(phase => phase.Samples).Select(sample => sample.ReadbackMilliseconds).ToArray();
        report.ReadbackCount = costs.Length;
        report.TotalReadbackMilliseconds = costs.Sum();
        report.MaximumReadbackMilliseconds = costs.Length > 0 ? costs.Max() : 0;
        using (var outputFile = new FileStream(Path.Combine(output, "shift-capture-probe-review.json"), FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(outputFile, report, new JsonSerializerOptions
            { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
        Console.WriteLine($"Pixel readback smoke check: {report.Phases.Count}/5 phases, {report.Failures.Count} failures; own synthetic WPF window only.");
        return report.Completed && report.Failures.Count == 0 ? 0 : 1;
    }

    private static nint BlockActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0021) return 0; // WM_MOUSEACTIVATE
        handled = true;
        return 4; // MA_NOACTIVATEANDEAT
    }

    private static bool OwnVisibleRegion(nint hwnd, ShiftCaptureRect region)
    {
        if (!IsWindowVisible(hwnd) || !GetClientRect(hwnd, out var client)) return false;
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin) || region.X < origin.X || region.Y < origin.Y ||
            (long)region.X + region.Width > (long)origin.X + client.Right ||
            (long)region.Y + region.Height > (long)origin.Y + client.Bottom) return false;
        NativePoint[] points =
        [
            new(region.X, region.Y), new(region.X + region.Width - 1, region.Y),
            new(region.X, region.Y + region.Height - 1), new(region.X + region.Width - 1, region.Y + region.Height - 1),
            new(region.X + region.Width / 2, region.Y + region.Height / 2)
        ];
        return points.All(point => GetAncestor(WindowFromPoint(point), 2) == hwnd);
    }

    private sealed class Report
    {
        public string Method => "Desktop BitBlt of a 24x24 physical pixel region inside an opaque, nonactivating own WPF window; actual ShiftCueVisual off/on/off/on/off. No game reads, activation, external-window capture, images, or raw pixels are saved.";
        public string TimingLimit => "QPC intervals measure CPU readback calls, not compositor timestamps, physical scanout or photon visibility. Synthetic WPF qualification cannot qualify the native DirectComposition HUD or gameplay capture.";
        public bool NativeHudQualified => false;
        public bool GameCaptureQualified => false;
        public bool SyntheticWpfQualified { get; set; }
        public bool Completed { get; set; }
        public bool ForegroundPreserved { get; set; }
        public int RegionWidth { get; set; }
        public int RegionHeight { get; set; }
        public double DpiScale { get; set; }
        public double DurationMilliseconds { get; set; }
        public int ReadbackCount { get; set; }
        public double TotalReadbackMilliseconds { get; set; }
        public double MaximumReadbackMilliseconds { get; set; }
        public List<Phase> Phases { get; } = [];
        public List<string> Failures { get; } = [];
    }

    private sealed record Phase(string Name, bool RingOn, long AppliedAtQpc, bool Passed, ShiftCapturePresentationSample[] Samples);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
}
