using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App.DebugLogging;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class NativeRendererIntegrationTests
{
    internal static void AssertOnCurrentDispatcher(bool cpuRendering = false)
    {
        var hardwareAvailable = cpuRendering || ProbeHardwareSupport();
        var expectedStatus = cpuRendering ? "HUD renderer: CPU (WARP) / DirectComposition" : hardwareAvailable
            ? "HUD renderer: Direct3D 11 / DirectComposition"
            : "HUD renderer: unavailable (0x887A0004)";
        var expectedContentVisibility = Visibility.Hidden;
        var settings = new AppSettings
        {
            StartWithWindows = false,
            StartWithForza = false,
            AutomaticApplicationUpdateChecks = false,
            DebugLoggingEnabled = false,
            CpuRenderingEnabled = cpuRendering,
            LayoutMode = HudLayoutMode.Native,
            NativeGaugeMode = NativeGaugeMode.Analogue,
            OverlayOpacity = 1,
            GForceEnabled = false,
            BoostGaugeEnabled = false,
            TireTemperatureGaugeEnabled = false
        };
        var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        var previousShutdown = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        OverlayWindow? window = null;
        try
        {
            if (!cpuRendering && hardwareAvailable) CompositorNeedleWorkerTests.AssertOnCurrentDispatcher();
            window = new OverlayWindow(controller)
            {
                // Successful Present requires an unoccluded surface. An off-screen
                // HWND can initialize D3D successfully but never submit a frame.
                Left = SystemParameters.WorkArea.Left + 20,
                Top = SystemParameters.WorkArea.Top + 20,
                Topmost = true
            };
            var panel = Assert.IsType<Grid>(window.FindName("NativeAnalogPanel"));
            var gauge = Assert.IsType<NativeAnalogSpeedometer>(Assert.Single(panel.Children.Cast<UIElement>()));
            SetFrame(gauge, 314, 4500);
            window.SetTelemetryVisible(true, 1);
            PumpUntil(() => controller.ViewModel.NativeRendererStatus.Contains("/ DirectComposition", StringComparison.Ordinal) ||
                controller.ViewModel.NativeRendererStatus.Contains("unavailable", StringComparison.Ordinal), 8000);
            Assert.Equal(expectedStatus, controller.ViewModel.NativeRendererStatus);
            Assert.Equal(expectedContentVisibility, Assert.IsAssignableFrom<UIElement>(gauge.Content).Visibility);
            if (!hardwareAvailable) Assert.True(HudNativeHost.IsWpfContentSuppressed(gauge));
            var hwnd = new WindowInteropHelper(window).Handle;
            Assert.NotEqual(hwnd, GetForegroundWindow());
            Assert.False(window.IsActive);

            window.ApplyAppearance(1.25, .8, .5);
            Pump();
            SetFrame(gauge, 314, 8900);
            window.ApplyLayout(HudLayoutMode.Native, NativeGaugeMode.Digital, 1, 1, 1);
            Pump();
            Assert.False(gauge.IsVisible);
            window.ApplyLayout(HudLayoutMode.Native, NativeGaugeMode.Analogue, 1, 1, 1);
            SetFrame(gauge, 315, 2800);
            Pump();
            Assert.True(gauge.IsVisible);
            window.SetEditMode(true);
            Assert.NotEqual(hwnd, GetForegroundWindow());
            window.SetEditMode(false);
            window.SetTelemetryVisible(false, 1, hideImmediately: true);
            Pump();
            Assert.False(window.IsVisible);
            window.SetTelemetryVisible(true, 1);
            SetFrame(gauge, 315, 7200);
            PumpUntil(() => window.IsVisible && gauge.IsVisible, 1000);
            Assert.Equal(expectedStatus, controller.ViewModel.NativeRendererStatus);
            PumpUntil(() => Assert.IsAssignableFrom<UIElement>(gauge.Content).Visibility == expectedContentVisibility, 4000);
            if (!hardwareAvailable) Assert.True(HudNativeHost.IsWpfContentSuppressed(gauge));
            Assert.NotEqual(hwnd, GetForegroundWindow());

            AssertAttachedGForceToggleKeepsRendering(controller, window, gauge, hardwareAvailable);
        }
        finally
        {
            window?.Close();
            Pump();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Application.Current.ShutdownMode = previousShutdown;
        }
    }

    private static void AssertAttachedGForceToggleKeepsRendering(
        AppController controller, OverlayWindow window, NativeAnalogSpeedometer gauge, bool hardwareAvailable)
    {
        var previousDiagnosticsEnabled = TachDiagnostics.IsEnabled;
        var previousOverlay = controller.Overlay;
        var hwnd = new WindowInteropHelper(window).Handle;
        var root = Assert.IsType<Grid>(window.FindName("RootPanel"));
        var viewbox = Assert.IsType<Viewbox>(window.FindName("RootViewbox"));
        var gForce = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("AttachedNativeGForce"));
        controller.Overlay = window;
        controller.Settings.OverlayWidthScale = 4d / 3;
        controller.Settings.OverlayHeightScale = 4d / 3;
        controller.ViewModel.GForceAttached = true;
        TachDiagnostics.SetEnabled(true);
        try
        {
            // Each settled state must consume new input, not merely retain the
            // old surface or report the renderer's initialization status.
            AssertState(true, 4300);
            var stableBounds = new Rect(window.Left, window.Top, window.Width, window.Height);
            for (int cycle = 0; cycle < 4; cycle++)
            {
                AssertState(false, 4800 + cycle * 200);
                Assert.Equal(stableBounds, new Rect(window.Left, window.Top, window.Width, window.Height));
                AssertState(true, 6800 + cycle * 200);
                Assert.Equal(stableBounds, new Rect(window.Left, window.Top, window.Width, window.Height));
            }
        }
        finally
        {
            controller.Overlay = previousOverlay;
            TachDiagnostics.SetEnabled(previousDiagnosticsEnabled);
            if (!previousDiagnosticsEnabled) TachDiagnostics.Clear();
        }

        void AssertState(bool enabled, double rpm)
        {
            var started = Stopwatch.GetTimestamp();
            // Appearance updates the binding first, then ApplyViewOptions
            // applies this layout. Avoid its live focus query in this fixture.
            controller.ViewModel.GForceEnabled = enabled;
            controller.Settings.GForceEnabled = enabled;
            window.ApplyLayout(HudLayoutMode.Native, NativeGaugeMode.Analogue,
                controller.Settings.OverlayWidthScale, controller.Settings.OverlayHeightScale, 1);
            Pump();

            const double top = 72;
            Assert.Equal(293.5 + top, root.Height);
            Assert.Equal((293.5 + top) * 4 / 3, window.Height, 6);
            // The HWND rounds to physical pixels before the Viewbox scales its content.
            Assert.Equal(top * viewbox.ActualHeight / root.Height,
                gauge.TransformToAncestor(window).Transform(new Point()).Y, 6);
            Assert.Equal(enabled ? Visibility.Visible : Visibility.Collapsed, gForce.Visibility);
            Assert.True(window.IsVisible);
            Assert.True(gauge.IsVisible);

            if (hardwareAvailable)
            {
                PumpUntil(() =>
                {
                    SetFrame(gauge, 315, rpm);
                    var snapshot = TachDiagnostics.Snapshot();
                    if (snapshot is null) return false;
                    var fresh = snapshot.NeedleStartup.Concat(snapshot.NeedleRecent)
                        .Where(row => row.Route == (controller.ViewModel.ActiveCpuRendering ? "directcomposition" : "compositor_snapshot") && row.HostKind == nameof(OverlayWindow) &&
                            row.Source == "fallback" &&
                            row.HostWindowHandle == HudNativeHost.PresentationHandle(window).ToInt64() && row.RawRpm == rpm &&
                            row.AppliedTimestamp >= started && row.ReceivedTimestamp is long received && received >= started)
                        .DistinctBy(row => (row.ControlId, row.AppliedTimestamp)).ToArray();
                    return fresh.Length >= 4 && fresh.Select(row => row.ReceivedTimestamp).Distinct().Count() >= 2;
                }, 2000);
                var renderer = TachDiagnostics.Snapshot()!.RendererRecent
                    .Where(row => row.HostWindowHandle == HudNativeHost.PresentationHandle(window).ToInt64() && row.StartedTimestamp >= started)
                    .ToArray();
                var presented = renderer.Where(row => row.Stage == "present" && row.Result == "submitted").ToArray();
                Assert.NotEmpty(presented);
                if (!controller.ViewModel.ActiveCpuRendering)
                    Assert.Contains(renderer, row => row.Stage == "compositor_update" && row.Result == "ready");
                Assert.All(renderer, row => Assert.True(row.NativeThreadId is > 0));
                Assert.All(renderer, row => Assert.Equal(controller.ViewModel.ActiveCpuRendering, row.CpuRendering));
                Assert.All(presented, row =>
                {
                    Assert.True(row.SampleTimestamp is > 0 && row.SampleTimestamp <= row.StartedTimestamp);
                    Assert.True(row.QueuedTimestamp is > 0 && row.QueuedTimestamp <= row.SampleTimestamp);
                    Assert.Equal(0, row.HResult);
                });
                var draws = renderer.Where(row => row.Stage == "draw" && row.Result == "ready").ToArray();
                Assert.NotEmpty(draws);
                Assert.All(draws, row =>
                {
                    Assert.True(row.DrawCommands > 0);
                    // Reusing the cached static dial skips its one buffer map;
                    // the command count still includes that dial.
                    Assert.InRange(row.MapCount, Math.Max(1, row.DrawCommands - 1), row.DrawCommands);
                });
                Assert.Contains(draws, row => row.NativeDrawTicks > 0 && row.NativeDrawTicks >= row.MapTicks &&
                    row.CompletedTimestamp >= row.StartedTimestamp + row.NativeDrawTicks);
                Assert.Contains(renderer, row => row.Stage == "frame_wait" && row.Result == "ready" &&
                    row.NativeWaitTicks is > 0 && row.WaitPrecheckTicks is >= 0 &&
                    row.WaitCallTicks is >= 0 && row.WaitPostcheckTicks is >= 0 &&
                    row.PacingWaitTicks is >= 0 && row.PresentationSyncInterval is 0 or 1 &&
                    row.PresentationRefreshRate is not null && row.PacingHResult is not null && row.PacingWaitReturnCode is not null &&
                    row.SwapChainGeneration is > 0 && row.WaitReturnCode == 1 &&
                    row.NativeWaitTicks >= row.WaitPrecheckTicks + row.WaitCallTicks + row.WaitPostcheckTicks + row.PacingWaitTicks &&
                    row.CompletedTimestamp >= row.StartedTimestamp + row.NativeWaitTicks);
                Assert.All(renderer.Where(row => row.Stage != "frame_wait"), row =>
                {
                    Assert.Null(row.NativeWaitTicks);
                    Assert.Null(row.WaitCallTicks);
                    Assert.Null(row.PacingWaitTicks);
                    Assert.Null(row.PresentationSyncInterval);
                    Assert.Null(row.PresentationRefreshRate);
                    Assert.Null(row.PacingHResult);
                    Assert.Null(row.PacingWaitReturnCode);
                    Assert.Null(row.CpuThreadTicks);
                });
            }
            else
            {
                SetFrame(gauge, 315, rpm);
                Assert.True(HudNativeHost.IsWpfContentSuppressed(gauge));
                Assert.Equal(Visibility.Hidden, Assert.IsAssignableFrom<UIElement>(gauge.Content).Visibility);
            }
            Assert.NotEqual(hwnd, GetForegroundWindow());
            Assert.False(window.IsActive);
        }
    }

    private static bool ProbeHardwareSupport()
    {
        // Match the production device request, independently of the native DLL.
        // An unsupported hardware device must leave the live WPF HUD suppressed.
        const int unsupported = unchecked((int)0x887A0004);
        const uint featureLevel11 = 0xB000;
        var result = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20,
            [featureLevel11], 1, 7, out var device, out var featureLevel, out var context);
        try
        {
            Assert.True(result is 0 or unsupported, $"Hardware capability probe failed unexpectedly: 0x{result:X8}.");
            if (result == 0)
            {
                Assert.NotEqual(IntPtr.Zero, device);
                Assert.NotEqual(IntPtr.Zero, context);
                Assert.Equal(featureLevel11, featureLevel);
            }
            return result == 0;
        }
        finally
        {
            if (context != IntPtr.Zero) Marshal.Release(context);
            if (device != IntPtr.Zero) Marshal.Release(device);
        }
    }

    private static void SetFrame(NativeAnalogSpeedometer gauge, int car, double rpm)
    {
        var now = Stopwatch.GetTimestamp();
        gauge.SetCurrentValue(NativeAnalogSpeedometer.FrameProperty, new NativeGaugeFrame(
            true, 123, rpm, 10000, TransmissionGear.Fourth, SpeedUnit.MilesPerHour,
            ExactRedlineResult.Exact(8500 * Math.PI / 30), CarOrdinal: car,
            GameTimestampMilliseconds: unchecked((uint)Environment.TickCount), ReceivedTimestamp: now,
            NativeNeedleAngleDegrees: double.NaN, NativeNeedleBlurAmount: double.NaN));
    }

    private static void PumpUntil(Func<bool> predicate, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.ElapsedMilliseconds < milliseconds)
        {
            Pump();
            Thread.Sleep(10);
        }
        var reached = predicate();
        var evidence = reached ? string.Empty : string.Join(", ",
            TachDiagnostics.Snapshot()?.RendererRecent.TakeLast(24).Select(row =>
                $"{row.Stage}/{row.Result}:hr=0x{row.HResult:X8},wait={row.WaitReturnCode},generation={row.SwapChainGeneration}") ?? []);
        Assert.True(reached, "Native renderer did not reach the expected bounded lifecycle state. " + evidence);
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software,
        uint flags, [In] uint[] featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out uint featureLevel, out IntPtr immediateContext);
}
