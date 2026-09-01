using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DiagnosticsViewModelTests
{
    [Fact]
    public void DashboardDefaultsAreExplicitWithoutLiveTelemetry()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings
        {
            SpeedUnit = SpeedUnit.KilometersPerHour
        });

        Assert.Equal("—", viewModel.DashboardSpeed);
        Assert.Equal("KM/H", viewModel.DashboardSpeedUnit);
        Assert.Equal("—", viewModel.DashboardGear);
        Assert.Equal("—", viewModel.DashboardVehicleType);
        Assert.Equal("—", viewModel.DashboardPower);
        Assert.Equal("—", viewModel.DashboardTorque);
    }

    [Theory]
    [InlineData(SpeedUnit.MilesPerHour, 93.99, "93", "MPH")]
    [InlineData(SpeedUnit.KilometersPerHour, 149.99, "149", "KM/H")]
    public void DashboardSpeedUsesTheDisplayedIntegerAndSelectedUnit(
        SpeedUnit unit,
        double displayedSpeed,
        string expectedSpeed,
        string expectedUnit)
    {
        var viewModel = UpdateDashboard(
            DrivingState(),
            unit,
            GearDisplayMode.Manual,
            displayedSpeed);

        Assert.Equal(expectedSpeed, viewModel.DashboardSpeed);
        Assert.Equal(expectedUnit, viewModel.DashboardSpeedUnit);
    }

    [Theory]
    [InlineData(GearDisplayMode.Manual, TransmissionGear.Second, "2")]
    [InlineData(GearDisplayMode.Automatic, TransmissionGear.Second, "D")]
    [InlineData(GearDisplayMode.Manual, TransmissionGear.Reverse, "R")]
    [InlineData(GearDisplayMode.Automatic, TransmissionGear.Neutral, "N")]
    [InlineData(GearDisplayMode.Manual, TransmissionGear.Unknown, "—")]
    public void DashboardGearFollowsTheConfiguredDisplayMode(
        GearDisplayMode displayMode,
        TransmissionGear gear,
        string expected)
    {
        var viewModel = UpdateDashboard(
            DrivingState() with { Gear = gear },
            SpeedUnit.MilesPerHour,
            displayMode,
            30);

        Assert.Equal(expected, viewModel.DashboardGear);
    }

    [Theory]
    [InlineData(0, "Electric")]
    [InlineData(4, "Combustion")]
    public void DashboardVehicleTypeUsesTheFramePowertrain(int cylinders, string expected)
    {
        var viewModel = UpdateDashboard(
            DrivingState() with { NumCylinders = cylinders },
            SpeedUnit.MilesPerHour,
            GearDisplayMode.Manual,
            30);

        Assert.Equal(expected, viewModel.DashboardVehicleType);
    }

    [Theory]
    [InlineData(84_500, 310.25, "+113.3 HP", "+310 Nm")]
    [InlineData(-84_500, -310.25, "-113.3 HP", "-310 Nm")]
    [InlineData(0, 0, "0.0 HP", "0 Nm")]
    public void DashboardPowerAndTorqueRemainSignedAndUseDisplayUnits(
        double powerWatts,
        double torqueNm,
        string expectedPower,
        string expectedTorque)
    {
        var viewModel = UpdateDashboard(
            DrivingState() with
            {
                PowerWatts = (float)powerWatts,
                TorqueNm = (float)torqueNm
            },
            SpeedUnit.MilesPerHour,
            GearDisplayMode.Manual,
            30);

        Assert.Equal(expectedPower, viewModel.DashboardPower);
        Assert.Equal(expectedTorque, viewModel.DashboardTorque);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(9_007_199_254_740_993L)]
    public void NativeFramePreservesOptionalMonotonicReceiveTimestamp(long? receivedTimestamp)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        Assert.Null(viewModel.NativePreviewFrame.ReceivedTimestamp);
        var state = DrivingState() with { ReceivedTimestamp = receivedTimestamp };

        viewModel.Update(
            state,
            new IndicatedSpeed(0, 30, true, false, "Rear"),
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false),
            default(ReceiverStatistics),
            TimeSpan.Zero,
            SpeedUnit.MilesPerHour,
            60,
            refreshDiagnostics: false,
            updateGForce: false);

        Assert.Equal(receivedTimestamp, viewModel.NativeGaugeFrame.ReceivedTimestamp);
        Assert.Equal(receivedTimestamp, viewModel.NativePreviewFrame.ReceivedTimestamp);
        Assert.Equal(state.GameTimestampMilliseconds, viewModel.NativeGaugeFrame.GameTimestampMilliseconds);
        Assert.Equal(state.EngineRpm, viewModel.NativeGaugeFrame.EngineRpm);
    }

    [Fact]
    public void CompatibilityDiagnosticsDoNotAlterTheNativeFrame()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var original = viewModel.NativeGaugeFrame;
        viewModel.UpdateNativeCompatibility("Verified build", "Offline", false, false);

        Assert.Equal("Verified build", viewModel.NativeCompatibilityValidation);
        Assert.Equal("Offline", viewModel.NativeCompatibilityUpdates);
        Assert.False(viewModel.CanCheckNativeCompatibility);
        Assert.False(viewModel.CanImportNativeCompatibility);
        Assert.Equal(original, viewModel.NativeGaugeFrame);
    }

    [Theory]
    [InlineData(NativeGameplayVisibility.Visible, true, "Visible")]
    [InlineData(NativeGameplayVisibility.Visible, false, "Visible (stale)")]
    [InlineData(NativeGameplayVisibility.Hidden, true, "Hidden")]
    [InlineData(NativeGameplayVisibility.Hidden, false, "Hidden (stale)")]
    [InlineData(NativeGameplayVisibility.Unknown, true, "Unavailable")]
    [InlineData(NativeGameplayVisibility.Unknown, false, "Unavailable")]
    [InlineData((NativeGameplayVisibility)99, true, "Unavailable")]
    [InlineData((NativeGameplayVisibility)99, false, "Unavailable")]
    public void GameplayHudDiagnosticIsIndependentOfReadyNativeGauges(
        NativeGameplayVisibility visibility,
        bool fresh,
        string expected)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        Assert.Equal("Unavailable", viewModel.GameplayHudVisibility);
        var assists = NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.Ready, 1, 1) with
        {
            Available = true
        };
        var native = new NativeHudSnapshot(true, 1, 1, NativeAssistProviderStatus.Ready,
            ExactRedlineResult.Exact(6_500 * 2 * Math.PI / 60), 8_000, assists);
        viewModel.Update(DrivingState(), new IndicatedSpeed(0, 30, true, false, "Rear"),
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false), native,
            default, TimeSpan.Zero, SpeedUnit.MilesPerHour, 60, true, false);
        var frame = viewModel.NativeGaugeFrame;
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        viewModel.UpdateNativeGameplayVisibility(visibility, fresh);

        Assert.Equal(expected, viewModel.GameplayHudVisibility);
        Assert.Equal("Ready", viewModel.ExactRedlineStateText);
        Assert.Equal("Ready", viewModel.NativeAssistState);
        Assert.Equal(frame, viewModel.NativeGaugeFrame);
        Assert.All(changes, name => Assert.Equal(nameof(DiagnosticsViewModel.GameplayHudVisibility), name));
    }

    [Fact]
    public void GameplayHudDiagnosticOnlyNotifiesWhenItsDisplayedStateChanges()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        viewModel.UpdateNativeGameplayVisibility(NativeGameplayVisibility.Visible, true);
        viewModel.UpdateNativeGameplayVisibility(NativeGameplayVisibility.Visible, true);
        viewModel.UpdateNativeGameplayVisibility(NativeGameplayVisibility.Visible, false);
        viewModel.UpdateNativeGameplayVisibility(NativeGameplayVisibility.Visible, false);
        viewModel.UpdateNativeGameplayVisibility(NativeGameplayVisibility.Unknown, false);
        viewModel.UpdateNativeGameplayVisibility(NativeGameplayVisibility.Unknown, false);

        Assert.Equal(3, changes.Count);
        Assert.All(changes, name => Assert.Equal(nameof(DiagnosticsViewModel.GameplayHudVisibility), name));
        Assert.Equal("Unavailable", viewModel.GameplayHudVisibility);
    }

    [Theory]
    [InlineData(NativeGameplayVisibility.Visible, "Visible (stale)")]
    [InlineData(NativeGameplayVisibility.Hidden, "Hidden (stale)")]
    public void RetainedHudKeepsVisibilityDiagnosticButSessionClearInvalidatesIt(
        NativeGameplayVisibility visibility,
        string expected)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        viewModel.UpdateNativeGameplayVisibility(visibility, false);

        viewModel.UpdateWaiting(default, TelemetryConnectionState.Lost, TimeSpan.FromSeconds(1), 0,
            preserveHudVisuals: true);
        Assert.Equal(expected, viewModel.GameplayHudVisibility);

        viewModel.ClearHudVisuals();
        Assert.Equal("Unavailable", viewModel.GameplayHudVisibility);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PartialNativeCapabilitiesRemainIndependentInTheUi(bool tachAvailable)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var assists = NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.ReadFailure, 1, 1) with
        {
            Available = !tachAvailable,
            Status = tachAvailable ? NativeAssistProviderStatus.ReadFailure : NativeAssistProviderStatus.Ready,
            IsABSAvailable = !tachAvailable
        };
        var exact = tachAvailable
            ? ExactRedlineResult.Exact(6_500 * 2 * Math.PI / 60)
            : ExactRedlineResult.Unavailable(ExactRedlineStatus.ReadFailure);
        var native = new NativeHudSnapshot(tachAvailable, 1, 1,
            tachAvailable ? NativeAssistProviderStatus.Ready : NativeAssistProviderStatus.ReadFailure,
            exact, tachAvailable ? 8_000 : 0, assists);

        viewModel.Update(DrivingState(), new IndicatedSpeed(0, 30, true, false, "Rear"),
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false), native,
            default, TimeSpan.Zero, SpeedUnit.MilesPerHour, 60, true, false);

        Assert.Equal(tachAvailable, viewModel.NativeGaugeFrame.ExactRedline.IsExact);
        Assert.Equal(!tachAvailable, viewModel.NativeGaugeFrame.NativeAssists.Available);
        Assert.Equal(tachAvailable, viewModel.ExactRedlineStateText == "Ready");
        Assert.Equal(!tachAvailable, viewModel.NativeAssistState == "Ready");
        Assert.Equal("30", viewModel.HudSpeed);
    }

    [Theory]
    [InlineData(7.99, "7", 7)]
    [InlineData(93.99, "93", 93)]
    [InlineData(102.99, "102", 102)]
    public void NativeAndSharedHudUseFh6IntegerSpeedSemantics(
        double rawSpeed,
        string expectedText,
        int expectedNativeSpeed)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());

        viewModel.Update(
            DrivingState(),
            new IndicatedSpeed(0, rawSpeed, true, false, "Rear (RL + RR)"),
            new CalibrationResult(0.3, null, 1, 12, true, string.Empty, true),
            default(ReceiverStatistics),
            TimeSpan.Zero,
            SpeedUnit.MilesPerHour,
            60,
            refreshDiagnostics: true,
            updateGForce: false);

        Assert.Equal(expectedText, viewModel.HudSpeed);
        Assert.Equal(expectedNativeSpeed, viewModel.NativeGaugeFrame.Speed);
    }

    [Fact]
    public void CalibrationProgressUsesTheEstimatorRequirement()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        const int acceptedSamples = 5;

        viewModel.Update(
            DrivingState(),
            new IndicatedSpeed(0, 0, false, false, "Rear (RL + RR)"),
            new CalibrationResult(
                null,
                0.3,
                0.2,
                acceptedSamples,
                true,
                string.Empty,
                false),
            default(ReceiverStatistics),
            TimeSpan.Zero,
            SpeedUnit.MilesPerHour,
            60,
            refreshDiagnostics: true,
            updateGForce: false);

        Assert.Equal(
            $"{acceptedSamples}/{CalibrationOptions.DefaultMinimumSamples}",
            viewModel.Confidence);
    }

    [Fact]
    public void ResetProgressUsesTheEstimatorRequirement()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());

        viewModel.MarkTireProfileReset();

        Assert.Equal($"0/{CalibrationOptions.DefaultMinimumSamples}", viewModel.Confidence);
    }

    [Fact]
    public void Fh6SpeedSourceIsReadyWithoutATireCalibration()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var state = DrivingState();
        var speed = new SpeedModel().CalculateVehicleSpeed(state, SpeedUnit.MilesPerHour);

        viewModel.Update(
            state,
            speed,
            new CalibrationResult(null, null, 0, 0, false, "Waiting for clean data", false),
            default(ReceiverStatistics),
            TimeSpan.Zero,
            SpeedUnit.MilesPerHour,
            60,
            refreshDiagnostics: true,
            updateGForce: false,
            speedSource: SpeedSourceMode.Fh6VehicleSpeed);

        Assert.Equal("FH6 Speed Ready", viewModel.StatusText);
        Assert.Equal("FH6 vehicle speed is active", viewModel.StatusDetail);
        Assert.Equal("67", viewModel.HudSpeed);
        Assert.Equal("67.1 mph", viewModel.IndicatedSpeed);
        Assert.Equal("Not required for FH6 speed", viewModel.Radius);
        Assert.Equal("N/A", viewModel.Confidence);
        Assert.Equal("FH6 vehicle speed", viewModel.SelectedWheels);
    }

    [Fact]
    public void DiagnosticsExposeRawRpmAndRenderedNativeScale()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());

        viewModel.Update(
            DrivingState(),
            new IndicatedSpeed(0, 0, false, false, "Rear (RL + RR)"),
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false),
            default(ReceiverStatistics),
            TimeSpan.Zero,
            SpeedUnit.MilesPerHour,
            60,
            refreshDiagnostics: true,
            updateGForce: false);

        Assert.Equal("1000 rpm", viewModel.EngineRpm);
        Assert.Equal("8000 rpm", viewModel.EngineMaximumRpm);
        Assert.Equal("Unavailable", viewModel.NativeTachScale);
    }

    [Fact]
    public void NativeFrameCarriesExtendedPowertrainTelemetry()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var state = DrivingState() with
        {
            NumCylinders = 0,
            PowerWatts = -84_500,
            TorqueNm = -310.25f,
            Accelerator = 192,
            Brake = 64
        };

        viewModel.Update(
            state,
            new IndicatedSpeed(0, 0, false, false, "Rear (RL + RR)"),
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false),
            default(ReceiverStatistics),
            TimeSpan.Zero,
            SpeedUnit.MilesPerHour,
            60,
            refreshDiagnostics: true,
            updateGForce: false);

        Assert.True(viewModel.NativeGaugeFrame.IsElectric);
        Assert.Equal(-84_500d, viewModel.NativeGaugeFrame.PowerWatts);
        Assert.Equal(-310.25d, viewModel.NativeGaugeFrame.TorqueNm);
        Assert.Equal(192, viewModel.NativeGaugeFrame.Accelerator);
        Assert.Equal(64, viewModel.NativeGaugeFrame.Brake);
    }

    [Fact]
    public void WaitingStateClearsRpmEvidence()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        viewModel.Update(
            DrivingState(),
            new IndicatedSpeed(0, 0, false, false, "Rear (RL + RR)"),
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false),
            default(ReceiverStatistics),
            TimeSpan.Zero,
            SpeedUnit.MilesPerHour,
            60,
            refreshDiagnostics: true,
            updateGForce: false);

        viewModel.UpdateWaiting(default, TelemetryConnectionState.Waiting, null, 0);

        Assert.Equal("—", viewModel.EngineRpm);
        Assert.Equal("—", viewModel.EngineMaximumRpm);
        Assert.Equal("—", viewModel.NativeTachScale);
    }

    [Fact]
    public void DiagnosticsAndNativeFrameExposeTheSameAssistSnapshot()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var assists = NativeAssistStateCalculator.Calculate(
            new NativeAssistRawState(
                true,
                true,
                false,
                true,
                0,
                0.2f,
                0,
                0,
                [0, 0, 0, 0],
                0,
                0,
                2,
                0),
            0.1f,
            4,
            1);
        var exact = ExactRedlineResult.Exact(6_500 * 2 * Math.PI / 60);
        var nativeHud = new NativeHudSnapshot(
            true,
            4,
            1,
            NativeAssistProviderStatus.Ready,
            exact,
            8_000,
            assists);

        viewModel.Update(
            DrivingState(),
            new IndicatedSpeed(0, 0, false, false, "Rear (RL + RR)"),
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false),
            nativeHud,
            default,
            TimeSpan.Zero,
            SpeedUnit.MilesPerHour,
            60,
            refreshDiagnostics: true,
            updateGForce: false);

        Assert.Same(assists, viewModel.NativeGaugeFrame.NativeAssists);
        Assert.Equal(8_000, viewModel.NativeGaugeFrame.TachometerMaximumRpm);
        Assert.Equal("6500 rpm", viewModel.ExactNativeRedline);
        Assert.Equal("0–8 ×1000 rpm · redline 6500", viewModel.NativeTachScale);
        Assert.Equal("Ready", viewModel.NativeAssistState);
        Assert.Equal("ABS OFF · TCR ON · STM — · LC ON", viewModel.NativeAssistDetails);

        viewModel.UpdateWaiting(default, TelemetryConnectionState.Waiting, null, 0);
        Assert.Equal("Unavailable", viewModel.NativeAssistState);
        Assert.Equal("—", viewModel.NativeAssistDetails);
        Assert.False(viewModel.NativeGaugeFrame.NativeAssists.Available);
    }

    [Fact]
    public void StaleAltTabPreservesNativeFrameAndGForceWhileUpdatingDiagnostics()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var state = DrivingState() with
        {
            LateralAccelerationMetersPerSecondSquared = 4,
            LongitudinalAccelerationMetersPerSecondSquared = -3
        };
        var speed = new SpeedModel().CalculateVehicleSpeed(state, SpeedUnit.MilesPerHour);
        viewModel.Update(
            state,
            speed,
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false),
            default,
            TimeSpan.Zero,
            SpeedUnit.MilesPerHour,
            60,
            refreshDiagnostics: true,
            updateGForce: true);
        var frame = viewModel.NativeGaugeFrame;
        var offsetX = viewModel.GForceOffsetX;
        var offsetY = viewModel.GForceOffsetY;

        viewModel.UpdateWaiting(
            default,
            TelemetryConnectionState.Lost,
            TimeSpan.FromSeconds(1),
            0,
            preserveHudVisuals: true);

        Assert.Equal("Telemetry Lost", viewModel.StatusText);
        Assert.Equal(frame, viewModel.NativeGaugeFrame);
        Assert.Equal(offsetX, viewModel.GForceOffsetX);
        Assert.Equal(offsetY, viewModel.GForceOffsetY);
    }

    [Fact]
    public void FreshNativeGenerationUpdatesNeedleWithoutReplacingTheTelemetryFrame()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var telemetry = DrivingState() with
        {
            GameTimestampMilliseconds = 4_200,
            ReceivedTimestamp = 8_400
        };
        viewModel.Update(
            telemetry,
            new IndicatedSpeed(0, 30, true, false, "Rear (RL + RR)"),
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false),
            default,
            TimeSpan.Zero,
            SpeedUnit.MilesPerHour,
            120,
            refreshDiagnostics: false,
            updateGForce: false,
            speedSource: SpeedSourceMode.Fh6VehicleSpeed);
        var first = NativeSnapshot(generation: 1, angle: 120, blur: -0.2, observedTimestamp: 10);
        var displayedSpeed = new NativeDisplayedSpeedState(
            true, 1, 2, 3, false, false, false, SpeedUnit.MilesPerHour);
        var second = NativeSnapshot(generation: 2, angle: 240, blur: 0.4, observedTimestamp: 20) with
        {
            DisplayedSpeedState = displayedSpeed
        };

        Assert.True(viewModel.UpdateNativeHudSnapshot(first));
        Assert.True(viewModel.UpdateNativeHudSnapshot(second));

        Assert.Equal(telemetry.GameTimestampMilliseconds, viewModel.NativeGaugeFrame.GameTimestampMilliseconds);
        Assert.Equal(telemetry.ReceivedTimestamp, viewModel.NativeGaugeFrame.ReceivedTimestamp);
        Assert.Equal(240, viewModel.NativeGaugeFrame.NativeNeedleAngleDegrees);
        Assert.Equal(0.4, viewModel.NativeGaugeFrame.NativeNeedleBlurAmount);
        Assert.Equal(20, viewModel.NativeGaugeFrame.NativeGaugeObservedTimestamp);
        Assert.Equal(displayedSpeed, viewModel.NativeGaugeFrame.DisplayedSpeedState);
        Assert.Equal(SpeedSourceMode.Fh6VehicleSpeed, viewModel.NativeGaugeFrame.SpeedSource);
    }

    [Theory]
    [InlineData(NativeAssistProviderStatus.Unavailable, true)]
    [InlineData(NativeAssistProviderStatus.UnsupportedBuild, true)]
    [InlineData(NativeAssistProviderStatus.ReadFailure, false)]
    [InlineData(NativeAssistProviderStatus.InvalidSourceVector, false)]
    public void OnlySessionBuildAndAccessFailuresHardInvalidateNeedlePlayback(
        NativeAssistProviderStatus status,
        bool expected)
    {
        var snapshot = NativeHudSnapshot.Unavailable(status, generation: 3, carOrdinal: 1);

        Assert.Equal(expected, DiagnosticsViewModel.IsNativeGaugeSourceInvalidated(snapshot));
    }

    [Fact]
    public void FreshRaceOffClearsNativeFrameAndGForce()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var state = DrivingState() with
        {
            LateralAccelerationMetersPerSecondSquared = 4,
            LongitudinalAccelerationMetersPerSecondSquared = -3
        };
        viewModel.Update(
            state,
            new SpeedModel().CalculateVehicleSpeed(state, SpeedUnit.MilesPerHour),
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false),
            default,
            TimeSpan.Zero,
            SpeedUnit.MilesPerHour,
            60,
            refreshDiagnostics: true,
            updateGForce: true);

        viewModel.UpdateWaiting(
            default,
            TelemetryConnectionState.Connected,
            TimeSpan.Zero,
            0,
            preserveHudVisuals: false);

        Assert.False(viewModel.NativeGaugeFrame.SpeedAvailable);
        Assert.Equal(0, viewModel.GForceOffsetX);
        Assert.Equal(0, viewModel.GForceOffsetY);
    }

    [Theory]
    [InlineData(true, TelemetryConnectionState.Lost, false, true, true, true)]
    [InlineData(true, TelemetryConnectionState.Lost, true, true, true, true)]
    [InlineData(true, TelemetryConnectionState.Connected, false, true, true, false)]
    [InlineData(false, TelemetryConnectionState.Lost, false, true, true, false)]
    [InlineData(true, TelemetryConnectionState.Lost, false, false, true, false)]
    [InlineData(true, TelemetryConnectionState.Lost, false, true, false, false)]
    public void HudFrameRetentionRequiresStaleTelemetryAndLiveForzaOwner(
        bool nativeHudTelemetryActive,
        TelemetryConnectionState connectionState,
        bool forzaForeground,
        bool forzaRunning,
        bool forzaWindowKnown,
        bool expected)
    {
        Assert.Equal(
            expected,
            AppController.ShouldPreserveHudVisuals(
                nativeHudTelemetryActive,
                connectionState,
                forzaForeground,
                forzaRunning,
                forzaWindowKnown));
    }

    private static VehicleState DrivingState() => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = 1,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        CarOrdinal = 1,
        Drivetrain = DrivetrainType.RearWheelDrive,
        GroundSpeedMetersPerSecond = 30,
        WheelRotationRadiansPerSecond = new WheelValues(100, 100, 100, 100),
        TireSlipRatio = new WheelValues(0, 0, 0, 0),
        TireSlipAngle = new WheelValues(0, 0, 0, 0),
        NormalizedSuspensionTravel = new WheelValues(0.5f, 0.5f, 0.5f, 0.5f),
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        EngineRpm = 1_000,
        EngineMaximumRpm = 8_000,
        Gear = TransmissionGear.Second,
        Steering = 0,
        Accelerator = 0,
        Brake = 0
    };

    private static NativeHudSnapshot NativeSnapshot(
        ulong generation,
        double angle,
        double blur,
        long observedTimestamp) =>
        new(
            true,
            generation,
            1,
            NativeAssistProviderStatus.Ready,
            ExactRedlineResult.Exact(6_500 * 2 * Math.PI / 60),
            8_000,
            NativeAssistSnapshot.Unavailable(
                NativeAssistProviderStatus.ReadFailure,
                generation,
                1),
            NativeNeedleAngleDegrees: angle,
            NativeNeedleBlurAmount: blur,
            NativeGaugeObservedTimestamp: observedTimestamp);

    private static DiagnosticsViewModel UpdateDashboard(
        VehicleState state,
        SpeedUnit unit,
        GearDisplayMode gearDisplayMode,
        double displayedSpeed)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings
        {
            SpeedUnit = unit,
            GearDisplayMode = gearDisplayMode
        });
        viewModel.Update(
            state,
            new IndicatedSpeed(0, displayedSpeed, true, false, "Rear (RL + RR)"),
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false),
            default(ReceiverStatistics),
            TimeSpan.Zero,
            unit,
            60,
            refreshDiagnostics: false,
            updateGForce: false);
        return viewModel;
    }
}
