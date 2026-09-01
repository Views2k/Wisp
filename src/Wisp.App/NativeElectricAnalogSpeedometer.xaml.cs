using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Wisp.App;

public partial class NativeElectricAnalogSpeedometer : UserControl
{
    private static readonly (double X, double Y)[] DialNumberOffsets =
    [
        (-108, 67), (-126, 2), (-108, -63), (-60, -110), (0, -126),
        (60, -110), (108, -63), (126, 2), (108, 67)
    ];

    private const double PowerBarWidth = 204;
    private readonly NativeNeedlePlayback _nativeNeedlePlayback = new();
    private readonly NativeTachometerInterpolator _speedNeedleInterpolator = new();
    private readonly NativeElectricPowerGaugeModel _powerGaugeModel = new();
    private readonly NativeRenderLifetime _renderLifetime;
    private readonly Image[] _dialNumberImages;
    private NativeGaugeFrame _latestFrame;
    private double _previousNeedleAngle = double.NaN;
    private long _previousNeedleTimestamp;
    private int _dialNumberStep;
    private bool _hasFrame;
    private bool _framePending;
    private bool _needsSpeedNeedleSeed = true;
    private bool _renderingAttached;
    private bool _usingNativeNeedle;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;

    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame),
        typeof(NativeGaugeFrame),
        typeof(NativeElectricAnalogSpeedometer),
        new FrameworkPropertyMetadata(default(NativeGaugeFrame), OnFrameChanged));

    public NativeElectricAnalogSpeedometer()
    {
        InitializeComponent();
        _dialNumberImages =
        [
            DialNumber0, DialNumber1, DialNumber2, DialNumber3, DialNumber4,
            DialNumber5, DialNumber6, DialNumber7, DialNumber8
        ];
        _renderLifetime = new NativeRenderLifetime(this, OnRenderActivityChanged);
        SetBinding(FrameProperty, new Binding(nameof(DiagnosticsViewModel.NativeGaugeFrame)));
        DialImage.Source = NativeAssetCache.Get(NativeAssetFamily.Electric, "SpeedDial.png");
        GearArcImage.Source = NativeAssetCache.Get(NativeAssetFamily.Electric, "HUD_EV_Gear_Arc.png");
        RegenLabelImage.Source = NativeAssetCache.Get(NativeAssetFamily.Electric, "HUD_EV_RGN.png");
        PowerLabelImage.Source = NativeAssetCache.Get(NativeAssetFamily.Electric, "HUD_EV_PWR.png");
        PositionDialNumbers();
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
        ((NativeElectricAnalogSpeedometer)dependencyObject).UpdateFrame((NativeGaugeFrame)eventArgs.NewValue);

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
        _usingNativeNeedle = ObserveNativeNeedle(frame, timestamp, out var nativeNeedle);
        var renderedSpeed = IsVisible && frame.SpeedAvailable
            ? ObserveSpeedNeedle(frame, timestamp)
            : frame.Speed;
        var speedDisplay = NativeElectricSpeedDisplaySelector.Resolve(frame, timestamp);
        HundredsImage.Source = ElectricDigit(speedDisplay.Hundreds);
        TensImage.Source = ElectricDigit(speedDisplay.Tens);
        OnesImage.Source = ElectricDigit(speedDisplay.Ones);
        var availableOpacity = frame.SpeedAvailable ? 1d : 0.20d;
        HundredsImage.Opacity = availableOpacity *
            (speedDisplay.SpeedLessHundred ? 0.16 : 1);
        TensImage.Opacity = availableOpacity *
            (speedDisplay.SpeedLessTen ? 0.16 : 1);
        OnesImage.Opacity = availableOpacity *
            (speedDisplay.SpeedLessOrEqualOne ? 0.16 : 1);

        UpdateUnit(frame.Unit == Wisp.Core.SpeedUnit.MilesPerHour);

        UpdateGear(frame);

        UpdateAssists(frame.NativeAssists);
        if (!_renderingAttached)
        {
            if (_usingNativeNeedle)
                UpdateNativeNeedle(nativeNeedle);
            else
                UpdateFallbackNeedle(frame, renderedSpeed, timestamp);
        }
        UpdatePowerBar(frame);
        _framePending = false;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        _renderLifetime.Refresh();
        RefreshFrame();
        return base.MeasureOverride(constraint);
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        ResetNeedlePlayback();
        _renderLifetime.Loaded();
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        ResetNeedlePlayback();
        _renderLifetime.Unloaded();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        ResetNeedlePlayback();
        _renderLifetime.Refresh();
    }

    private void OnRenderActivityChanged()
    {
        ResetNeedlePlayback();
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
        if (accepted != _usingNativeNeedle)
        {
            ResetNeedleBlur();
        }

        return accepted;
    }

    private double ObserveSpeedNeedle(NativeGaugeFrame frame, long timestamp)
    {
        var speed = _speedNeedleInterpolator.Observe(
            frame.CarOrdinal,
            frame.GameTimestampMilliseconds,
            frame.Speed,
            timestamp,
            frame.ReceivedTimestamp);
        _needsSpeedNeedleSeed = false;
        return speed;
    }

    private double SampleSpeedNeedle(long timestamp) => _needsSpeedNeedleSeed
        ? ObserveSpeedNeedle(_latestFrame, timestamp)
        : _speedNeedleInterpolator.Sample(timestamp);

    private void ResetNeedlePlayback()
    {
        _nativeNeedlePlayback.Reset();
        _speedNeedleInterpolator.Reset();
        _needsSpeedNeedleSeed = true;
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

    private void UpdateGear(NativeGaugeFrame frame)
    {
        var state = frame.ElectricGearState;
        var currentToken = NativeElectricGearModel.CurrentToken(state, NativeGaugeMode.Analogue);
        GearImage.Visibility = currentToken is null ? Visibility.Collapsed : Visibility.Visible;

        var gaugeAsset = NativeElectricGearModel.GaugeAsset(state, digital: false);
        GearGaugeImage.Visibility = gaugeAsset is null ? Visibility.Collapsed : Visibility.Visible;
        if (gaugeAsset is not null)
        {
            GearGaugeImage.Source = NativeAssetCache.Get(NativeAssetFamily.Electric, gaugeAsset);
        }

        UpdateAdjacentGear(PreviousGearImage, NativeElectricGearModel.AdjacentToken(state, next: false));
        UpdateAdjacentGear(NextGearImage, NativeElectricGearModel.AdjacentToken(state, next: true));

        if (currentToken is null)
        {
            GearArcImage.Source = NativeAssetCache.Get(
                NativeAssetFamily.Electric,
                "HUD_EV_Gear_Arc.png");
            return;
        }

        GearImage.Source = NativeAssetCache.Get(
            NativeAssetFamily.Electric,
            $"HUD_EV_Gear_{currentToken}.png");
        GearArcImage.Source = NativeAssetCache.Get(
            NativeAssetFamily.Electric,
            "HUD_EV_Gear_Arc.png");
    }

    private static void UpdateAdjacentGear(Image image, string? token)
    {
        image.Visibility = token is null ? Visibility.Collapsed : Visibility.Visible;
        if (token is not null)
        {
            image.Source = NativeAssetCache.Get(
                NativeAssetFamily.Electric,
                $"HUD_EV_Gear_Small_{token}.png");
        }
    }

    private void UpdateFallbackNeedle(NativeGaugeFrame frame, double speed, long timestamp)
    {
        var authoredMaximum = frame.Unit == Wisp.Core.SpeedUnit.MilesPerHour ? 240d : 400d;
        var maximumSpeed = double.IsFinite(frame.NativeElectricMaximumSpeed) &&
                           frame.NativeElectricMaximumSpeed > 0
            ? frame.NativeElectricMaximumSpeed
            : authoredMaximum;
        var angle = NativeGaugeGeometry.ElectricAnalogNeedleAngle(speed, maximumSpeed);
        var elapsedSeconds = _previousNeedleTimestamp == 0
            ? 0
            : (timestamp - _previousNeedleTimestamp) / (double)Stopwatch.Frequency;
        var angleDelta = double.IsFinite(_previousNeedleAngle)
            ? angle - _previousNeedleAngle
            : 0;
        if (!frame.SpeedAvailable || elapsedSeconds > 0.25)
        {
            angleDelta = 0;
        }

        Needle.Visibility = frame.SpeedAvailable ? Visibility.Visible : Visibility.Collapsed;
        NeedleRotation.Angle = angle;
        NeedleMaterial.BlurAmount = NativeGaugeGeometry.AnalogNeedleBlurRadians(
            angleDelta,
            elapsedSeconds);
        _previousNeedleAngle = frame.SpeedAvailable ? angle : double.NaN;
        _previousNeedleTimestamp = timestamp;
    }

    private void UpdateNativeNeedle(NativeNeedleRenderState needle)
    {
        Needle.Visibility = Visibility.Visible;
        NeedleRotation.Angle = needle.Angle;
        NeedleMaterial.BlurAmount = needle.Blur;
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
            UpdateNativeNeedle(nativeNeedle);
        else
            UpdateFallbackNeedle(_latestFrame, SampleSpeedNeedle(timestamp), timestamp);
    }

    private void UpdatePowerBar(NativeGaugeFrame frame)
    {
        var display = _powerGaugeModel.Update(
            frame.NativeRegenFillAmount,
            frame.NativePowerFillAmount,
            frame.NativeRegenPowerRatio);
        PowerBarPanel.Visibility = display.Available ? Visibility.Visible : Visibility.Collapsed;
        var regenWidth = PowerBarWidth * display.RegenRatio;
        var powerWidth = PowerBarWidth - regenWidth;
        RegenColumn.Width = new GridLength(regenWidth);
        PowerColumn.Width = new GridLength(powerWidth);
        RegenIndicator.Width = regenWidth * display.RegenFill;
        PowerIndicator.Width = powerWidth * display.PowerFill;
    }

    private void UpdateAssists(Wisp.Core.NativeAssistSnapshot assists)
    {
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
    }

    private static void UpdateAssist(
        Image image,
        string name,
        bool available,
        bool on,
        Wisp.Core.NativeAssistSnapshot snapshot)
    {
        image.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (available)
        {
            image.Source = NativeAssetCache.Get(
                NativeAssetFamily.Analogue,
                NativeAssistAssetSelector.FileName(NativeGaugeMode.Analogue, name, on, snapshot));
        }
    }

    private void PositionDialNumbers()
    {
        var images = _dialNumberImages;
        for (var index = 0; index < images.Length; index++)
        {
            images[index].RenderTransform = new TranslateTransform(
                DialNumberOffsets[index].X,
                DialNumberOffsets[index].Y);
        }
    }

    private void UpdateUnit(bool milesPerHour)
    {
        var step = milesPerHour ? 30 : 50;
        if (_dialNumberStep == step)
            return;

        UnitImage.Source = NativeAssetCache.Get(
            NativeAssetFamily.Analogue,
            milesPerHour ? "HUD_Dial_Unit_MPH.png" : "HUD_Dial_Unit_KPH.png");
        var images = _dialNumberImages;
        for (var index = 0; index < images.Length; index++)
        {
            images[index].Source = NativeAssetCache.GetTinted(
                NativeAssetFamily.Electric,
                $"HUD_EV_Dial_Speed{index * step}.png",
                Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        }
        _dialNumberStep = step;
    }

    private static ImageSource ElectricDigit(int value) =>
        NativeAssetCache.Get(NativeAssetFamily.Electric, $"HUD_EV_Speed{value}.png");
}
