using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Wisp.App.NativeRendering;

namespace Wisp.App.Laps;

internal sealed class LapDeltaWindow : Window
{
    private readonly bool _map;
    private double BaseWidth => _map ? TrackMapHudLayer.Size : LapDeltaHudLayer.Width;
    private double BaseHeight => _map ? TrackMapHudLayer.Size : LapDeltaHudLayer.Height;
    private readonly NonActivatingWindowDrag _drag;
    private readonly FrameworkElement _view;
    private readonly Viewbox _root;
    private bool _enabled, _telemetryVisible, _editMode;
    private double _scale = 1, _opacity = 1;

    internal LapDeltaWindow(AppController controller, bool map = false)
    {
        _map = map;
        Title = map ? "Wisp Live Lap" : "Wisp Lap Delta";
        Width = BaseWidth; Height = BaseHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        DataContext = controller.ViewModel;
        _view = map ? new TrackMapView { Width = BaseWidth, Height = BaseHeight, Service = controller.LapDelta } :
            new LapDeltaView { Width = BaseWidth, Height = BaseHeight, Service = controller.LapDelta };
        _root = new Viewbox { Child = _view, Stretch = Stretch.Uniform, Opacity = 0 };
        Content = _root;
        HudNativeHost.Attach(this);
        _drag = new NonActivatingWindowDrag(this, map ? controller.SaveLapMapPlacement : controller.SaveLapDeltaPlacement);
        ApplyAppearance(map ? controller.Settings.LapMapScale : controller.Settings.LapDeltaScale, controller.Settings.OverlayOpacity);
    }

    internal void Configure(Wisp.Core.LapDeltaReference reference, bool bar, AppSettings? settings = null)
    {
        if (_view is LapDeltaView delta)
        {
            delta.Reference = reference;
            delta.ShowBar = bar;
            delta.AheadColor = settings?.LapDeltaAheadColor;
            delta.BehindColor = settings?.LapDeltaBehindColor;
        }
        else if (_view is TrackMapView map)
        {
            map.TrackColor = settings?.LapMapTrackColor;
            map.CarColor = settings?.LapMapCarColor;
            map.BackgroundColor = settings?.LapMapBackgroundColor;
        }
        _view.InvalidateArrange();
    }

    public void ApplyAppearance(double scale, double opacity)
    {
        _scale = double.IsFinite(scale) ? Math.Clamp(scale, .5, _map ? 3 : 2) : 1;
        FitSizeToArea(MonitorWorkArea(new WindowInteropHelper(this).Handle));
        _opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1;
        ApplyVisibility();
    }
    public void SetEnabled(bool enabled) { _enabled = enabled; ApplyVisibility(); }
    public void SetEditMode(bool edit)
    {
        _editMode = edit;
        Cursor = edit ? Cursors.SizeAll : Cursors.Arrow;
        ApplyVisibility();
    }
    public void SetTelemetryVisible(bool visible, double opacity, bool hideImmediately = false)
    {
        _telemetryVisible = visible;
        _opacity = Math.Clamp(opacity, 0, 1);
        ApplyVisibility();
    }
    private void ApplyVisibility()
    {
        _root.Opacity = _enabled && _telemetryVisible ? _opacity : 0;
        if (_enabled && _telemetryVisible && _opacity > 0) { if (!IsVisible) Show(); }
        else if (IsVisible) Hide();
        _drag.SetInteractive(_enabled && _telemetryVisible && _editMode);
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ClampToMonitor();
    }
    public bool OwnsWindowHandle(IntPtr handle) => handle != IntPtr.Zero && new WindowInteropHelper(this).Handle == handle;

    public void ResetPosition(IntPtr gameWindow = default)
    {
        var reference = gameWindow != IntPtr.Zero ? gameWindow : new WindowInteropHelper(this).Handle;
        var area = MonitorWorkArea(reference);
        FitSizeToArea(area);
        var point = DefaultPosition(area, new Size(Width, Height), _map);
        Left = point.X; Top = point.Y;
        _ = new WindowInteropHelper(this).EnsureHandle();
        // Creating or moving the HWND may change its DPI. Recalculate in the new coordinate space.
        area = MonitorWorkArea(reference != IntPtr.Zero ? reference : new WindowInteropHelper(this).Handle);
        FitSizeToArea(area);
        point = DefaultPosition(area, new Size(Width, Height), _map);
        Left = point.X; Top = point.Y;
    }

    internal static Point DefaultPosition(Rect area, Size size, bool map = false) => OverlayPlacementGeometry.ClampInside(area, size,
        new Point(map ? area.Right - size.Width - 32 : area.Left + (area.Width - size.Width) / 2, area.Top + 145));

    internal static Size FitSize(Rect area, double scale, bool map = false)
    {
        var BaseWidth = map ? TrackMapHudLayer.Size : LapDeltaHudLayer.Width;
        var BaseHeight = map ? TrackMapHudLayer.Size : LapDeltaHudLayer.Height;
        var requested = double.IsFinite(scale) ? Math.Clamp(scale, .5, map ? 3 : 2) : 1;
        var fit = Math.Min(requested, Math.Min(Math.Max(1, area.Width - 48) / BaseWidth, Math.Max(1, area.Height - 48) / BaseHeight));
        return new(BaseWidth * fit, BaseHeight * fit);
    }

    private void FitSizeToArea(Rect area)
    {
        var size = FitSize(area, _scale, _map);
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
            ? $"{info.Device}-{info.Monitor.Right - info.Monitor.Left}x{info.Monitor.Bottom - info.Monitor.Top}-{(_map ? "LapMapV1" : "LapDeltaV1")}"
            : $"Primary-{SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0}-{(_map ? "LapMapV1" : "LapDeltaV1")}";
    }

    private Rect MonitorWorkArea(IntPtr reference)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (reference == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(reference, 2), ref info)) return SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new(info.WorkArea.Left / dpi.DpiScaleX, info.WorkArea.Top / dpi.DpiScaleY,
            (info.WorkArea.Right - info.WorkArea.Left) / dpi.DpiScaleX, (info.WorkArea.Bottom - info.WorkArea.Top) / dpi.DpiScaleY);
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
}
