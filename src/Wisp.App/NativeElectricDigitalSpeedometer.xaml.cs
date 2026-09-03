using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Wisp.App;

public partial class NativeElectricDigitalSpeedometer : UserControl
{
    private const double SingleSpeedPowerBarWidth = 215;
    private const double MultiGearPowerBarWidth = 234;
    private readonly NativeElectricPowerGaugeModel _powerGaugeModel = new();
    private readonly NativeRenderLifetime _renderLifetime;
    private NativeGaugeFrame _latestFrame;
    private bool _hasFrame;
    private bool _framePending;

    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame),
        typeof(NativeGaugeFrame),
        typeof(NativeElectricDigitalSpeedometer),
        new FrameworkPropertyMetadata(default(NativeGaugeFrame), OnFrameChanged));

    public NativeElectricDigitalSpeedometer()
    {
        InitializeComponent();
        _renderLifetime = new NativeRenderLifetime(this, OnRenderActivityChanged);
        SetBinding(FrameProperty, new Binding(nameof(DiagnosticsViewModel.NativeGaugeFrame)));
        RegenLabelImage.Source = NativeAssetCache.Get(NativeAssetFamily.Electric, "HUD_EV_RGN.png");
        PowerLabelImage.Source = NativeAssetCache.Get(NativeAssetFamily.Electric, "HUD_EV_PWR.png");
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
        ((NativeElectricDigitalSpeedometer)dependencyObject).UpdateFrame((NativeGaugeFrame)eventArgs.NewValue);

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
        var speedDisplay = NativeElectricSpeedDisplaySelector.Resolve(frame, Stopwatch.GetTimestamp());
        HundredsImage.Source = DigitalDigit(speedDisplay.Hundreds);
        TensImage.Source = DigitalDigit(speedDisplay.Tens);
        OnesImage.Source = DigitalDigit(speedDisplay.Ones);
        var availableOpacity = frame.SpeedAvailable ? 1d : 0.30d;
        HundredsImage.Opacity = availableOpacity *
            (speedDisplay.SpeedLessHundred ? 0.30 : 0.80);
        TensImage.Opacity = availableOpacity *
            (speedDisplay.SpeedLessTen ? 0.30 : 0.80);
        OnesImage.Opacity = availableOpacity *
            (speedDisplay.SpeedLessOrEqualOne ? 0.30 : 0.80);

        UnitImage.Source = NativeAssetCache.Get(
            NativeAssetFamily.Digital,
            frame.Unit == Wisp.Core.SpeedUnit.MilesPerHour
                ? "HUD_Dial_Unit_Digital_MPH.png"
                : "HUD_Dial_Unit_Digital_KPH.png");

        var gearState = frame.ElectricGearState;
        var gear = NativeElectricGearModel.CurrentToken(
            gearState,
            NativeGaugeMode.Digital,
            frame.Gear);
        GearImage.Visibility = gear is null ? Visibility.Collapsed : Visibility.Visible;
        if (gear is not null)
        {
            GearImage.Source = NativeAssetCache.Get(
                NativeAssetFamily.Digital,
                NativeGearAssetSelector.FileName(
                    NativeGaugeMode.Digital,
                    gear,
                    false,
                    frame.NativeAssists));
        }

        var nextGear = NativeElectricGearModel.AdjacentToken(gearState, next: true);
        NextGearImage.Visibility = nextGear is null ? Visibility.Collapsed : Visibility.Visible;
        if (nextGear is not null)
        {
            NextGearImage.Source = NativeAssetCache.Get(
                NativeAssetFamily.Electric,
                $"HUD_EV_Gear_{nextGear}.png");
        }

        var gaugeAsset = NativeElectricGearModel.GaugeAsset(gearState, digital: true);
        GearGaugeImage.Visibility = gaugeAsset is null ? Visibility.Collapsed : Visibility.Visible;
        if (gaugeAsset is not null)
        {
            GearGaugeImage.Source = NativeAssetCache.Get(NativeAssetFamily.Electric, gaugeAsset);
        }

        UpdateAssists(frame.NativeAssists);
        UpdatePowerBar(frame);
        _framePending = false;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        _renderLifetime.Refresh();
        RefreshFrame();
        return base.MeasureOverride(constraint);
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs) => _renderLifetime.Loaded();

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs) => _renderLifetime.Unloaded();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs eventArgs) =>
        _renderLifetime.Refresh();

    private void OnRenderActivityChanged()
    {
        _framePending = _hasFrame;
        RefreshFrame();
    }

    private void UpdatePowerBar(NativeGaugeFrame frame)
    {
        var display = _powerGaugeModel.Update(
            frame.NativeRegenFillAmount,
            frame.NativePowerFillAmount,
            frame.NativeRegenPowerRatio);
        PowerBarPanel.Visibility = display.Available ? Visibility.Visible : Visibility.Collapsed;
        var multiGear = NativeElectricGearModel.IsMultiGear(frame.ElectricGearState);
        var totalWidth = multiGear ? MultiGearPowerBarWidth : SingleSpeedPowerBarWidth;
        PowerBarGrid.Width = totalWidth;
        PowerBarPanel.Margin = multiGear
            ? new Thickness(24, 0, -13, 7)
            : new Thickness(24, 0, 6, 7);
        var regenWidth = totalWidth * display.RegenRatio;
        var powerWidth = totalWidth - regenWidth;
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
        AssistStack.Visibility = assists.Available &&
                                 (assists.IsABSAvailable || assists.IsTCRAvailable ||
                                  assists.IsSTMAvailable || assists.IsLCAvailable)
            ? Visibility.Visible
            : Visibility.Collapsed;
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
            NativeAssetFamily.Digital,
            NativeAssistAssetSelector.FileName(NativeGaugeMode.Digital, name, on, snapshot));
        image.Opacity = on ? 1 : 77d / 255d;
    }

    private static ImageSource DigitalDigit(int value) =>
        NativeAssetCache.Get(NativeAssetFamily.Digital, $"HUD_Dial_Speed_Digital_{value}.png");
}
