using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Wisp.App;

public partial class TireTemperatureGaugeWindow : Window
{
    private const uint DefaultToNearestMonitor = 2;
    private const double DigitalWidth = 310;
    private const double DigitalHeight = 92;
    private const double AnalogSize = 126;
    private readonly NonActivatingWindowDrag _windowDrag;
    private NativeGaugeMode _gaugeMode;
    private double _scale = 1;
    private double _configuredOpacity = 1;
    private bool _enabled;
    private bool _telemetryVisible;

    public TireTemperatureGaugeWindow(AppController controller)
    {
        InitializeComponent();
        DataContext = controller.ViewModel;
        BoostGaugeThemeResources.Apply(Resources, controller.Settings.BoostGaugeTheme);
        _windowDrag = new NonActivatingWindowDrag(this, controller.SaveTireTemperatureGaugePlacement);
        ApplyGaugeMode(controller.Settings.NativeGaugeMode);
    }

    public void ApplyBoostGaugeTheme(string? name) => BoostGaugeThemeResources.Apply(Resources, name);

    public void ApplyGaugeMode(NativeGaugeMode mode)
    {
        _gaugeMode = mode;
        var digital = mode == NativeGaugeMode.Digital;
        DigitalGauge.Visibility = digital ? Visibility.Visible : Visibility.Collapsed;
        AnalogGauge.Visibility = digital ? Visibility.Collapsed : Visibility.Visible;
        RootPanel.Width = digital ? DigitalWidth : AnalogSize;
        RootPanel.Height = digital ? DigitalHeight : AnalogSize;
        Width = RootPanel.Width * _scale;
        Height = RootPanel.Height * _scale;
    }

    public void ApplyAppearance(double scale, double opacity)
    {
        _scale = double.IsFinite(scale) ? Math.Clamp(scale, 0.5, 2.0) : 1.0;
        _configuredOpacity = opacity;
        Width = RootPanel.Width * _scale;
        Height = RootPanel.Height * _scale;
        RootViewbox.Opacity = _telemetryVisible && _enabled ? opacity : 0;
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
        _configuredOpacity = opacity;
        RootViewbox.Opacity = visible && _enabled ? opacity : 0;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        if (_enabled && _telemetryVisible)
        {
            RootViewbox.Opacity = _configuredOpacity;
            if (!IsVisible)
            {
                Show();
            }
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    public void ResetPosition(Rect anchorBounds, Rect workArea)
    {
        var position = OverlayPlacementGeometry.PlaceAbove(workArea, anchorBounds, new Size(Width, Height));
        Left = position.X;
        Top = position.Y;
    }

    public void RestorePosition(double left, double top)
    {
        var position = OverlayPlacementGeometry.ClampInside(
            CurrentMonitorWorkArea(),
            new Size(Width, Height),
            new Point(left, top));
        Left = position.X;
        Top = position.Y;
    }

    public bool OwnsWindowHandle(IntPtr handle) =>
        handle != IntPtr.Zero && new WindowInteropHelper(this).Handle == handle;

    public string GetDisplayKey()
    {
        var suffix = $"TireTempV1-{_gaugeMode}";
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return $"Primary-{SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0}-{suffix}";
        }

        var monitor = MonitorFromWindow(handle, DefaultToNearestMonitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info)
            ? $"{info.Device}-{info.Monitor.Right - info.Monitor.Left}x{info.Monitor.Bottom - info.Monitor.Top}-{suffix}"
            : $"Primary-{SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0}-{suffix}";
    }

    private Rect CurrentMonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }

        var monitor = MonitorFromWindow(handle, DefaultToNearestMonitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return SystemParameters.WorkArea;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            info.WorkArea.Left / dpi.DpiScaleX,
            info.WorkArea.Top / dpi.DpiScaleY,
            (info.WorkArea.Right - info.WorkArea.Left) / dpi.DpiScaleX,
            (info.WorkArea.Bottom - info.WorkArea.Top) / dpi.DpiScaleY);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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
