using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Wisp.App;

public partial class OverlayWindow : Window
{
    private const uint DefaultToNearestMonitor = 2;
    private const double MinimalWidth = 160;
    private const double MinimalHeight = 120;
    private const double BoxedWidth = 190;
    private const double BoxedHeight = 150;
    private const double CombinedWidth = 390;
    private const double CombinedHeight = 166;
    // FH6's 302-wide non-gauge layer extends 7.5px beyond its nominal
    // 320px template. Keep that authored overflow inside the transparent HWND.
    private const double NativeDigitalWidth = 327.5;
    private const double NativeDigitalHeight = 160;
    private const double NativeDigitalBoostHeight = 220;
    private const double NativeDigitalTireHeight = 220;
    private const double NativeDigitalBoostTireHeight = 258;
    // The native 288x288 template authors a +5 right / -5.5 bottom external
    // margin. Keep that overflow inside the transparent HWND instead of
    // clipping the final redline segment and 9/10 labels.
    private const double NativeAnalogWidth = 293;
    private const double NativeAnalogHeight = 293.5;
    private const double NativeAnalogBoostWidth = 416;
    private const double NativeElectricDigitalWidth = 327.5;
    private const double NativeElectricDigitalHeight = 160;
    private const double NativeElectricAnalogWidth = 345;
    private const double NativeElectricAnalogHeight = 345;
    private const double AttachedGForceTopPadding = 72;
    private double _nativeTopPadding = AttachedGForceTopPadding;
    private double _gForceScale = 1;

    private readonly AppController _controller;
    private readonly NonActivatingWindowDrag _windowDrag;
    private bool _editMode;
    private bool _telemetryVisible;
    private double _targetOpacity = double.NaN;
    private HudLayoutMode _layoutMode;
    private NativeGaugeMode _nativeGaugeMode;
    private bool _isElectricPowertrain;
    private bool _attachedBoostVisible;
    private bool _attachedTireTemperatureVisible;
    private bool _attachedGForceVisible;
    private bool _combinedGForceVisible;
    private bool _attachedPowerVisible;
    private bool _attachedTorqueVisible;
    private double _powerScale;
    private double _torqueScale;
    private double _boostScale;
    private double _tireScale;
    private int _visibilityRevision;

    public OverlayWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
        HudBorderThemeResources.Apply(
            Resources,
            controller.Settings.HudBorderTheme,
            controller.Settings.CustomHudBorderColor);
        BoostGaugeThemeResources.Apply(
            Resources,
            controller.Settings.BoostGaugeTheme,
            controller.Settings.CustomBoostLowColor,
            controller.Settings.CustomBoostMidColor,
            controller.Settings.CustomBoostHighColor);
        TractionCueThemeResources.Apply(Resources, ColorCustomization.ResolveTractionCue(controller.Settings));
        _windowDrag = new NonActivatingWindowDrag(this, controller.SaveOverlayPlacement);
        DataContext = controller.ViewModel;
        NativeRendering.HudNativeHost.Attach(this);
        controller.ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => controller.ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ApplyLayout(
            controller.Settings.LayoutMode,
            controller.Settings.NativeGaugeMode,
            controller.Settings.OverlayWidthScale,
            controller.Settings.OverlayHeightScale,
            controller.Settings.OverlayOpacity);
    }

    public void ApplyHudBorderTheme(string? themeName) =>
        HudBorderThemeResources.Apply(Resources, themeName);

    public void ApplyHudBorderCustomization(string? themeName, string? customColor) =>
        HudBorderThemeResources.Apply(Resources, themeName, customColor);

    public void ApplyBoostGaugeTheme(string? themeName) =>
        BoostGaugeThemeResources.Apply(Resources, themeName);

    public void ApplyBoostGaugeCustomization(
        string? themeName,
        string? low,
        string? mid,
        string? high) =>
        BoostGaugeThemeResources.Apply(Resources, themeName, low, mid, high);

    public void ApplyTractionCueCustomization(Color color) =>
        TractionCueThemeResources.Apply(Resources, color);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiagnosticsViewModel.BoostDisplay) or
            nameof(DiagnosticsViewModel.BoostGaugeEnabled) or
            nameof(DiagnosticsViewModel.BoostGaugeAttached) or
            nameof(DiagnosticsViewModel.TireTemperatureDisplay) or
            nameof(DiagnosticsViewModel.TireTemperatureGaugeEnabled) or
            nameof(DiagnosticsViewModel.TireTemperatureGaugeAttached) or
            nameof(DiagnosticsViewModel.PowerTorqueDisplay) or
            nameof(DiagnosticsViewModel.PowerGaugeEnabled) or
            nameof(DiagnosticsViewModel.PowerGaugeAttached) or
            nameof(DiagnosticsViewModel.TorqueGaugeEnabled) or
            nameof(DiagnosticsViewModel.TorqueGaugeAttached) or
            nameof(DiagnosticsViewModel.PowerGaugeScale) or
            nameof(DiagnosticsViewModel.TorqueGaugeScale) or
            nameof(DiagnosticsViewModel.BoostGaugeScale) or
            nameof(DiagnosticsViewModel.TireTemperatureGaugeScale) or
            nameof(DiagnosticsViewModel.GForceEnabled) or
            nameof(DiagnosticsViewModel.GForceAttached) or
            nameof(DiagnosticsViewModel.GForceGaugeScale))
        {
            var boostAvailable = !_isElectricPowertrain &&
                                 _controller.ViewModel.BoostGaugeEnabled &&
                                 _controller.ViewModel.BoostDisplay.IsAvailable;
            var shouldShow = _layoutMode == HudLayoutMode.Native && boostAvailable &&
                             (_nativeGaugeMode == NativeGaugeMode.Digital ||
                              _controller.ViewModel.BoostGaugeAttached);
            var shouldShowTireTemperature = _layoutMode == HudLayoutMode.Native &&
                                             _controller.ViewModel.TireTemperatureGaugeEnabled &&
                                             _controller.ViewModel.TireTemperatureGaugeAttached &&
                                             _controller.ViewModel.TireTemperatureDisplay.IsAvailable;
            var shouldShowGForce = _layoutMode == HudLayoutMode.Native &&
                                   _controller.ViewModel.GForceEnabled &&
                                   _controller.ViewModel.GForceAttached;
            var showPower = _controller.ViewModel.PowerGaugeEnabled &&
                            _controller.ViewModel.PowerGaugeAttached &&
                            _controller.ViewModel.PowerTorqueDisplay.Available;
            var showTorque = _controller.ViewModel.TorqueGaugeEnabled &&
                             _controller.ViewModel.TorqueGaugeAttached &&
                             _controller.ViewModel.PowerTorqueDisplay.Available;
            if (shouldShow == _attachedBoostVisible &&
                shouldShowTireTemperature == _attachedTireTemperatureVisible &&
                shouldShowGForce == _attachedGForceVisible &&
                showPower == _attachedPowerVisible && showTorque == _attachedTorqueVisible &&
                _powerScale == _controller.ViewModel.PowerGaugeScale &&
                _torqueScale == _controller.ViewModel.TorqueGaugeScale &&
                _boostScale == _controller.ViewModel.BoostGaugeScale &&
                _tireScale == _controller.ViewModel.TireTemperatureGaugeScale &&
                _gForceScale == _controller.ViewModel.GForceGaugeScale &&
                (_layoutMode != HudLayoutMode.Combined || _combinedGForceVisible == _controller.ViewModel.GForceEnabled))
            {
                return;
            }
            var left = Left;
            var top = Top;
            var previousAnchor = _layoutMode == HudLayoutMode.Native ? NativePlacementAnchorBounds() : Rect.Empty;
            ApplyLayout(_layoutMode, _nativeGaugeMode,
                _controller.Settings.OverlayWidthScale,
                _controller.Settings.OverlayHeightScale,
                _controller.Settings.OverlayOpacity);
            var position = new Point(left, top);
            if (!previousAnchor.IsEmpty)
                position = OverlayPlacementGeometry.PreserveAnchorPosition(position, previousAnchor, NativePlacementAnchorBounds());
            RestorePosition(position.X, position.Y);
        }
    }

    public void SetEditMode(bool editMode)
    {
        _editMode = editMode;
        EditChrome.Visibility = editMode && _layoutMode != HudLayoutMode.Minimal
            ? Visibility.Visible
            : Visibility.Collapsed;
        Cursor = editMode ? Cursors.SizeAll : Cursors.Arrow;
        _windowDrag.SetInteractive(editMode);
        SetTelemetryVisible(_telemetryVisible, _controller.Settings.OverlayOpacity);
    }

    public void SetTelemetryVisible(bool visible, double configuredOpacity, bool hideImmediately = false)
    {
        _telemetryVisible = visible;
        var target = visible ? configuredOpacity : 0;
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
            _windowDrag.SetInteractive(_editMode);
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
        ApplyLayout(_layoutMode, _nativeGaugeMode, widthScale, heightScale, opacity);
    }

    public void SetElectricPowertrain(bool isElectric)
    {
        if (_isElectricPowertrain == isElectric)
        {
            return;
        }

        var previousAnchor = _layoutMode == HudLayoutMode.Native
            ? NativePlacementAnchorBounds()
            : new Rect(new Size(Width, Height));
        var position = new Point(Left, Top);
        var right = Left + previousAnchor.Right;
        var bottom = Top + previousAnchor.Bottom;
        var preserveBottomRight = double.IsFinite(right) && double.IsFinite(bottom);
        _isElectricPowertrain = isElectric;
        ApplyLayout(
            _layoutMode,
            _nativeGaugeMode,
            _controller.Settings.OverlayWidthScale,
            _controller.Settings.OverlayHeightScale,
            _controller.Settings.OverlayOpacity);

        if (preserveBottomRight)
        {
            var nextAnchor = _layoutMode == HudLayoutMode.Native
                ? NativePlacementAnchorBounds()
                : new Rect(new Size(Width, Height));
            var preservedPosition = OverlayPlacementGeometry.PreserveAnchorPosition(
                position, previousAnchor, nextAnchor);
            RestorePosition(preservedPosition.X, preservedPosition.Y);
        }
    }

    public void ApplyLayout(
        HudLayoutMode layoutMode,
        NativeGaugeMode nativeGaugeMode,
        double widthScale,
        double heightScale,
        double opacity)
    {
        _layoutMode = layoutMode;
        _nativeGaugeMode = nativeGaugeMode;
        _gForceScale = GForceGaugeLayout.NormalizeScale(_controller.ViewModel.GForceGaugeScale);
        _nativeTopPadding = GForceGaugeLayout.NativeTopPadding(_gForceScale);
        var combinedSize = GForceGaugeLayout.CombinedSize(_gForceScale);
        CombinedPanel.Width = combinedSize.Width;
        CombinedPanel.Height = combinedSize.Height;
        CombinedGForceMeter.LayoutTransform = new ScaleTransform(_gForceScale, _gForceScale);
        MinimalPanel.Visibility = layoutMode == HudLayoutMode.Minimal ? Visibility.Visible : Visibility.Collapsed;
        _combinedGForceVisible = layoutMode == HudLayoutMode.Combined && _controller.ViewModel.GForceEnabled;
        CombinedPanel.Visibility = _combinedGForceVisible ? Visibility.Visible : Visibility.Collapsed;
        BoxedSpeedPanel.Visibility = layoutMode == HudLayoutMode.SeparateBoxes || layoutMode == HudLayoutMode.Combined && !_combinedGForceVisible
            ? Visibility.Visible : Visibility.Collapsed;
        NativeDigitalPanel.Visibility = layoutMode == HudLayoutMode.Native &&
                                        nativeGaugeMode == NativeGaugeMode.Digital &&
                                        !_isElectricPowertrain
            ? Visibility.Visible
            : Visibility.Collapsed;
        NativeAnalogPanel.Visibility = layoutMode == HudLayoutMode.Native &&
                                       nativeGaugeMode == NativeGaugeMode.Analogue &&
                                       !_isElectricPowertrain
            ? Visibility.Visible
            : Visibility.Collapsed;
        NativeElectricDigitalPanel.Visibility = layoutMode == HudLayoutMode.Native &&
                                                nativeGaugeMode == NativeGaugeMode.Digital &&
                                                _isElectricPowertrain
            ? Visibility.Visible
            : Visibility.Collapsed;
        NativeElectricAnalogPanel.Visibility = layoutMode == HudLayoutMode.Native &&
                                               nativeGaugeMode == NativeGaugeMode.Analogue &&
                                               _isElectricPowertrain
            ? Visibility.Visible
            : Visibility.Collapsed;

        var boostAvailable = !_isElectricPowertrain &&
                             _controller.ViewModel.BoostGaugeEnabled &&
                             _controller.ViewModel.BoostDisplay.IsAvailable;
        var digitalBoostVisible = layoutMode == HudLayoutMode.Native &&
                                  boostAvailable && nativeGaugeMode == NativeGaugeMode.Digital;
        var analogBoostVisible = layoutMode == HudLayoutMode.Native &&
                                 boostAvailable && nativeGaugeMode == NativeGaugeMode.Analogue &&
                                 _controller.ViewModel.BoostGaugeAttached;
        var tireTemperatureAvailable = _controller.ViewModel.TireTemperatureGaugeEnabled &&
                                       _controller.ViewModel.TireTemperatureGaugeAttached &&
                                       _controller.ViewModel.TireTemperatureDisplay.IsAvailable;
        var digitalTireTemperatureVisible = layoutMode == HudLayoutMode.Native &&
                                            tireTemperatureAvailable &&
                                            nativeGaugeMode == NativeGaugeMode.Digital;
        var analogTireTemperatureVisible = layoutMode == HudLayoutMode.Native &&
                                           tireTemperatureAvailable &&
                                           nativeGaugeMode == NativeGaugeMode.Analogue;
        var attachedGForceVisible = layoutMode == HudLayoutMode.Native &&
                                    _controller.ViewModel.GForceEnabled &&
                                    _controller.ViewModel.GForceAttached;
        // Keep the HWND and native surface stationary when this meter is toggled.
        // Moving the window first briefly moves its previously presented pixels too.
        var nativeTop = layoutMode == HudLayoutMode.Native ? _nativeTopPadding : 0;
        AttachedDigitalBoost.Visibility = digitalBoostVisible ? Visibility.Visible : Visibility.Collapsed;
        AttachedAnalogBoost.Visibility = analogBoostVisible ? Visibility.Visible : Visibility.Collapsed;
        AttachedDigitalTireTemperature.Visibility = digitalTireTemperatureVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        AttachedAnalogTireTemperature.Visibility = analogTireTemperatureVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        NativeDigitalPanel.Margin = new Thickness(0, nativeTop, 0, 0);
        NativeAnalogPanel.Margin = new Thickness(0, nativeTop, 0, 0);
        NativeElectricDigitalPanel.Margin = new Thickness(0, nativeTop, 0, 0);
        NativeElectricAnalogPanel.Margin = new Thickness(0, nativeTop, 0, 0);
        AttachedDigitalBoost.Margin = new Thickness(11, 132 + nativeTop, 0, 0);
        AttachedAnalogBoost.Margin = new Thickness(276, 4 + nativeTop, 0, 0);
        AttachedDigitalTireTemperature.Margin = new Thickness(
            11,
            (digitalBoostVisible ? 170 : 132) + nativeTop,
            0,
            0);
        AttachedAnalogTireTemperature.Margin = new Thickness(
            PowerTorqueGaugeLayout.NativeSatelliteLeft,
            (analogBoostVisible ? 142 : 4) + nativeTop,
            0,
            0);
        AttachedNativeGForce.Visibility = attachedGForceVisible ? Visibility.Visible : Visibility.Collapsed;
        var gForceBounds = _isElectricPowertrain && nativeGaugeMode == NativeGaugeMode.Analogue
            ? ElectricSupplementaryGaugeLayout.GForceBounds(_gForceScale)
            : GForceGaugeLayout.NativeBounds(nativeGaugeMode, _gForceScale);
        AttachedNativeGForce.Margin = new Thickness(gForceBounds.Left, gForceBounds.Top, 0, 0);
        AttachedNativeGForce.LayoutTransform = new ScaleTransform(_gForceScale, _gForceScale);
        _attachedBoostVisible = digitalBoostVisible || analogBoostVisible;
        _attachedTireTemperatureVisible = digitalTireTemperatureVisible || analogTireTemperatureVisible;
        _attachedGForceVisible = attachedGForceVisible;

        var (baseWidth, baseHeight) = layoutMode switch
        {
            HudLayoutMode.Combined => _combinedGForceVisible ? (combinedSize.Width, combinedSize.Height) : (BoxedWidth, BoxedHeight),
            HudLayoutMode.SeparateBoxes => (BoxedWidth, BoxedHeight),
            HudLayoutMode.Native when _isElectricPowertrain && nativeGaugeMode == NativeGaugeMode.Analogue =>
                (NativeElectricAnalogWidth, NativeElectricAnalogHeight),
            HudLayoutMode.Native when _isElectricPowertrain =>
                (NativeElectricDigitalWidth,
                    digitalTireTemperatureVisible ? NativeDigitalTireHeight : NativeElectricDigitalHeight),
            HudLayoutMode.Native when nativeGaugeMode == NativeGaugeMode.Analogue =>
                (analogBoostVisible || analogTireTemperatureVisible ? NativeAnalogBoostWidth : NativeAnalogWidth,
                    NativeAnalogHeight),
            HudLayoutMode.Native => (NativeDigitalWidth,
                digitalTireTemperatureVisible
                    ? digitalBoostVisible ? NativeDigitalBoostTireHeight : NativeDigitalTireHeight
                    : digitalBoostVisible ? NativeDigitalBoostHeight : NativeDigitalHeight),
            _ => (MinimalWidth, MinimalHeight)
        };
        baseHeight += nativeTop;
        if (layoutMode == HudLayoutMode.Native)
            baseWidth = Math.Max(baseWidth, gForceBounds.Right);
        _attachedPowerVisible = _controller.ViewModel.PowerGaugeEnabled &&
                                _controller.ViewModel.PowerGaugeAttached &&
                                _controller.ViewModel.PowerTorqueDisplay.Available;
        _attachedTorqueVisible = _controller.ViewModel.TorqueGaugeEnabled &&
                                 _controller.ViewModel.TorqueGaugeAttached &&
                                 _controller.ViewModel.PowerTorqueDisplay.Available;
        _powerScale = _controller.ViewModel.PowerGaugeScale;
        _torqueScale = _controller.ViewModel.TorqueGaugeScale;
        _boostScale = _controller.ViewModel.BoostGaugeScale;
        _tireScale = _controller.ViewModel.TireTemperatureGaugeScale;
        Size size;
        if (layoutMode == HudLayoutMode.Native && nativeGaugeMode == NativeGaugeMode.Analogue)
        {
            var gauges = _isElectricPowertrain
                ? ElectricSupplementaryGaugeLayout.Calculate(
                    new Size(baseWidth, baseHeight), nativeTop,
                    analogTireTemperatureVisible, _attachedPowerVisible, _attachedTorqueVisible,
                    _tireScale, _powerScale, _torqueScale, _gForceScale,
                    VisualTreeHelper.GetDpi(NativeElectricAnalogPanel).DpiScaleX)
                : AnalogSupplementaryGaugeLayout.Calculate(
                    new Size(baseWidth, baseHeight), nativeTop + 4,
                    analogBoostVisible, analogTireTemperatureVisible,
                    _attachedPowerVisible, _attachedTorqueVisible,
                    _boostScale, _tireScale, _powerScale, _torqueScale);
            ApplyAnalogGaugeBounds(AttachedAnalogBoost, gauges.BoostBounds);
            ApplyAnalogGaugeBounds(AttachedAnalogTireTemperature, gauges.TireBounds);
            ApplyPowerTorqueBounds(AttachedPowerGauge, gauges.PowerBounds);
            ApplyPowerTorqueBounds(AttachedTorqueGauge, gauges.TorqueBounds);
            size = gauges.Size;
        }
        else
        {
            if (digitalTireTemperatureVisible)
            {
                AttachedDigitalTireTemperature.LayoutTransform = new ScaleTransform(_tireScale, _tireScale);
                baseWidth = Math.Max(baseWidth,
                    AttachedDigitalTireTemperature.Margin.Left + AttachedDigitalTireTemperature.Width * _tireScale + 4);
                baseHeight = Math.Max(baseHeight,
                    AttachedDigitalTireTemperature.Margin.Top + AttachedDigitalTireTemperature.Height * _tireScale + 4);
            }
            var gauges = PowerTorqueGaugeLayout.Calculate(
                new Size(baseWidth, baseHeight), nativeTop + 4,
                _attachedPowerVisible, _attachedTorqueVisible, _powerScale, _torqueScale);
            ApplyPowerTorqueBounds(AttachedPowerGauge, gauges.PowerBounds);
            ApplyPowerTorqueBounds(AttachedTorqueGauge, gauges.TorqueBounds);
            size = gauges.Size;
        }
        RootPanel.Width = size.Width;
        RootPanel.Height = size.Height;
        Width = size.Width * widthScale;
        Height = size.Height * heightScale;
        EditChrome.Visibility = _editMode && layoutMode != HudLayoutMode.Minimal
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetTelemetryVisible(_telemetryVisible, opacity);
    }

    private static void ApplyPowerTorqueBounds(FrameworkElement gauge, Rect bounds)
    {
        gauge.Visibility = bounds.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (bounds.IsEmpty) return;
        gauge.Width = bounds.Width;
        gauge.Height = bounds.Height;
        gauge.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);
    }

    private static void ApplyAnalogGaugeBounds(FrameworkElement gauge, Rect bounds)
    {
        gauge.Visibility = bounds.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (bounds.IsEmpty) return;
        var scale = bounds.Width / PowerTorqueGaugeLayout.GaugeDiameter;
        gauge.LayoutTransform = new ScaleTransform(scale, scale);
        gauge.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);
    }

    public void ResetPosition()
    {
        var referenceScale = _layoutMode == HudLayoutMode.Native ? CurrentNativeReferenceScale() : 1;
        var placementArea = CurrentMonitorPlacementArea();
        var dpi = VisualTreeHelper.GetDpi(this);
        var position = _layoutMode == HudLayoutMode.Native
            ? OverlayPlacementGeometry.PlaceNativeBottomRight(
                placementArea,
                new Size(Width, Height),
                NativePlacementAnchorBounds(),
                referenceScale,
                dpi.DpiScaleX,
                dpi.DpiScaleY)
            : OverlayPlacementGeometry.PlaceTopRight(placementArea, new Size(Width, Height));
        Left = position.X;
        Top = position.Y;
    }

    private Rect NativePlacementAnchorBounds()
    {
        var scaleY = Height / RootPanel.Height;
        var anchor = OverlayPlacementGeometry.NativeContentAnchorBounds(
            _nativeGaugeMode,
            _isElectricPowertrain,
            Width / RootPanel.Width,
            scaleY);
        if (_layoutMode == HudLayoutMode.Native)
        {
            anchor.Offset(0, _nativeTopPadding * scaleY);
        }

        return anchor;
    }

    // Saved placements and detached-gauge defaults retain their existing bounds:
    // the reserved transparent inset counts only while the attached meter is visible.
    public Rect GetPlacementBounds()
    {
        var inset = HiddenGForceInset();
        return new Rect(Left, Top + inset, Width, Height - inset);
    }

    public void RestorePlacementPosition(double left, double top) =>
        RestorePosition(left, top - HiddenGForceInset());

    private double HiddenGForceInset() => _layoutMode == HudLayoutMode.Native && !_attachedGForceVisible
        ? _nativeTopPadding * Height / RootPanel.Height
        : 0;

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
        var placementArea = CurrentMonitorPlacementArea();
        var size = new Size(Width, Height);
        var requested = new Point(left, top);
        var position = _layoutMode == HudLayoutMode.Native
            ? OverlayPlacementGeometry.ClampNativeInside(placementArea, size, requested)
            : OverlayPlacementGeometry.ClampInside(placementArea, size, requested);
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
            return $"Primary-{SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0}-SpeedV4-{LayoutKey()}";
        }

        var monitor = MonitorFromWindow(handle, DefaultToNearestMonitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return $"Primary-{SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0}-SpeedV4-{LayoutKey()}";
        }

        return $"{info.Device}-{info.Monitor.Right - info.Monitor.Left}x{info.Monitor.Bottom - info.Monitor.Top}-SpeedV4-{LayoutKey()}";
    }

    private string LayoutKey() => _layoutMode == HudLayoutMode.Native
        ? $"Native-{_nativeGaugeMode}"
        : _layoutMode.ToString();

    public Rect CurrentMonitorWorkArea()
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

    public Rect CurrentMonitorPlacementArea() =>
        _layoutMode == HudLayoutMode.Native ? CurrentMonitorBounds() : CurrentMonitorWorkArea();

    private Rect CurrentMonitorBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return new Rect(
                0,
                0,
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight);
        }

        var monitor = MonitorFromWindow(handle, DefaultToNearestMonitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return new Rect(
                0,
                0,
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight);
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            info.Monitor.Left / dpi.DpiScaleX,
            info.Monitor.Top / dpi.DpiScaleY,
            (info.Monitor.Right - info.Monitor.Left) / dpi.DpiScaleX,
            (info.Monitor.Bottom - info.Monitor.Top) / dpi.DpiScaleY);
    }

    public double CurrentNativeReferenceScale()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var monitor = MonitorFromWindow(handle, DefaultToNearestMonitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return 1;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return OverlayPlacementGeometry.NativeReferenceScale(
            info.Monitor.Bottom - info.Monitor.Top,
            dpi.DpiScaleY);
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
