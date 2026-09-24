using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App.DebugLogging;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class HudNativeHostTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var shutdown = Application.Current.ShutdownMode;
        var logging = TachDiagnostics.IsEnabled;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settings = new AppSettings
        {
            StartWithWindows = false,
            StartWithForza = false,
            AutomaticApplicationUpdateChecks = false,
            DebugLoggingEnabled = false,
            CpuRenderingEnabled = true,
            LayoutMode = HudLayoutMode.Native,
            NativeGaugeMode = NativeGaugeMode.Analogue,
            OverlayOpacity = 1,
            GForceEnabled = false,
            BoostGaugeEnabled = false,
            TireTemperatureGaugeEnabled = false,
            PowerGaugeEnabled = false,
            TorqueGaugeEnabled = false
        };
        var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        TachDiagnostics.SetEnabled(true);
        var window = new OverlayWindow(controller)
        {
            Left = SystemParameters.WorkArea.Left + 20,
            Top = SystemParameters.WorkArea.Top + 20,
            Topmost = true
        };
        var started = Stopwatch.GetTimestamp();
        IntPtr handle = IntPtr.Zero;
        IntPtr presentationHandle = IntPtr.Zero;
        try
        {
            AssertInitialFailureStaysSuppressed(controller);
            HudNativeHost.Attach(window);
            var host = Host(window);
            HudNativeHost.Attach(window);
            Assert.Same(host, Host(window));
            var analog = Gauge<NativeAnalogSpeedometer>(window, "NativeAnalogPanel");
            var electric = Gauge<NativeElectricAnalogSpeedometer>(window, "NativeElectricAnalogPanel");
            var digital = Gauge<NativeDigitalSpeedometer>(window, "NativeDigitalPanel");
            var electricDigital = Gauge<NativeElectricDigitalSpeedometer>(window, "NativeElectricDigitalPanel");
            var controls = new UserControl[] { analog, electric, digital, electricDigital };
            foreach (var control in controls) Assert.False(HudNativeHost.IsPresented(control));
            // Device initialization alone is not evidence that pixels were submitted.
            Callback(host, "OnStatus", true, 0);
            Pump();
            foreach (var control in controls)
            {
                Assert.False(HudNativeHost.IsPresented(control));
                Assert.Equal(Visibility.Visible, ((UIElement)control.Content).Visibility);
            }
            Seed(analog, electric, digital, electricDigital, 1);
            // Regression: an already-unlocked HUD must initialize the native
            // worker, rather than remaining on composition-callback rendering.
            window.SetEditMode(true);
            window.SetTelemetryVisible(true, 1);
            handle = new WindowInteropHelper(window).Handle;
            PumpUntil(() => HudNativeHost.PresentationHandle(window) != IntPtr.Zero, 4000);
            presentationHandle = HudNativeHost.PresentationHandle(window);
            WaitFor(analog);
            Assert.True(Snapshot(host).Active);
            Assert.True(Worker(host).SurfaceVisible);
            Assert.Equal(Visibility.Hidden, ((UIElement)analog.Content).Visibility);
            window.SetEditMode(false);
            NativeCompositionWindowTests.AssertHost(window, presentationHandle);
            var firstCreated = Rows().Where(row => row.Stage == "state" && row.Result == "created").ToArray();
            Assert.Single(firstCreated);
            var workerId = firstCreated[0].ControlId;
            var worker = Worker(host);

            foreach (var unlocked in new[] { true, false, true, false })
            {
                var changedAt = Stopwatch.GetTimestamp();
                window.SetEditMode(unlocked);
                Seed(analog, electric, digital, electricDigital, unlocked ? 3u : 4u);
                PumpUntil(() => Rows().Any(row => row.Stage == "present" && row.Result == "submitted" &&
                    row.StartedTimestamp >= changedAt), 4000);
                Assert.True(Snapshot(host).Active);
                Assert.True(worker.SurfaceVisible);
                Assert.Same(worker, Worker(host));
                Assert.True(HudNativeHost.IsPresented(analog));
                Assert.Equal(Visibility.Hidden, ((UIElement)analog.Content).Visibility);
            }

            window.SetElectricPowertrain(true);
            WaitFor(electric);
            window.ApplyLayout(HudLayoutMode.Native, NativeGaugeMode.Digital, 1.25, .8, 1);
            WaitFor(electricDigital);
            window.SetElectricPowertrain(false);
            WaitFor(digital);
            window.ApplyLayout(HudLayoutMode.Native, NativeGaugeMode.Analogue, 1, 1, 1);
            WaitFor(analog);
            Assert.True(worker.SurfaceVisible);
            window.SetTelemetryVisible(false, 1, hideImmediately: true);
            Pump();
            Assert.False(window.IsVisible);
            Callback(host, "Capture");
            Assert.False(Snapshot(host).Active);
            PumpUntil(() => !worker.SurfaceVisible, 1000);
            var hiddenQuietAt = Stopwatch.GetTimestamp();
            Thread.Sleep(50);
            Assert.DoesNotContain(Rows(), row => row.Stage == "present" && row.Result == "submitted" &&
                row.StartedTimestamp >= hiddenQuietAt);
            // Hiding cannot change the input focus or dispose/recreate this target.
            Assert.NotEqual(handle, GetForegroundWindow());
            var resumedAt = Stopwatch.GetTimestamp();
            window.SetTelemetryVisible(true, 1);
            Seed(analog, electric, digital, electricDigital, 2);
            PumpUntil(() => Snapshot(host).Active && Rows().Any(row => row.Stage == "present" &&
                row.Result == "submitted" && row.StartedTimestamp >= resumedAt), 4000);
            Assert.True(HudNativeHost.IsPresented(analog));
            Assert.Same(worker, Worker(host));
            var rows = Rows();
            Assert.Single(rows, row => row.Stage == "state" && row.Result == "created");
            Assert.All(rows, row => Assert.Equal(workerId, row.ControlId));
            Assert.All(rows, row => Assert.True(row.CpuRendering));
            Assert.DoesNotContain(rows, row => row.Result == "error");
            Assert.False(window.IsActive);
            AssertMinimizedHostStopsRendering(window, host, handle, Rows);

            // A device failure retires the native HUD while keeping WPF artwork
            // suppressed; the edit/layout window remains available.
            Callback(host, "OnStatus", false, unchecked((int)0x80004005));
            PumpUntil(() => controller.ViewModel.NativeRendererStatus == "HUD renderer: unavailable (0x80004005)", 1000);
            PumpUntil(() => !IsWindow(presentationHandle), 2000);
            foreach (var control in controls)
            {
                Assert.False(HudNativeHost.IsPresented(control));
                Assert.True(HudNativeHost.IsWpfContentSuppressed(control));
                Assert.Equal(Visibility.Hidden, ((UIElement)control.Content).Visibility);
            }
            Assert.True(window.IsVisible);
            var lastNeedleAngle = Assert.IsType<RotateTransform>(analog.FindName("NeedleRotation")).Angle;
            Seed(analog, electric, digital, electricDigital, 5);
            window.SetEditMode(true);
            Pump();
            Assert.Equal(lastNeedleAngle, Assert.IsType<RotateTransform>(analog.FindName("NeedleRotation")).Angle);
            Assert.Equal(Visibility.Hidden, ((UIElement)analog.Content).Visibility);
            var status = controller.ViewModel.NativeRendererStatus;
            Callback(host, "OnStatus", true, 0);
            Pump();
            Assert.Equal(status, controller.ViewModel.NativeRendererStatus);
            window.Close();
            Pump();
            Assert.False(HudNativeHost.IsAttached(window));
            // A late worker callback after Close must not alter restored controls/status.
            Callback(host, "OnStatus", true, 0);
            Pump();
            Assert.Equal(status, controller.ViewModel.NativeRendererStatus);
            Assert.All(controls, control => Assert.False(HudNativeHost.IsPresented(control)));
            foreach (var control in controls)
            {
                Assert.False(HudNativeHost.IsWpfContentSuppressed(control));
                Assert.Equal(Visibility.Visible, ((UIElement)control.Content).Visibility);
            }

            TachRendererDiagnostic[] Rows()
            {
                var snapshot = TachDiagnostics.Snapshot();
                return snapshot is null ? [] : snapshot.RendererStartup.Concat(snapshot.RendererRecent)
                    .Where(row => row.HostWindowHandle == presentationHandle.ToInt64() && row.StartedTimestamp >= started)
                    .DistinctBy(row => (row.ControlId, row.Sequence, row.Stage, row.StartedTimestamp)).ToArray();
            }
            void WaitFor(UserControl control)
            {
                var after = Stopwatch.GetTimestamp();
                PumpUntil(() => HudNativeHost.IsPresented(control) && control.IsVisible &&
                    Rows().Any(row => row.Stage == "present" && row.Result == "submitted" && row.StartedTimestamp >= after), 4000,
                    () => $"status={controller.ViewModel.NativeRendererStatus}; presented={HudNativeHost.IsPresented(control)}; visible={control.IsVisible}; active={Snapshot(host).Active}; " +
                        string.Join(",", Rows().TakeLast(8).Select(row => $"{row.Stage}/{row.Result}/0x{row.HResult:X8}")));
                Assert.Equal(Visibility.Hidden, ((UIElement)control.Content).Visibility);
                Assert.NotEqual(handle, GetForegroundWindow());
            }
        }
        finally
        {
            window.Close();
            Pump();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            TachDiagnostics.SetEnabled(logging);
            if (!logging) TachDiagnostics.Clear();
            Application.Current.ShutdownMode = shutdown;
        }
    }

    private static void AssertInitialFailureStaysSuppressed(AppController controller)
    {
        var window = new OverlayWindow(controller)
        {
            Left = SystemParameters.WorkArea.Left + 20,
            Top = SystemParameters.WorkArea.Top + 20,
            Topmost = true
        };
        var host = Host(window);
        var analog = Gauge<NativeAnalogSpeedometer>(window, "NativeAnalogPanel");
        var content = Assert.IsAssignableFrom<UIElement>(analog.Content);
        try
        {
            // Exercise the native device's initial unsupported status before
            // there is any successful submission or worker to retire.
            Callback(host, "Discover", window.FindName("RootPanel"));
            Assert.True(HudNativeHost.IsWpfContentSuppressed(analog));
            Assert.False(HudNativeHost.IsPresented(analog));
            Assert.Equal(Visibility.Hidden, content.Visibility);
            Callback(host, "OnStatus", false, unchecked((int)0x887A0004));
            PumpUntil(() => controller.ViewModel.NativeRendererStatus == "HUD renderer: unavailable (0x887A0004)", 1000);
            window.SetEditMode(true);
            window.SetTelemetryVisible(true, 1);
            Pump();
            Assert.True(window.IsVisible);
            Assert.True(analog.RenderSize.Width > 0 && analog.RenderSize.Height > 0);
            Assert.Equal(IntPtr.Zero, HudNativeHost.PresentationHandle(window));
            Assert.True(HudNativeHost.IsWpfContentSuppressed(analog));
            Assert.False(HudNativeHost.IsPresented(analog));
            Assert.Equal(Visibility.Hidden, content.Visibility);
            Callback(host, "OnStatus", true, 0);
            Pump();
            Assert.Equal("HUD renderer: unavailable (0x887A0004)", controller.ViewModel.NativeRendererStatus);
            Assert.Equal(Visibility.Hidden, content.Visibility);
        }
        finally
        {
            window.Close();
            Pump();
        }
        Assert.False(HudNativeHost.IsAttached(window));
        Assert.False(HudNativeHost.IsPresented(analog));
        Assert.False(HudNativeHost.IsWpfContentSuppressed(analog));
        Assert.Equal(Visibility.Visible, content.Visibility);
    }

    private static void AssertMinimizedHostStopsRendering(OverlayWindow window, HudNativeHost host,
        IntPtr handle, Func<TachRendererDiagnostic[]> rows)
    {
        var foreground = GetForegroundWindow();
        var before = SnapshotStatus();
        var worker = Worker(host);
        Assert.True(worker.SurfaceVisible);
        var minimizedAt = Stopwatch.GetTimestamp();
        try
        {
            // A renderer diagnostic may intentionally be omitted while export
            // owns this gate. The applied lifecycle acknowledgement must survive.
            var capture = typeof(TachDiagnostics).GetField("_capture", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var diagnosticGate = capture.GetType().GetField("Gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
            var omissions = TachDiagnostics.Snapshot()!.RendererContentionOmissions;
            lock (diagnosticGate)
            {
                ShowWindow(handle, 7); // SW_SHOWMINNOACTIVE
                PumpUntil(() => window.WindowState == WindowState.Minimized, 1000,
                    () => $"step=minimize-window; current=[{SnapshotStatus()}]");
                Assert.True(foreground == GetForegroundWindow(), "Minimizing the synthetic HUD changed the foreground window.");
                Assert.True(GetClientRect(handle, out var client));
                Callback(host, "Capture");
                var snapshot = Snapshot(host);
                Assert.False(snapshot.Active, "A minimized native HUD must publish an inactive snapshot even with an empty client rectangle.");
                PumpUntil(() => !worker.SurfaceVisible, 1000,
                    () => $"step=minimize-worker; client={client.Right - client.Left}x{client.Bottom - client.Top}; current=[{SnapshotStatus()}]");
            }
            Assert.True(TachDiagnostics.Snapshot()!.RendererContentionOmissions > omissions,
                "The minimize check must exercise the diagnostic contention path.");
            var quietAt = Stopwatch.GetTimestamp();
            Thread.Sleep(50);
            Assert.DoesNotContain(rows(), row => row.Stage == "present" && row.Result == "submitted" &&
                row.StartedTimestamp >= quietAt);
        }
        finally
        {
            ShowWindow(handle, 4); // SW_SHOWNOACTIVATE
            PumpUntil(() => window.WindowState == WindowState.Normal, 1000,
                () => FailureDetails("restore-window"));
            Assert.True(foreground == GetForegroundWindow(), "Restoring the synthetic HUD changed the foreground window.");
        }
        var restoredAt = Stopwatch.GetTimestamp();
        Callback(host, "Capture");
        PumpUntil(() => rows().Any(row => row.Stage == "present" && row.Result == "submitted" &&
            row.StartedTimestamp >= restoredAt), 4000, () => FailureDetails("restore-worker"));
        Assert.Same(worker, Worker(host));
        Assert.True(worker.SurfaceVisible);
        Assert.True(foreground == GetForegroundWindow(), "Restoring native rendering changed the foreground window.");

        string SnapshotStatus()
        {
            var type = typeof(HudNativeHost);
            var snapshot = type.GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(host) as HudWindowSnapshot;
            var failed = type.GetField("_failed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);
            return snapshot is null ? $"snapshot=none; failed={failed}" :
                $"active={snapshot.Active}; layers={snapshot.Layers.Length}; size={snapshot.Width}x{snapshot.Height}; failed={failed}";
        }

        string FailureDetails(string step)
        {
            var recorded = rows();
            var beforeRows = recorded.Where(row => row.StartedTimestamp < minimizedAt)
                .OrderBy(row => row.StartedTimestamp).TakeLast(4);
            var afterRows = recorded.Where(row => row.StartedTimestamp >= minimizedAt)
                .OrderBy(row => row.StartedTimestamp).TakeLast(8);
            var stages = string.Join(", ", beforeRows.Concat(afterRows).Select(row =>
                $"{(row.StartedTimestamp - minimizedAt) * 1000d / Stopwatch.Frequency:F3}ms:{row.Stage}/{row.Result}/0x{row.HResult:X8}"));
            return $"step={step}; diagnosticsEnabled={TachDiagnostics.IsEnabled}; foregroundUnchanged={foreground == GetForegroundWindow()}; " +
                $"windowState={window.WindowState}; before=[{before}]; current=[{SnapshotStatus()}]; stages=[{stages}]";
        }
    }

    private static HudWindowSnapshot Snapshot(HudNativeHost host) =>
        Assert.IsType<HudWindowSnapshot>(typeof(HudNativeHost)
            .GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host));

    private static AnalogHudRenderWorker Worker(HudNativeHost host) =>
        Assert.IsType<AnalogHudRenderWorker>(typeof(HudNativeHost)
            .GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host));

    private static T Gauge<T>(OverlayWindow window, string panelName) where T : UserControl =>
        Assert.IsType<T>(Assert.Single(((Grid)window.FindName(panelName)).Children.Cast<UIElement>()));
    private static void Seed(NativeAnalogSpeedometer analog, NativeElectricAnalogSpeedometer electric,
        NativeDigitalSpeedometer digital, NativeElectricDigitalSpeedometer electricDigital, uint tick)
    {
        var now = Stopwatch.GetTimestamp();
        var frame = new NativeGaugeFrame(true, 137, 5000, 9000, TransmissionGear.Third,
            SpeedUnit.MilesPerHour, ExactRedlineResult.Exact(8000 * Math.PI / 30),
            CarOrdinal: 1, GameTimestampMilliseconds: tick, ReceivedTimestamp: now,
            NativeNeedleAngleDegrees: 210, NativeNeedleBlurAmount: -.2, NativeGaugeObservedTimestamp: now);
        Set(analog, NativeAnalogSpeedometer.FrameProperty, frame);
        Set(digital, NativeDigitalSpeedometer.FrameProperty, frame);
        frame = frame with { IsElectric = true, CarOrdinal = 2, NativeRegenFillAmount = .1, NativePowerFillAmount = .6, NativeRegenPowerRatio = .3 };
        Set(electric, NativeElectricAnalogSpeedometer.FrameProperty, frame);
        Set(electricDigital, NativeElectricDigitalSpeedometer.FrameProperty, frame);
        static void Set(DependencyObject control, DependencyProperty property, NativeGaugeFrame value)
        {
            BindingOperations.ClearBinding(control, property);
            control.SetValue(property, value);
        }
    }
    private static HudNativeHost Host(Window window)
    {
        var table = (ConditionalWeakTable<Window, HudNativeHost>)typeof(HudNativeHost)
            .GetField("Hosts", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.True(table.TryGetValue(window, out var host));
        return host!;
    }
    private static void Callback(HudNativeHost host, string name, params object[] arguments) =>
        typeof(HudNativeHost).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, arguments);
    private static void PumpUntil(Func<bool> predicate, int milliseconds, Func<string>? failureDetails = null)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(10); }
        var reached = predicate();
        Assert.True(reached, reached ? null :
            $"Shared native HUD did not reach the expected bounded lifecycle state. {failureDetails?.Invoke()}");
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    private sealed class NoStartupRegistration : IStartupRegistrationService
    { public void Apply(bool startWithWindows, bool startWithForza) { } }
    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out ClientRect rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
}
