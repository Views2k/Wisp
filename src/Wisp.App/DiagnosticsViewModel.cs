using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using Wisp.Core;
using Wisp.Telemetry;

namespace Wisp.App;

public sealed class DiagnosticsViewModel : INotifyPropertyChanged
{
    private const double WattsPerHorsepower = 745.69987158227022;
    private readonly GForceDisplayModel _gForceDisplayModel = new();
    private readonly BoostDisplayModel _boostDisplayModel = new();
    private readonly TireTemperatureDisplayModel _tireTemperatureDisplayModel = new();
    private string _statusText = "Waiting for FH6";
    private string _statusDetail = "No valid 324-byte packets received";
    private string _hudSpeed = "—";
    private string _packetRate = "0.0 Hz";
    private string _renderRate = "—";
    private string _packetAge = "—";
    private string _rejectedPackets = "0";
    private string _carOrdinal = "—";
    private string _drivetrain = "—";
    private string _groundSpeed = "—";
    private string _indicatedSpeed = "—";
    private string _wheelSpeeds = "—";
    private string _slipRatios = "—";
    private string _radius = "—";
    private string _confidence = "0%";
    private string _selectedWheels = "—";
    private string _engineRpm = "—";
    private string _engineMaximumRpm = "—";
    private string _nativeTachScale = "—";
    private string _exactNativeRedline = "Unavailable";
    private string _exactRedlineSource = "—";
    private string _exactRedlineServiceState = "Inactive";
    private string _nativeAssistState = "Unavailable";
    private string _nativeAssistDetails = "—";
    private string _gameplayHudVisibility = "Unavailable";
    private string _nativeCompatibilityValidation = "Awaiting FH6 build validation";
    private string _nativeCompatibilityUpdates = "Offline; bundled compatibility is available";
    private bool _canCheckNativeCompatibility;
    private bool _canImportNativeCompatibility;
    private string _lateralGText = "0.00 g";
    private string _longitudinalGText = "0.00 g";
    private string _gForceScaleText = "1.0 G";
    private double _gForceOffsetX;
    private double _gForceOffsetY;
    private Point _gForceTrailPosition;
    private int _udpPort;
    private string _udpPortText;
    private int _unitSelectionIndex;
    private int _speedSourceSelectionIndex;
    private double _overlayWidthScale;
    private double _overlayHeightScale;
    private double _overlayOpacity;
    private double _smoothing;
    private bool _gForceEnabled;
    private bool _gForceAttached;
    private bool _boostGaugeEnabled;
    private bool _boostGaugeAttached;
    private bool _boostGaugeColorNumber;
    private bool _digitalBoostGaugeColorNumber;
    private bool _digitalBoostGaugeStockColors;
    private double _boostGaugeScale;
    private BoostDisplay _boostDisplay = BoostDisplay.Unavailable;
    private bool _tireTemperatureGaugeEnabled;
    private bool _tireTemperatureGaugeAttached;
    private bool _tireTemperatureReactiveColors;
    private bool _useCelsiusTireTemperature;
    private double _tireTemperatureGaugeScale;
    private TireTemperatureDisplay _tireTemperatureDisplay = TireTemperatureDisplay.Unavailable;
    private double _gForceWidthScale;
    private double _gForceHeightScale;
    private int _layoutSelectionIndex;
    private int _nativeGaugeSelectionIndex;
    private int _gearDisplaySelectionIndex;
    private bool _invertLateralG;
    private bool _invertLongitudinalG;
    private NativeGaugeFrame _nativeGaugeFrame;
    private bool _hasLiveTelemetry;
    private bool _gameAwareVisibility;
    private bool _autoMinimizeOnTelemetry;
    private bool _startWithWindows;
    private bool _startWithForza;
    private bool _startMinimizedWithForza;
    private bool _animatedBackground;
    private bool _tractionCueEnabled;
    private bool _isTractionCueActive;
    private bool _canRelearnCurrentTires;
    private string _applicationUpdateStatus = "Updates are checked only when requested.";
    private string _applicationUpdateAction = "Check for updates";
    private bool _canCheckApplicationUpdate = true;

    public DiagnosticsViewModel(AppSettings settings)
    {
        _udpPort = settings.UdpPort;
        _udpPortText = settings.UdpPort.ToString(CultureInfo.InvariantCulture);
        _unitSelectionIndex = settings.SpeedUnit == SpeedUnit.MilesPerHour ? 0 : 1;
        _speedSourceSelectionIndex = (int)settings.SpeedSource;
        _overlayWidthScale = settings.OverlayWidthScale;
        _overlayHeightScale = settings.OverlayHeightScale;
        _overlayOpacity = settings.OverlayOpacity;
        _smoothing = settings.Smoothing;
        _gForceEnabled = settings.GForceEnabled;
        _gForceAttached = settings.GForceAttached;
        _boostGaugeEnabled = settings.BoostGaugeEnabled;
        _boostGaugeAttached = settings.BoostGaugeAttached;
        _boostGaugeColorNumber = settings.BoostGaugeColorNumber;
        _digitalBoostGaugeColorNumber = settings.DigitalBoostGaugeColorNumber;
        _digitalBoostGaugeStockColors = settings.DigitalBoostGaugeStockColors;
        _boostGaugeScale = settings.BoostGaugeScale;
        _tireTemperatureGaugeEnabled = settings.TireTemperatureGaugeEnabled;
        _tireTemperatureGaugeAttached = settings.TireTemperatureGaugeAttached;
        _tireTemperatureReactiveColors = settings.TireTemperatureReactiveColors;
        _useCelsiusTireTemperature = settings.TireTemperatureUnit == TireTemperatureUnit.Celsius;
        _tireTemperatureGaugeScale = settings.TireTemperatureGaugeScale;
        _gForceWidthScale = settings.GForceWidthScale;
        _gForceHeightScale = settings.GForceHeightScale;
        _layoutSelectionIndex = (int)settings.LayoutMode;
        _nativeGaugeSelectionIndex = (int)settings.NativeGaugeMode;
        _gearDisplaySelectionIndex = (int)settings.GearDisplayMode;
        _invertLateralG = settings.InvertLateralG;
        _invertLongitudinalG = settings.InvertLongitudinalG;
        _nativeGaugeFrame = NativeGaugeFrame.Empty(settings.SpeedUnit);
        _gameAwareVisibility = settings.GameAwareVisibility;
        _autoMinimizeOnTelemetry = settings.AutoMinimizeOnTelemetry;
        _startWithWindows = settings.StartWithWindows;
        _startWithForza = settings.StartWithForza;
        _startMinimizedWithForza = settings.StartMinimizedWithForza;
        _animatedBackground = settings.AnimatedBackground;
        _tractionCueEnabled = settings.TractionCueEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public string HudSpeed
    {
        get => _hudSpeed;
        private set
        {
            if (Set(ref _hudSpeed, value))
            {
                OnPropertyChanged(nameof(PreviewSpeed));
            }
        }
    }
    public string PacketRate { get => _packetRate; private set => Set(ref _packetRate, value); }
    public string RenderRate { get => _renderRate; private set => Set(ref _renderRate, value); }
    public string PacketAge { get => _packetAge; private set => Set(ref _packetAge, value); }
    public string RejectedPackets { get => _rejectedPackets; private set => Set(ref _rejectedPackets, value); }
    public string CarOrdinal { get => _carOrdinal; private set => Set(ref _carOrdinal, value); }
    public string Drivetrain { get => _drivetrain; private set => Set(ref _drivetrain, value); }
    public string GroundSpeed { get => _groundSpeed; private set => Set(ref _groundSpeed, value); }
    public string IndicatedSpeed { get => _indicatedSpeed; private set => Set(ref _indicatedSpeed, value); }
    public string WheelSpeeds { get => _wheelSpeeds; private set => Set(ref _wheelSpeeds, value); }
    public string SlipRatios { get => _slipRatios; private set => Set(ref _slipRatios, value); }
    public string Radius { get => _radius; private set => Set(ref _radius, value); }
    public string Confidence { get => _confidence; private set => Set(ref _confidence, value); }
    public string SelectedWheels { get => _selectedWheels; private set => Set(ref _selectedWheels, value); }
    public string EngineRpm { get => _engineRpm; private set => Set(ref _engineRpm, value); }
    public string EngineMaximumRpm { get => _engineMaximumRpm; private set => Set(ref _engineMaximumRpm, value); }
    public string NativeTachScale { get => _nativeTachScale; private set => Set(ref _nativeTachScale, value); }
    public string ExactNativeRedline { get => _exactNativeRedline; private set => Set(ref _exactNativeRedline, value); }
    public string ExactRedlineSource { get => _exactRedlineSource; private set => Set(ref _exactRedlineSource, value); }
    public string ExactRedlineStateText { get => _exactRedlineServiceState; private set => Set(ref _exactRedlineServiceState, value); }
    public string NativeAssistState { get => _nativeAssistState; private set => Set(ref _nativeAssistState, value); }
    public string NativeAssistDetails { get => _nativeAssistDetails; private set => Set(ref _nativeAssistDetails, value); }
    public string GameplayHudVisibility { get => _gameplayHudVisibility; private set => Set(ref _gameplayHudVisibility, value); }
    public string NativeCompatibilityValidation { get => _nativeCompatibilityValidation; private set => Set(ref _nativeCompatibilityValidation, value); }
    public string NativeCompatibilityUpdates { get => _nativeCompatibilityUpdates; private set => Set(ref _nativeCompatibilityUpdates, value); }
    public bool CanCheckNativeCompatibility { get => _canCheckNativeCompatibility; private set => Set(ref _canCheckNativeCompatibility, value); }
    public bool CanImportNativeCompatibility { get => _canImportNativeCompatibility; private set => Set(ref _canImportNativeCompatibility, value); }
    public string LateralGText { get => _lateralGText; private set => Set(ref _lateralGText, value); }
    public string LongitudinalGText { get => _longitudinalGText; private set => Set(ref _longitudinalGText, value); }
    public string GForceScaleText { get => _gForceScaleText; private set => Set(ref _gForceScaleText, value); }
    public string DashboardSpeed => HasLiveTelemetry && NativeGaugeFrame.SpeedAvailable
        ? NativeGaugeFrame.Speed.ToString(CultureInfo.InvariantCulture)
        : "—";
    public string DashboardSpeedUnit => NativeGaugeFrame.Unit == SpeedUnit.KilometersPerHour ? "KM/H" : "MPH";
    public string DashboardGear
    {
        get
        {
            if (!HasLiveTelemetry) return "—";
            return NativeGaugeGeometry.GearToken(NativeGaugeFrame.Gear, NativeGaugeFrame.GearDisplayMode) switch
            {
                "Drive" => "D",
                { } token => token,
                _ => "—"
            };
        }
    }
    public string DashboardPower => HasLiveTelemetry
        ? $"{NativeGaugeFrame.PowerWatts / WattsPerHorsepower:+0.0;-0.0;0.0} HP"
        : "—";
    public string DashboardTorque => HasLiveTelemetry
        ? $"{NativeGaugeFrame.TorqueNm:+0;-0;0} Nm"
        : "—";
    public string DashboardVehicleType => HasLiveTelemetry
        ? NativeGaugeFrame.IsElectric ? "Electric" : "Combustion"
        : "—";
    public double GForceOffsetX { get => _gForceOffsetX; private set => Set(ref _gForceOffsetX, value); }
    public double GForceOffsetY { get => _gForceOffsetY; private set => Set(ref _gForceOffsetY, value); }
    public Point GForceTrailPosition { get => _gForceTrailPosition; private set => Set(ref _gForceTrailPosition, value); }
    public NativeGaugeFrame NativeGaugeFrame
    {
        get => _nativeGaugeFrame;
        private set
        {
            if (Set(ref _nativeGaugeFrame, value))
            {
                OnPropertyChanged(nameof(NativePreviewFrame));
                NotifyDashboardFrame();
            }
        }
    }

    public bool HasLiveTelemetry
    {
        get => _hasLiveTelemetry;
        private set
        {
            if (Set(ref _hasLiveTelemetry, value))
            {
                OnPropertyChanged(nameof(IsPreviewLive));
                OnPropertyChanged(nameof(NativePreviewFrame));
                OnPropertyChanged(nameof(PreviewSpeed));
                OnPropertyChanged(nameof(PreviewCaption));
                NotifyDashboardFrame();
            }
        }
    }

    public bool IsPreviewLive => HasLiveTelemetry;
    public NativeGaugeFrame NativePreviewFrame => IsPreviewLive
        ? NativeGaugeFrame
        : HudPreviewSample.Create(
            UnitSelectionIndex == 0 ? SpeedUnit.MilesPerHour : SpeedUnit.KilometersPerHour,
            (GearDisplayMode)Math.Clamp(
                GearDisplaySelectionIndex,
                (int)GearDisplayMode.Manual,
                (int)GearDisplayMode.Automatic));
    public string PreviewSpeed => IsPreviewLive
        ? HudSpeed
        : NativePreviewFrame.Speed.ToString(CultureInfo.InvariantCulture);
    public string PreviewCaption => IsPreviewLive
        ? "Live preview · current FH6 data"
        : HudPreviewSample.Caption;
    public BoostDisplay PreviewBoostDisplay => BoostDisplay.IsAvailable
        ? BoostDisplay
        : HudPreviewSample.Boost;
    public TireTemperatureDisplay PreviewTireTemperatureDisplay => TireTemperatureDisplay.IsAvailable
        ? TireTemperatureDisplay
        : HudPreviewSample.TireTemperature;

    private void NotifyDashboardFrame()
    {
        OnPropertyChanged(nameof(DashboardSpeed));
        OnPropertyChanged(nameof(DashboardSpeedUnit));
        OnPropertyChanged(nameof(DashboardGear));
        OnPropertyChanged(nameof(DashboardPower));
        OnPropertyChanged(nameof(DashboardTorque));
        OnPropertyChanged(nameof(DashboardVehicleType));
    }

    public void UpdateNativeCompatibility(string validation, string updates, bool canCheck, bool canImport)
    {
        NativeCompatibilityValidation = validation;
        NativeCompatibilityUpdates = updates;
        CanCheckNativeCompatibility = canCheck;
        CanImportNativeCompatibility = canImport;
    }

    public void UpdateNativeGameplayVisibility(NativeGameplayVisibility visibility, bool fresh)
    {
        GameplayHudVisibility = visibility switch
        {
            NativeGameplayVisibility.Visible => fresh ? "Visible" : "Visible (stale)",
            NativeGameplayVisibility.Hidden => fresh ? "Hidden" : "Hidden (stale)",
            _ => "Unavailable"
        };
    }

    public int UdpPort
    {
        get => _udpPort;
        set
        {
            Set(ref _udpPort, value);
            UdpPortText = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public string UdpPortText { get => _udpPortText; set => Set(ref _udpPortText, value); }
    public int UnitSelectionIndex
    {
        get => _unitSelectionIndex;
        set
        {
            if (Set(ref _unitSelectionIndex, value))
            {
                OnPropertyChanged(nameof(NativePreviewFrame));
                OnPropertyChanged(nameof(PreviewSpeed));
            }
        }
    }
    public int SpeedSourceSelectionIndex { get => _speedSourceSelectionIndex; set => Set(ref _speedSourceSelectionIndex, value); }
    public double OverlayWidthScale { get => _overlayWidthScale; set => Set(ref _overlayWidthScale, value); }
    public double OverlayHeightScale { get => _overlayHeightScale; set => Set(ref _overlayHeightScale, value); }
    public double OverlayOpacity { get => _overlayOpacity; set => Set(ref _overlayOpacity, value); }
    public double Smoothing { get => _smoothing; set => Set(ref _smoothing, value); }
    public bool GForceEnabled { get => _gForceEnabled; set => Set(ref _gForceEnabled, value); }
    public bool GForceAttached { get => _gForceAttached; set => Set(ref _gForceAttached, value); }
    public bool BoostGaugeEnabled { get => _boostGaugeEnabled; set => Set(ref _boostGaugeEnabled, value); }
    public bool BoostGaugeAttached { get => _boostGaugeAttached; set => Set(ref _boostGaugeAttached, value); }
    public bool BoostGaugeColorNumber { get => _boostGaugeColorNumber; set => Set(ref _boostGaugeColorNumber, value); }
    public bool DigitalBoostGaugeColorNumber { get => _digitalBoostGaugeColorNumber; set => Set(ref _digitalBoostGaugeColorNumber, value); }
    public bool DigitalBoostGaugeStockColors { get => _digitalBoostGaugeStockColors; set => Set(ref _digitalBoostGaugeStockColors, value); }
    public double BoostGaugeScale { get => _boostGaugeScale; set => Set(ref _boostGaugeScale, value); }
    public BoostDisplay BoostDisplay
    {
        get => _boostDisplay;
        private set
        {
            if (Set(ref _boostDisplay, value))
            {
                OnPropertyChanged(nameof(PreviewBoostDisplay));
            }
        }
    }
    public bool TireTemperatureGaugeEnabled { get => _tireTemperatureGaugeEnabled; set => Set(ref _tireTemperatureGaugeEnabled, value); }
    public bool TireTemperatureGaugeAttached { get => _tireTemperatureGaugeAttached; set => Set(ref _tireTemperatureGaugeAttached, value); }
    public bool TireTemperatureReactiveColors { get => _tireTemperatureReactiveColors; set => Set(ref _tireTemperatureReactiveColors, value); }
    public bool UseCelsiusTireTemperature
    {
        get => _useCelsiusTireTemperature;
        set
        {
            if (Set(ref _useCelsiusTireTemperature, value))
            {
                OnPropertyChanged(nameof(SelectedTireTemperatureUnit));
            }
        }
    }
    public TireTemperatureUnit SelectedTireTemperatureUnit => UseCelsiusTireTemperature
        ? TireTemperatureUnit.Celsius
        : TireTemperatureUnit.Fahrenheit;
    public double TireTemperatureGaugeScale { get => _tireTemperatureGaugeScale; set => Set(ref _tireTemperatureGaugeScale, value); }
    public TireTemperatureDisplay TireTemperatureDisplay
    {
        get => _tireTemperatureDisplay;
        private set
        {
            if (Set(ref _tireTemperatureDisplay, value))
            {
                OnPropertyChanged(nameof(PreviewTireTemperatureDisplay));
            }
        }
    }
    public double GForceWidthScale { get => _gForceWidthScale; set => Set(ref _gForceWidthScale, value); }
    public double GForceHeightScale { get => _gForceHeightScale; set => Set(ref _gForceHeightScale, value); }
    public int LayoutSelectionIndex
    {
        get => _layoutSelectionIndex;
        set
        {
            if (_layoutSelectionIndex == value)
            {
                return;
            }

            _layoutSelectionIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LayoutSelectionIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNativeLayout)));
        }
    }

    public int NativeGaugeSelectionIndex { get => _nativeGaugeSelectionIndex; set => Set(ref _nativeGaugeSelectionIndex, value); }
    public int GearDisplaySelectionIndex
    {
        get => _gearDisplaySelectionIndex;
        set
        {
            if (Set(ref _gearDisplaySelectionIndex, value))
            {
                OnPropertyChanged(nameof(NativePreviewFrame));
            }
        }
    }
    public bool InvertLateralG { get => _invertLateralG; set => Set(ref _invertLateralG, value); }
    public bool InvertLongitudinalG { get => _invertLongitudinalG; set => Set(ref _invertLongitudinalG, value); }
    public bool IsNativeLayout => LayoutSelectionIndex == (int)HudLayoutMode.Native;

    public bool GameAwareVisibility { get => _gameAwareVisibility; set => Set(ref _gameAwareVisibility, value); }
    public bool AutoMinimizeOnTelemetry { get => _autoMinimizeOnTelemetry; set => Set(ref _autoMinimizeOnTelemetry, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public bool StartWithForza
    {
        get => _startWithForza;
        set
        {
            if (Set(ref _startWithForza, value))
            {
                OnPropertyChanged(nameof(CanSetWindowsStartup));
            }
        }
    }
    public bool CanSetWindowsStartup => !StartWithForza;
    public bool StartMinimizedWithForza { get => _startMinimizedWithForza; set => Set(ref _startMinimizedWithForza, value); }
    public bool AnimatedBackground { get => _animatedBackground; set => Set(ref _animatedBackground, value); }
    public bool TractionCueEnabled { get => _tractionCueEnabled; set => Set(ref _tractionCueEnabled, value); }
    public bool IsTractionCueActive { get => _isTractionCueActive; set => Set(ref _isTractionCueActive, value); }
    public bool CanRelearnCurrentTires { get => _canRelearnCurrentTires; private set => Set(ref _canRelearnCurrentTires, value); }
    public string InstalledApplicationVersion
    {
        get
        {
            var version = typeof(DiagnosticsViewModel).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }
    public string ApplicationUpdateStatus
    {
        get => _applicationUpdateStatus;
        private set => Set(ref _applicationUpdateStatus, value);
    }
    public string ApplicationUpdateAction
    {
        get => _applicationUpdateAction;
        private set => Set(ref _applicationUpdateAction, value);
    }
    public bool CanCheckApplicationUpdate
    {
        get => _canCheckApplicationUpdate;
        private set => Set(ref _canCheckApplicationUpdate, value);
    }

    public void UpdateApplicationUpdateStatus(string status, string action, bool canCheck)
    {
        ApplicationUpdateStatus = status;
        ApplicationUpdateAction = action;
        CanCheckApplicationUpdate = canCheck;
    }

    public void Update(
        VehicleState state,
        IndicatedSpeed speed,
        CalibrationResult calibration,
        ReceiverStatistics statistics,
        TimeSpan packetAge,
        SpeedUnit unit,
        double renderRate,
        bool refreshDiagnostics,
        bool updateGForce,
        SpeedSourceMode speedSource = SpeedSourceMode.WheelIndicated) =>
        Update(
            state,
            speed,
            calibration,
            NativeHudSnapshot.Unavailable(),
            statistics,
            packetAge,
            unit,
            renderRate,
            refreshDiagnostics,
            updateGForce,
            speedSource);

    public void Update(
        VehicleState state,
        IndicatedSpeed speed,
        CalibrationResult calibration,
        NativeHudSnapshot nativeHud,
        ReceiverStatistics statistics,
        TimeSpan packetAge,
        SpeedUnit unit,
        double renderRate,
        bool refreshDiagnostics,
        bool updateGForce,
        SpeedSourceMode speedSource = SpeedSourceMode.WheelIndicated)
    {
        var exactRedline = nativeHud.ExactRedline;
        var nativeAssists = nativeHud.Assists;
        CanRelearnCurrentTires = state.CarOrdinal > 0;
        var displayedSpeed = speed.IsAvailable
            ? NativeGaugeGeometry.ClampSpeed((int)Math.Floor(Math.Max(0, speed.DisplayValue)))
            : 0;
        HudSpeed = speed.IsAvailable
            ? displayedSpeed.ToString(CultureInfo.InvariantCulture)
            : "—";
        BoostDisplay = _boostDisplayModel.Calculate(
            state.CarOrdinal,
            state.IsElectric,
            state.BoostPressurePsi);
        TireTemperatureDisplay = _tireTemperatureDisplayModel.Calculate(
            state.CarOrdinal,
            state.TireTemperatureFahrenheit);
        var nextNativeGaugeFrame = new NativeGaugeFrame(
            speed.IsAvailable,
            displayedSpeed,
            state.EngineRpm,
            nativeHud.TachometerMaximumRpm,
            state.Gear,
            unit,
            exactRedline,
            nativeAssists,
            (GearDisplayMode)Math.Clamp(
                GearDisplaySelectionIndex,
                (int)GearDisplayMode.Manual,
                (int)GearDisplayMode.Automatic),
            state.IsElectric,
            state.PowerWatts,
            state.TorqueNm,
            state.CarOrdinal,
            state.GameTimestampMilliseconds,
            state.Accelerator,
            state.Brake,
            state.ReceivedTimestamp,
            nativeHud.NativeNeedleAngleDegrees,
            nativeHud.NativeNeedleBlurAmount,
            nativeHud.NativeRegenFillAmount,
            nativeHud.NativePowerFillAmount,
            nativeHud.NativeRegenPowerRatio,
            nativeHud.NativeElectricMaximumSpeed,
            nativeHud.NativeGaugeObservedTimestamp,
            IsNativeGaugeSourceInvalidated(nativeHud),
            nativeHud.ElectricGearState,
            nativeHud.DisplayedSpeedState,
            speedSource);
        NativeGaugeFrame = nextNativeGaugeFrame.PreserveStableTachometerState(NativeGaugeFrame);
        HasLiveTelemetry = true;
        GForceDisplay? gForce = null;
        if (updateGForce)
        {
            // Keep the 14 px ball fully inside the 76 px traction circle.
            const double displayRadius = 31;
            gForce = _gForceDisplayModel.Calculate(
                state.LateralAccelerationMetersPerSecondSquared,
                state.LongitudinalAccelerationMetersPerSecondSquared,
                state.ReceivedAtUtc);
            var offsetX = gForce.Value.NormalizedX * displayRadius * (InvertLateralG ? -1 : 1);
            var offsetY = gForce.Value.NormalizedY * displayRadius * (InvertLongitudinalG ? -1 : 1);
            GForceOffsetX = offsetX;
            GForceOffsetY = offsetY;
            GForceTrailPosition = new Point(offsetX, offsetY);
        }

        if (!refreshDiagnostics)
        {
            return;
        }

        if (gForce is { } currentGForce)
        {
            LateralGText = $"{currentGForce.LateralG:+0.00;-0.00;0.00} g";
            LongitudinalGText = $"{currentGForce.LongitudinalG:+0.00;-0.00;0.00} g";
            var scale = currentGForce.FullScaleG.ToString("0.##", CultureInfo.InvariantCulture);
            GForceScaleText = $"{scale} G{(currentGForce.IsOverRange ? "+" : string.Empty)}";
        }

        var multiplier = unit == SpeedUnit.MilesPerHour
            ? SpeedModel.MetersPerSecondToMilesPerHour
            : SpeedModel.MetersPerSecondToKilometersPerHour;
        var suffix = unit == SpeedUnit.MilesPerHour ? "mph" : "km/h";
        StatusText = speedSource == SpeedSourceMode.Fh6VehicleSpeed
            ? "FH6 Speed Ready"
            : calibration.IsCalibrated ? "Wheel Speed Ready" : "Learning Tire Size";
        StatusDetail = speedSource == SpeedSourceMode.Fh6VehicleSpeed
            ? "FH6 vehicle speed is active"
            : calibration.IsCalibrated
                ? "Driven-wheel speed is active"
                : CalibrationStatusDetail(calibration.RejectionReason);
        PacketRate = $"{statistics.PacketsPerSecond:F1} Hz";
        RenderRate = $"{renderRate:F1} Hz";
        PacketAge = $"{packetAge.TotalMilliseconds:F0} ms";
        RejectedPackets = statistics.RejectedPackets.ToString();
        CarOrdinal = state.CarOrdinal.ToString();
        Drivetrain = FormatDrivetrain(state.Drivetrain);
        GroundSpeed = $"{Math.Abs(state.GroundSpeedMetersPerSecond) * multiplier:F1} {suffix}";
        IndicatedSpeed = speed.IsAvailable
            ? $"{speed.DisplayValue:F1} {suffix}"
            : speedSource == SpeedSourceMode.Fh6VehicleSpeed
                ? "Unavailable — waiting for FH6 speed"
                : "Unavailable — verifying tire size";
        var wheels = state.WheelRotationRadiansPerSecond;
        WheelSpeeds = $"FL {wheels.FrontLeft:F1}  FR {wheels.FrontRight:F1}  RL {wheels.RearLeft:F1}  RR {wheels.RearRight:F1}";
        var slip = state.TireSlipRatio;
        SlipRatios = $"FL {slip.FrontLeft:F2}  FR {slip.FrontRight:F2}  RL {slip.RearLeft:F2}  RR {slip.RearRight:F2}";
        Radius = speedSource == SpeedSourceMode.Fh6VehicleSpeed
            ? "Not required for FH6 speed"
            : FormatRadii(
                calibration.TrustedRadii,
                calibration.ProvisionalRadii,
                state.Drivetrain);
        EngineRpm = $"{state.EngineRpm:F0} rpm";
        EngineMaximumRpm = $"{state.EngineMaximumRpm:F0} rpm";
        NativeTachScale = nativeHud.Available
            ? $"0–{NativeGaugeGeometry.ScaleMaximumThousands(nativeHud.TachometerMaximumRpm)} ×1000 rpm · redline {exactRedline.Rpm:F0}"
            : "Unavailable";
        ExactNativeRedline = exactRedline.IsExact
            ? $"{exactRedline.Rpm:F0} rpm"
            : FormatExactRedlineStatus(exactRedline.Status);
        ExactRedlineSource = exactRedline.IsExact ? exactRedline.Source : "—";
        ExactRedlineStateText = nativeHud.Available
            ? "Ready"
            : FormatNativeAssistStatus(nativeHud.Status);
        NativeAssistState = nativeAssists.Available
            ? "Ready"
            : FormatNativeAssistStatus(nativeAssists.Status);
        NativeAssistDetails = nativeAssists.Available
            ? string.Join(
                " · ",
                AssistText("ABS", nativeAssists.IsABSAvailable, nativeAssists.IsABSOn),
                AssistText("TCR", nativeAssists.IsTCRAvailable, nativeAssists.IsTCROn),
                AssistText("STM", nativeAssists.IsSTMAvailable, nativeAssists.IsSTMOn),
                AssistText("LC", nativeAssists.IsLCAvailable, nativeAssists.IsLCOn))
            : "—";
        var requiredSamples = CalibrationOptions.DefaultMinimumSamples;
        Confidence = speedSource == SpeedSourceMode.Fh6VehicleSpeed
            ? "N/A"
            : calibration.IsTrusted
                ? calibration.Confidence.ToString("P0")
                : calibration.ProvisionalRadiusMeters is null && calibration.AcceptedSamples >= requiredSamples
                    ? "VERIFYING"
                    : $"{Math.Min(calibration.AcceptedSamples, requiredSamples)}/{requiredSamples}";
        SelectedWheels = speed.SelectedWheels;
    }

    internal bool UpdateNativeHudSnapshot(NativeHudSnapshot nativeHud)
    {
        ArgumentNullException.ThrowIfNull(nativeHud);
        var frame = NativeGaugeFrame;
        if (!HasLiveTelemetry || frame.CarOrdinal <= 0 || nativeHud.CarOrdinal != frame.CarOrdinal)
        {
            return false;
        }

        var nextNativeGaugeFrame = frame with
        {
            TachometerMaximumRpm = nativeHud.TachometerMaximumRpm,
            ExactRedline = nativeHud.ExactRedline,
            Assists = nativeHud.Assists,
            NativeNeedleAngleDegrees = nativeHud.NativeNeedleAngleDegrees,
            NativeNeedleBlurAmount = nativeHud.NativeNeedleBlurAmount,
            NativeRegenFillAmount = nativeHud.NativeRegenFillAmount,
            NativePowerFillAmount = nativeHud.NativePowerFillAmount,
            NativeRegenPowerRatio = nativeHud.NativeRegenPowerRatio,
            NativeElectricMaximumSpeed = nativeHud.NativeElectricMaximumSpeed,
            NativeGaugeObservedTimestamp = nativeHud.NativeGaugeObservedTimestamp,
            NativeGaugeSourceInvalidated = IsNativeGaugeSourceInvalidated(nativeHud),
            ElectricGearState = nativeHud.ElectricGearState,
            DisplayedSpeedState = nativeHud.DisplayedSpeedState
        };
        NativeGaugeFrame = nextNativeGaugeFrame.PreserveStableTachometerState(frame);
        return true;
    }

    internal static bool IsNativeGaugeSourceInvalidated(NativeHudSnapshot nativeHud) =>
        !nativeHud.HasAvailableCapabilities &&
        nativeHud.Status is NativeAssistProviderStatus.Unavailable or
            NativeAssistProviderStatus.GameNotRunning or
            NativeAssistProviderStatus.UnsupportedBuild or
            NativeAssistProviderStatus.AccessDenied;

    public void UpdateWaiting(
        ReceiverStatistics statistics,
        TelemetryConnectionState connectionState,
        TimeSpan? age,
        double renderRate,
        bool preserveHudVisuals = false)
    {
        HasLiveTelemetry = false;
        CanRelearnCurrentTires = false;
        StatusText = connectionState == TelemetryConnectionState.Lost ? "Telemetry Lost" : "Waiting for FH6";
        StatusDetail = statistics.ListenerError is not null
            ? statistics.ListenerError
            : statistics.RejectedPackets > 0 && statistics.AcceptedPackets == 0
                ? $"Packets rejected · last error: {statistics.LastParseError}"
                : "Enable Data Out to 127.0.0.1 using the port below";
        PacketRate = $"{statistics.PacketsPerSecond:F1} Hz";
        RenderRate = renderRate > 0 ? $"{renderRate:F1} Hz" : "Measuring";
        PacketAge = age is null ? "—" : $"{age.Value.TotalMilliseconds:F0} ms";
        RejectedPackets = statistics.RejectedPackets.ToString();
        if (!preserveHudVisuals)
        {
            ClearHudVisuals();
        }
        EngineRpm = "—";
        EngineMaximumRpm = "—";
        NativeTachScale = "—";
        ExactNativeRedline = "Unavailable";
        ExactRedlineSource = "—";
        ExactRedlineStateText = "Inactive";
        NativeAssistState = "Unavailable";
        NativeAssistDetails = "—";
    }

    public void ClearHudVisuals()
    {
        HasLiveTelemetry = false;
        GameplayHudVisibility = "Unavailable";
        GForceOffsetX = 0;
        GForceOffsetY = 0;
        GForceTrailPosition = default;
        _gForceDisplayModel.Reset();
        _boostDisplayModel.Reset();
        BoostDisplay = BoostDisplay.Unavailable;
        TireTemperatureDisplay = TireTemperatureDisplay.Unavailable;
        LateralGText = "0.00 g";
        LongitudinalGText = "0.00 g";
        GForceScaleText = "1.0 G";
        NativeGaugeFrame = NativeGaugeFrame.Empty(UnitSelectionIndex == 0
            ? SpeedUnit.MilesPerHour
            : SpeedUnit.KilometersPerHour);
    }

    public void MarkTireProfileReset()
    {
        HudSpeed = "—";
        IndicatedSpeed = "Unavailable — learning tire size";
        Radius = "Waiting for clean data";
        Confidence = $"0/{CalibrationOptions.DefaultMinimumSamples}";
        StatusText = "Learning Tire Size";
        StatusDetail = "Tire profile cleared; drive straight and steady with clean grip";
    }

    public void ReportControlError(string detail)
    {
        HasLiveTelemetry = false;
        StatusText = "Action Required";
        StatusDetail = detail;
    }

    private static string FormatDrivetrain(DrivetrainType drivetrain) => drivetrain switch
    {
        DrivetrainType.FrontWheelDrive => "FWD",
        DrivetrainType.RearWheelDrive => "RWD",
        DrivetrainType.AllWheelDrive => "AWD",
        _ => "Unknown"
    };

    private static string FormatRadii(
        RollingRadii? trusted,
        RollingRadii? provisional,
        DrivetrainType drivetrain)
    {
        var radii = trusted ?? provisional;
        if (radii is not { } value)
        {
            return "Waiting for clean data";
        }

        var suffix = trusted.HasValue ? string.Empty : " (learning)";
        return drivetrain == DrivetrainType.AllWheelDrive
            ? $"F {value.FrontMeters:F4} m  R {value.RearMeters:F4} m{suffix}"
            : $"{value.Representative(drivetrain):F4} m{suffix}";
    }

    private static string CalibrationStatusDetail(string reason) => reason switch
    {
        "Verifying changed tire setup" => "Confirming a different tire or tune profile with clean wheel data",
        "Changed tire candidate outlier" => "Ignoring one inconsistent tune-change sample",
        "Radius change pending" => "A tire-size change was detected and is being verified",
        "Ground speed outside calibration range" => "Drive above 7 mph, straight and steady, with clean grip",
        "Tire slip ratio" or "Wheel speeds disagree" =>
            "Wait for clean grip, then drive straight and steady for a moment",
        "Steering input" or "Cornering acceleration" =>
            "Straighten the car and hold a steady line for a moment",
        "Longitudinal acceleration" or "Braking input" =>
            "Hold a steady speed without braking for a moment",
        "Driven axle unloaded" => "Keep the driven tires loaded and on the road",
        "Stable tire consensus pending" => "Collecting matching clean wheel samples; keep a steady line",
        "Candidate radius outlier" => "Ignoring one inconsistent wheel sample; keep driving steadily",
        _ => "Drive straight and steady with clean grip for a moment"
    };

    private static string FormatExactRedlineStatus(ExactRedlineStatus status) => status switch
    {
        ExactRedlineStatus.GameNotRunning => "FH6 not running",
        ExactRedlineStatus.UnsupportedBuild => "Unsupported FH6 build",
        ExactRedlineStatus.AccessDenied => "Read access denied",
        ExactRedlineStatus.InvalidProvider => "Provider unavailable",
        ExactRedlineStatus.PlayerNotUnique => "Local player unresolved",
        ExactRedlineStatus.TelemetryMismatch => "Telemetry mismatch",
        ExactRedlineStatus.ReadFailure => "Read unavailable",
        _ => "Unavailable"
    };

    private static string FormatNativeAssistStatus(NativeAssistProviderStatus status) => status switch
    {
        NativeAssistProviderStatus.Unavailable => "Inactive",
        NativeAssistProviderStatus.GameNotRunning => "FH6 not running",
        NativeAssistProviderStatus.UnsupportedBuild => "Unsupported FH6 build",
        NativeAssistProviderStatus.AccessDenied => "Read access denied",
        NativeAssistProviderStatus.InvalidSourceVector => "Source unavailable",
        NativeAssistProviderStatus.InvalidProvider => "Provider unavailable",
        NativeAssistProviderStatus.PlayerNotUnique => "Local player unresolved",
        NativeAssistProviderStatus.TelemetryMismatch => "Telemetry mismatch",
        NativeAssistProviderStatus.ReadFailure => "Read unavailable",
        NativeAssistProviderStatus.Ready => "Ready",
        _ => "Unavailable"
    };

    private static string AssistText(string name, bool available, bool on) =>
        available ? $"{name} {(on ? "ON" : "OFF")}" : $"{name} —";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
