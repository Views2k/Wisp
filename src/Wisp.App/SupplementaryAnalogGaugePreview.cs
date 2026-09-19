using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Wisp.App;

public sealed class SupplementaryAnalogGaugePreview : Grid
{
    private readonly AnalogBoostGaugeView _boost = new();
    private readonly AnalogTireTemperatureGaugeView _tire = new();
    private readonly PowerTorqueGaugeView _power = new();
    private readonly PowerTorqueGaugeView _torque = new() { IsTorque = true };
    private DiagnosticsViewModel? _subscribedModel;
    private bool _lastElectric;

    public SupplementaryAnalogGaugePreview()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
        foreach (var gauge in new FrameworkElement[] { _boost, _tire, _power, _torque })
        {
            gauge.Width = gauge.Height = PowerTorqueGaugeLayout.GaugeDiameter;
            gauge.HorizontalAlignment = HorizontalAlignment.Left;
            gauge.VerticalAlignment = VerticalAlignment.Top;
            Children.Add(gauge);
        }
        Bind(_boost, BoostVisualBase.DisplayProperty, nameof(DiagnosticsViewModel.PreviewBoostDisplay));
        Bind(_boost, BoostVisualBase.PressureUnitProperty, nameof(DiagnosticsViewModel.SelectedBoostPressureUnit));
        Bind(_boost, BoostVisualBase.ColorNumberProperty, nameof(DiagnosticsViewModel.BoostGaugeColorNumber));
        Bind(_boost, AnalogBoostGaugeView.IsElectricMaterialProperty, "NativePreviewFrame.IsElectric");
        _boost.SetResourceReference(BoostVisualBase.LowBrushProperty, "BoostLowBrush");
        _boost.SetResourceReference(BoostVisualBase.MidBrushProperty, "BoostMidBrush");
        _boost.SetResourceReference(BoostVisualBase.HighBrushProperty, "BoostHighBrush");
        Bind(_tire, TireTemperatureVisualBase.DisplayProperty, nameof(DiagnosticsViewModel.PreviewTireTemperatureDisplay));
        Bind(_tire, TireTemperatureVisualBase.TemperatureUnitProperty, nameof(DiagnosticsViewModel.SelectedTireTemperatureUnit));
        Bind(_tire, TireTemperatureVisualBase.ReactiveColorsProperty, nameof(DiagnosticsViewModel.TireTemperatureReactiveColors));
        Bind(_tire, TireTemperatureVisualBase.IsAttachedProperty, nameof(DiagnosticsViewModel.TireTemperatureGaugeAttached));
        Bind(_tire, AnalogTireTemperatureGaugeView.IsElectricMaterialProperty, "NativePreviewFrame.IsElectric");
        _tire.SetResourceReference(TireTemperatureVisualBase.LowBrushProperty, "BoostLowBrush");
        _tire.SetResourceReference(TireTemperatureVisualBase.MidBrushProperty, "BoostMidBrush");
        _tire.SetResourceReference(TireTemperatureVisualBase.HighBrushProperty, "BoostHighBrush");
        foreach (var gauge in new[] { _power, _torque })
        {
            Bind(gauge, PowerTorqueGaugeView.DisplayProperty, nameof(DiagnosticsViewModel.PreviewPowerTorqueDisplay));
            Bind(gauge, PowerTorqueGaugeView.IsElectricMaterialProperty, "NativePreviewFrame.IsElectric");
            Bind(gauge, PowerTorqueGaugeView.TorqueUnitProperty, nameof(DiagnosticsViewModel.SelectedTorqueUnit));
            var prefix = gauge.IsTorque ? "TorqueGauge" : "PowerGauge";
            Bind(gauge, PowerTorqueGaugeView.MaximumProperty, "Preview" + prefix + "Maximum");
            Bind(gauge, PowerTorqueGaugeView.LowBrushProperty, prefix + "LowBrush");
            Bind(gauge, PowerTorqueGaugeView.MidBrushProperty, prefix + "MidBrush");
            Bind(gauge, PowerTorqueGaugeView.HighBrushProperty, prefix + "HighBrush");
            Bind(gauge, PowerTorqueGaugeView.ColorNumberProperty, prefix + "ColorNumber");
            gauge.SetResourceReference(PowerTorqueGaugeView.AccentBrushProperty, "AccentBrush");
        }
        DataContextChanged += (_, _) => { SubscribeToModel(); RefreshLayout(); };
        Loaded += (_, _) => { SubscribeToModel(); RefreshLayout(); };
        Unloaded += (_, _) => UnsubscribeFromModel();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RefreshLayout();
    }

    private static void Bind(FrameworkElement target, DependencyProperty property, string path) =>
        target.SetBinding(property, new Binding(path));

    private void SubscribeToModel()
    {
        UnsubscribeFromModel();
        if (!IsLoaded || DataContext is not DiagnosticsViewModel model) return;
        _subscribedModel = model;
        model.PropertyChanged += OnModelPropertyChanged;
    }

    private void UnsubscribeFromModel()
    {
        if (_subscribedModel is not null) _subscribedModel.PropertyChanged -= OnModelPropertyChanged;
        _subscribedModel = null;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiagnosticsViewModel.NativePreviewFrame) &&
            DataContext is DiagnosticsViewModel model && model.NativePreviewFrame.IsElectric == _lastElectric) return;
        if (e.PropertyName is nameof(DiagnosticsViewModel.BoostGaugeScale) or nameof(DiagnosticsViewModel.TireTemperatureGaugeScale)
            or nameof(DiagnosticsViewModel.PowerGaugeScale) or nameof(DiagnosticsViewModel.TorqueGaugeScale)
            or nameof(DiagnosticsViewModel.BoostGaugeEnabled) or nameof(DiagnosticsViewModel.TireTemperatureGaugeEnabled)
            or nameof(DiagnosticsViewModel.PowerGaugeEnabled) or nameof(DiagnosticsViewModel.TorqueGaugeEnabled)
            or nameof(DiagnosticsViewModel.GForceGaugeScale)
            or nameof(DiagnosticsViewModel.NativePreviewFrame)) RefreshLayout();
    }

    private void RefreshLayout()
    {
        if (DataContext is not DiagnosticsViewModel model) return;
        _lastElectric = model.NativePreviewFrame.IsElectric;
        var nativeTop = GForceGaugeLayout.NativeTopPadding(model.GForceGaugeScale);
        // Detached gauges still need to be visible while editing their appearance.
        var tire = model.TireTemperatureGaugeEnabled;
        var power = model.PowerGaugeEnabled;
        var torque = model.TorqueGaugeEnabled;
        var layout = _lastElectric
            ? ElectricSupplementaryGaugeLayout.Calculate(
                new Size(Math.Max(345, GForceGaugeLayout.NativeBounds(NativeGaugeMode.Analogue, model.GForceGaugeScale).Right), nativeTop + 345),
                nativeTop, tire, power, torque,
                model.TireTemperatureGaugeScale, model.PowerGaugeScale, model.TorqueGaugeScale,
                model.GForceGaugeScale, VisualTreeHelper.GetDpi(this).DpiScaleX)
            : AnalogSupplementaryGaugeLayout.Calculate(new Size(), nativeTop + 4,
                model.BoostGaugeEnabled, tire, power, torque,
                model.BoostGaugeScale, model.TireTemperatureGaugeScale,
                model.PowerGaugeScale, model.TorqueGaugeScale);
        if (Width == layout.Size.Width && Height == layout.Size.Height &&
            _lastLayout == layout) return;
        _lastLayout = layout;
        Width = layout.Size.Width;
        Height = layout.Size.Height;
        Apply(_boost, layout.BoostBounds);
        Apply(_tire, layout.TireBounds);
        Apply(_power, layout.PowerBounds);
        Apply(_torque, layout.TorqueBounds);
    }

    private AnalogSupplementaryGaugeLayout? _lastLayout;

    private static void Apply(FrameworkElement gauge, Rect bounds)
    {
        gauge.Visibility = bounds.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (bounds.IsEmpty) return;
        var scale = bounds.Width / PowerTorqueGaugeLayout.GaugeDiameter;
        gauge.LayoutTransform = new ScaleTransform(scale, scale);
        gauge.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);
    }
}
