using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DashboardTelemetryPresentationTests
{
    [Fact]
    public void DashboardStartsWithNamedAssistsAndUnavailableTelemetry()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());

        Assert.Equal("Anti-lock braking", viewModel.AbsAssistLabel);
        Assert.Equal("Traction control", viewModel.TcrAssistLabel);
        Assert.Equal("Stability management", viewModel.StmAssistLabel);
        Assert.Equal("Launch control", viewModel.LcAssistLabel);
        AssertUnavailable(viewModel);
    }

    [Theory]
    [InlineData(true, true, true, "Active")]
    [InlineData(true, true, false, "Enabled")]
    [InlineData(true, false, false, "Disabled")]
    [InlineData(true, false, true, "Disabled")]
    [InlineData(false, true, true, "Unavailable")]
    [InlineData(false, true, false, "Unavailable")]
    [InlineData(false, false, true, "Unavailable")]
    [InlineData(false, false, false, "Unavailable")]
    public void AssistStatusSeparatesMissingObservationsDisabledEnabledAndActive(
        bool providerAvailable, bool assistEnabled, bool active, string expected)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var assists = Assists(assistEnabled, active) with { Available = providerAvailable };

        Update(viewModel, assists);

        Assert.All(AssistStatuses(viewModel), status => Assert.Equal(expected, status));
    }

    [Fact]
    public void AssistsRemainIndependentOfEachOtherAndTheTachometer()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        var assists = Assists(true, false) with
        {
            IsTCROn = true,
            IsSTMAvailable = false,
            IsSTMOn = true,
            IsLCOn = true
        };

        Update(viewModel, assists);

        Assert.Equal("Enabled", viewModel.AbsAssistStatus);
        Assert.Equal("Active", viewModel.TcrAssistStatus);
        Assert.Equal("Disabled", viewModel.StmAssistStatus);
        Assert.Equal("Active", viewModel.LcAssistStatus);
        Assert.Equal("ABS OFF · TCR ON · STM — · LC ON", viewModel.NativeAssistDetails);
        Assert.Equal("Unavailable", viewModel.NativeTachScale);
        Assert.Same(assists, viewModel.NativeGaugeFrame.NativeAssists);
    }

    [Fact]
    public void AssistActivityChangesNotifyBindingsWithoutRepeatingUnchangedValues()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        Update(viewModel, Assists(true, true));
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is { } name && name.EndsWith("AssistStatus", StringComparison.Ordinal))
            {
                changed.Add(name);
            }
        };

        Update(viewModel, Assists(true, true));
        Assert.Empty(changed);
        Update(viewModel, Assists(true, false));

        Assert.Equal(
            new[] { nameof(viewModel.AbsAssistStatus), nameof(viewModel.TcrAssistStatus),
                nameof(viewModel.StmAssistStatus), nameof(viewModel.LcAssistStatus) },
            changed);
        Assert.All(AssistStatuses(viewModel), status => Assert.Equal("Enabled", status));
    }

    [Theory]
    [InlineData(0, 0, 0, "0%", "0%", "0%")]
    [InlineData(255, 0, -128, "100%", "0%", "-100%")]
    [InlineData(0, 255, 127, "0%", "100%", "+100%")]
    [InlineData(128, 64, -64, "50%", "25%", "-50%")]
    [InlineData(64, 128, 64, "25%", "50%", "+50%")]
    [InlineData(255, 255, -1, "100%", "100%", "-1%")]
    [InlineData(255, 255, 1, "100%", "100%", "+1%")]
    public void DriverInputsUsePedalPercentagesAndSignedSteeringRange(
        byte throttle, byte brake, sbyte steering,
        string throttleText, string brakeText, string steeringText)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());

        Update(viewModel, Assists(true, false), throttle, brake, steering);

        Assert.Equal(throttleText, viewModel.ThrottleInputText);
        Assert.Equal(brakeText, viewModel.BrakeInputText);
        Assert.Equal(steeringText, viewModel.SteeringInputText);
    }

    [Fact]
    public void DashboardPresentationUsesTheExistingDiagnosticRefresh()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        Update(viewModel, Assists(true, true), 255, 0, 127);

        Update(viewModel, Assists(true, false), 0, 255, -128, refreshDiagnostics: false);

        Assert.All(AssistStatuses(viewModel), status => Assert.Equal("Active", status));
        Assert.Equal("100%", viewModel.ThrottleInputText);
        Assert.Equal("0%", viewModel.BrakeInputText);
        Assert.Equal("+100%", viewModel.SteeringInputText);

        Update(viewModel, Assists(true, false), 0, 255, -128);

        Assert.All(AssistStatuses(viewModel), status => Assert.Equal("Enabled", status));
        Assert.Equal("0%", viewModel.ThrottleInputText);
        Assert.Equal("100%", viewModel.BrakeInputText);
        Assert.Equal("-100%", viewModel.SteeringInputText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TelemetryLossClearsDashboardValuesEvenWhenHudPixelsAreRetained(bool preserveHudVisuals)
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        Update(viewModel, Assists(true, true), 255, 255, 127);

        viewModel.UpdateWaiting(default, TelemetryConnectionState.Lost, TimeSpan.FromSeconds(1), 60,
            preserveHudVisuals);

        AssertUnavailable(viewModel);
        Assert.Equal(preserveHudVisuals, viewModel.NativeGaugeFrame.NativeAssists.Available);

        Update(viewModel, Assists(true, false), 0, 0, 0);

        Assert.All(AssistStatuses(viewModel), status => Assert.Equal("Enabled", status));
        Assert.Equal("0%", viewModel.ThrottleInputText);
        Assert.Equal("0%", viewModel.BrakeInputText);
        Assert.Equal("0%", viewModel.SteeringInputText);
    }

    [Fact]
    public void ClearingHudVisualsAlsoClearsDashboardPresentation()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        Update(viewModel, Assists(true, true), 255, 255, 127);

        viewModel.ClearHudVisuals();

        AssertUnavailable(viewModel);
    }

    private static void AssertUnavailable(DiagnosticsViewModel viewModel)
    {
        Assert.All(AssistStatuses(viewModel), status => Assert.Equal("Unavailable", status));
        Assert.Equal("Unavailable", viewModel.ThrottleInputText);
        Assert.Equal("Unavailable", viewModel.BrakeInputText);
        Assert.Equal("Unavailable", viewModel.SteeringInputText);
    }

    private static string[] AssistStatuses(DiagnosticsViewModel viewModel) =>
        [viewModel.AbsAssistStatus, viewModel.TcrAssistStatus, viewModel.StmAssistStatus, viewModel.LcAssistStatus];

    private static NativeAssistSnapshot Assists(bool available, bool active) =>
        new(true, 1, 1, NativeAssistProviderStatus.Ready,
            available, active, available, active, available, active, available, active,
            0, 0, 0, 0);

    private static void Update(
        DiagnosticsViewModel viewModel, NativeAssistSnapshot assists,
        byte throttle = 0, byte brake = 0, sbyte steering = 0, bool refreshDiagnostics = true)
    {
        var state = new VehicleState
        {
            IsRaceOn = true,
            GameTimestampMilliseconds = 1,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            CarOrdinal = 1,
            Drivetrain = DrivetrainType.RearWheelDrive,
            GroundSpeedMetersPerSecond = 30,
            WheelRotationRadiansPerSecond = new WheelValues(100, 100, 100, 100),
            TireSlipRatio = default,
            TireSlipAngle = default,
            NormalizedSuspensionTravel = new WheelValues(0.5f, 0.5f, 0.5f, 0.5f),
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = 0,
            EngineRpm = 1_000,
            EngineMaximumRpm = 8_000,
            Gear = TransmissionGear.Second,
            Accelerator = throttle,
            Brake = brake,
            Steering = steering
        };
        var native = NativeHudSnapshot.Unavailable(carOrdinal: 1) with { Assists = assists };
        viewModel.Update(state, new IndicatedSpeed(30, 67, true, false, "Rear"),
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false),
            native, default, TimeSpan.Zero, SpeedUnit.MilesPerHour, 60,
            refreshDiagnostics, updateGForce: false);
    }
}
