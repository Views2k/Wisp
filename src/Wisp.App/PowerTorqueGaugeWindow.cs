using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Wisp.App;

public sealed class PowerTorqueGaugeWindow : Window
{
    private const uint DefaultToNearestMonitor = 2;
    private const double BaseSize = PowerTorqueGaugeLayout.GaugeDiameter + 8;
    private readonly NonActivatingWindowDrag _windowDrag;
    private readonly AppController _controller;
    private readonly Viewbox _rootViewbox;
    private bool _enabled;
    private bool _telemetryVisible;

    public PowerTorqueGaugeWindow(AppController controller, bool isTorque)
    {
        _controller = controller;
        IsTorque = isTorque;
        Title = isTorque ? "Wisp Torque" : "Wisp Power";
        Width = Height = BaseSize;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        Topmost = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        DataContext = controller.ViewModel;
        NativeRendering.HudNativeHost.Attach(this);

        var gauge = new PowerTorqueGaugeView
        {
            Width = PowerTorqueGaugeLayout.GaugeDiameter,
            Height = PowerTorqueGaugeLayout.GaugeDiameter,
            Margin = new Thickness(4),
            IsTorque = isTorque
        };
        gauge.SetBinding(PowerTorqueGaugeView.DisplayProperty, new Binding(nameof(DiagnosticsViewModel.PowerTorqueDisplay)));
        gauge.SetBinding(PowerTorqueGaugeView.IsElectricMaterialProperty, new Binding("NativeGaugeFrame.IsElectric"));
        gauge.SetBinding(PowerTorqueGaugeView.TorqueUnitProperty, new Binding(nameof(DiagnosticsViewModel.SelectedTorqueUnit)));
        gauge.SetBinding(PowerTorqueGaugeView.MaximumProperty,
            new Binding(isTorque ? nameof(DiagnosticsViewModel.TorqueGaugeMaximum) : nameof(DiagnosticsViewModel.PowerGaugeMaximum)));
        var prefix = isTorque ? "TorqueGauge" : "PowerGauge";
        gauge.SetBinding(PowerTorqueGaugeView.LowBrushProperty, new Binding(prefix + "LowBrush"));
        gauge.SetBinding(PowerTorqueGaugeView.MidBrushProperty, new Binding(prefix + "MidBrush"));
        gauge.SetBinding(PowerTorqueGaugeView.HighBrushProperty, new Binding(prefix + "HighBrush"));
        gauge.SetBinding(PowerTorqueGaugeView.ColorNumberProperty, new Binding(prefix + "ColorNumber"));
        gauge.SetResourceReference(PowerTorqueGaugeView.AccentBrushProperty, "AccentBrush");
        var panel = new Grid { Width = BaseSize, Height = BaseSize, Background = Brushes.Transparent };
        panel.Children.Add(gauge);
        _rootViewbox = new Viewbox { Stretch = Stretch.Uniform, Opacity = 0, Child = panel };
        Content = _rootViewbox;
        _windowDrag = new NonActivatingWindowDrag(this,
            isTorque ? controller.SaveTorqueGaugePlacement : controller.SavePowerGaugePlacement);
    }

    public bool IsTorque { get; }
    internal string PlacementSuffix => IsTorque ? "-TorqueV1" : "-PowerV1";

    public void ApplyAppearance(double scale, double opacity)
    {
        scale = double.IsFinite(scale) ? Math.Clamp(scale, .5, 2) : 1;
        Width = Height = BaseSize * scale;
        _rootViewbox.Opacity = _telemetryVisible && _enabled ? opacity : 0;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        ApplyVisibility();
    }

    public void SetEditMode(bool editMode)
    {
        Cursor = editMode ? Cursors.SizeAll : Cursors.Arrow;
        _windowDrag.SetInteractive(editMode);
    }

    public void SetTelemetryVisible(bool visible, double opacity, bool hideImmediately = false)
    {
        _telemetryVisible = visible;
        _rootViewbox.Opacity = visible && _enabled ? opacity : 0;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        if (_enabled && _telemetryVisible)
        {
            if (!IsVisible) Show();
        }
        else if (IsVisible) Hide();
    }

    public void ResetPosition(Rect anchorBounds, Rect workArea)
    {
        var position = DetachedSupplementaryGaugeLayout.Place(workArea, anchorBounds,
            new Size(Width, Height), _controller.DetachedSupplementaryGaugeCellSize, IsTorque ? 3 : 2);
        Left = position.X;
        Top = position.Y;
    }

    public void RestorePosition(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top)) return;
        _ = new WindowInteropHelper(this).EnsureHandle();
        Left = left;
        Top = top;
        var position = OverlayPlacementGeometry.ClampInside(CurrentMonitorWorkArea(),
            new Size(Width, Height), new Point(left, top));
        Left = position.X;
        Top = position.Y;
    }

    public bool OwnsWindowHandle(IntPtr handle) =>
        handle != IntPtr.Zero && new WindowInteropHelper(this).Handle == handle;

    public string GetDisplayKey()
    {
        var fallback = $"Primary-{SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0}{PlacementSuffix}";
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return fallback;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromWindow(handle, DefaultToNearestMonitor), ref info)
            ? $"{info.Device}-{info.Monitor.Right - info.Monitor.Left}x{info.Monitor.Bottom - info.Monitor.Top}{PlacementSuffix}"
            : fallback;
    }

    private Rect CurrentMonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, DefaultToNearestMonitor), ref info)) return SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(info.WorkArea.Left / dpi.DpiScaleX, info.WorkArea.Top / dpi.DpiScaleY,
            (info.WorkArea.Right - info.WorkArea.Left) / dpi.DpiScaleX,
            (info.WorkArea.Bottom - info.WorkArea.Top) / dpi.DpiScaleY);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
}
