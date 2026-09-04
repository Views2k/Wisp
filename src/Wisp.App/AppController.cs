using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Telemetry;
using Wisp.Update;

namespace Wisp.App;

public sealed class AppController : IAsyncDisposable
{
    private static readonly TimeSpan ConnectedTimerInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan IdleTimerInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TelemetryTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RaceOffHysteresis = TimeSpan.FromMilliseconds(100);
    private static readonly long NativeVisibilityFreshnessTicks = Stopwatch.Frequency / 4;

    private readonly Action<AppSettings> _saveSettings;
    private readonly Action<AppSettings> _saveCompletedSetup;
    private readonly TelemetryUdpReceiver _receiver = new();
    private readonly RollingRadiusEstimator _calibration = new();
    private readonly SpeedModel _speedModel = new();
    private readonly TractionHookDetector _tractionHookDetector = new();
    private readonly TransmissionDisplayFilter _transmissionDisplayFilter = new();
    private readonly DisplayFrameRateCounter _displayFrameRateCounter = new();
    private readonly ForzaFocusService _forzaFocusService = new();
    private readonly IStartupRegistrationService _startupRegistrationService;
    private readonly NativeHudProcessService _nativeHudProcessService = new();
    private readonly NativeCompatibilityUpdateClient _compatibilityUpdates = NativeCompatibilityRuntime.CreateUpdateClient();
    private readonly CancellationTokenSource _compatibilityLifetime = new();
    private readonly WispUpdateClient _applicationUpdates = WispUpdateClient.CreateDefault();
    private readonly CancellationTokenSource _applicationUpdateLifetime = new();
    private readonly Dictionary<int, RollingRadii> _savedCalibrationRadii;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private TelemetryFreshness _freshness = new(TelemetryTimeout);
    private VehicleState? _lastFreshnessState;
    private VehicleState? _lastProcessedState;
    private DateTimeOffset _lastRenderAtUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _nextStatisticsAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextCompatibilityCheckAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextDiagnosticsAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextVisibilityCheckAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextFullscreenZOrderAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? _raceOffObservedAtUtc;
    private TimeSpan _lastCompositionRenderingTime = TimeSpan.MinValue;
    private int _activationDispatchPending;
    private double _renderRate;
    private bool _receiverNotificationsAttached;
    private bool _compositionRenderingAttached;
    private bool _applyingViewOptions;
    private bool _wasDrivingConnected;
    private bool _nativeHudTelemetryActive;
    private bool _autoMinimizeWasDriving;
    private bool _overlayVisibleRequested;
    private bool _lastForzaFullscreen;
    private IntPtr _lastConfirmedForzaWindow;
    private bool _disposed;
    private bool _runtimeSuspended;
    private bool _startupRegistrationInitialized;
    private bool _startupRegistrationSucceeded;
    private bool _compatibilityImportRunning;
    private int _applicationUpdateOperation;
    private VerifiedInstaller? _pendingInstaller;
    private string? _compatibilityImportStatus;
    private DateTimeOffset _tractionCueUntilUtc = DateTimeOffset.MinValue;
    private ReceiverStatistics _cachedStatistics;
    private string? _activeOverlayPlacementKey;
    private NativeHudPublicationKey _lastNativeHudPublication;
    private bool _hasNativeHudPublication;

    public AppController(AppSettings settings, SettingsService settingsService)
        : this(
            settings,
            settingsService.Save,
            new StartupRegistrationService(),
            settingsService.SaveCompletedSetup)
    {
    }

    internal AppController(
        AppSettings settings,
        Action<AppSettings> saveSettings,
        IStartupRegistrationService startupRegistrationService,
        Action<AppSettings>? saveCompletedSetup = null)
    {
        Settings = settings;
        _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
        _saveCompletedSetup = saveCompletedSetup ?? _saveSettings;
        _startupRegistrationService = startupRegistrationService;
        SetupTelemetry = new SetupTelemetryTest(new SetupTelemetrySource(_receiver));
        _calibration.ImportSnapshots(settings.Calibrations);
        _savedCalibrationRadii = settings.Calibrations
            .Where(snapshot =>
                snapshot.Drivetrain is { } drivetrain &&
                RollingRadiusEstimator.TrySnapshotRadii(snapshot, drivetrain, out _))
            .GroupBy(snapshot => snapshot.CarOrdinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var snapshot = group.Last();
                    _ = RollingRadiusEstimator.TrySnapshotRadii(
                        snapshot,
                        snapshot.Drivetrain!.Value,
                        out var radii);
                    return radii;
                });
        ViewModel = new DiagnosticsViewModel(settings);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _uiTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = IdleTimerInterval
        };
        _uiTimer.Tick += OnUiTimer;
        _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SaveSettings();
        };
        UpdateCompatibilityDiagnostics();
    }

    public AppSettings Settings { get; }
    public DiagnosticsViewModel ViewModel { get; }
    public SetupTelemetryTest SetupTelemetry { get; }
    public OverlayWindow? Overlay { get; set; }
    public GForceWindow? GForceOverlay { get; set; }
    public BoostGaugeWindow? BoostGaugeOverlay { get; set; }
    public TireTemperatureGaugeWindow? TireTemperatureGaugeOverlay { get; set; }
    public MainWindow? ControlPanel { get; set; }
    public event EventHandler? StartupOptionsChanged;
    public bool IsAttachedGForceMeterEnabled =>
        Settings.GForceEnabled && Settings.GForceAttached &&
        Settings.LayoutMode == HudLayoutMode.Native;
    public bool IsStandaloneGForceWindowEnabled =>
        Settings.GForceEnabled && Settings.LayoutMode != HudLayoutMode.Combined &&
        !IsAttachedGForceMeterEnabled;
    public bool IsDetachedBoostGaugeEnabled =>
        Settings.BoostGaugeEnabled && !Settings.BoostGaugeAttached &&
        Settings.LayoutMode == HudLayoutMode.Native &&
        Settings.NativeGaugeMode == NativeGaugeMode.Analogue &&
        !ViewModel.NativeGaugeFrame.IsElectric &&
        ViewModel.BoostDisplay.IsAvailable;
    public bool IsDetachedTireTemperatureGaugeEnabled =>
        Settings.TireTemperatureGaugeEnabled && !Settings.TireTemperatureGaugeAttached &&
        Settings.LayoutMode == HudLayoutMode.Native &&
        ViewModel.TireTemperatureDisplay.IsAvailable;

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Settings.RequiresSetup)
        {
            throw new InvalidOperationException("Complete the setup wizard before starting Wisp.");
        }

        InitializeStartupRegistration();
        _runtimeSuspended = false;

        if (!_receiverNotificationsAttached)
        {
            _receiver.PacketAvailable += OnPacketAvailable;
            _receiverNotificationsAttached = true;
        }

        _uiTimer.Start();
        await RestartListenerAsync(Settings.UdpPort);
    }

    internal bool InitializeStartupRegistration()
    {
        if (_disposed || Settings.RequiresSetup)
        {
            return false;
        }
        if (_startupRegistrationInitialized)
        {
            return _startupRegistrationSucceeded;
        }

        _startupRegistrationInitialized = true;
        var enableRequested = Settings.StartWithWindows || Settings.StartWithForza;
        _startupRegistrationSucceeded = TrySetStartupRegistration(
            Settings.StartWithWindows, Settings.StartWithForza);
        if (!_startupRegistrationSucceeded && !enableRequested)
        {
            ViewModel.ReportControlError(
                "Windows could not remove Wisp's sign-in entry; automatic startup remains disabled in Wisp. Restart Wisp to retry.");
        }
        else if (!_startupRegistrationSucceeded)
        {
            Settings.StartWithWindows = false;
            Settings.StartWithForza = false;
            ViewModel.StartWithWindows = false;
            ViewModel.StartWithForza = false;
            ViewModel.ReportControlError("Windows could not enable automatic startup; both startup options were left off");
            SaveSettings();
            StartupOptionsChanged?.Invoke(this, EventArgs.Empty);
        }
        return _startupRegistrationSucceeded;
    }

    internal async Task SuspendForForzaAsync()
    {
        if (_disposed)
        {
            return;
        }

        // Closing to the opt-in companion releases UDP and native demand.
        // Queued packet/compositor callbacks cannot restart a suspended session.
        _runtimeSuspended = true;
        _uiTimer.Stop();
        _settingsSaveTimer.Stop();
        if (_receiverNotificationsAttached)
        {
            _receiver.PacketAvailable -= OnPacketAvailable;
            _receiverNotificationsAttached = false;
        }
        ResetControllerSession();
        WindowZOrder.DetachFromGame(Overlay);
        WindowZOrder.DetachFromGame(GForceOverlay);
        WindowZOrder.DetachFromGame(BoostGaugeOverlay);
        WindowZOrder.DetachFromGame(TireTemperatureGaugeOverlay);
        if (!Settings.RequiresSetup)
        {
            Settings.Calibrations = _calibration.ExportSnapshots().ToList();
            SaveSettings();
        }
        await _receiver.StopAsync().ConfigureAwait(false);
    }

    public void CompleteSetup(SetupPreferences preferences)
    {
        if (SetupTelemetry.IsRunning)
        {
            throw new InvalidOperationException("Wait for the Data Out test to finish before completing setup.");
        }

        SetupCompletion.Save(
            Settings, preferences, SetupTelemetry.SuccessfulEvidence, _saveCompletedSetup, DateTimeOffset.UtcNow);
        ViewModel.UdpPort = Settings.UdpPort;
        ViewModel.UnitSelectionIndex = (int)Settings.SpeedUnit;
        ViewModel.SpeedSourceSelectionIndex = (int)Settings.SpeedSource;
        ViewModel.LayoutSelectionIndex = (int)Settings.LayoutMode;
        ViewModel.NativeGaugeSelectionIndex = (int)Settings.NativeGaugeMode;
        ViewModel.GearDisplaySelectionIndex = (int)Settings.GearDisplayMode;
    }

    public async Task CheckNativeCompatibilityUpdatesAsync()
    {
        if (_disposed)
        {
            return;
        }

        _nextCompatibilityCheckAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromDays(1);
        _compatibilityImportStatus = null;
        var check = _compatibilityUpdates.CheckOnceAsync(_compatibilityLifetime.Token);
        UpdateCompatibilityDiagnostics();
        await check;
        if (!_disposed)
        {
            UpdateCompatibilityDiagnostics();
        }
    }

    public async Task<VerifiedInstaller?> CheckForApplicationUpdateAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _applicationUpdateOperation, 1) != 0)
        {
            return null;
        }

        string? createdAttemptDirectory = null;
        try
        {
            var retainedAttemptDirectory = _pendingInstaller is { } retainedPending &&
                                           File.Exists(retainedPending.StagedPath)
                ? Path.GetDirectoryName(retainedPending.StagedPath)
                : null;
            ApplicationUpdateStaging.TryPrune(retainedAttemptDirectory);

            if (_pendingInstaller is { } pending && File.Exists(pending.StagedPath))
            {
                ViewModel.UpdateApplicationUpdateStatus(
                    $"Wisp {pending.Version} is downloaded and ready to install.",
                    "Install update",
                    canCheck: true);
                return pending;
            }

            _pendingInstaller = null;
            ViewModel.UpdateApplicationUpdateStatus(
                "Checking the latest Wisp release…",
                "Checking…",
                canCheck: false);

            var installedVersion = CurrentApplicationVersion();
            var release = await _applicationUpdates.CheckForUpdateAsync(
                installedVersion,
                _applicationUpdateLifetime.Token);
            if (release is null)
            {
                ViewModel.UpdateApplicationUpdateStatus(
                    $"Wisp {installedVersion.Major}.{installedVersion.Minor}.{installedVersion.Build} is current.",
                    "Check again",
                    canCheck: true);
                return null;
            }

            createdAttemptDirectory = ApplicationUpdateStaging.CreateAttemptDirectory(release.Version);
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                ViewModel.UpdateApplicationUpdateStatus(
                    $"Downloading Wisp {release.Version} — {Math.Clamp(value.Percentage, 0, 100):0}%",
                    "Downloading…",
                    canCheck: false);
            });
            _pendingInstaller = await _applicationUpdates.DownloadInstallerAsync(
                release,
                createdAttemptDirectory,
                progress,
                _applicationUpdateLifetime.Token);
            ViewModel.UpdateApplicationUpdateStatus(
                $"Wisp {_pendingInstaller.Version} is verified and ready to install.",
                "Install update",
                canCheck: true);
            return _pendingInstaller;
        }
        catch (OperationCanceledException) when (_applicationUpdateLifetime.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            ViewModel.UpdateApplicationUpdateStatus(
                "The update check timed out. No files were installed.",
                "Try again",
                canCheck: true);
            return null;
        }
        catch (HttpRequestException)
        {
            ViewModel.UpdateApplicationUpdateStatus(
                "The release service could not be reached. Check the connection and try again.",
                "Try again",
                canCheck: true);
            return null;
        }
        catch (UpdateSecurityException)
        {
            ViewModel.UpdateApplicationUpdateStatus(
                "The latest release could not be verified. No update was installed.",
                "Check again",
                canCheck: true);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ViewModel.UpdateApplicationUpdateStatus(
                "Wisp could not prepare the update on this PC. No update was installed.",
                "Try again",
                canCheck: true);
            return null;
        }
        finally
        {
            if (createdAttemptDirectory is not null && _pendingInstaller is null)
            {
                ApplicationUpdateStaging.TryDeleteAttemptDirectory(createdAttemptDirectory);
            }
            Interlocked.Exchange(ref _applicationUpdateOperation, 0);
        }
    }

    public void MarkApplicationUpdateDeferred(VerifiedInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ViewModel.UpdateApplicationUpdateStatus(
            $"Wisp {installer.Version} is downloaded and ready to install.",
            "Install update",
            canCheck: true);
    }

    public void MarkApplicationUpdateStarting(VerifiedInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ViewModel.UpdateApplicationUpdateStatus(
            $"Installing Wisp {installer.Version}…",
            "Restarting…",
            canCheck: false);
    }

    public void MarkApplicationUpdatePreparing(VerifiedInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ViewModel.UpdateApplicationUpdateStatus(
            $"Preparing verified Wisp {installer.Version} for installation…",
            "Preparing…",
            canCheck: false);
    }

    public async Task ImportNativeCompatibilityPackAsync(string path)
    {
        if (_disposed || _compatibilityImportRunning)
        {
            return;
        }

        _compatibilityImportRunning = true;
        UpdateCompatibilityDiagnostics();
        try
        {
            var result = await NativeCompatibilityRuntime.ImportFileAsync(
                NativeCompatibilityRuntime.Catalog, path, _compatibilityLifetime.Token);
            _compatibilityImportStatus = result.Message;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _compatibilityImportRunning = false;
            if (!_disposed)
            {
                UpdateCompatibilityDiagnostics();
            }
        }
    }

    private void UpdateCompatibilityDiagnostics() => ViewModel.UpdateNativeCompatibility(
        _nativeHudProcessService.CompatibilityStatus,
        NativeCompatibilityRuntime.DescribeCatalog(NativeCompatibilityRuntime.Catalog) + " " +
        (_compatibilityImportStatus ?? (_compatibilityUpdates.IsConfigured
            ? _compatibilityUpdates.Status
            : "Offline: no update publisher configured.")),
        _compatibilityUpdates.IsConfigured && !_compatibilityUpdates.IsChecking,
        NativeCompatibilityRuntime.Catalog.HasTrustedPublishers && !_compatibilityImportRunning);

    public async Task RestartListenerAsync(int port)
    {
        TelemetryUdpReceiver.ValidatePort(port);
        if (Settings.RequiresSetup)
        {
            throw new InvalidOperationException("Use the setup wizard's Data Out test before starting the dashboard listener.");
        }
        if (_runtimeSuspended)
        {
            throw new InvalidOperationException("Open Wisp before starting its telemetry listener.");
        }

        if (_receiver.IsRunning && _receiver.ListeningPort == port)
        {
            Settings.UdpPort = port;
            ViewModel.UdpPort = port;
            return;
        }

        try
        {
            await _receiver.RestartAsync(port);
        }
        catch
        {
            ViewModel.UdpPort = Settings.UdpPort;
            throw;
        }

        Settings.UdpPort = port;
        ResetControllerSession();
        ScheduleSettingsSave();
    }

    public void ApplyViewOptions()
    {
        if (Settings.RequiresSetup || _applyingViewOptions)
        {
            return;
        }

        _applyingViewOptions = true;
        try
        {
            var previousStartWithWindows = Settings.StartWithWindows;
            var previousStartWithForza = Settings.StartWithForza;
            var previousStartMinimizedWithForza = Settings.StartMinimizedWithForza;
            var previousSpeedSource = Settings.SpeedSource;
            var layoutMode = (HudLayoutMode)Math.Clamp(
                ViewModel.LayoutSelectionIndex,
                (int)HudLayoutMode.Minimal,
                (int)HudLayoutMode.Native);
            var nativeGaugeMode = (NativeGaugeMode)Math.Clamp(
                ViewModel.NativeGaugeSelectionIndex,
                (int)NativeGaugeMode.Digital,
                (int)NativeGaugeMode.Analogue);
            var layoutChanged = Settings.LayoutMode != layoutMode ||
                                Settings.NativeGaugeMode != nativeGaugeMode;
            Settings.SpeedUnit = ViewModel.UnitSelectionIndex == 0
                ? SpeedUnit.MilesPerHour
                : SpeedUnit.KilometersPerHour;
            Settings.SpeedSource = (SpeedSourceMode)Math.Clamp(
                ViewModel.SpeedSourceSelectionIndex,
                (int)SpeedSourceMode.WheelIndicated,
                (int)SpeedSourceMode.Fh6VehicleSpeed);
            if (Settings.SpeedSource != previousSpeedSource)
            {
                _speedModel.Reset();
            }
            Settings.AggregationMode = WheelAggregationMode.RawDrivenWheels;
            Settings.OverlayWidthScale = Math.Clamp(ViewModel.OverlayWidthScale, 0.5, 2.0);
            Settings.OverlayHeightScale = Math.Clamp(ViewModel.OverlayHeightScale, 0.5, 2.0);
            Settings.OverlayOpacity = Math.Clamp(ViewModel.OverlayOpacity, 0.35, 1.0);
            Settings.Smoothing = Math.Clamp(ViewModel.Smoothing, 0, 1);
            Settings.GForceEnabled = ViewModel.GForceEnabled;
            Settings.GForceAttached = ViewModel.GForceAttached;
            Settings.BoostGaugeEnabled = ViewModel.BoostGaugeEnabled;
            Settings.BoostGaugeAttached = ViewModel.BoostGaugeAttached;
            Settings.BoostGaugeColorNumber = ViewModel.BoostGaugeColorNumber;
            Settings.DigitalBoostGaugeColorNumber = ViewModel.DigitalBoostGaugeColorNumber;
            Settings.DigitalBoostGaugeStockColors = ViewModel.DigitalBoostGaugeStockColors;
            Settings.BoostPressureUnit = ViewModel.SelectedBoostPressureUnit;
            Settings.BoostGaugeScale = Math.Clamp(ViewModel.BoostGaugeScale, 0.5, 2.0);
            Settings.TireTemperatureGaugeEnabled = ViewModel.TireTemperatureGaugeEnabled;
            Settings.TireTemperatureGaugeAttached = ViewModel.TireTemperatureGaugeAttached;
            Settings.TireTemperatureReactiveColors = ViewModel.TireTemperatureReactiveColors;
            Settings.TireTemperatureUnit = ViewModel.SelectedTireTemperatureUnit;
            Settings.TireTemperatureGaugeScale = Math.Clamp(
                ViewModel.TireTemperatureGaugeScale,
                0.5,
                2.0);
            Settings.GForceWidthScale = Math.Clamp(ViewModel.GForceWidthScale, 0.5, 2.0);
            Settings.GForceHeightScale = Math.Clamp(ViewModel.GForceHeightScale, 0.5, 2.0);
            Settings.LayoutMode = layoutMode;
            Settings.NativeGaugeMode = nativeGaugeMode;
            Settings.GearDisplayMode = (GearDisplayMode)Math.Clamp(
                ViewModel.GearDisplaySelectionIndex,
                (int)GearDisplayMode.Manual,
                (int)GearDisplayMode.Automatic);
            Settings.InvertLateralG = ViewModel.InvertLateralG;
            Settings.InvertLongitudinalG = ViewModel.InvertLongitudinalG;
            Settings.GameAwareVisibility = ViewModel.GameAwareVisibility;
            Settings.AutoMinimizeOnTelemetry = ViewModel.AutoMinimizeOnTelemetry;
            Settings.StartWithWindows = ViewModel.StartWithWindows;
            Settings.StartWithForza = ViewModel.StartWithForza;
            Settings.StartMinimizedWithForza = ViewModel.StartMinimizedWithForza;
            Settings.AnimatedBackground = ViewModel.AnimatedBackground;
            Settings.TractionCueEnabled = ViewModel.TractionCueEnabled;
            if ((previousStartWithWindows != Settings.StartWithWindows ||
                 previousStartWithForza != Settings.StartWithForza) &&
                !TrySetStartupRegistration(Settings.StartWithWindows, Settings.StartWithForza))
            {
                Settings.StartWithWindows = previousStartWithWindows;
                Settings.StartWithForza = previousStartWithForza;
                Settings.StartMinimizedWithForza = previousStartMinimizedWithForza;
                ViewModel.StartWithWindows = previousStartWithWindows;
                ViewModel.StartWithForza = previousStartWithForza;
                ViewModel.StartMinimizedWithForza = previousStartMinimizedWithForza;
                ViewModel.ReportControlError("Windows could not change automatic startup; the previous options were restored");
            }
            if (previousStartWithWindows != Settings.StartWithWindows ||
                previousStartWithForza != Settings.StartWithForza ||
                previousStartMinimizedWithForza != Settings.StartMinimizedWithForza)
            {
                StartupOptionsChanged?.Invoke(this, EventArgs.Empty);
            }

            Overlay?.ApplyLayout(
                Settings.LayoutMode,
                Settings.NativeGaugeMode,
                Settings.OverlayWidthScale,
                Settings.OverlayHeightScale,
                Settings.OverlayOpacity);
            GForceOverlay?.SetEnabled(IsStandaloneGForceWindowEnabled);
            GForceOverlay?.ApplyAppearance(
                Settings.GForceWidthScale,
                Settings.GForceHeightScale,
                Settings.OverlayOpacity);
            BoostGaugeOverlay?.SetEnabled(IsDetachedBoostGaugeEnabled);
            BoostGaugeOverlay?.ApplyAppearance(Settings.BoostGaugeScale, Settings.OverlayOpacity);
            TireTemperatureGaugeOverlay?.ApplyGaugeMode(Settings.NativeGaugeMode);
            TireTemperatureGaugeOverlay?.SetEnabled(IsDetachedTireTemperatureGaugeEnabled);
            TireTemperatureGaugeOverlay?.ApplyAppearance(
                Settings.TireTemperatureGaugeScale,
                Settings.OverlayOpacity);
            if (layoutChanged)
            {
                RestoreOverlayPlacement();
                if (Settings.LayoutMode is HudLayoutMode.SeparateBoxes or HudLayoutMode.Native)
                {
                    RestoreGForcePlacement();
                }
                if (IsDetachedBoostGaugeEnabled)
                {
                    RestoreBoostGaugePlacement();
                }
                if (IsDetachedTireTemperatureGaugeEnabled)
                {
                    RestoreTireTemperatureGaugePlacement();
                }
            }

            UpdateCurrentPlacementScales();

            UpdateOverlayVisibility(DateTimeOffset.UtcNow, force: true);
            ScheduleSettingsSave();
        }
        finally
        {
            _applyingViewOptions = false;
        }
    }

    public void SetColorTheme(string? themeName)
    {
        var normalized = AppColorThemes.NormalizeName(themeName);
        if (Settings.ColorTheme == normalized)
        {
            return;
        }

        Settings.ColorTheme = normalized;
        ScheduleSettingsSave();
    }

    public void SetBackgroundTheme(string? themeName)
    {
        var normalized = AppBackgroundThemes.NormalizeName(themeName);
        if (Settings.BackgroundTheme == normalized)
        {
            return;
        }

        Settings.BackgroundTheme = normalized;
        ScheduleSettingsSave();
    }

    public void SetHudBorderTheme(string? themeName)
    {
        var normalized = AppColorThemes.NormalizeName(themeName);
        if (Settings.HudBorderTheme == normalized)
        {
            return;
        }

        Settings.HudBorderTheme = normalized;
        Overlay?.ApplyHudBorderTheme(normalized);
        GForceOverlay?.ApplyHudBorderTheme(normalized);
        ScheduleSettingsSave();
    }

    public void SetBoostGaugeTheme(string? themeName)
    {
        var normalized = BoostGaugeThemes.NormalizeName(themeName);
        if (Settings.BoostGaugeTheme == normalized)
        {
            return;
        }

        Settings.BoostGaugeTheme = normalized;
        Overlay?.ApplyBoostGaugeTheme(normalized);
        BoostGaugeOverlay?.ApplyBoostGaugeTheme(normalized);
        TireTemperatureGaugeOverlay?.ApplyBoostGaugeTheme(normalized);
        ScheduleSettingsSave();
    }

    public void SetSidebarCollapsed(bool collapsed)
    {
        if (Settings.SidebarCollapsed == collapsed)
        {
            return;
        }

        Settings.SidebarCollapsed = collapsed;
        ScheduleSettingsSave();
    }

    public void SetOverlayLocked(bool locked)
    {
        if (Settings.RequiresSetup)
        {
            return;
        }

        Settings.OverlayLocked = locked;
        Overlay?.SetEditMode(!locked);
        GForceOverlay?.SetEditMode(!locked);
        BoostGaugeOverlay?.SetEditMode(!locked);
        TireTemperatureGaugeOverlay?.SetEditMode(!locked);
        UpdateOverlayVisibility(DateTimeOffset.UtcNow, force: true);
        ScheduleSettingsSave();
    }

    public void SaveOverlayPlacement()
    {
        if (Overlay is null)
        {
            return;
        }

        var key = Overlay.GetDisplayKey();
        _activeOverlayPlacementKey = key;
        Settings.LastOverlayPlacementKey = key;
        Settings.Placements[key] = new OverlayPlacement(
            Overlay.Left,
            Overlay.Top,
            Settings.OverlayWidthScale,
            Settings.OverlayHeightScale);
        ScheduleSettingsSave();
    }

    public void SaveGForcePlacement()
    {
        if (GForceOverlay is null)
        {
            return;
        }

        var key = GForceOverlay.GetDisplayKey();
        Settings.LastGForcePlacementKey = key;
        Settings.GForcePlacements[key] = new OverlayPlacement(
            GForceOverlay.Left,
            GForceOverlay.Top,
            Settings.GForceWidthScale,
            Settings.GForceHeightScale);
        ScheduleSettingsSave();
    }

    public void SaveBoostGaugePlacement()
    {
        if (BoostGaugeOverlay is null)
        {
            return;
        }

        var key = BoostGaugeOverlay.GetDisplayKey();
        Settings.LastBoostGaugePlacementKey = key;
        Settings.BoostGaugePlacements[key] = new OverlayPlacement(
            BoostGaugeOverlay.Left,
            BoostGaugeOverlay.Top,
            Settings.BoostGaugeScale,
            Settings.BoostGaugeScale);
        ScheduleSettingsSave();
    }

    public void SaveTireTemperatureGaugePlacement()
    {
        if (TireTemperatureGaugeOverlay is null)
        {
            return;
        }

        var key = TireTemperatureGaugeOverlay.GetDisplayKey();
        Settings.LastTireTemperatureGaugePlacementKey = key;
        Settings.TireTemperatureGaugePlacements[key] = new OverlayPlacement(
            TireTemperatureGaugeOverlay.Left,
            TireTemperatureGaugeOverlay.Top,
            Settings.TireTemperatureGaugeScale,
            Settings.TireTemperatureGaugeScale);
        ScheduleSettingsSave();
    }

    public void RestoreOverlayPlacement()
    {
        if (Overlay is null)
        {
            return;
        }

        var key = PreferredOverlayPlacementKey();
        if (Settings.Placements.TryGetValue(key, out var placement))
        {
            _activeOverlayPlacementKey = key;
            Settings.LastOverlayPlacementKey = key;
            Overlay.RestorePosition(placement.Left, placement.Top);
            ViewModel.OverlayWidthScale = placement.WidthScale;
            ViewModel.OverlayHeightScale = placement.HeightScale;
            Settings.OverlayWidthScale = Math.Clamp(placement.WidthScale, 0.5, 2.0);
            Settings.OverlayHeightScale = Math.Clamp(placement.HeightScale, 0.5, 2.0);
            Overlay.ApplyLayout(
                Settings.LayoutMode,
                Settings.NativeGaugeMode,
                Settings.OverlayWidthScale,
                Settings.OverlayHeightScale,
                Settings.OverlayOpacity);
        }
        else
        {
            if (Settings.LayoutMode == HudLayoutMode.Native)
            {
                var referenceScale = Overlay.CurrentNativeReferenceScale();
                Settings.OverlayWidthScale = referenceScale;
                Settings.OverlayHeightScale = referenceScale;
                ViewModel.OverlayWidthScale = referenceScale;
                ViewModel.OverlayHeightScale = referenceScale;
                Overlay.ApplyLayout(
                    Settings.LayoutMode,
                    Settings.NativeGaugeMode,
                    referenceScale,
                    referenceScale,
                    Settings.OverlayOpacity);
            }

            Overlay.ResetPosition();
            _activeOverlayPlacementKey = Overlay.GetDisplayKey();
            Settings.LastOverlayPlacementKey = _activeOverlayPlacementKey;
            Settings.Placements[_activeOverlayPlacementKey] = new OverlayPlacement(
                Overlay.Left,
                Overlay.Top,
                Settings.OverlayWidthScale,
                Settings.OverlayHeightScale);
        }
    }

    public void RestoreGForcePlacement()
    {
        if (GForceOverlay is null)
        {
            return;
        }

        var speedDisplayKey = _activeOverlayPlacementKey ?? Overlay?.GetDisplayKey();
        var placement = OverlayPlacementResolver.FindGForcePlacementForSpeedDisplay(
            Settings.GForcePlacements,
            speedDisplayKey,
            out var key);
        if (placement is not null && key is not null)
        {
            Settings.LastGForcePlacementKey = key;
            GForceOverlay.RestorePosition(placement.Left, placement.Top);
            ViewModel.GForceWidthScale = placement.WidthScale;
            ViewModel.GForceHeightScale = placement.HeightScale;
            Settings.GForceWidthScale = Math.Clamp(placement.WidthScale, 0.5, 2.0);
            Settings.GForceHeightScale = Math.Clamp(placement.HeightScale, 0.5, 2.0);
            GForceOverlay.ApplyAppearance(
                Settings.GForceWidthScale,
                Settings.GForceHeightScale,
                Settings.OverlayOpacity);
        }
        else
        {
            Settings.LastGForcePlacementKey = null;
            ResetGForcePosition();
        }
    }

    public void RestoreBoostGaugePlacement()
    {
        if (BoostGaugeOverlay is null)
        {
            return;
        }

        var key = BoostGaugeOverlay.GetDisplayKey();
        if (Settings.BoostGaugePlacements.TryGetValue(key, out var placement))
        {
            Settings.LastBoostGaugePlacementKey = key;
            Settings.BoostGaugeScale = Math.Clamp(placement.WidthScale, 0.5, 2.0);
            ViewModel.BoostGaugeScale = Settings.BoostGaugeScale;
            BoostGaugeOverlay.ApplyAppearance(Settings.BoostGaugeScale, Settings.OverlayOpacity);
            BoostGaugeOverlay.RestorePosition(placement.Left, placement.Top);
            return;
        }

        if (Overlay is not null)
        {
            BoostGaugeOverlay.ResetPosition(
                new Rect(Overlay.Left, Overlay.Top, Overlay.Width, Overlay.Height),
                Overlay.CurrentMonitorPlacementArea());
            SaveBoostGaugePlacement();
        }
    }

    public void RestoreTireTemperatureGaugePlacement()
    {
        if (TireTemperatureGaugeOverlay is null)
        {
            return;
        }

        TireTemperatureGaugeOverlay.ApplyGaugeMode(Settings.NativeGaugeMode);
        var key = TireTemperatureGaugeOverlay.GetDisplayKey();
        if (Settings.TireTemperatureGaugePlacements.TryGetValue(key, out var placement))
        {
            Settings.LastTireTemperatureGaugePlacementKey = key;
            Settings.TireTemperatureGaugeScale = Math.Clamp(placement.WidthScale, 0.5, 2.0);
            ViewModel.TireTemperatureGaugeScale = Settings.TireTemperatureGaugeScale;
            TireTemperatureGaugeOverlay.ApplyAppearance(
                Settings.TireTemperatureGaugeScale,
                Settings.OverlayOpacity);
            TireTemperatureGaugeOverlay.RestorePosition(placement.Left, placement.Top);
            return;
        }

        if (Overlay is not null)
        {
            TireTemperatureGaugeOverlay.ResetPosition(
                new Rect(Overlay.Left, Overlay.Top, Overlay.Width, Overlay.Height),
                Overlay.CurrentMonitorPlacementArea());
            SaveTireTemperatureGaugePlacement();
        }
    }

    public void ResetOverlayPosition()
    {
        if (Overlay is not null && Settings.LayoutMode == HudLayoutMode.Native)
        {
            var scale = Overlay.CurrentNativeReferenceScale();
            Settings.OverlayWidthScale = scale;
            Settings.OverlayHeightScale = scale;
            ViewModel.OverlayWidthScale = scale;
            ViewModel.OverlayHeightScale = scale;
            Overlay.ApplyLayout(
                Settings.LayoutMode, Settings.NativeGaugeMode, scale, scale, Settings.OverlayOpacity);
        }

        Overlay?.ResetPosition();
        SaveOverlayPlacement();
        ResetGForcePosition();
        SaveGForcePlacement();
        if (IsDetachedBoostGaugeEnabled)
        {
            RestoreBoostGaugePlacement();
        }
        if (IsDetachedTireTemperatureGaugeEnabled)
        {
            RestoreTireTemperatureGaugePlacement();
        }
    }

    private void ResetGForcePosition()
    {
        if (GForceOverlay is null)
        {
            return;
        }

        if (Settings.LayoutMode == HudLayoutMode.SeparateBoxes && Overlay is not null)
        {
            GForceOverlay.ResetPositionAdjacentTo(new Rect(
                Overlay.Left,
                Overlay.Top,
                Overlay.Width,
                Overlay.Height),
                Overlay.CurrentMonitorPlacementArea());
            return;
        }

        if (Settings.LayoutMode == HudLayoutMode.Native && Overlay is not null)
        {
            GForceOverlay.ResetPositionBelow(new Rect(
                Overlay.Left,
                Overlay.Top,
                Overlay.Width,
                Overlay.Height),
                Overlay.CurrentMonitorPlacementArea());
            return;
        }

        if (IsStandaloneGForceWindowEnabled && Overlay is not null)
        {
            GForceOverlay.ResetPositionBelow(new Rect(
                Overlay.Left,
                Overlay.Top,
                Overlay.Width,
                Overlay.Height),
                Overlay.CurrentMonitorPlacementArea());
            return;
        }

        GForceOverlay.ResetPosition();
    }

    public bool RelearnCurrentTires()
    {
        var carOrdinal = _lastProcessedState?.CarOrdinal ?? 0;
        if (carOrdinal <= 0)
        {
            return false;
        }

        _calibration.ResetProfile(carOrdinal);
        _savedCalibrationRadii.Remove(carOrdinal);
        Settings.Calibrations = _calibration.ExportSnapshots().ToList();
        _speedModel.Reset();
        _tractionHookDetector.Reset();
        _tractionCueUntilUtc = DateTimeOffset.MinValue;
        ViewModel.IsTractionCueActive = false;
        ViewModel.MarkTireProfileReset();
        ScheduleSettingsSave();
        return true;
    }

    private string PreferredOverlayPlacementKey()
    {
        if (Settings.LastOverlayPlacementKey is { } lastKey)
        {
            const string marker = "-SpeedV4-";
            var markerIndex = lastKey.LastIndexOf(marker, StringComparison.Ordinal);
            var layoutKey = markerIndex >= 0
                ? lastKey[..(markerIndex + marker.Length)] + CurrentLayoutKey()
                : lastKey;
            if (Settings.Placements.ContainsKey(layoutKey))
            {
                return layoutKey;
            }
        }

        var currentKey = Overlay!.GetDisplayKey();
        if (Settings.Placements.ContainsKey(currentKey))
        {
            return currentKey;
        }

        return currentKey;
    }

    private string CurrentLayoutKey() => Settings.LayoutMode == HudLayoutMode.Native
        ? $"Native-{Settings.NativeGaugeMode}"
        : Settings.LayoutMode.ToString();

    private void UpdateCurrentPlacementScales()
    {
        if (Overlay is not null && Settings.LastOverlayPlacementKey is { } overlayKey &&
            Settings.Placements.TryGetValue(overlayKey, out var overlayPlacement))
        {
            overlayPlacement.Left = Overlay.Left;
            overlayPlacement.Top = Overlay.Top;
            overlayPlacement.WidthScale = Settings.OverlayWidthScale;
            overlayPlacement.HeightScale = Settings.OverlayHeightScale;
        }

        if (GForceOverlay is not null && Settings.LastGForcePlacementKey is { } gForceKey &&
            Settings.GForcePlacements.TryGetValue(gForceKey, out var gForcePlacement))
        {
            gForcePlacement.Left = GForceOverlay.Left;
            gForcePlacement.Top = GForceOverlay.Top;
            gForcePlacement.WidthScale = Settings.GForceWidthScale;
            gForcePlacement.HeightScale = Settings.GForceHeightScale;
        }

        if (BoostGaugeOverlay is not null && Settings.LastBoostGaugePlacementKey is { } boostKey &&
            Settings.BoostGaugePlacements.TryGetValue(boostKey, out var boostPlacement))
        {
            boostPlacement.Left = BoostGaugeOverlay.Left;
            boostPlacement.Top = BoostGaugeOverlay.Top;
            boostPlacement.WidthScale = Settings.BoostGaugeScale;
            boostPlacement.HeightScale = Settings.BoostGaugeScale;
        }

        if (TireTemperatureGaugeOverlay is not null &&
            Settings.LastTireTemperatureGaugePlacementKey is { } tireTemperatureKey &&
            Settings.TireTemperatureGaugePlacements.TryGetValue(
                tireTemperatureKey,
                out var tireTemperaturePlacement))
        {
            tireTemperaturePlacement.Left = TireTemperatureGaugeOverlay.Left;
            tireTemperaturePlacement.Top = TireTemperatureGaugeOverlay.Top;
            tireTemperaturePlacement.WidthScale = Settings.TireTemperatureGaugeScale;
            tireTemperaturePlacement.HeightScale = Settings.TireTemperatureGaugeScale;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _compatibilityLifetime.Cancel();
        _applicationUpdateLifetime.Cancel();
        _compatibilityUpdates.Dispose();
        _applicationUpdates.Dispose();
        _uiTimer.Stop();
        SetCompositionRenderingEnabled(false);
        _settingsSaveTimer.Stop();
        if (_receiverNotificationsAttached)
        {
            _receiver.PacketAvailable -= OnPacketAvailable;
            _receiverNotificationsAttached = false;
        }

        Overlay?.SetTelemetryVisible(false, Settings.OverlayOpacity, hideImmediately: true);
        GForceOverlay?.SetTelemetryVisible(false, Settings.OverlayOpacity, hideImmediately: true);
        BoostGaugeOverlay?.SetTelemetryVisible(false, Settings.OverlayOpacity, hideImmediately: true);
        TireTemperatureGaugeOverlay?.SetTelemetryVisible(
            false,
            Settings.OverlayOpacity,
            hideImmediately: true);
        ViewModel.ClearHudVisuals();
        if (_wasDrivingConnected)
        {
            _calibration.EndTelemetrySession();
            SetDrivingConnected(false);
        }

        if (!Settings.RequiresSetup)
        {
            Settings.Calibrations = _calibration.ExportSnapshots().ToList();
            SaveSettings();
        }
        await _nativeHudProcessService.DisposeAsync().ConfigureAwait(false);
        await _receiver.DisposeAsync().ConfigureAwait(false);
        _compatibilityLifetime.Dispose();
        _applicationUpdateLifetime.Dispose();
    }

    private static Version CurrentApplicationVersion()
    {
        var version = typeof(AppController).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), 0);
    }

    private void OnPacketAvailable(object? sender, EventArgs eventArgs)
    {
        // Keep live telemetry independent from WPF's presentation cadence. Some
        // systems throttle CompositionTarget.Rendering while Wisp is in the
        // background, but packet delivery must still advance the HUD promptly.
        if (_disposed || _runtimeSuspended || Settings.RequiresSetup || _dispatcher.HasShutdownStarted ||
            _receiver.Latest is not { IsRaceOn: true } ||
            Interlocked.Exchange(ref _activationDispatchPending, 1) != 0)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, ProcessPendingActivation);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _activationDispatchPending, 0);
        }
    }

    private void ProcessPendingActivation()
    {
        Interlocked.Exchange(ref _activationDispatchPending, 0);
        if (!_disposed)
        {
            ProcessUiUpdate();
        }
    }

    private void OnCompositionRendering(object? sender, EventArgs eventArgs)
    {
        if (_disposed || !_overlayVisibleRequested)
        {
            SetCompositionRenderingEnabled(false);
            return;
        }

        if (eventArgs is RenderingEventArgs rendering &&
            rendering.RenderingTime == _lastCompositionRenderingTime)
        {
            return;
        }

        if (eventArgs is RenderingEventArgs currentRendering)
        {
            _lastCompositionRenderingTime = currentRendering.RenderingTime;
            _renderRate = _displayFrameRateCounter.Observe(currentRendering.RenderingTime);
        }

        var latest = _receiver.Latest;
        RecordLatestFreshness(latest);
        var hasNewPacket = !ReferenceEquals(latest, _lastProcessedState);
        var now = DateTimeOffset.UtcNow;
        UpdateOverlayVisibility(now);
        if (!_overlayVisibleRequested)
        {
            return;
        }

        if (latest is { IsRaceOn: true, CarOrdinal: > 0 })
        {
            _nativeHudProcessService.RequestNativeGaugeSample();
            if (!hasNewPacket)
            {
                PublishNativeHudSnapshot(latest);
            }
        }

    }

    private void OnUiTimer(object? sender, EventArgs eventArgs)
    {
        if (!_disposed)
        {
            if (_compatibilityUpdates.IsConfigured && DateTimeOffset.UtcNow >= _nextCompatibilityCheckAtUtc)
            {
                _ = CheckNativeCompatibilityUpdatesAsync();
            }

            ProcessUiUpdate(processLatestPacket: true);
        }
    }

    private void PublishNativeHudSnapshot(VehicleState latest)
    {
        var nativeHud = _nativeHudProcessService.SnapshotFor(latest.CarOrdinal);
        var publication = NativeHudPublicationKey.From(nativeHud);
        if (_hasNativeHudPublication && publication == _lastNativeHudPublication)
        {
            return;
        }

        if (ViewModel.UpdateNativeHudSnapshot(nativeHud))
        {
            _lastNativeHudPublication = publication;
            _hasNativeHudPublication = true;
        }
    }

    private void RememberNativeHudPublication(NativeHudSnapshot nativeHud)
    {
        _lastNativeHudPublication = NativeHudPublicationKey.From(nativeHud);
        _hasNativeHudPublication = true;
    }

    private void ProcessUiUpdate(bool processLatestPacket = true)
    {
        if (Settings.RequiresSetup)
        {
            return;
        }
        if (_runtimeSuspended)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now >= _nextStatisticsAtUtc)
        {
            _nextStatisticsAtUtc = now + TimeSpan.FromMilliseconds(250);
            _cachedStatistics = _receiver.GetStatistics(now);
        }

        var latest = _receiver.Latest;
        RecordLatestFreshness(latest);
        var hasNewPacket = processLatestPacket && latest is not null &&
                           !ReferenceEquals(latest, _lastProcessedState);
        if (hasNewPacket)
        {
            _lastProcessedState = latest;
        }

        var refreshDiagnostics = now >= _nextDiagnosticsAtUtc;
        if (refreshDiagnostics)
        {
            UpdateCompatibilityDiagnostics();
            _nextDiagnosticsAtUtc = now + TimeSpan.FromMilliseconds(250);
        }

        var connectionState = _freshness.GetState(now);
        var age = _freshness.GetAge(now);
        var hasFreshTelemetry = latest is not null &&
                                connectionState == TelemetryConnectionState.Connected &&
                                age is not null;
        var nativeHudTransition = EvaluateNativeHudTelemetryTransition(
            _nativeHudTelemetryActive,
            _raceOffObservedAtUtc,
            _wasDrivingConnected,
            hasFreshTelemetry,
            latest?.IsRaceOn ?? false,
            now);
        _nativeHudTelemetryActive = nativeHudTransition.Active;
        _raceOffObservedAtUtc = nativeHudTransition.RaceOffObservedAtUtc;
        var nativeHudTelemetryChanged = nativeHudTransition.ActiveChanged;
        var hasDrivingSignal = hasFreshTelemetry && latest!.IsRaceOn;
        if (nativeHudTransition.HoldForRaceOffHysteresis)
        {
            UpdateOverlayVisibility(now);
            return;
        }

        if (!hasDrivingSignal)
        {
            var wasDriving = _wasDrivingConnected;
            var focus = _nativeHudTelemetryActive &&
                        connectionState == TelemetryConnectionState.Lost
                ? _forzaFocusService.GetState(now)
                : default;
            var forzaWindowKnown = _lastConfirmedForzaWindow != IntPtr.Zero &&
                                   WindowZOrder.IsWindowAvailable(_lastConfirmedForzaWindow);
            var preserveHudVisuals = ShouldPreserveHudVisuals(
                _nativeHudTelemetryActive,
                connectionState,
                focus.IsForzaForeground,
                focus.IsForzaRunning,
                forzaWindowKnown);

            if (!preserveHudVisuals)
            {
                _nativeHudProcessService.UpdateTelemetry(null, nativeLayoutActive: false);
                SetDrivingConnected(false);
                if (latest is { IsRaceOn: false } &&
                    connectionState == TelemetryConnectionState.Connected &&
                    age is not null)
                {
                    _autoMinimizeWasDriving = DrivingTransitionPolicy.Evaluate(
                        _autoMinimizeWasDriving,
                        DrivingTelemetrySignal.NotDriving,
                        Settings.AutoMinimizeOnTelemetry).IsDriving;
                }
            }
            else if (latest is { IsRaceOn: true })
            {
                // A retained telemetry frame does not freeze the independent scene
                // state. The existing UI timer keeps menu changes observable.
                _nativeHudProcessService.UpdateTelemetry(latest, nativeLayoutActive: true);
            }

            if (refreshDiagnostics || (wasDriving && !preserveHudVisuals))
            {
                ViewModel.UpdateWaiting(
                    _cachedStatistics,
                    connectionState,
                    age,
                    _renderRate,
                    preserveHudVisuals);
            }

            if (wasDriving && !preserveHudVisuals)
            {
                _calibration.EndTelemetrySession();
                _speedModel.Reset();
                _tractionHookDetector.Reset();
                _transmissionDisplayFilter.Reset();
                _tractionCueUntilUtc = DateTimeOffset.MinValue;
                ViewModel.IsTractionCueActive = false;
            }

            UpdateOverlayVisibility(
                now,
                force: (wasDriving && !preserveHudVisuals) || nativeHudTelemetryChanged);
            _lastRenderAtUtc = now;
            return;
        }

        var autoMinimizeTransition = DrivingTransitionPolicy.Evaluate(
            _autoMinimizeWasDriving,
            DrivingTelemetrySignal.Driving,
            Settings.AutoMinimizeOnTelemetry);
        _autoMinimizeWasDriving = autoMinimizeTransition.IsDriving;
        var startedDriving = !_wasDrivingConnected;
        if (startedDriving)
        {
            SetDrivingConnected(true);
        }

        if (autoMinimizeTransition.ShouldMinimizeControlPanel && ControlPanel is not null)
        {
            ControlPanel.WindowState = WindowState.Minimized;
        }

        // Every live layout needs the guarded gameplay-state capability, including
        // while hidden or waiting for another display packet. Visibility is not a
        // telemetry-session boundary: a menu may keep Data Out fully active.
        _nativeHudProcessService.UpdateTelemetry(latest, nativeLayoutActive: true);
        var overlayVisible = UpdateOverlayVisibility(
            now,
            force: startedDriving || nativeHudTelemetryChanged);
        if (!hasNewPacket)
        {
            ViewModel.IsTractionCueActive = Settings.TractionCueEnabled && now < _tractionCueUntilUtc;
            return;
        }

        var current = latest!;
        // FH6's official Horizon packet reports zero cylinders for electric
        // powertrains. Route those cars to the authored Electric template while
        // preserving the user's Digital/Analogue presentation choice.
        Overlay?.SetElectricPowertrain(current.IsElectric);
        var calibration = _calibration.Observe(current, isFresh: true);
        var elapsed = now - _lastRenderAtUtc;
        var indicated = Settings.SpeedSource == SpeedSourceMode.Fh6VehicleSpeed
            ? _speedModel.CalculateVehicleSpeed(current, Settings.SpeedUnit)
            : _speedModel.CalculateWithRadii(
                current,
                calibration.IsTrusted ? calibration.TrustedRadii : null,
                Settings.SpeedUnit,
                Settings.AggregationMode,
                ResolveSpeedSmoothing(Settings.LayoutMode, Settings.Smoothing),
                elapsed);

        var gForceVisible = overlayVisible &&
                            (Settings.LayoutMode == HudLayoutMode.Combined ||
                             IsStandaloneGForceWindowEnabled ||
                             IsAttachedGForceMeterEnabled);
        var displayState = current with { Gear = _transmissionDisplayFilter.Observe(current) };
        var nativeHud = _nativeHudProcessService.SnapshotFor(current.CarOrdinal);
        var detachedBoostWasEnabled = IsDetachedBoostGaugeEnabled;
        var detachedTireTemperatureWasEnabled = IsDetachedTireTemperatureGaugeEnabled;
        ViewModel.Update(
            displayState,
            indicated,
            calibration,
            nativeHud,
            _cachedStatistics,
            age!.Value,
            Settings.SpeedUnit,
            _renderRate,
            refreshDiagnostics,
            gForceVisible,
            Settings.SpeedSource);
        var detachedBoostEnabled = IsDetachedBoostGaugeEnabled;
        var detachedTireTemperatureEnabled = IsDetachedTireTemperatureGaugeEnabled;
        if (detachedBoostEnabled != detachedBoostWasEnabled ||
            detachedTireTemperatureEnabled != detachedTireTemperatureWasEnabled)
        {
            if (detachedBoostEnabled)
            {
                RestoreBoostGaugePlacement();
            }
            if (detachedTireTemperatureEnabled)
            {
                RestoreTireTemperatureGaugePlacement();
            }

            UpdateOverlayVisibility(now, force: true);
        }
        RememberNativeHudPublication(nativeHud);

        if (Settings.TractionCueEnabled &&
            calibration.IsTrusted &&
            calibration.TrustedRadii is { } hookRadii)
        {
            if (_tractionHookDetector.ObserveWithRadii(current, hookRadii))
            {
                _tractionCueUntilUtc = now + TimeSpan.FromMilliseconds(650);
            }

            ViewModel.IsTractionCueActive = now < _tractionCueUntilUtc;
        }
        else
        {
            _tractionHookDetector.Reset();
            _tractionCueUntilUtc = DateTimeOffset.MinValue;
            ViewModel.IsTractionCueActive = false;
        }

        if (calibration.IsCalibrated && calibration.TrustedRadii is { } radii)
        {
            // Trusted radii are immutable between explicit estimator
            // transitions, so persist every real transition. A coarse save
            // tolerance can otherwise discard a small but meaningful parity
            // correction and resurrect the old radius after restart.
            var profileChanged = !_savedCalibrationRadii.TryGetValue(current.CarOrdinal, out var savedRadii) ||
                                 RadiiDiffer(radii, savedRadii, 1e-9);
            if (profileChanged)
            {
                Settings.Calibrations = _calibration.ExportSnapshots().ToList();
                _savedCalibrationRadii[current.CarOrdinal] = radii;
                _settingsSaveTimer.Stop();
                SaveSettings();
            }
        }

        _lastRenderAtUtc = now;
    }

    private static bool RadiiDiffer(RollingRadii first, RollingRadii second, double tolerance) =>
        Math.Abs(first.FrontMeters - second.FrontMeters) / first.FrontMeters > tolerance ||
        Math.Abs(first.RearMeters - second.RearMeters) / first.RearMeters > tolerance;

    internal static bool ShouldPreserveHudVisuals(
        bool nativeHudTelemetryActive,
        TelemetryConnectionState connectionState,
        bool forzaForeground,
        bool forzaRunning,
        bool forzaWindowKnown)
    {
        // Foreground status controls whether a stale HUD is shown, not whether
        // its last good frame and driving session remain valid. Focus can
        // return one scheduling tick before Data Out resumes after alt-tab.
        _ = forzaForeground;
        return nativeHudTelemetryActive &&
               connectionState == TelemetryConnectionState.Lost &&
               forzaRunning &&
               forzaWindowKnown;
    }

    internal static double ResolveSpeedSmoothing(
        HudLayoutMode layoutMode,
        double configuredSmoothing) =>
        Math.Clamp(configuredSmoothing, 0, 1);

    internal static (NativeGameplayVisibility Visibility, bool Fresh) EvaluateNativeGameplayVisibility(
        NativeHudSnapshot snapshot,
        long nowTimestamp)
    {
        var observedTimestamp = snapshot.VisibilityObservedTimestamp;
        if (snapshot.GameplayVisibility is not (NativeGameplayVisibility.Visible or NativeGameplayVisibility.Hidden) ||
            observedTimestamp <= 0 || nowTimestamp < observedTimestamp)
        {
            return (NativeGameplayVisibility.Unknown, false);
        }

        return (snapshot.GameplayVisibility, nowTimestamp - observedTimestamp <= NativeVisibilityFreshnessTicks);
    }

    internal static NativeHudTelemetryTransition EvaluateNativeHudTelemetryTransition(
        bool nativeHudTelemetryActive,
        DateTimeOffset? raceOffObservedAtUtc,
        bool wasDrivingConnected,
        bool hasFreshTelemetry,
        bool isRaceOn,
        DateTimeOffset now)
    {
        if (!hasFreshTelemetry)
        {
            return new NativeHudTelemetryTransition(
                nativeHudTelemetryActive,
                raceOffObservedAtUtc,
                HoldForRaceOffHysteresis: false,
                ActiveChanged: false);
        }

        if (isRaceOn)
        {
            return new NativeHudTelemetryTransition(
                Active: true,
                RaceOffObservedAtUtc: null,
                HoldForRaceOffHysteresis: false,
                ActiveChanged: !nativeHudTelemetryActive);
        }

        if (!nativeHudTelemetryActive || !wasDrivingConnected)
        {
            return new NativeHudTelemetryTransition(
                Active: false,
                RaceOffObservedAtUtc: null,
                HoldForRaceOffHysteresis: false,
                ActiveChanged: nativeHudTelemetryActive);
        }

        var observedAtUtc = raceOffObservedAtUtc ?? now;
        if (now - observedAtUtc < RaceOffHysteresis)
        {
            return new NativeHudTelemetryTransition(
                Active: true,
                RaceOffObservedAtUtc: observedAtUtc,
                HoldForRaceOffHysteresis: true,
                ActiveChanged: false);
        }

        return new NativeHudTelemetryTransition(
            Active: false,
            RaceOffObservedAtUtc: null,
            HoldForRaceOffHysteresis: false,
            ActiveChanged: true);
    }

    internal readonly record struct NativeHudTelemetryTransition(
        bool Active,
        DateTimeOffset? RaceOffObservedAtUtc,
        bool HoldForRaceOffHysteresis,
        bool ActiveChanged);

    private bool UpdateOverlayVisibility(DateTimeOffset now, bool force = false)
    {
        if (Settings.RequiresSetup || _runtimeSuspended)
        {
            _overlayVisibleRequested = false;
            SetCompositionRenderingEnabled(false);
            Overlay?.SetTelemetryVisible(false, Settings.OverlayOpacity, hideImmediately: true);
            GForceOverlay?.SetTelemetryVisible(false, Settings.OverlayOpacity, hideImmediately: true);
            BoostGaugeOverlay?.SetTelemetryVisible(false, Settings.OverlayOpacity, hideImmediately: true);
            TireTemperatureGaugeOverlay?.SetTelemetryVisible(
                false,
                Settings.OverlayOpacity,
                hideImmediately: true);
            return false;
        }

        if (!force && now < _nextVisibilityCheckAtUtc)
        {
            return _overlayVisibleRequested;
        }

        _nextVisibilityCheckAtUtc = now + TimeSpan.FromMilliseconds(33);
        var requiresFocusState =
            (!Settings.OverlayLocked) ||
            _nativeHudTelemetryActive ||
            _overlayVisibleRequested;
        var focus = requiresFocusState
            ? _forzaFocusService.GetState(now)
            : default;
        var standaloneGForceEnabled = IsStandaloneGForceWindowEnabled;
        var detachedBoostEnabled = IsDetachedBoostGaugeEnabled;
        var detachedTireTemperatureEnabled = IsDetachedTireTemperatureGaugeEnabled;
        BoostGaugeOverlay?.SetEnabled(detachedBoostEnabled);
        TireTemperatureGaugeOverlay?.SetEnabled(detachedTireTemperatureEnabled);
        var overlayForeground =
            !Settings.OverlayLocked &&
            ((Overlay?.OwnsWindowHandle(focus.ForegroundWindow) ?? false) ||
             standaloneGForceEnabled &&
             (GForceOverlay?.OwnsWindowHandle(focus.ForegroundWindow) ?? false) ||
             detachedBoostEnabled &&
             (BoostGaugeOverlay?.OwnsWindowHandle(focus.ForegroundWindow) ?? false) ||
             detachedTireTemperatureEnabled &&
             (TireTemperatureGaugeOverlay?.OwnsWindowHandle(focus.ForegroundWindow) ?? false));
        if (_lastConfirmedForzaWindow != IntPtr.Zero &&
            !WindowZOrder.IsWindowAvailable(_lastConfirmedForzaWindow))
        {
            _lastConfirmedForzaWindow = IntPtr.Zero;
        }

        var confirmedForzaWindow = focus.IsForzaForeground && focus.ForegroundWindow != IntPtr.Zero
            ? focus.ForegroundWindow
            : _lastConfirmedForzaWindow;
        var forzaWindowKnown = WindowZOrder.IsWindowAvailable(confirmedForzaWindow);
        var telemetryFresh = _freshness.GetState(now) == TelemetryConnectionState.Connected;
        var nativeVisibility = EvaluateNativeGameplayVisibility(
            _nativeHudProcessService.SnapshotFor(_receiver.Latest?.CarOrdinal ?? 0),
            Stopwatch.GetTimestamp());
        ViewModel.UpdateNativeGameplayVisibility(nativeVisibility.Visibility, nativeVisibility.Fresh);
        var overlayVisible = OverlayVisibilityPolicy.ShouldShow(
            _nativeHudTelemetryActive,
            telemetryFresh,
            Settings.GameAwareVisibility,
            focus.IsForzaForeground,
            forzaWindowKnown,
            !Settings.OverlayLocked,
            focus.IsForzaRunning,
            overlayForeground,
            nativeVisibility.Visibility,
            nativeVisibility.Fresh);
        // Once guarded native gameplay visibility hides (or the owning game window
        // is gone), remove Wisp in the same update. A fade-out can otherwise
        // leave the replacement gauge visible over the first loading frame.
        var hideImmediately = _overlayVisibleRequested && !overlayVisible;
        var wasOverlayVisible = _overlayVisibleRequested;
        _overlayVisibleRequested = overlayVisible;
        Overlay?.SetTelemetryVisible(overlayVisible, Settings.OverlayOpacity, hideImmediately);
        GForceOverlay?.SetTelemetryVisible(
            overlayVisible && standaloneGForceEnabled,
            Settings.OverlayOpacity,
            hideImmediately || !standaloneGForceEnabled);
        BoostGaugeOverlay?.SetTelemetryVisible(
            overlayVisible && detachedBoostEnabled,
            Settings.OverlayOpacity,
            hideImmediately || !detachedBoostEnabled);
        TireTemperatureGaugeOverlay?.SetTelemetryVisible(
            overlayVisible && detachedTireTemperatureEnabled,
            Settings.OverlayOpacity,
            hideImmediately || !detachedTireTemperatureEnabled);

        var forzaWindowChanged = focus.IsForzaForeground &&
                                 focus.ForegroundWindow != IntPtr.Zero &&
                                 focus.ForegroundWindow != _lastConfirmedForzaWindow;
        if (focus.IsForzaForeground && focus.ForegroundWindow != IntPtr.Zero)
        {
            _lastConfirmedForzaWindow = focus.ForegroundWindow;
            confirmedForzaWindow = focus.ForegroundWindow;
        }

        var fullscreenChanged = focus.IsFullscreen != _lastForzaFullscreen;
        var fullscreenRefreshDue = focus.IsFullscreen && now >= _nextFullscreenZOrderAtUtc;
        var ownerAttachmentRequired = overlayVisible && confirmedForzaWindow != IntPtr.Zero &&
                                      (!WindowZOrder.IsAttachedToGame(Overlay, confirmedForzaWindow) ||
                                       standaloneGForceEnabled &&
                                       !WindowZOrder.IsAttachedToGame(
                                           GForceOverlay,
                                           confirmedForzaWindow) ||
                                       detachedBoostEnabled &&
                                       !WindowZOrder.IsAttachedToGame(
                                           BoostGaugeOverlay,
                                           confirmedForzaWindow) ||
                                       detachedTireTemperatureEnabled &&
                                       !WindowZOrder.IsAttachedToGame(
                                           TireTemperatureGaugeOverlay,
                                           confirmedForzaWindow));
        if (overlayVisible && confirmedForzaWindow != IntPtr.Zero &&
            (!wasOverlayVisible || forzaWindowChanged || fullscreenChanged ||
             fullscreenRefreshDue || ownerAttachmentRequired))
        {
            _ = WindowZOrder.AttachAboveGame(
                Overlay,
                confirmedForzaWindow,
                raise: focus.IsForzaForeground);
            if (standaloneGForceEnabled)
            {
                _ = WindowZOrder.AttachAboveGame(
                    GForceOverlay,
                    confirmedForzaWindow,
                    raise: focus.IsForzaForeground);
            }
            if (detachedBoostEnabled)
            {
                _ = WindowZOrder.AttachAboveGame(
                    BoostGaugeOverlay,
                    confirmedForzaWindow,
                    raise: focus.IsForzaForeground);
            }
            if (detachedTireTemperatureEnabled)
            {
                _ = WindowZOrder.AttachAboveGame(
                    TireTemperatureGaugeOverlay,
                    confirmedForzaWindow,
                    raise: focus.IsForzaForeground);
            }
            _nextFullscreenZOrderAtUtc = focus.IsFullscreen
                ? now + TimeSpan.FromSeconds(1)
                : DateTimeOffset.MaxValue;
        }
        else if (!overlayVisible)
        {
            _nextFullscreenZOrderAtUtc = DateTimeOffset.MinValue;
            WindowZOrder.DetachFromGame(Overlay);
            WindowZOrder.DetachFromGame(GForceOverlay);
            WindowZOrder.DetachFromGame(BoostGaugeOverlay);
            WindowZOrder.DetachFromGame(TireTemperatureGaugeOverlay);
        }

        if (!standaloneGForceEnabled)
        {
            WindowZOrder.DetachFromGame(GForceOverlay);
        }
        if (!detachedBoostEnabled)
        {
            WindowZOrder.DetachFromGame(BoostGaugeOverlay);
        }
        if (!detachedTireTemperatureEnabled)
        {
            WindowZOrder.DetachFromGame(TireTemperatureGaugeOverlay);
        }

        _lastForzaFullscreen = focus.IsFullscreen;
        SetCompositionRenderingEnabled(overlayVisible);
        return overlayVisible;
    }

    private void SetCompositionRenderingEnabled(bool enabled)
    {
        enabled &= !Settings.RequiresSetup && !_runtimeSuspended;
        if (_compositionRenderingAttached == enabled)
        {
            return;
        }

        _compositionRenderingAttached = enabled;
        _lastCompositionRenderingTime = TimeSpan.MinValue;
        _displayFrameRateCounter.Reset();
        _renderRate = 0;
        if (enabled)
        {
            CompositionTarget.Rendering += OnCompositionRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
        }
    }

    private void SetDrivingConnected(bool connected)
    {
        if (_wasDrivingConnected == connected)
        {
            return;
        }

        _wasDrivingConnected = connected;
        _uiTimer.Interval = connected ? ConnectedTimerInterval : IdleTimerInterval;
    }

    private void RecordLatestFreshness(VehicleState? latest)
    {
        if (latest is null || ReferenceEquals(latest, _lastFreshnessState))
        {
            return;
        }

        var previous = _lastFreshnessState;
        _lastFreshnessState = latest;
        if (ShouldRecordTelemetryActivity(previous, latest))
        {
            _freshness.RecordPacket(latest.ReceivedAtUtc);
        }
    }

    internal static bool ShouldRecordTelemetryActivity(
        VehicleState? previous,
        VehicleState current) =>
        previous is null ||
        !current.IsRaceOn ||
        current.GameTimestampMilliseconds != previous.GameTimestampMilliseconds;

    private void ResetControllerSession()
    {
        if (_wasDrivingConnected)
        {
            _calibration.EndTelemetrySession();
        }

        _freshness = new TelemetryFreshness(TelemetryTimeout);
        _lastFreshnessState = null;
        _lastProcessedState = null;
        _lastRenderAtUtc = DateTimeOffset.UtcNow;
        _nextStatisticsAtUtc = DateTimeOffset.MinValue;
        _nextDiagnosticsAtUtc = DateTimeOffset.MinValue;
        _nextVisibilityCheckAtUtc = DateTimeOffset.MinValue;
        _nextFullscreenZOrderAtUtc = DateTimeOffset.MinValue;
        _displayFrameRateCounter.Reset();
        _renderRate = 0;
        _raceOffObservedAtUtc = null;
        _autoMinimizeWasDriving = false;
        _nativeHudTelemetryActive = false;
        _hasNativeHudPublication = false;
        _speedModel.Reset();
        _tractionHookDetector.Reset();
        _transmissionDisplayFilter.Reset();
        _nativeHudProcessService.UpdateTelemetry(null, nativeLayoutActive: false);
        _tractionCueUntilUtc = DateTimeOffset.MinValue;
        ViewModel.IsTractionCueActive = false;
        SetDrivingConnected(false);
        _overlayVisibleRequested = false;
        _lastConfirmedForzaWindow = IntPtr.Zero;
        _lastForzaFullscreen = false;
        SetCompositionRenderingEnabled(false);
        Overlay?.SetTelemetryVisible(false, Settings.OverlayOpacity, hideImmediately: true);
        GForceOverlay?.SetTelemetryVisible(false, Settings.OverlayOpacity, hideImmediately: true);
        BoostGaugeOverlay?.SetTelemetryVisible(false, Settings.OverlayOpacity, hideImmediately: true);
        TireTemperatureGaugeOverlay?.SetTelemetryVisible(
            false,
            Settings.OverlayOpacity,
            hideImmediately: true);
        ViewModel.ClearHudVisuals();
    }

    private void SaveSettings()
    {
        if (Settings.RequiresSetup)
        {
            return;
        }

        try
        {
            _saveSettings(Settings);
        }
        catch (IOException)
        {
            // The next user change or clean shutdown retries the local save.
        }
        catch (UnauthorizedAccessException)
        {
            // The HUD remains usable if local settings are temporarily read-only.
        }
        catch (SecurityException)
        {
            // Local policy can temporarily block the settings directory.
        }
    }

    private void ScheduleSettingsSave()
    {
        if (_disposed)
        {
            return;
        }

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private bool TrySetStartupRegistration(bool startWithWindows, bool startWithForza)
    {
        try
        {
            _startupRegistrationService.Apply(startWithWindows, startWithForza);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private readonly record struct NativeHudPublicationKey(
        ulong Generation,
        int CarOrdinal,
        NativeAssistProviderStatus Status,
        bool HasAvailableCapabilities,
        long NativeGaugeObservedTimestamp)
    {
        public static NativeHudPublicationKey From(NativeHudSnapshot snapshot) => new(
            snapshot.Generation,
            snapshot.CarOrdinal,
            snapshot.Status,
            snapshot.HasAvailableCapabilities,
            snapshot.NativeGaugeObservedTimestamp);
    }

}
