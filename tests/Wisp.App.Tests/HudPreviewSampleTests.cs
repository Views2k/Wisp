using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HudPreviewSampleTests
{
    [Theory]
    [InlineData(SpeedUnit.MilesPerHour, GearDisplayMode.Manual, 67, "4")]
    [InlineData(SpeedUnit.MilesPerHour, GearDisplayMode.Automatic, 67, "Drive")]
    [InlineData(SpeedUnit.KilometersPerHour, GearDisplayMode.Manual, 108, "4")]
    [InlineData(SpeedUnit.KilometersPerHour, GearDisplayMode.Automatic, 108, "Drive")]
    public void OfflineSampleUsesSelectedUnitsAndGearWithoutClaimingLiveReadiness(
        SpeedUnit unit,
        GearDisplayMode gearDisplayMode,
        int speed,
        string gearToken)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings
        {
            SpeedUnit = unit,
            GearDisplayMode = gearDisplayMode
        });

        var preview = viewModel.NativePreviewFrame;

        Assert.False(viewModel.HasLiveTelemetry);
        Assert.False(viewModel.IsPreviewLive);
        Assert.Equal(HudPreviewSample.Caption, viewModel.PreviewCaption);
        Assert.Contains("not live FH6 data", viewModel.PreviewCaption);
        Assert.True(preview.SpeedAvailable);
        Assert.Equal(speed, preview.Speed);
        Assert.Equal(speed.ToString(), viewModel.PreviewSpeed);
        Assert.Equal(unit, preview.Unit);
        Assert.Equal(TransmissionGear.Fourth, preview.Gear);
        Assert.Equal(gearDisplayMode, preview.GearDisplayMode);
        Assert.Equal(gearToken, NativeGaugeGeometry.GearToken(preview.Gear, preview.GearDisplayMode));
        Assert.True(NativeGaugeGeometry.HasExactTachometerState(preview.ExactRedline, preview.TachometerMaximumRpm));
        Assert.Equal(4_200d, preview.EngineRpm);
        Assert.Equal(7_000d, preview.ExactRedline.Rpm);
        Assert.Equal(8_000d, preview.TachometerMaximumRpm);
        Assert.Equal("Illustrative preview only", preview.ExactRedline.Source);
        Assert.True(viewModel.PreviewBoostDisplay.IsAvailable);
        Assert.Equal(24, viewModel.PreviewBoostDisplay.PressurePsi);
        Assert.Equal(32, viewModel.PreviewBoostDisplay.LearnedPeakPsi);
        Assert.Equal(0.75, viewModel.PreviewBoostDisplay.Fraction);
        Assert.Equal(70, viewModel.PreviewBoostDisplay.ScaleMaximumPsi);
        Assert.True(viewModel.PreviewTireTemperatureDisplay.IsAvailable);
        Assert.Equal(286, viewModel.PreviewTireTemperatureDisplay.FrontFahrenheit);
        Assert.Equal(272, viewModel.PreviewTireTemperatureDisplay.RearFahrenheit);
        Assert.Equal((286d - 50) / 300, viewModel.PreviewTireTemperatureDisplay.FrontFraction);
        Assert.Equal((272d - 50) / 300, viewModel.PreviewTireTemperatureDisplay.RearFraction);
        Assert.Equal(0, preview.CarOrdinal);
        Assert.Equal(0u, preview.GameTimestampMilliseconds);
        Assert.False(preview.NativeAssists.Available);
        Assert.Equal(NativeGaugeFrame.Empty(unit), viewModel.NativeGaugeFrame);
        Assert.Equal("—", viewModel.HudSpeed);
        Assert.Equal("Unavailable", viewModel.ExactNativeRedline);
        Assert.Equal("Inactive", viewModel.ExactRedlineStateText);
        Assert.Equal("Unavailable", viewModel.NativeAssistState);
    }

    [Fact]
    public void OfflineSelectionChangesNotifyOnlyPresentationAndSelectedOptions()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var original = viewModel.NativeGaugeFrame;
        var changed = ObserveChanges(viewModel);

        viewModel.UnitSelectionIndex = 1;
        viewModel.GearDisplaySelectionIndex = (int)GearDisplayMode.Automatic;

        Assert.Equal("108", viewModel.PreviewSpeed);
        Assert.Equal(SpeedUnit.KilometersPerHour, viewModel.NativePreviewFrame.Unit);
        Assert.Equal(GearDisplayMode.Automatic, viewModel.NativePreviewFrame.GearDisplayMode);
        Assert.Equal(original, viewModel.NativeGaugeFrame);
        Assert.Equal("—", viewModel.HudSpeed);
        Assert.Contains(nameof(DiagnosticsViewModel.UnitSelectionIndex), changed);
        Assert.Contains(nameof(DiagnosticsViewModel.GearDisplaySelectionIndex), changed);
        Assert.Contains(nameof(DiagnosticsViewModel.NativePreviewFrame), changed);
        Assert.Contains(nameof(DiagnosticsViewModel.PreviewSpeed), changed);
        Assert.DoesNotContain(nameof(DiagnosticsViewModel.NativeGaugeFrame), changed);
        Assert.DoesNotContain(nameof(DiagnosticsViewModel.HudSpeed), changed);
        Assert.DoesNotContain(nameof(DiagnosticsViewModel.HasLiveTelemetry), changed);
    }

    [Fact]
    public void SampleReadsAndUnchangedSelectionsAreStableAndProduceNoUpdates()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var original = viewModel.NativeGaugeFrame;
        var preview = viewModel.NativePreviewFrame;
        var changed = ObserveChanges(viewModel);

        viewModel.UnitSelectionIndex = 0;
        viewModel.GearDisplaySelectionIndex = (int)GearDisplayMode.Manual;

        Assert.Equal(preview, viewModel.NativePreviewFrame);
        Assert.Equal(preview, viewModel.NativePreviewFrame);
        Assert.Equal("67", viewModel.PreviewSpeed);
        Assert.Equal(original, viewModel.NativeGaugeFrame);
        Assert.Empty(changed);
    }

    [Theory]
    [InlineData(NativeAssistProviderStatus.Unavailable, false)]
    [InlineData(NativeAssistProviderStatus.Unavailable, true)]
    [InlineData(NativeAssistProviderStatus.UnsupportedBuild, false)]
    [InlineData(NativeAssistProviderStatus.UnsupportedBuild, true)]
    [InlineData(NativeAssistProviderStatus.ReadFailure, false)]
    [InlineData(NativeAssistProviderStatus.ReadFailure, true)]
    public void LiveMissingCapabilitiesAreNeverFilledWithSampleData(
        NativeAssistProviderStatus status,
        bool speedAvailable)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var native = NativeHudSnapshot.Unavailable(status, 3, 42);

        UpdateLive(viewModel, native, speedAvailable);

        Assert.True(viewModel.HasLiveTelemetry);
        Assert.True(viewModel.IsPreviewLive);
        Assert.Equal("Live preview · current FH6 data", viewModel.PreviewCaption);
        Assert.Equal(viewModel.NativeGaugeFrame, viewModel.NativePreviewFrame);
        Assert.Equal(viewModel.HudSpeed, viewModel.PreviewSpeed);
        Assert.Equal(speedAvailable ? "93" : "—", viewModel.PreviewSpeed);
        Assert.Equal(speedAvailable, viewModel.NativePreviewFrame.SpeedAvailable);
        Assert.Equal(native.ExactRedline, viewModel.NativePreviewFrame.ExactRedline);
        Assert.False(viewModel.NativePreviewFrame.ExactRedline.IsExact);
        Assert.Equal(0d, viewModel.NativePreviewFrame.TachometerMaximumRpm);
        Assert.Same(native.Assists, viewModel.NativePreviewFrame.NativeAssists);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LivePartialNativeCapabilitiesPassThroughUnchanged(bool tachAvailable)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var assists = NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.ReadFailure, 7, 42) with
        {
            Available = !tachAvailable,
            Status = tachAvailable ? NativeAssistProviderStatus.ReadFailure : NativeAssistProviderStatus.Ready,
            IsABSAvailable = !tachAvailable,
            IsABSOn = !tachAvailable
        };
        var native = new NativeHudSnapshot(
            tachAvailable,
            7,
            42,
            tachAvailable ? NativeAssistProviderStatus.Ready : NativeAssistProviderStatus.ReadFailure,
            tachAvailable
                ? ExactRedlineResult.Exact(6_500 * 2 * Math.PI / 60)
                : ExactRedlineResult.Unavailable(ExactRedlineStatus.ReadFailure),
            tachAvailable ? 7_700 : 0,
            assists);

        UpdateLive(viewModel, native);

        Assert.Equal(viewModel.NativeGaugeFrame, viewModel.NativePreviewFrame);
        Assert.Equal(native.ExactRedline, viewModel.NativePreviewFrame.ExactRedline);
        Assert.Equal(native.TachometerMaximumRpm, viewModel.NativePreviewFrame.TachometerMaximumRpm);
        Assert.Same(assists, viewModel.NativePreviewFrame.NativeAssists);
        Assert.Equal(42, viewModel.NativePreviewFrame.CarOrdinal);
        Assert.Equal(123u, viewModel.NativePreviewFrame.GameTimestampMilliseconds);
        Assert.Equal(3_600d, viewModel.NativePreviewFrame.EngineRpm);
        Assert.Equal(TransmissionGear.Third, viewModel.NativePreviewFrame.Gear);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WaitingSelectsSampleWithoutChangingTheExistingOverlayRetentionPolicy(bool preserveHudVisuals)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        UpdateLive(viewModel);
        var frame = viewModel.NativeGaugeFrame;
        var speed = viewModel.HudSpeed;
        var offsetX = viewModel.GForceOffsetX;
        var offsetY = viewModel.GForceOffsetY;
        var changed = ObserveChanges(viewModel);

        viewModel.UpdateWaiting(default, TelemetryConnectionState.Lost,
            TimeSpan.FromSeconds(1), 0, preserveHudVisuals);

        Assert.False(viewModel.HasLiveTelemetry);
        Assert.False(viewModel.IsPreviewLive);
        Assert.Equal(HudPreviewSample.Caption, viewModel.PreviewCaption);
        Assert.Equal("67", viewModel.PreviewSpeed);
        Assert.Equal("Illustrative preview only", viewModel.NativePreviewFrame.ExactRedline.Source);
        Assert.Equal(preserveHudVisuals ? frame : NativeGaugeFrame.Empty(SpeedUnit.MilesPerHour),
            viewModel.NativeGaugeFrame);
        Assert.Equal(preserveHudVisuals ? offsetX : 0, viewModel.GForceOffsetX);
        Assert.Equal(preserveHudVisuals ? offsetY : 0, viewModel.GForceOffsetY);
        Assert.Equal(speed, viewModel.HudSpeed);
        Assert.Contains(nameof(DiagnosticsViewModel.HasLiveTelemetry), changed);
        Assert.Contains(nameof(DiagnosticsViewModel.IsPreviewLive), changed);
        Assert.Contains(nameof(DiagnosticsViewModel.NativePreviewFrame), changed);
        Assert.Contains(nameof(DiagnosticsViewModel.PreviewSpeed), changed);
        Assert.Contains(nameof(DiagnosticsViewModel.PreviewCaption), changed);
    }

    [Fact]
    public void AnIdenticalFrameResumingAfterAGapStillNotifiesThePreviewTransition()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        UpdateLive(viewModel);
        var frame = viewModel.NativeGaugeFrame;
        viewModel.UpdateWaiting(default, TelemetryConnectionState.Lost,
            TimeSpan.FromSeconds(1), 0, preserveHudVisuals: true);
        var changed = ObserveChanges(viewModel);

        UpdateLive(viewModel);

        Assert.True(viewModel.IsPreviewLive);
        Assert.Equal(frame, viewModel.NativeGaugeFrame);
        Assert.Equal(frame, viewModel.NativePreviewFrame);
        Assert.Equal("93", viewModel.PreviewSpeed);
        Assert.DoesNotContain(nameof(DiagnosticsViewModel.NativeGaugeFrame), changed);
        Assert.Contains(nameof(DiagnosticsViewModel.HasLiveTelemetry), changed);
        Assert.Contains(nameof(DiagnosticsViewModel.NativePreviewFrame), changed);
        Assert.Contains(nameof(DiagnosticsViewModel.PreviewSpeed), changed);
        Assert.Contains(nameof(DiagnosticsViewModel.PreviewCaption), changed);
    }

    [Fact]
    public void LiveFrameChangesNotifyThePreviewWithoutRestartingItsLiveState()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        UpdateLive(viewModel);
        var changed = ObserveChanges(viewModel);

        UpdateLive(viewModel, displayedSpeed: 52.7);

        Assert.True(viewModel.IsPreviewLive);
        Assert.Equal(viewModel.NativeGaugeFrame, viewModel.NativePreviewFrame);
        Assert.Equal(52, viewModel.NativePreviewFrame.Speed);
        Assert.Equal("52", viewModel.PreviewSpeed);
        Assert.Contains(nameof(DiagnosticsViewModel.NativePreviewFrame), changed);
        Assert.Contains(nameof(DiagnosticsViewModel.PreviewSpeed), changed);
        Assert.DoesNotContain(nameof(DiagnosticsViewModel.HasLiveTelemetry), changed);
        Assert.DoesNotContain(nameof(DiagnosticsViewModel.IsPreviewLive), changed);
        Assert.DoesNotContain(nameof(DiagnosticsViewModel.PreviewCaption), changed);
    }

    [Fact]
    public void ClearingHudVisualsClearsTheLiveIndicatorAndLeavesSampleOutOfTheOverlay()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        UpdateLive(viewModel);

        viewModel.ClearHudVisuals();

        Assert.False(viewModel.HasLiveTelemetry);
        Assert.False(viewModel.IsPreviewLive);
        Assert.Equal(NativeGaugeFrame.Empty(SpeedUnit.MilesPerHour), viewModel.NativeGaugeFrame);
        Assert.True(viewModel.NativePreviewFrame.SpeedAvailable);
        Assert.Equal("67", viewModel.PreviewSpeed);
    }

    [Fact]
    public void ControlErrorsClearOnlyThePreviewLiveIndicatorAndDoNotResetTheStoredFrame()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        UpdateLive(viewModel);
        var frame = viewModel.NativeGaugeFrame;
        var speed = viewModel.HudSpeed;
        var offsetX = viewModel.GForceOffsetX;
        var offsetY = viewModel.GForceOffsetY;

        viewModel.ReportControlError("A setting could not be changed");

        Assert.False(viewModel.HasLiveTelemetry);
        Assert.False(viewModel.IsPreviewLive);
        Assert.Equal(HudPreviewSample.Caption, viewModel.PreviewCaption);
        Assert.Equal(frame, viewModel.NativeGaugeFrame);
        Assert.Equal(speed, viewModel.HudSpeed);
        Assert.Equal(offsetX, viewModel.GForceOffsetX);
        Assert.Equal(offsetY, viewModel.GForceOffsetY);
        Assert.Equal("Action Required", viewModel.StatusText);
    }

    [Fact]
    public void LiveSelectionChangesNeverSubstituteOrRewriteTheLastReceivedFrame()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        UpdateLive(viewModel);
        var frame = viewModel.NativeGaugeFrame;

        viewModel.UnitSelectionIndex = 1;
        viewModel.GearDisplaySelectionIndex = (int)GearDisplayMode.Automatic;

        Assert.True(viewModel.IsPreviewLive);
        Assert.Equal(frame, viewModel.NativeGaugeFrame);
        Assert.Equal(frame, viewModel.NativePreviewFrame);
        Assert.Equal(viewModel.HudSpeed, viewModel.PreviewSpeed);
        Assert.False(viewModel.NativePreviewFrame.ExactRedline.IsExact);
    }

    [Fact]
    public void TireRelearningDoesNotSubstituteASampleForCurrentTelemetry()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        UpdateLive(viewModel);
        var frame = viewModel.NativeGaugeFrame;
        var changed = ObserveChanges(viewModel);

        viewModel.MarkTireProfileReset();

        Assert.True(viewModel.HasLiveTelemetry);
        Assert.True(viewModel.IsPreviewLive);
        Assert.Equal(frame, viewModel.NativePreviewFrame);
        Assert.Equal("—", viewModel.PreviewSpeed);
        Assert.Equal(viewModel.HudSpeed, viewModel.PreviewSpeed);
        Assert.Contains(nameof(DiagnosticsViewModel.PreviewSpeed), changed);
    }

    private static List<string> ObserveChanges(DiagnosticsViewModel viewModel)
    {
        var names = new List<string>();
        viewModel.PropertyChanged += (_, args) => names.Add(args.PropertyName!);
        return names;
    }

    private static void UpdateLive(
        DiagnosticsViewModel viewModel,
        NativeHudSnapshot? native = null,
        bool speedAvailable = true,
        double displayedSpeed = 93.9) =>
        viewModel.Update(
            DrivingState(),
            new IndicatedSpeed(0, displayedSpeed, speedAvailable, false, "Rear"),
            new CalibrationResult(0.3, null, 1, 12, true, string.Empty, true),
            native ?? NativeHudSnapshot.Unavailable(carOrdinal: 42),
            default,
            TimeSpan.Zero,
            SpeedUnit.MilesPerHour,
            60,
            refreshDiagnostics: false,
            updateGForce: true);

    private static VehicleState DrivingState() => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = 123,
        ReceivedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(10),
        CarOrdinal = 42,
        Drivetrain = DrivetrainType.RearWheelDrive,
        GroundSpeedMetersPerSecond = 30,
        WheelRotationRadiansPerSecond = new WheelValues(100, 100, 100, 100),
        TireSlipRatio = new WheelValues(0, 0, 0, 0),
        TireSlipAngle = new WheelValues(0, 0, 0, 0),
        NormalizedSuspensionTravel = new WheelValues(0.5f, 0.5f, 0.5f, 0.5f),
        LateralAccelerationMetersPerSecondSquared = 4,
        LongitudinalAccelerationMetersPerSecondSquared = -3,
        EngineRpm = 3_600,
        EngineMaximumRpm = 8_000,
        Gear = TransmissionGear.Third,
        Steering = 0,
        Accelerator = 64,
        Brake = 0
    };
}
