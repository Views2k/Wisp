using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Wisp.App.Drift;

public sealed class DriftGaugeWindow : Window, IDisposable
{
    internal const double BaseWidth = 620;
    internal const double BaseHeight = 108;
    private readonly AppController _controller;
    private readonly NonActivatingWindowDrag _drag;
    private DriftGaugeRenderWorker? _worker;
    private Task _rendererRelease = Task.CompletedTask;
    private bool _waitingForRendererRelease;
    private bool _enabled;
    private bool _telemetryVisible;
    private bool _editMode;
    private bool _disposed;
    private bool _failed;
    private double _opacity = 1;
    private double _scale = 1;
    private int _rendererRevision;
    internal bool RendererFailed => _failed;

    public DriftGaugeWindow(AppController controller)
    {
        _controller = controller;
        Title = "Wisp Drift Angle";
        Width = BaseWidth; Height = BaseHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        Topmost = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        // Dragging uses the HWND hook. Keep the artwork unframed even while unlocked.
        Content = new Grid { Background = Brushes.Transparent, IsHitTestVisible = false };
        _drag = new NonActivatingWindowDrag(this, controller.SaveDriftGaugePlacement);
        SizeChanged += (_, _) => RefreshPresentation();
        IsVisibleChanged += (_, _) => RefreshPresentation();
        Closed += (_, _) => Dispose();
        ApplyAppearance(controller.Settings.DriftGaugeScale, controller.Settings.OverlayOpacity);
    }

    public void ApplyAppearance(double scale, double opacity)
    {
        var normalized = double.IsFinite(scale) ? Math.Clamp(scale, .5, 2) : 1;
        _scale = normalized;
        var center = double.IsFinite(Left) ? Left + Width / 2 : double.NaN;
        FitSizeToArea(MonitorWorkArea(new WindowInteropHelper(this).Handle));
        if (double.IsFinite(center)) Left = center - Width / 2;
        _opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1;
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero) ClampToMonitor();
        RefreshPresentation();
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled) return;
        _enabled = enabled;
        if (!enabled)
        {
            if (IsVisible) Hide();
            DisposeRenderer();
            _failed = false;
        }
        _drag.SetInteractive(_enabled && _editMode && _telemetryVisible);
        ApplyVisibility();
    }

    public void SetEditMode(bool editMode)
    {
        _editMode = editMode;
        Cursor = editMode ? Cursors.SizeAll : Cursors.Arrow;
        _drag.SetInteractive(editMode && _enabled && _telemetryVisible);
    }

    public void SetTelemetryVisible(bool visible, double opacity, bool hideImmediately = false)
    {
        if (_disposed) return;
        _telemetryVisible = visible;
        _opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        if (_disposed) return;
        if (_enabled && _telemetryVisible && !_failed && _opacity > 0)
        {
            if (!IsVisible) Show();
        }
        else if (IsVisible) Hide();
        _drag.SetInteractive(_enabled && _editMode && _telemetryVisible && !_failed);
        RefreshPresentation();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RefreshPresentation();
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ClampToMonitor();
        RefreshPresentation();
    }

    private void RefreshPresentation()
    {
        if (_disposed || _failed) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetClientRect(handle, out var client)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var active = _enabled && _telemetryVisible && IsVisible && _opacity > 0 && WindowState != WindowState.Minimized;
        var presentation = new DriftGaugePresentation(Math.Max(1, client.Right - client.Left), Math.Max(1, client.Bottom - client.Top),
            (float)dpi.DpiScaleX, (float)dpi.DpiScaleY, (float)_opacity, active,
            _controller.Settings.DriftTargetDegrees, _controller.Settings.DriftToleranceDegrees, _controller.Settings.DriftGaugeDarkMode,
            _controller.Settings.DriftGaugeGuidanceMode, BackgroundEnabled: _controller.Settings.DriftGaugeBackgroundEnabled,
            BackgroundOpacity: _controller.Settings.DriftGaugeBackgroundOpacity);
        try
        {
            if (_worker is null && active)
            {
                if (!_rendererRelease.IsCompleted)
                {
                    _ = QueueRendererRefreshAfterRelease();
                    return;
                }
                var revision = ++_rendererRevision;
                _worker = new DriftGaugeRenderWorker(handle, () => _controller.LatestDriftTelemetry, presentation,
                    (ready, code) => ReportRendererStatus(revision, ready, code), _controller.ViewModel.ActiveCpuRendering,
                    () => _controller.CurrentDriftZoneProfile);
            }
            else _worker?.UpdatePresentation(presentation);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            _failed = true;
            _controller.UpdateDriftGaugeStatus(false, error.HResult);
            if (IsVisible) Hide();
        }
    }

    private void ReportRendererStatus(int revision, bool ready, int code)
    {
        PostToDispatcher(() =>
        {
            if (_disposed || !_enabled || revision != _rendererRevision) return;
            if (!ready) { _failed = true; if (IsVisible) Hide(); }
            _controller.UpdateDriftGaugeStatus(ready, code);
        });
    }

    public bool OwnsWindowHandle(IntPtr handle) => handle != IntPtr.Zero && new WindowInteropHelper(this).Handle == handle;

    public void ResetPosition(IntPtr gameWindow = default)
    {
        var reference = gameWindow != IntPtr.Zero ? gameWindow : new WindowInteropHelper(this).Handle;
        var area = MonitorWorkArea(reference);
        FitSizeToArea(area);
        var point = DefaultPosition(area, new Size(Width, Height));
        Left = point.X; Top = point.Y;
        _ = new WindowInteropHelper(this).EnsureHandle();
        // Creating or moving the HWND may change its DPI. Recalculate in the new coordinate space.
        area = MonitorWorkArea(reference != IntPtr.Zero ? reference : new WindowInteropHelper(this).Handle);
        FitSizeToArea(area);
        point = DefaultPosition(area, new Size(Width, Height));
        Left = point.X; Top = point.Y;
    }

    internal static Point DefaultPosition(Rect area, Size size) => OverlayPlacementGeometry.ClampInside(area, size,
        new Point(area.Left + (area.Width - size.Width) / 2, area.Top + 24));

    internal static Size FitSize(Rect area, double scale)
    {
        var requested = double.IsFinite(scale) ? Math.Clamp(scale, .5, 2) : 1;
        var fit = Math.Min(requested, Math.Min(Math.Max(1, area.Width - 48) / BaseWidth, Math.Max(1, area.Height - 48) / BaseHeight));
        return new(BaseWidth * fit, BaseHeight * fit);
    }

    private void FitSizeToArea(Rect area)
    {
        var size = FitSize(area, _scale);
        Width = size.Width; Height = size.Height;
    }

    public void RestorePosition(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top)) { ResetPosition(); return; }
        Left = left; Top = top;
        _ = new WindowInteropHelper(this).EnsureHandle();
        ClampToMonitor();
    }

    internal void ClampToMonitor()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top)) return;
        var area = MonitorWorkArea(new WindowInteropHelper(this).Handle);
        FitSizeToArea(area);
        var point = OverlayPlacementGeometry.ClampInside(area,
            new Size(Width, Height), new Point(Left, Top));
        Left = point.X; Top = point.Y;
    }

    public string GetDisplayKey(IntPtr reference = default)
    {
        if (reference == IntPtr.Zero) reference = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return reference != IntPtr.Zero && GetMonitorInfo(MonitorFromWindow(reference, 2), ref info)
            ? $"{info.Device}-{info.Monitor.Right - info.Monitor.Left}x{info.Monitor.Bottom - info.Monitor.Top}-DriftV1"
            : $"Primary-{SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0}-DriftV1";
    }

    private Rect MonitorWorkArea(IntPtr reference)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (reference == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(reference, 2), ref info)) return SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new(info.WorkArea.Left / dpi.DpiScaleX, info.WorkArea.Top / dpi.DpiScaleY,
            (info.WorkArea.Right - info.WorkArea.Left) / dpi.DpiScaleX, (info.WorkArea.Bottom - info.WorkArea.Top) / dpi.DpiScaleY);
    }

    private void DisposeRenderer()
    {
        _rendererRevision++;
        if (_worker is { } worker)
        {
            worker.Dispose();
            _rendererRelease = worker.Completion;
        }
        _worker = null;
    }

    private async Task QueueRendererRefreshAfterRelease()
    {
        if (_waitingForRendererRelease) return;
        _waitingForRendererRelease = true;
        await _rendererRelease.ConfigureAwait(false);
        PostToDispatcher(() =>
        {
            _waitingForRendererRelease = false;
            if (!_disposed) RefreshPresentation();
        });
    }

    private void PostToDispatcher(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try { _ = Dispatcher.BeginInvoke(action); }
        catch (InvalidOperationException) { /* The dispatcher can stop between the check and enqueue. */ }
        catch (TaskCanceledException) { /* Shutdown can cancel a queued renderer notification. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeRenderer();
        _drag.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr handle, out NativeRectangle rectangle);
}
