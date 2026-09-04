using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Wisp.App;

public partial class BoostGaugeWindow : Window
{
    private const uint DefaultToNearestMonitor = 2;
    private const double BaseSize = 148;
    private readonly NonActivatingWindowDrag _windowDrag;
    private bool _enabled;
    private bool _telemetryVisible;

    public BoostGaugeWindow(AppController controller)
    {
        InitializeComponent();
        DataContext = controller.ViewModel;
        BoostGaugeThemeResources.Apply(
            Resources,
            controller.Settings.BoostGaugeTheme,
            controller.Settings.CustomBoostLowColor,
            controller.Settings.CustomBoostMidColor,
            controller.Settings.CustomBoostHighColor);
        _windowDrag = new NonActivatingWindowDrag(this, controller.SaveBoostGaugePlacement);
    }

    public void ApplyBoostGaugeTheme(string? name) => BoostGaugeThemeResources.Apply(Resources, name);

    public void ApplyBoostGaugeCustomization(string? name, string? low, string? mid, string? high) =>
        BoostGaugeThemeResources.Apply(Resources, name, low, mid, high);

    public void ApplyAppearance(double scale, double opacity)
    {
        var normalizedScale = double.IsFinite(scale) ? Math.Clamp(scale, 0.5, 2.0) : 1.0;
        Width = BaseSize * normalizedScale;
        Height = BaseSize * normalizedScale;
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
        RootViewbox.Opacity = visible && _enabled ? opacity : 0;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        if (_enabled && _telemetryVisible)
        {
            if (!IsVisible) Show();
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
        if (!double.IsFinite(left) || !double.IsFinite(top))
        {
            return;
        }

        _ = new WindowInteropHelper(this).EnsureHandle();
        Left = left;
        Top = top;
        var position = OverlayPlacementGeometry.ClampInside(
            CurrentMonitorWorkArea(),
            new Size(Width, Height),
            new Point(left, top));
        Left = position.X;
        Top = position.Y;
    }

    public bool OwnsWindowHandle(IntPtr handle) => handle != IntPtr.Zero && new WindowInteropHelper(this).Handle == handle;

    public string GetDisplayKey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return $"Primary-{SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0}-BoostV1";
        var monitor = MonitorFromWindow(handle, DefaultToNearestMonitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info)
            ? $"{info.Device}-{info.Monitor.Right - info.Monitor.Left}x{info.Monitor.Bottom - info.Monitor.Top}-BoostV1"
            : $"Primary-{SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0}-BoostV1";
    }

    private Rect CurrentMonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        var monitor = MonitorFromWindow(handle, DefaultToNearestMonitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
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
