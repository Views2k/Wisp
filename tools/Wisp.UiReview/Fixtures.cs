using Wisp.App;
using Wisp.Core;
using Wisp.Telemetry;

namespace Wisp.UiReview;

internal sealed record Fixture(string Name, HudLayoutMode Layout, NativeGaugeMode Gauge,
    bool Electric = false, string? HudBorderTheme = null)
{
    public const string SyntheticPreviewCaption = "Sample preview · illustrative fixture values, not live FH6 data";

    public static readonly Fixture[] All =
    [
        new("native-digital", HudLayoutMode.Native, NativeGaugeMode.Digital),
        new("native-analogue", HudLayoutMode.Native, NativeGaugeMode.Analogue),
        new("native-ev-digital", HudLayoutMode.Native, NativeGaugeMode.Digital, true),
        new("native-ev-analogue", HudLayoutMode.Native, NativeGaugeMode.Analogue, true),
        new("minimal", HudLayoutMode.Minimal, NativeGaugeMode.Digital),
        new("combined", HudLayoutMode.Combined, NativeGaugeMode.Digital),
        new("separate-boxes", HudLayoutMode.SeparateBoxes, NativeGaugeMode.Digital, HudBorderTheme: "Green")
    ];

    public AppSettings CreateSettings()
    {
        var settings = new AppSettings
        {
            StartWithWindows = false,
            HasCompletedSetup = true,
            GameAwareVisibility = false,
            AutoMinimizeOnTelemetry = false,
            LayoutMode = Layout,
            NativeGaugeMode = Gauge,
            GearDisplayMode = Electric ? GearDisplayMode.Automatic : GearDisplayMode.Manual,
            GForceEnabled = true,
            OverlayLocked = true,
            HudBorderTheme = HudBorderTheme ?? AppColorThemes.DefaultName
        };
        settings.MigrateSettings();
        return settings;
    }

    public void Apply(DiagnosticsViewModel viewModel, bool waiting)
    {
        var statistics = new ReceiverStatistics(1_200, 0, 60, PacketParseError.None, null);
        if (waiting)
        {
            viewModel.UpdateWaiting(default, TelemetryConnectionState.Waiting, null, 0);
        }
        else
        {
            const int ordinal = 1_335;
            const double speedMph = 123;
            var speedMeters = speedMph / SpeedModel.MetersPerSecondToMilesPerHour;
            var angularVelocity = (float)(speedMeters / 0.34);
            var state = new VehicleState
            {
                IsRaceOn = true,
                GameTimestampMilliseconds = 12_000,
                ReceivedAtUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
                CarOrdinal = ordinal,
                Drivetrain = DrivetrainType.AllWheelDrive,
                NumCylinders = Electric ? 0 : 8,
                PowerWatts = 84_500,
                TorqueNm = 310.25f,
                GroundSpeedMetersPerSecond = (float)speedMeters,
                WheelRotationRadiansPerSecond = new(angularVelocity, angularVelocity, angularVelocity, angularVelocity),
                TireSlipRatio = new(0.01f, 0.01f, 0.02f, 0.02f),
                TireSlipAngle = default,
                NormalizedSuspensionTravel = new(0.5f, 0.5f, 0.5f, 0.5f),
                LateralAccelerationMetersPerSecondSquared = 3.2f,
                LongitudinalAccelerationMetersPerSecondSquared = 2.1f,
                EngineRpm = Electric ? 0 : 4_500,
                EngineMaximumRpm = Electric ? 0 : 8_000,
                Gear = Electric ? TransmissionGear.First : TransmissionGear.Fourth,
                Steering = 8,
                Accelerator = 160,
                Brake = 0
            };
            var assists = NativeAssistStateCalculator.Calculate(
                new NativeAssistRawState(true, true, true, true, 0, 0.2f, 0, 0,
                    [0, 0, 0, 0], 0, 0, 2, 0), 0.1f, 1, ordinal);
            var native = new NativeHudSnapshot(
                !Electric,
                1,
                ordinal,
                NativeAssistProviderStatus.Ready,
                Electric ? ExactRedlineResult.Unavailable() : ExactRedlineResult.Exact(7_500 * 2 * Math.PI / 60),
                Electric ? 0 : 8_000,
                assists,
                NativeRegenFillAmount: Electric ? 0.19 : double.NaN,
                NativePowerFillAmount: Electric ? 0.77 : double.NaN,
                NativeRegenPowerRatio: Electric ? 0.42 : double.NaN,
                NativeElectricMaximumSpeed: Electric ? 310 : double.NaN,
                NativeGaugeObservedTimestamp: Electric ? System.Diagnostics.Stopwatch.GetTimestamp() : 0,
                ElectricGearState: Electric
                    ? new NativeElectricGearState(true, 1, 2, 0, -1, true)
                    : NativeElectricGearState.Unavailable);
            viewModel.Update(state,
                new IndicatedSpeed(speedMeters, speedMph, true, false, "All four wheels"),
                new CalibrationResult(0.34, null, 1, 120, true, string.Empty, true, RollingRadii.Uniform(0.34)),
                native, statistics, TimeSpan.FromMilliseconds(8), SpeedUnit.MilesPerHour,
                60, refreshDiagnostics: true, updateGForce: true);
            viewModel.UpdateNativeGameplayVisibility(NativeGameplayVisibility.Visible, fresh: true);
        }

        viewModel.UpdateNativeCompatibility("Synthetic UI fixture; no game validation",
            "Offline visual review; no live services started.", false, false);
    }
}
