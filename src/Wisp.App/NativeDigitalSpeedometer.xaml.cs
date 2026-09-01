using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Wisp.App;

public partial class NativeDigitalSpeedometer : UserControl
{
    private readonly NativeTachometerInterpolator _tachometerInterpolator = new();
    private readonly NativeRenderLifetime _renderLifetime;
    private NativeGaugeFrame _latestFrame;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private bool _hasFrame;
    private bool _framePending;
    private bool _renderingAttached;
    private bool _needsTachometerSeed = true;

    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame),
        typeof(NativeGaugeFrame),
        typeof(NativeDigitalSpeedometer),
        new FrameworkPropertyMetadata(default(NativeGaugeFrame), OnFrameChanged));

    public NativeDigitalSpeedometer()
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
        ((NativeDigitalSpeedometer)dependencyObject).UpdateFrame((NativeGaugeFrame)eventArgs.NewValue);

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
        var renderedRpm = IsVisible ? ObserveTachometer(frame, timestamp) : frame.EngineRpm;

        var digits = NativeGaugeGeometry.SpeedDigits(frame.Speed);
        HundredsImage.Source = Digit(digits.Hundreds);
        TensImage.Source = Digit(digits.Tens);
        OnesImage.Source = Digit(digits.Ones);
        var availableOpacity = frame.SpeedAvailable ? 1d : 0.30d;
        HundredsImage.Opacity = availableOpacity * (frame.Speed < 100 ? 0.30 : 0.80);
        TensImage.Opacity = availableOpacity * (frame.Speed < 10 ? 0.30 : 0.80);
        OnesImage.Opacity = availableOpacity * (frame.Speed <= 1 ? 0.30 : 0.80);

        UnitImage.Source = NativeAssetCache.Get(
            NativeGaugeMode.Digital,
            frame.Unit == Wisp.Core.SpeedUnit.MilesPerHour
                ? "HUD_Dial_Unit_Digital_MPH.png"
                : "HUD_Dial_Unit_Digital_KPH.png");

        var gear = NativeGaugeGeometry.GearToken(frame.Gear, frame.GearDisplayMode);
        var shift = NativeGaugeGeometry.IsShiftLightActive(frame.EngineRpm, frame.ExactRedline);
        GearImage.Visibility = gear is null ? Visibility.Collapsed : Visibility.Visible;
        if (gear is not null)
        {
            GearImage.Source = NativeAssetCache.Get(
                NativeGaugeMode.Digital,
                NativeGearAssetSelector.FileName(
                    NativeGaugeMode.Digital,
                    gear,
                    shift,
                    frame.NativeAssists));
        }

        UpdateAssist(AbsImage, "ABS", frame.NativeAssists.IsABSAvailable, frame.NativeAssists.IsABSOn, frame.NativeAssists);
        UpdateAssist(TcrImage, "TCR", frame.NativeAssists.IsTCRAvailable, frame.NativeAssists.IsTCROn, frame.NativeAssists);
        UpdateAssist(StmImage, "STM", frame.NativeAssists.IsSTMAvailable, frame.NativeAssists.IsSTMOn, frame.NativeAssists);
        UpdateAssist(LcImage, "LC", frame.NativeAssists.IsLCAvailable, frame.NativeAssists.IsLCOn, frame.NativeAssists);
        AssistStack.Visibility = frame.NativeAssists.Available &&
                                 (frame.NativeAssists.IsABSAvailable ||
                                  frame.NativeAssists.IsTCRAvailable ||
                                  frame.NativeAssists.IsSTMAvailable ||
                                  frame.NativeAssists.IsLCAvailable)
            ? Visibility.Visible
            : Visibility.Collapsed;

        GaugeVisual.UpdateFrame(frame with { EngineRpm = renderedRpm });
        _framePending = false;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        _renderLifetime.Refresh();
        RefreshFrame();
        return base.MeasureOverride(constraint);
    }

    private double ObserveTachometer(NativeGaugeFrame frame, long timestamp)
    {
        var renderedRpm = _tachometerInterpolator.Observe(
            frame.CarOrdinal,
            frame.GameTimestampMilliseconds,
            frame.EngineRpm,
            timestamp,
            frame.ReceivedTimestamp);
        _needsTachometerSeed = false;
        return renderedRpm;
    }

    private double SampleTachometer(long timestamp) => _needsTachometerSeed
        ? ObserveTachometer(_latestFrame, timestamp)
        : _tachometerInterpolator.Sample(timestamp);

    private void ResetTachometerPlayback()
    {
        _tachometerInterpolator.Reset();
        _needsTachometerSeed = true;
        _lastRenderingTime = TimeSpan.MinValue;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        ResetTachometerPlayback();
        _renderLifetime.Refresh();
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

        var renderedRpm = SampleTachometer(Stopwatch.GetTimestamp());
        GaugeVisual.UpdateFrame(_latestFrame with { EngineRpm = renderedRpm });
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
            NativeGaugeMode.Digital,
            NativeAssistAssetSelector.FileName(NativeGaugeMode.Digital, name, on, snapshot));
        // Native Digital Off inherits A_Color_Faded_HUDInfo_Digital
        // (#4DFFFFFF). On and On_glow override that color with white.
        image.Opacity = on ? 1 : 77d / 255d;
    }

    private static System.Windows.Media.ImageSource Digit(int value) =>
        NativeAssetCache.Get(NativeGaugeMode.Digital, $"HUD_Dial_Speed_Digital_{value}.png");
}
