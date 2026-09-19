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
        try
        {
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
            window.SetTelemetryVisible(true, 1);
            handle = new WindowInteropHelper(window).Handle;
            WaitFor(analog);
            var firstCreated = Rows().Where(row => row.Stage == "state" && row.Result == "created").ToArray();
            Assert.Single(firstCreated);
            var workerId = firstCreated[0].ControlId;

            window.SetElectricPowertrain(true);
            WaitFor(electric);
            window.ApplyLayout(HudLayoutMode.Native, NativeGaugeMode.Digital, 1.25, .8, 1);
            WaitFor(electricDigital);
            window.SetElectricPowertrain(false);
            WaitFor(digital);
            window.ApplyLayout(HudLayoutMode.Native, NativeGaugeMode.Analogue, 1, 1, 1);
            WaitFor(analog);
            var hiddenAt = Stopwatch.GetTimestamp();
            window.SetTelemetryVisible(false, 1, hideImmediately: true);
            Pump();
            Assert.False(window.IsVisible);
            // Hiding cannot change the input focus or dispose/recreate this target.
            Assert.NotEqual(handle, GetForegroundWindow());
            window.SetTelemetryVisible(true, 1);
            Seed(analog, electric, digital, electricDigital, 2);
            PumpUntil(() => Rows().Any(row => row.Stage == "present" && row.Result == "submitted" && row.StartedTimestamp > hiddenAt), 4000);
            Assert.True(HudNativeHost.IsPresented(analog));
            var rows = Rows();
            Assert.Single(rows, row => row.Stage == "state" && row.Result == "created");
            Assert.All(rows, row => Assert.Equal(workerId, row.ControlId));
            Assert.All(rows, row => Assert.True(row.CpuRendering));
            Assert.DoesNotContain(rows, row => row.Result == "error");
            Assert.False(window.IsActive);

            // Inject the same callback a failed native device reports. This tests
            // visible fallback restoration without deliberately losing a device.
            Callback(host, "OnStatus", false, unchecked((int)0x80004005));
            PumpUntil(() => controller.ViewModel.NativeRendererStatus.Contains("fallback", StringComparison.Ordinal), 1000);
            foreach (var control in controls)
            {
                Assert.False(HudNativeHost.IsPresented(control));
                Assert.Equal(Visibility.Visible, ((UIElement)control.Content).Visibility);
            }
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

            TachRendererDiagnostic[] Rows()
            {
                var snapshot = TachDiagnostics.Snapshot();
                return snapshot is null ? [] : snapshot.RendererStartup.Concat(snapshot.RendererRecent)
                    .Where(row => row.HostWindowHandle == handle.ToInt64() && row.StartedTimestamp >= started)
                    .DistinctBy(row => (row.ControlId, row.Sequence, row.Stage, row.StartedTimestamp)).ToArray();
            }
            void WaitFor(UserControl control)
            {
                var after = Stopwatch.GetTimestamp();
                PumpUntil(() => HudNativeHost.IsPresented(control) && control.IsVisible &&
                    Rows().Any(row => row.Stage == "present" && row.Result == "submitted" && row.StartedTimestamp >= after), 4000);
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
    private static void PumpUntil(Func<bool> predicate, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(10); }
        Assert.True(predicate(), "Shared native HUD did not reach the expected bounded lifecycle state.");
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    private sealed class NoStartupRegistration : IStartupRegistrationService
    { public void Apply(bool startWithWindows, bool startWithForza) { } }
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
