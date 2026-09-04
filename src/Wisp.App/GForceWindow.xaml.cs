using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Wisp.App;

public partial class GForceWindow : Window
{
    private const uint DefaultToNearestMonitor = 2;
    private const double BaseWidth = 210;
    private const double BaseHeight = 150;
    private const double NativeBaseWidth = 144;
    private const double NativeBaseHeight = 100;

    private readonly AppController _controller;
    private readonly NonActivatingWindowDrag _windowDrag;
    private bool _editMode;
    private bool _enabled;
    private bool _telemetryVisible;
    private double _configuredOpacity = 1.0;
    private double _targetOpacity = double.NaN;
    private int _visibilityRevision;

    public GForceWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
        HudBorderThemeResources.Apply(
            Resources,
            controller.Settings.HudBorderTheme,
            controller.Settings.CustomHudBorderColor);
        _windowDrag = new NonActivatingWindowDrag(this, controller.SaveGForcePlacement);
        DataContext = controller.ViewModel;
        ApplyAppearance(
            controller.Settings.GForceWidthScale,
            controller.Settings.GForceHeightScale,
            controller.Settings.OverlayOpacity);
    }

    public void ApplyHudBorderTheme(string? themeName) =>
        HudBorderThemeResources.Apply(Resources, themeName);

    public void ApplyHudBorderCustomization(string? themeName, string? customColor) =>
        HudBorderThemeResources.Apply(Resources, themeName, customColor);

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _windowDrag.SetInteractive(_editMode && enabled);
        SetTelemetryVisible(_telemetryVisible, _configuredOpacity, hideImmediately: !enabled);
    }

    public void SetEditMode(bool editMode)
    {
        _editMode = editMode;
        EditChrome.Visibility = editMode ? Visibility.Visible : Visibility.Collapsed;
        Cursor = editMode ? Cursors.SizeAll : Cursors.Arrow;
        _windowDrag.SetInteractive(editMode && _enabled);
        SetTelemetryVisible(_telemetryVisible, _configuredOpacity);
    }

    public void SetTelemetryVisible(bool visible, double configuredOpacity, bool hideImmediately = false)
    {
        _telemetryVisible = visible;
        _configuredOpacity = configuredOpacity;
        var target = _enabled && visible ? configuredOpacity : 0;
        if (target <= 0 && hideImmediately)
        {
            _visibilityRevision++;
            _targetOpacity = 0;
            RootViewbox.BeginAnimation(OpacityProperty, null);
            RootViewbox.Opacity = 0;
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (target <= 0 && !IsVisible)
        {
            _targetOpacity = 0;
            RootViewbox.Opacity = 0;
            return;
        }

        if (target > 0 && !IsVisible)
        {
            RootViewbox.BeginAnimation(OpacityProperty, null);
            RootViewbox.Opacity = 0;
            Show();
            _windowDrag.SetInteractive(_editMode && _enabled);
            _targetOpacity = double.NaN;
        }

        if (double.IsFinite(_targetOpacity) && Math.Abs(target - _targetOpacity) < 0.001)
        {
            return;
        }

        _targetOpacity = target;
        var revision = ++_visibilityRevision;
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(target > 0 ? 90 : 120),
            EasingFunction = new QuadraticEase()
        };
        if (target <= 0)
        {
            animation.Completed += (_, _) =>
            {
                if (revision == _visibilityRevision && _targetOpacity <= 0 && IsVisible)
                {
                    Hide();
                }
            };
        }

        RootViewbox.BeginAnimation(OpacityProperty, animation);
    }

    public void ApplyAppearance(double widthScale, double heightScale, double opacity)
    {
        var native = _controller.Settings.LayoutMode == HudLayoutMode.Native;
        GForcePanelBorder.Visibility = native ? Visibility.Collapsed : Visibility.Visible;
        NativeGForceMeter.Visibility = native ? Visibility.Visible : Visibility.Collapsed;
        var baseWidth = native ? NativeBaseWidth : BaseWidth;
        var baseHeight = native ? NativeBaseHeight : BaseHeight;
        RootPanel.Width = baseWidth;
        RootPanel.Height = baseHeight;
        Width = baseWidth * widthScale;
        Height = baseHeight * heightScale;
        SetTelemetryVisible(_telemetryVisible, opacity);
    }

    public void ResetPosition()
    {
        var workArea = CurrentMonitorWorkArea();
        var position = OverlayPlacementGeometry.PlaceTopRight(
            workArea,
            new Size(Width, Height));
        Left = position.X;
        Top = position.Y;
    }

    public void ResetPositionAdjacentTo(Rect anchorBounds, Rect anchorWorkArea)
    {
        var position = OverlayPlacementGeometry.PlaceAdjacentHorizontally(
            anchorWorkArea,
            anchorBounds,
            new Size(Width, Height));
        Left = position.X;
        Top = position.Y;
    }

    public void ResetPositionBelow(Rect anchorBounds, Rect anchorWorkArea)
    {
        var position = OverlayPlacementGeometry.PlaceBelow(
            anchorWorkArea,
            anchorBounds,
            new Size(Width, Height));
        Left = position.X;
        Top = position.Y;
    }

    public void ResetPositionAbove(Rect anchorBounds, Rect anchorWorkArea)
    {
        var position = OverlayPlacementGeometry.PlaceAbove(
            anchorWorkArea,
            anchorBounds,
            new Size(Width, Height));
        Left = position.X;
        Top = position.Y;
    }

    public void RestorePosition(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
        {
            ResetPosition();
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

    public bool OwnsWindowHandle(IntPtr windowHandle)
    {
        return windowHandle != IntPtr.Zero &&
               new WindowInteropHelper(this).Handle == windowHandle;
    }

    public string GetDisplayKey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return $"Primary-{SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0}-GForceV2";
        }

        var monitor = MonitorFromWindow(handle, DefaultToNearestMonitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info)
            ? $"{info.Device}-{info.Monitor.Right - info.Monitor.Left}x{info.Monitor.Bottom - info.Monitor.Top}-GForceV2"
            : $"Primary-{SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0}-GForceV2";
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
