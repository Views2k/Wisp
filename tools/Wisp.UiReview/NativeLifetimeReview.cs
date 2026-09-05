using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App;
using Wisp.Core;

namespace Wisp.UiReview;

internal sealed class NativeLifetimeCheckReport
{
    public int AutoCloseSeconds => 8;
    public int HardProcessLimitSeconds => 10;
    public double DurationSeconds { get; set; }
    public string CloseReason { get; set; } = "not-shown";
    public bool ContentRendered { get; set; }
    public int VerifiedResumes { get; set; }
    public bool ClosedCleanly { get; set; }
    public List<NativeLifetimeStage> Stages { get; } = [];
    public List<string> Failures { get; } = [];
    public bool Completed => CloseReason == "completed" && ContentRendered &&
        VerifiedResumes == 2 && ClosedCleanly && Stages.Count == 6 && Failures.Count == 0;
    public bool HasFindings => !Completed;
}

internal sealed record NativeLifetimeStage(string Phase, string WindowState, bool HostVisible,
    bool HostActive, NativeLifetimeConsumer[] Controls);
internal sealed record NativeLifetimeConsumer(string Name, bool Loaded, bool Visible, bool? RenderingAttached,
    bool LatestFrameRetained, bool FramePending, int SuppliedFrames, int DigitChanges,
    int ObservedRenderingAdvances, double? BlurAmount);

internal static class NativeLifetimeReview
{
    public static void Run(NativeLifetimeCheckReport run, BindingTrace bindings, CancellationToken cancellation)
    {
        var elapsed = Stopwatch.StartNew();
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("Native lifecycle check exceeded 10 seconds; terminating only this review process (124).");
            Environment.Exit(124);
        }, null, TimeSpan.FromSeconds(run.HardProcessLimitSeconds), Timeout.InfiniteTimeSpan);
        var controls = new[]
        {
            new Probe(new NativeAnalogSpeedometer(), NativeAnalogSpeedometer.FrameProperty, electric: false, compositor: true),
            new Probe(new NativeDigitalSpeedometer(), NativeDigitalSpeedometer.FrameProperty, electric: false, compositor: true),
            new Probe(new NativeElectricAnalogSpeedometer(), NativeElectricAnalogSpeedometer.FrameProperty, electric: true, compositor: false),
            new Probe(new NativeElectricDigitalSpeedometer(), NativeElectricDigitalSpeedometer.FrameProperty, electric: true, compositor: false)
        };
        var surface = new UniformGrid { Rows = 2, Columns = 2, IsHitTestVisible = false };
        foreach (var probe in controls)
        {
            probe.Feed(0, false, SpeedUnit.MilesPerHour);
            surface.Children.Add(new Viewbox { Child = probe.Control, Margin = new Thickness(8) });
        }
        var host = new Window
        {
            Title = "Wisp native lifecycle check - synthetic, auto-close",
            Width = 800,
            Height = 540,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowActivated = false,
            ShowInTaskbar = false,
            Focusable = false,
            Background = Brushes.Black,
            Content = surface
        };
        var dispatcherFrame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, host.Dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
        var deadline = new DispatcherTimer(DispatcherPriority.Send, host.Dispatcher) { Interval = TimeSpan.FromSeconds(run.AutoCloseSeconds) };
        string[] phases = ["visible", "minimized", "restored", "collapsed", "visible-again"];
        var phase = 0;
        var phaseStarted = 0d;
        var supplied = 0;
        var sequence = 0;
        var awaitingResume = false;

        void Check(bool condition, string code)
        {
            if (condition) return;
            run.Failures.Add(phases[phase] + "/" + code);
            throw new InvalidOperationException("Native lifecycle assertion failed.");
        }
        void Close(string reason)
        {
            if (run.CloseReason == "running") run.CloseReason = reason;
            timer.Stop();
            deadline.Stop();
            host.Close();
        }
        void ResetObservations()
        {
            supplied = 0;
            phaseStarted = elapsed.Elapsed.TotalMilliseconds;
            foreach (var probe in controls) probe.ResetObservations();
        }
        NativeLifetimeStage Snapshot(string name) => new(name, host.WindowState.ToString(), host.IsVisible,
            host.IsActive, controls.Select(probe => probe.Snapshot()).ToArray());

        host.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(host).Handle;
            const int extendedStyle = -20;
            const int noActivate = 0x08000000;
            SetWindowLong(handle, extendedStyle, GetWindowLong(handle, extendedStyle) | noActivate);
            Check((GetWindowLong(handle, extendedStyle) & noActivate) != 0, "nonactivating-style");
        };
        KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.None);
        KeyboardNavigation.SetControlTabNavigation(surface, KeyboardNavigationMode.None);
        host.PreviewKeyDown += (_, e) => { e.Handled = true; if (e.Key == Key.Escape) Close("escape"); };
        host.PreviewKeyUp += (_, e) => e.Handled = true;
        host.PreviewTextInput += (_, e) => e.Handled = true;
        host.PreviewMouseDown += (_, e) => e.Handled = true;
        host.PreviewMouseUp += (_, e) => e.Handled = true;
        host.PreviewMouseWheel += (_, e) => e.Handled = true;
        host.Closed += (_, _) =>
        {
            if (run.CloseReason == "running") run.CloseReason = "external-close";
            dispatcherFrame.Continue = false;
        };
        host.ContentRendered += (_, _) =>
        {
            if (run.ContentRendered) return;
            run.ContentRendered = true;
            ResetObservations();
        };
        deadline.Tick += (_, _) => Close("deadline");
        timer.Tick += (_, _) =>
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (!run.ContentRendered) return;
                bindings.Phase = "native-lifetime/" + phases[phase];
                Check(!host.IsActive, "host-activated");
                var active = phase is 0 or 2 or 4;
                if (awaitingResume)
                {
                    if (!controls.All(probe => probe.Control.IsVisible && !probe.FramePending &&
                                              probe.RenderingAttached is not false)) return;
                    Check(controls.All(probe => probe.LatestRetained && probe.ResumedVisualsMatch && probe.BlurIsReset),
                        "resume-latest-visuals-or-blur");
                    Check(controls.Where(probe => probe.Electric).All(probe => probe.ResumedGearMatches),
                        "resume-ev-latest-gear");
                    run.VerifiedResumes++;
                    awaitingResume = false;
                    ResetObservations();
                }
                var unit = phase is 1 or 2 ? SpeedUnit.KilometersPerHour : SpeedUnit.MilesPerHour;
                foreach (var probe in controls) probe.Feed(++sequence, !active, unit);
                supplied++;
                if (elapsed.Elapsed.TotalMilliseconds - phaseStarted < 600) return;

                var stage = Snapshot(phases[phase]);
                run.Stages.Add(stage);
                Check(host.WindowState == (phase == 1 ? WindowState.Minimized : WindowState.Normal), "window-state");
                foreach (var item in stage.Controls)
                {
                    Check(item.LatestFrameRetained && item.FramePending == !active, item.Name + "/latest-frame");
                    Check(item.RenderingAttached is null || item.RenderingAttached == active, item.Name + "/render-subscription");
                    Check(active ? item.Visible && item.DigitChanges > 0 : item.DigitChanges == 0,
                        item.Name + "/visual-work");
                    Check(!active ? item.BlurAmount is null or 0 : item.RenderingAttached is null || item.ObservedRenderingAdvances > 0,
                        item.Name + "/blur-or-live-callback");
                }
                if (phase == 4)
                {
                    Close("completed");
                    run.ClosedCleanly = !host.IsVisible && controls.All(probe => probe.RenderingAttached is not true);
                    Check(run.ClosedCleanly, "close-detachment");
                    run.Stages.Add(Snapshot("closed"));
                    return;
                }
                phase++;
                if (phase is 1 or 3)
                {
                    foreach (var probe in controls) probe.FreezeDisplayedState();
                    if (phase == 1) host.WindowState = WindowState.Minimized;
                    else surface.Visibility = Visibility.Collapsed;
                }
                else
                {
                    awaitingResume = true;
                    if (phase == 2) host.WindowState = WindowState.Normal;
                    else surface.Visibility = Visibility.Visible;
                }
                ResetObservations();
            }
            catch (Exception exception)
            {
                if (run.Failures.Count == 0) run.Failures.Add(phases[phase] + "/" + exception.GetType().Name);
                Close("failed");
            }
        };
        try
        {
            bindings.Phase = "native-lifetime/show";
            run.CloseReason = "running";
            timer.Start();
            deadline.Start();
            host.Show();
            Dispatcher.PushFrame(dispatcherFrame);
        }
        finally
        {
            timer.Stop();
            deadline.Stop();
            host.Close();
            host.Content = null;
            run.DurationSeconds = Math.Round(elapsed.Elapsed.TotalSeconds, 3);
        }
    }

    private sealed class Probe
    {
        private readonly DependencyProperty _frameProperty;
        private readonly bool _compositor;
        private readonly Image _ones;
        private readonly Image _unit;
        private readonly Image _gear;
        private readonly ImageSource? _reverseGearSource;
        private readonly NativeAnalogNeedleVisual? _needle;
        private NativeGaugeFrame _latest;
        private ImageSource? _lastSource, _frozenSource, _frozenUnit, _frozenGear;
        private TimeSpan _lastRendering;
        private int _frames, _digitChanges, _renderingAdvances;
        private static readonly NativeAssistSnapshot Assists = NativeAssistSnapshot.Unavailable();
        internal FrameworkElement Control { get; }
        internal bool Electric { get; }
        internal bool? RenderingAttached => _compositor ? Read<bool>("_renderingAttached") : null;
        internal bool FramePending => Read<bool>("_framePending");
        internal bool LatestRetained => Read<NativeGaugeFrame>("_latestFrame") == _latest;
        internal bool BlurIsReset => _needle is null || _needle.BlurAmount == 0;
        internal bool ResumedVisualsMatch => !ReferenceEquals(_frozenSource, _ones.Source) && !ReferenceEquals(_frozenUnit, _unit.Source);

        internal Probe(FrameworkElement control, DependencyProperty property, bool electric, bool compositor)
        {
            Control = control; _frameProperty = property; Electric = electric; _compositor = compositor;
            BindingOperations.ClearBinding(control, property);
            _ones = control.FindName("OnesImage") as Image ?? throw new InvalidOperationException("Missing native digit.");
            _unit = control.FindName("UnitImage") as Image ?? throw new InvalidOperationException("Missing native unit.");
            _gear = control.FindName("GearImage") as Image ?? throw new InvalidOperationException("Missing native gear.");
            _needle = control.FindName("NeedleMaterial") as NativeAnalogNeedleVisual;
            Feed(0, true, SpeedUnit.MilesPerHour);
            _reverseGearSource = _gear.Source;
        }
        internal void Feed(int sequence, bool hidden, SpeedUnit unit)
        {
            _latest = new NativeGaugeFrame(true, hidden ? 127 : 121 + sequence % 5,
                2_000 + sequence, 9_000,
                hidden ? TransmissionGear.Reverse : TransmissionGear.First, unit,
                ExactRedlineResult.Exact(7_500 * 2 * Math.PI / 60), Assists,
                IsElectric: Electric, CarOrdinal: 314, GameTimestampMilliseconds: (uint)sequence,
                ReceivedTimestamp: Stopwatch.GetTimestamp());
            Control.SetValue(_frameProperty, _latest);
            _frames++;
            if (!ReferenceEquals(_lastSource, _ones.Source)) _digitChanges++;
            _lastSource = _ones.Source;
            if (_compositor)
            {
                var rendering = Read<TimeSpan>("_lastRenderingTime");
                if (rendering != TimeSpan.MinValue && rendering != _lastRendering) _renderingAdvances++;
                _lastRendering = rendering;
            }
        }
        internal void ResetObservations()
        {
            _frames = _digitChanges = _renderingAdvances = 0;
            _lastSource = _ones.Source;
            _lastRendering = _compositor ? Read<TimeSpan>("_lastRenderingTime") : TimeSpan.MinValue;
        }
        internal void FreezeDisplayedState() => (_frozenSource, _frozenUnit, _frozenGear) = (_ones.Source, _unit.Source, _gear.Source);
        internal bool ResumedGearMatches
        {
            get
            {
                return _reverseGearSource is not null && _gear.Visibility == Visibility.Visible &&
                       ReferenceEquals(_reverseGearSource, _gear.Source) && !ReferenceEquals(_frozenGear, _gear.Source);
            }
        }
        internal NativeLifetimeConsumer Snapshot() => new(Control.GetType().Name, Control.IsLoaded, Control.IsVisible,
            RenderingAttached, LatestRetained, FramePending, _frames, _digitChanges, _renderingAdvances, _needle?.BlurAmount);
        private T Read<T>(string field) => (T)(Control.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Native control inspection contract changed.")).GetValue(Control)!;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
