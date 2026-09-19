using System.Windows.Media;
using System.Windows;
using System.Runtime.CompilerServices;

namespace Wisp.App.NativeRendering;

internal static class MainHudLayer
{
    private sealed class LayoutCache
    {
        internal object? Key;
        internal object? Layout;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, LayoutCache> Layouts = new();
    private static T Layout<T>(FrameworkElement control, NativeGaugeFrame frame, Func<T> capture) where T : class
    {
        var cached = Layouts.GetValue(control, static _ => new LayoutCache());
        var a = frame.NativeAssists;
        var key = (control.RenderSize, VisualTreeHelper.GetDpi(control), control.UseLayoutRounding, frame.Unit,
            a.Available, a.IsSTMAvailable, a.IsABSAvailable, a.IsLCAvailable, a.IsTCRAvailable,
            NativeElectricGearModel.IsMultiGear(frame.ElectricGearState), frame.GearDisplayMode,
            NativeElectricGearModel.CurrentToken(frame.ElectricGearState, NativeGaugeMode.Analogue, frame.Gear) is not null,
            NativeElectricGearModel.CurrentToken(frame.ElectricGearState, NativeGaugeMode.Digital, frame.Gear) is not null,
            NativeElectricGearModel.AdjacentToken(frame.ElectricGearState, false) is not null,
            NativeElectricGearModel.AdjacentToken(frame.ElectricGearState, true) is not null, frame.SpeedAvailable,
            NativeElectricPowerGaugeModel.TryNativeDisplay(frame.NativeRegenFillAmount, frame.NativePowerFillAmount, frame.NativeRegenPowerRatio, out _));
        if ((cached.Layout is not T || !Equals(cached.Key, key)) && control.IsArrangeValid)
        { cached.Layout = capture(); cached.Key = key; }
        return cached.Layout as T ?? capture();
    }
    internal static HudLayerSnapshot Capture(NativeAnalogSpeedometer control, DiagnosticsViewModel? vm)
    {
        var frame = control.Frame;
        return new AnalogSnapshot(frame, Layout(control, frame, () => AnalogHudLayout.Capture(control)),
            vm?.IsTractionCueActive == true, Color(control));
    }
    internal static HudLayerSnapshot Capture(NativeElectricAnalogSpeedometer control, DiagnosticsViewModel? vm)
    {
        var frame = control.Frame;
        return new ElectricSnapshot(frame, Layout(control, frame, () => ElectricHudLayout.Capture(control)),
            vm?.IsTractionCueActive == true, Color(control));
    }
    internal static HudLayerSnapshot Capture(NativeDigitalSpeedometer control, DiagnosticsViewModel? vm)
    {
        var frame = control.Frame;
        return new DigitalSnapshot(frame, Layout(control, frame, () => DigitalHudLayout.Capture(control)),
            vm?.IsTractionCueActive == true, Color(control));
    }
    internal static HudLayerSnapshot Capture(NativeElectricDigitalSpeedometer control, DiagnosticsViewModel? vm)
    {
        var frame = control.Frame;
        return new DigitalSnapshot(frame, Layout(control, frame, () => DigitalHudLayout.Capture(control)),
            vm?.IsTractionCueActive == true, Color(control));
    }

    private sealed class DigitalSnapshot(NativeGaugeFrame frame, DigitalHudLayout layout,
        bool traction, AnalogHudColor color) : HudLayerSnapshot
    {
        internal NativeGaugeFrame Frame { get; } = frame;
        internal DigitalHudLayout Layout { get; } = layout;
        internal bool Traction { get; } = traction;
        internal AnalogHudColor Color { get; } = color;
        internal override IReadOnlyList<AnalogHudTexture> Textures { get; } = DigitalHudAssets.LoadOnUiThread();
        internal override HudLayerPlayback CreatePlayback() => new DigitalPlayback();
        internal override object CompatibilityKey => (Frame.CarOrdinal, Frame.Unit, Frame.GearDisplayMode,
            Frame.NativeGaugeSourceInvalidated, Layout, Traction, Color);
    }
    private sealed class DigitalPlayback : HudLayerPlayback
    {
        private readonly DigitalHudPlayback _playback = new();
        private DigitalSnapshot? _snapshot;
        internal override void Update(HudLayerSnapshot snapshot, long timestamp)
        {
            _snapshot = (DigitalSnapshot)snapshot;
            _playback.ObserveQueued(_snapshot.Frame, timestamp, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        internal override DirectCompositionDrawCommand[] Build(long timestamp) => _snapshot is null ? [] :
            DigitalHudScene.Build(_playback.Sample(timestamp), _snapshot.Traction, _snapshot.Color, _snapshot.Layout);
    }
    private static AnalogHudColor Color(System.Windows.FrameworkElement control)
    {
        var c = (control.TryFindResource(TractionCueThemeResources.BrushKey) as SolidColorBrush)?.Color ?? Colors.White;
        return new(c.R, c.G, c.B, c.A);
    }
    private sealed class AnalogSnapshot(NativeGaugeFrame frame, AnalogHudLayout layout,
        bool traction, AnalogHudColor color) : HudLayerSnapshot
    {
        internal NativeGaugeFrame Frame { get; } = frame;
        internal AnalogHudLayout Layout { get; } = layout;
        internal bool Traction { get; } = traction;
        internal AnalogHudColor Color { get; } = color;
        internal override IReadOnlyList<AnalogHudTexture> Textures { get; } = AnalogHudAssets.LoadOnUiThread();
        internal override HudLayerPlayback CreatePlayback() => new AnalogPlayback();
        internal override object CompatibilityKey => (Frame.CarOrdinal, Frame.Unit, Frame.ExactRedline,
            Frame.TachometerMaximumRpm, Frame.GearDisplayMode, Frame.NativeGaugeSourceInvalidated, Layout, Traction, Color);
    }
    private sealed class AnalogPlayback : HudLayerPlayback
    {
        private readonly AnalogHudPlayback _playback = new();
        private AnalogHudSample _sample;
        internal override (string Kind, AnalogHudSample Sample)? NeedleDiagnostic => ("analogue", _sample);
        private bool _builtNative;
        internal override bool CanReuse(long timestamp) => _builtNative == _playback.HasNativeNeedle(timestamp);
        private AnalogSnapshot? _snapshot;
        internal override void Update(HudLayerSnapshot snapshot, long timestamp)
        {
            _snapshot = (AnalogSnapshot)snapshot;
            _playback.ObserveQueued(_snapshot.Frame, timestamp, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        internal override DirectCompositionDrawCommand[] Build(long timestamp)
        {
            if (_snapshot is null) return [];
            var sample = _playback.Sample(timestamp);
            _builtNative = sample.Native;
            _sample = sample;
            return AnalogHudScene.Build(sample.Frame, sample.Angle, sample.Blur, sample.NeedleVisible,
                _snapshot.Traction, _snapshot.Color, _snapshot.Layout, sample.AppliedRpm);
        }
    }
    private sealed class ElectricSnapshot(NativeGaugeFrame frame, ElectricHudLayout layout,
        bool traction, AnalogHudColor color) : HudLayerSnapshot
    {
        internal NativeGaugeFrame Frame { get; } = frame;
        internal ElectricHudLayout Layout { get; } = layout;
        internal bool Traction { get; } = traction;
        internal AnalogHudColor Color { get; } = color;
        internal override IReadOnlyList<AnalogHudTexture> Textures { get; } = ElectricHudAssets.LoadOnUiThread();
        internal override HudLayerPlayback CreatePlayback() => new ElectricPlayback();
        internal override object CompatibilityKey => (Frame.CarOrdinal, Frame.Unit, Frame.GearDisplayMode,
            Frame.NativeGaugeSourceInvalidated, Layout, Traction, Color);
    }
    private sealed class ElectricPlayback : HudLayerPlayback
    {
        private readonly ElectricHudPlayback _playback = new();
        private ElectricHudSample _sample;
        internal override (string Kind, AnalogHudSample Sample)? NeedleDiagnostic => ("electric_analogue",
            new(_sample.Frame, _sample.Angle, _sample.Blur, _sample.NeedleVisible, _sample.Native, _sample.Timestamp,
                null, _sample.PlaybackDelayMilliseconds, _sample.PlaybackTargetDelayMilliseconds, _sample.BufferedSamples,
                _sample.PlaybackAtNewest, _sample.ReseedCount, _sample.StarvationReseedCount));
        private bool _builtNative;
        internal override bool CanReuse(long timestamp) => _builtNative == _playback.HasNativeNeedle(timestamp);
        private ElectricSnapshot? _snapshot;
        internal override void Update(HudLayerSnapshot snapshot, long timestamp)
        {
            _snapshot = (ElectricSnapshot)snapshot;
            _playback.ObserveQueued(_snapshot.Frame, timestamp, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        internal override DirectCompositionDrawCommand[] Build(long timestamp)
        {
            if (_snapshot is null) return [];
            var sample = _playback.Sample(timestamp);
            _builtNative = sample.Native;
            _sample = sample;
            return ElectricHudScene.Build(sample, _snapshot.Traction, _snapshot.Color, _snapshot.Layout);
        }
    }
}
