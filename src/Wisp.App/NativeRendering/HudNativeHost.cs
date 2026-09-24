using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wisp.App.NativeRendering;

// Exactly one target owns each HUD HWND. Layout and appearance stay on WPF's
// thread; immutable scene inputs are sampled by the existing native worker.
internal sealed class HudNativeHost : IDisposable
{
    private static readonly ConditionalWeakTable<Window, HudNativeHost> Hosts = new();
    private static readonly DependencyProperty PresentedProperty = DependencyProperty.RegisterAttached(
        "Presented", typeof(bool), typeof(HudNativeHost), new PropertyMetadata(false));
    private static readonly DependencyProperty WpfContentSuppressedProperty = DependencyProperty.RegisterAttached(
        "WpfContentSuppressed", typeof(bool), typeof(HudNativeHost), new PropertyMetadata(false));
    private readonly Window _window;
    private readonly INativeNeedleHistorySource? _nativeSource;
    private readonly List<Source> _sources = [];
    private DiagnosticsViewModel? _vm;
    private AnalogHudRenderWorker? _worker;
    private NativeCompositionWindow? _compositionWindow;
    private bool _interactive;
    private HudWindowSnapshot? _snapshot;
    private bool _scheduled, _capturing, _failed, _disposed;
    private int _nextId;

    private sealed class Source(int id, FrameworkElement control)
    {
        internal int Id { get; } = id;
        internal FrameworkElement Control { get; } = control;
        internal Brush? OriginalMask { get; set; }
        internal UIElement? HiddenContent { get; set; }
        internal Visibility OriginalVisibility { get; set; }
        internal DependencyPropertyDescriptor? InputDescriptor { get; set; }
        internal EventHandler? InputHandler { get; set; }
        internal float Opacity { get; set; } = -1;
        internal HudLayerPlacement? Last { get; set; }
    }

    internal static void Attach(Window window, INativeNeedleHistorySource? nativeSource = null) =>
        Hosts.GetValue(window, owner => new HudNativeHost(owner, nativeSource));
    internal static bool IsAttached(Window window) => Hosts.TryGetValue(window, out _);
    internal static bool IsPresented(DependencyObject control) => (bool)control.GetValue(PresentedProperty);
    internal static bool IsWpfContentSuppressed(DependencyObject control) => (bool)control.GetValue(WpfContentSuppressedProperty);
    internal static IntPtr PresentationHandle(Window window) =>
        Hosts.TryGetValue(window, out var host) ? host._compositionWindow?.Handle ?? IntPtr.Zero : IntPtr.Zero;
    internal static void SynchronizeWindow(Window window)
    {
        if (!Hosts.TryGetValue(window, out var host) || host._disposed || host._failed) return;
        try { host._compositionWindow?.Synchronize(host._snapshot?.Active == true); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { host.OnStatus(false, error.HResult); }
    }
    internal static void SetInteractive(Window window, bool interactive)
    {
        if (!Hosts.TryGetValue(window, out var host) || host._disposed || host._failed) return;
        host._interactive = interactive;
        try
        {
            // Unlocking enables the WPF drag surface and edit chrome. It must
            // not stop the native needle or change its playback/rendering path.
            host._compositionWindow?.SetLayoutCloaked(!interactive && host._snapshot?.Active == true &&
                host._sources.Any(source => IsPresented(source.Control)));
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { host.OnStatus(false, error.HResult); }
        host.RequestCapture();
    }

    private HudNativeHost(Window window, INativeNeedleHistorySource? nativeSource)
    {
        _window = window;
        _nativeSource = nativeSource;
        window.Loaded += OnLoaded;
        window.LayoutUpdated += OnLayoutUpdated;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.StateChanged += OnLayoutUpdated;
        window.Closed += OnClosed;
        window.DataContextChanged += OnDataContextChanged;
        BindViewModel();
    }

    private void BindViewModel()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnPropertyChanged;
        _vm = _window.DataContext as DiagnosticsViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnPropertyChanged;
    }
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    { BindViewModel(); RequestCapture(); }
    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args) => RequestCapture();
    private void OnLayoutUpdated(object? sender, EventArgs args) => RequestCapture();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => RequestCapture();
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_sources.Count == 0) Discover(_window);
        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendering += OnRendering;
        RequestCapture();
    }
    private void OnRendering(object? sender, EventArgs args)
    {
        // Opacity animations do not cause layout. This observes appearance only;
        // no needle, rail or trail is sampled from WPF's composition callback.
        if (_disposed || _failed) return;
        foreach (var source in _sources)
        {
            var opacity = EffectiveOpacity(source.Control);
            if (source.Opacity != opacity) { RequestCapture(); break; }
        }
    }
    private void Discover(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && IsGauge(element))
            {
                var item = new Source(++_nextId, element);
                var property = element switch
                {
                    NativeAnalogSpeedometer => NativeAnalogSpeedometer.FrameProperty,
                    NativeElectricAnalogSpeedometer => NativeElectricAnalogSpeedometer.FrameProperty,
                    NativeDigitalSpeedometer => NativeDigitalSpeedometer.FrameProperty,
                    NativeElectricDigitalSpeedometer => NativeElectricDigitalSpeedometer.FrameProperty,
                    _ => null
                };
                if (property is not null)
                {
                    item.InputDescriptor = DependencyPropertyDescriptor.FromProperty(property, element.GetType());
                    item.InputHandler = (_, _) => RequestCapture();
                    item.InputDescriptor?.AddValueChanged(element, item.InputHandler);
                }
                _sources.Add(item);
                SuppressWpfContent(item);
                if (element.Name != "CombinedPanel") continue;
            }
            Discover(child);
        }
    }
    private static bool IsGauge(FrameworkElement element) => element is
        NativeAnalogSpeedometer or NativeElectricAnalogSpeedometer or NativeDigitalSpeedometer or
        NativeElectricDigitalSpeedometer or PowerTorqueGaugeView or AnalogBoostGaugeView or
        DigitalBoostRailView or AnalogTireTemperatureGaugeView or DigitalTireTemperatureGaugeView or
        NativeGForceMeterView or GForceMeterView ||
        element.Name is "MinimalPanel" or "BoxedSpeedPanel" or "CombinedPanel";

    private void RequestCapture()
    {
        if (_disposed || _failed || _scheduled || _capturing || _window.Dispatcher.HasShutdownStarted) return;
        _scheduled = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _scheduled = false;
            if (_disposed || _failed) return;
            _capturing = true;
            try { Capture(); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            { OnStatus(false, error.HResult); }
            finally { _capturing = false; }
        }));
    }

    private void Capture()
    {
        if (!_window.IsLoaded || !_window.IsVisible || _window.WindowState == WindowState.Minimized)
        {
            Deactivate();
            return;
        }
        if (_sources.Count == 0) Discover(_window);
        var source = PresentationSource.FromVisual(_window) as HwndSource;
        if (source?.CompositionTarget is null || !GetClientRect(source.Handle, out var client))
        {
            Deactivate();
            return;
        }
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
        {
            Deactivate();
            return;
        }
        var layers = new List<HudLayerPlacement>(_sources.Count);
        // The authored root opacity applies to the composited HUD as a group,
        // not independently to overlapping pieces of artwork.
        var groupOpacity = (float)Math.Clamp(_window.Opacity *
            (_window.Content is UIElement root ? root.Opacity : 1), 0, 1);
        var visible = _window.IsVisible && _window.WindowState != WindowState.Minimized;
        foreach (var item in _sources)
        {
            var control = item.Control;
            item.Opacity = EffectiveOpacity(control);
            if (!visible || !control.IsVisible || item.Opacity <= 0 ||
                control.RenderSize.Width <= 0 || control.RenderSize.Height <= 0 ||
                (!control.IsArrangeValid && item.Last is null)) continue;
            var snapshot = CaptureLayer(control);
            if (snapshot is null) continue;
            if (!control.IsArrangeValid && item.Last is { } previous)
            {
                item.Last = previous with { Snapshot = snapshot, Opacity = groupOpacity > 0 ? item.Opacity / groupOpacity : 0 };
                layers.Add(item.Last);
                continue;
            }
            var transform = control.TransformToAncestor(_window);
            var dpi = source.CompositionTarget.TransformToDevice;
            var origin = dpi.Transform(transform.Transform(new Point()));
            var x = dpi.Transform(transform.Transform(new Point(1, 0))) - origin;
            var y = dpi.Transform(transform.Transform(new Point(0, 1))) - origin;
            item.Last = new(item.Id, snapshot, (float)origin.X, (float)origin.Y,
                (float)x.X, (float)x.Y, (float)y.X, (float)y.Y, groupOpacity > 0 ? item.Opacity / groupOpacity : 0);
            layers.Add(item.Last);
        }
        _snapshot = new(width, height, visible && layers.Count > 0, layers.ToArray(), _vm?.NativeGaugeFrame ?? default, groupOpacity);
        if (_snapshot.Active)
        {
            _compositionWindow ??= new NativeCompositionWindow(source, RequestCapture, error => OnStatus(false, error),
                fillMonitor: _window is OverlayWindow);
            _compositionWindow.Synchronize(true);
            _snapshot = _snapshot with { HostOffsetX = _compositionWindow.OffsetX, HostOffsetY = _compositionWindow.OffsetY };
        }
        else
        {
            _compositionWindow?.Synchronize(false);
            _compositionWindow?.SetLayoutCloaked(false);
        }
        if (_worker is null)
        {
            if (!_snapshot.Active) return;
            var presentation = new AnalogHudPresentation(width, height, 0, 0, 1, 0, 0, 1, 1,
                true, false, default, null, _snapshot);
            _worker = new AnalogHudRenderWorker(_compositionWindow!.Handle, DebugLogging.TachDiagnostics.NextControlId(),
                [], presentation, OnStatus, _vm?.ActiveCpuRendering == true, _nativeSource);
            _worker.HudPresented += OnPresented;
            if (_vm is not null) _vm.NativeRendererStatus = "HUD renderer: native starting";
        }
        _worker.UpdateHud(_snapshot, Stopwatch.GetTimestamp());
    }

    private void Deactivate()
    {
        _compositionWindow?.Synchronize(false);
        _compositionWindow?.SetLayoutCloaked(false);
        // Minimized windows can have an empty client rectangle. Stop the
        // existing scene before rejecting geometry needed only for drawing.
        if (_snapshot is not { Active: true }) return;
        _snapshot = _snapshot with { Active = false, Layers = [] };
        _worker?.UpdateHud(_snapshot, Stopwatch.GetTimestamp());
    }

    private HudLayerSnapshot? CaptureLayer(FrameworkElement control) => control switch
    {
        NativeAnalogSpeedometer analog => MainHudLayer.Capture(analog, _vm),
        NativeElectricAnalogSpeedometer electric => MainHudLayer.Capture(electric, _vm),
        NativeDigitalSpeedometer digital => MainHudLayer.Capture(digital, _vm),
        NativeElectricDigitalSpeedometer electricDigital => MainHudLayer.Capture(electricDigital, _vm),
        PowerTorqueGaugeView power => PowerTorqueHudLayer.Capture(power, _vm),
        AnalogBoostGaugeView or DigitalBoostRailView => BoostHudLayer.Capture(control, _vm),
        AnalogTireTemperatureGaugeView or DigitalTireTemperatureGaugeView => TireHudLayer.Capture(control, _vm),
        NativeGForceMeterView or GForceMeterView => GForceHudLayer.Capture(control, _vm),
        _ when control.Name is "MinimalPanel" or "BoxedSpeedPanel" or "CombinedPanel" => TextHudLayer.Capture(control, _vm),
        _ => null
    };

    private float EffectiveOpacity(FrameworkElement control)
    {
        double opacity = 1;
        for (DependencyObject? current = control; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element) opacity *= element.Opacity;
            if (ReferenceEquals(current, _window)) break;
        }
        return (float)Math.Clamp(opacity, 0, 1);
    }
    private void OnPresented(HudWindowSnapshot presented)
    {
        if (_window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || _failed || _snapshot is null || !presented.CompatibleWith(_snapshot)) return;
            try { _compositionWindow?.SetLayoutCloaked(!_interactive); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            { OnStatus(false, error.HResult); return; }
            var visibleIds = presented.Layers.Select(layer => layer.Id).ToHashSet();
            foreach (var source in _sources)
            {
                if (!visibleIds.Contains(source.Id) || IsPresented(source.Control)) continue;
                source.Control.SetValue(PresentedProperty, true);
            }
            if (_vm is not null) _vm.NativeRendererStatus = _vm.ActiveCpuRendering
                ? "HUD renderer: CPU (WARP) / DirectComposition" : "HUD renderer: Direct3D 11 / DirectComposition";
        }));
    }
    private void OnStatus(bool ready, int hresult)
    {
        if (_window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            // An occluded device may initialize without submitting. Only the
            // presentation callback may publish ready status, independently of
            // the WPF content suppression required from startup onward.
            if (ready) return;
            _failed = true;
            _worker?.Dispose();
            RetireCompositionWindow();
            foreach (var source in _sources) source.Control.SetValue(PresentedProperty, false);
            if (_vm is not null) _vm.NativeRendererStatus = $"HUD renderer: unavailable (0x{hresult:X8})";
        }));
    }
    private static void SuppressWpfContent(Source source)
    {
        if (IsWpfContentSuppressed(source.Control)) return;
        // Preserve the layout model and edit chrome while keeping live gauge
        // artwork exclusively on the native renderer, including during failure.
        source.Control.SetValue(WpfContentSuppressedProperty, true);
        if (source.Control is UserControl { Content: UIElement content })
        {
            source.HiddenContent = content;
            source.OriginalVisibility = content.Visibility;
            content.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
        }
        else
        {
            source.OriginalMask = source.Control.OpacityMask;
            source.Control.SetCurrentValue(UIElement.OpacityMaskProperty, Brushes.Transparent);
        }
    }
    private void RestoreWpfContent()
    {
        foreach (var source in _sources)
        {
            if (!IsWpfContentSuppressed(source.Control)) continue;
            source.Control.SetValue(PresentedProperty, false);
            source.Control.SetValue(WpfContentSuppressedProperty, false);
            if (source.HiddenContent is { } content) content.SetCurrentValue(UIElement.VisibilityProperty, source.OriginalVisibility);
            else source.Control.SetCurrentValue(UIElement.OpacityMaskProperty, source.OriginalMask);
            source.Control.InvalidateVisual();
        }
    }
    private void OnClosed(object? sender, EventArgs args) => Dispose();
    private void RetireCompositionWindow()
    {
        var target = _compositionWindow;
        if (target is null) return;
        _compositionWindow = null;
        // Release the DComp target on its worker before destroying its HWND.
        // Await asynchronously so that COM can continue to message the UI thread.
        target.Detach();
        var completion = _worker?.Completion ?? Task.CompletedTask;
        _ = completion.ContinueWith(_ =>
        {
            if (!_window.Dispatcher.HasShutdownStarted)
                _window.Dispatcher.BeginInvoke(new Action(target.Dispose));
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CompositionTarget.Rendering -= OnRendering;
        _window.Loaded -= OnLoaded;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.StateChanged -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _window.DataContextChanged -= OnDataContextChanged;
        if (_vm is not null) _vm.PropertyChanged -= OnPropertyChanged;
        if (_worker is not null) { _worker.HudPresented -= OnPresented; _worker.Dispose(); }
        RetireCompositionWindow();
        RestoreWpfContent();
        foreach (var source in _sources)
            if (source.InputHandler is not null) source.InputDescriptor?.RemoveValueChanged(source.Control, source.InputHandler);
        Hosts.Remove(_window);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out ClientRect rectangle);
}
