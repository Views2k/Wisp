using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Wisp.App;

public partial class NativeAnalogSpeedometer : UserControl
{
    private readonly NativeNeedlePlayback _nativeNeedlePlayback = new();
    private readonly NativeTachometerInterpolator _tachometerInterpolator = new();
    private readonly NativeRenderLifetime _renderLifetime;
    private double _previousNeedleAngle = double.NaN;
    private long _previousNeedleTimestamp;
    private NativeGaugeFrame _latestFrame;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private bool _hasFrame;
    private bool _framePending;
    private bool _renderingAttached;
    private bool _needsTachometerSeed = true;
    private bool _usingNativeNeedle;

    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame),
        typeof(NativeGaugeFrame),
        typeof(NativeAnalogSpeedometer),
        new FrameworkPropertyMetadata(default(NativeGaugeFrame), OnFrameChanged));

    public NativeAnalogSpeedometer()
    {
        InitializeComponent();
        _renderLifetime = new NativeRenderLifetime(this, OnRenderActivityChanged);
        SetBinding(FrameProperty, new Binding(nameof(DiagnosticsViewModel.NativeGaugeFrame)));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public NativeGaugeFrame Frame
    {
        get => (NativeGaugeFrame)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    private static void OnFrameChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs) =>
        ((NativeAnalogSpeedometer)dependencyObject).UpdateFrame((NativeGaugeFrame)eventArgs.NewValue);

    private void UpdateFrame(NativeGaugeFrame frame)
    {
        _latestFrame = frame;
        _hasFrame = true;
        _framePending = true;
        _renderLifetime.Refresh();
        RefreshFrame();
    }

    private void RefreshFrame()
    {
        if (!_framePending || !_renderLifetime.CanUpdateVisuals)
            return;

        var frame = _latestFrame;
        var timestamp = Stopwatch.GetTimestamp();
        var nativeNeedle = default(NativeNeedleRenderState);
        var hasNativeNeedle = ObserveNativeNeedle(frame, timestamp, out nativeNeedle);
        var renderedRpm = IsVisible ? ObserveTachometer(frame, timestamp) : frame.EngineRpm;

        var digits = NativeGaugeGeometry.SpeedDigits(frame.Speed);
        HundredsImage.Source = Digit(digits.Hundreds);
        TensImage.Source = Digit(digits.Tens);
        OnesImage.Source = Digit(digits.Ones);
        var availableOpacity = frame.SpeedAvailable ? 1d : 0.20d;
        HundredsImage.Opacity = availableOpacity * (frame.Speed < 100 ? 0.16 : 1);
        TensImage.Opacity = availableOpacity * (frame.Speed < 10 ? 0.16 : 1);
        OnesImage.Opacity = availableOpacity * (frame.Speed <= 1 ? 0.16 : 1);

        var milesPerHour = frame.Unit == Wisp.Core.SpeedUnit.MilesPerHour;
        UnitImage.Source = NativeAssetCache.Get(
            NativeGaugeMode.Analogue,
            milesPerHour ? "HUD_Dial_Unit_MPH.png" : "HUD_Dial_Unit_KPH.png");
        UnitImage.Width = milesPerHour ? 52 : 62;
        UnitImage.Margin = milesPerHour
            ? new Thickness(0, 0, 2, 104)
            : new Thickness(0, 0, 6, 104);

        var gear = NativeGaugeGeometry.GearToken(frame.Gear, frame.GearDisplayMode);
        var shift = NativeGaugeGeometry.IsShiftLightActive(frame.EngineRpm, frame.ExactRedline);
        GearImage.Visibility = gear is null ? Visibility.Collapsed : Visibility.Visible;
        if (gear is not null)
        {
            // FH6 has no authored Analogue Drive glyph. Reuse its original
            // Digital Drive state textures instead of fabricating a letter.
            // Keep each HiRes asset at its authored 50% HUD scale: analogue
            // gears are 200px -> 100px, while Digital Drive is 136px -> 68px.
            var assetMode = gear == "Drive" ? NativeGaugeMode.Digital : NativeGaugeMode.Analogue;
            GearImage.Width = gear == "Drive" ? 68 : 100;
            GearImage.Height = gear == "Drive" ? 68 : 100;
            GearImage.Source = NativeAssetCache.Get(
                assetMode,
                NativeGearAssetSelector.FileName(
                    assetMode,
                    gear,
                    shift,
                    frame.NativeAssists));
        }

        var assists = frame.NativeAssists;
        UpdateAssist(AbsImage, "ABS", assists.IsABSAvailable, assists.IsABSOn, assists);
        UpdateAssist(TcrImage, "TCR", assists.IsTCRAvailable, assists.IsTCROn, assists);
        UpdateAssist(StmImage, "STM", assists.IsSTMAvailable, assists.IsSTMOn, assists);
        UpdateAssist(LcImage, "LC", assists.IsLCAvailable, assists.IsLCOn, assists);
        AssistGrid.Visibility = assists.Available &&
                                (assists.IsABSAvailable || assists.IsTCRAvailable ||
                                 assists.IsSTMAvailable || assists.IsLCAvailable)
            ? Visibility.Visible
            : Visibility.Collapsed;
        AbsRotation.Angle = assists.ABSAngle;
        TcrRotation.Angle = assists.TCRAngle;
        StmRotation.Angle = assists.STMAngle;
        LcRotation.Angle = assists.LCAngle;

        MaterialVisual.UpdateFrame(frame);
        if (ShouldUpdateTachometerImmediately(_renderingAttached))
        {
            if (hasNativeNeedle)
                UpdateNativeNeedle(frame, nativeNeedle);
            else
                UpdateFallbackNeedle(frame with { EngineRpm = renderedRpm }, timestamp);
        }
        _framePending = false;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        _renderLifetime.Refresh();
        RefreshFrame();
        return base.MeasureOverride(constraint);
    }

    internal static bool ShouldUpdateTachometerImmediately(bool renderingAttached) =>
        !renderingAttached;

    private bool ObserveNativeNeedle(
        NativeGaugeFrame frame,
        long timestamp,
        out NativeNeedleRenderState state)
    {
        var accepted = _nativeNeedlePlayback.Observe(
            frame.CarOrdinal,
            frame.GameTimestampMilliseconds,
            frame.NativeNeedleAngleDegrees,
            frame.NativeNeedleBlurAmount,
            timestamp,
            frame.NativeGaugeObservedTimestamp > 0 ? frame.NativeGaugeObservedTimestamp : null,
            frame.NativeGaugeSourceInvalidated,
            out state);
        if (accepted == _usingNativeNeedle)
        {
            return accepted;
        }

        _usingNativeNeedle = accepted;
        ResetNeedleBlur();
        return accepted;
    }

    private void ResetTachometerPlayback()
    {
        _nativeNeedlePlayback.Reset();
        _tachometerInterpolator.Reset();
        _needsTachometerSeed = true;
        _usingNativeNeedle = false;
        _lastRenderingTime = TimeSpan.MinValue;
        ResetNeedleBlur();
    }

    private void ResetNeedleBlur()
    {
        _previousNeedleAngle = double.NaN;
        _previousNeedleTimestamp = 0;
        NeedleMaterial.BlurAmount = 0;
    }

    private double ObserveTachometer(NativeGaugeFrame frame, long timestamp)
    {
        var previousCar = _tachometerInterpolator.AcceptedCarOrdinal;
        var renderedRpm = _tachometerInterpolator.Observe(
            frame.CarOrdinal,
            frame.GameTimestampMilliseconds,
            frame.EngineRpm,
            timestamp,
            frame.ReceivedTimestamp);
        _needsTachometerSeed = false;
        if (previousCar is int previous &&
            _tachometerInterpolator.AcceptedCarOrdinal is int current && previous != current)
        {
            ResetNeedleBlur();
        }

        return renderedRpm;
    }

    private double SampleTachometer(long timestamp) => _needsTachometerSeed
        ? ObserveTachometer(_latestFrame, timestamp)
        : _tachometerInterpolator.Sample(timestamp);

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        ResetTachometerPlayback();
        _renderLifetime.Refresh();
    }

    private void UpdateFallbackNeedle(NativeGaugeFrame frame, long timestamp)
    {
        var hasTachometer = NativeGaugeGeometry.HasExactTachometerState(
            frame.ExactRedline,
            frame.TachometerMaximumRpm);
        var angle = NativeGaugeGeometry.AnalogNeedleAngle(
            frame.EngineRpm,
            frame.TachometerMaximumRpm);
        var elapsedSeconds = _previousNeedleTimestamp == 0
            ? 0
            : (timestamp - _previousNeedleTimestamp) / (double)Stopwatch.Frequency;
        var angleDelta = double.IsFinite(_previousNeedleAngle)
            ? angle - _previousNeedleAngle
            : 0;
        if (!hasTachometer || elapsedSeconds > 0.25)
        {
            angleDelta = 0;
        }

        Needle.Visibility = hasTachometer ? Visibility.Visible : Visibility.Collapsed;
        NeedleRotation.Angle = angle;
        NeedleMaterial.BlurAmount = NativeGaugeGeometry.AnalogNeedleBlurRadians(
            angleDelta,
            elapsedSeconds);
        _previousNeedleAngle = hasTachometer ? angle : double.NaN;
        _previousNeedleTimestamp = timestamp;
        GaugeVisual.UpdateFrame(frame);
    }

    private void UpdateNativeNeedle(NativeGaugeFrame frame, NativeNeedleRenderState needle)
    {
        Needle.Visibility = Visibility.Visible;
        NeedleRotation.Angle = needle.Angle;
        NeedleMaterial.BlurAmount = needle.Blur;
        GaugeVisual.UpdateFrame(frame);
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_renderLifetime.IsLoaded)
        {
            return;
        }

        ResetTachometerPlayback();
        _renderLifetime.Loaded();
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        ResetTachometerPlayback();
        _renderLifetime.Unloaded();
    }

    private void OnRenderActivityChanged()
    {
        ResetTachometerPlayback();
        if (_renderLifetime.IsLive != _renderingAttached)
        {
            if (_renderLifetime.IsLive)
                CompositionTarget.Rendering += OnCompositionRendering;
            else
                CompositionTarget.Rendering -= OnCompositionRendering;
            _renderingAttached = _renderLifetime.IsLive;
        }

        _framePending = _hasFrame;
        RefreshFrame();
    }

    private void OnCompositionRendering(object? sender, EventArgs eventArgs)
    {
        if (!_hasFrame || !_renderLifetime.IsLive)
        {
            return;
        }

        if (eventArgs is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRenderingTime)
            {
                return;
            }

            _lastRenderingTime = rendering.RenderingTime;
        }

        var timestamp = Stopwatch.GetTimestamp();
        if (_usingNativeNeedle && _nativeNeedlePlayback.Sample(timestamp, out var nativeNeedle))
        {
            UpdateNativeNeedle(_latestFrame, nativeNeedle);
            return;
        }

        var renderedRpm = SampleTachometer(timestamp);
        UpdateFallbackNeedle(_latestFrame with { EngineRpm = renderedRpm }, timestamp);
    }

    private static void UpdateAssist(
        Image image,
        string name,
        bool available,
        bool on,
        Wisp.Core.NativeAssistSnapshot snapshot)
    {
        image.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            return;
        }

        image.Source = NativeAssetCache.Get(
            NativeGaugeMode.Analogue,
            NativeAssistAssetSelector.FileName(NativeGaugeMode.Analogue, name, on, snapshot));
        image.Opacity = 1;
    }

    private static System.Windows.Media.ImageSource Digit(int value) =>
        NativeAssetCache.Get(NativeGaugeMode.Analogue, $"HUD_Dial_Speed_Analogue_{value}.png");
}
