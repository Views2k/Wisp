using Wisp.App;
using Wisp.Core;
using Wisp.Telemetry;

namespace Wisp.UiReview;

internal sealed record Fixture(string Name, HudLayoutMode Layout, NativeGaugeMode Gauge,
    bool Electric = false, string? HudBorderTheme = null, bool OrbitReference = false, bool LegacyInterface = false,
    bool PurpleStyle = false, bool ExtremeMetrics = false)
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
        new("separate-boxes", HudLayoutMode.SeparateBoxes, NativeGaugeMode.Digital, HudBorderTheme: "Green"),
        new("orbit-reference", HudLayoutMode.Native, NativeGaugeMode.Digital, OrbitReference: true),
        new("legacy-reference", HudLayoutMode.Native, NativeGaugeMode.Digital, OrbitReference: true, LegacyInterface: true),
        new("orbit-purple", HudLayoutMode.Native, NativeGaugeMode.Analogue, OrbitReference: true, PurpleStyle: true),
        new("orbit-purple-extreme", HudLayoutMode.Native, NativeGaugeMode.Analogue, OrbitReference: true, PurpleStyle: true, ExtremeMetrics: true)
    ];

    public AppSettings CreateSettings()
    {
        var settings = new AppSettings
        {
            StartWithWindows = false,
            UseLegacyInterface = LegacyInterface,
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
        if (PurpleStyle)
        {
            settings.ColorTheme = "Purple";
            settings.CustomAccentColor = "#FFC28AFF";
            settings.CustomBackgroundColor = "#FF1C1922";
            settings.ApplicationStyle = new AppStyleSettings
            {
                BorderColor = "#FFAD75D1",
                TextColor = "#FFF4F2F7",
                MutedTextColor = "#FFB9AEC9",
                GlowStrength = ExtremeMetrics ? 0 : 100,
                SurfaceOpacity = ExtremeMetrics ? 30 : 100,
                BorderThickness = ExtremeMetrics ? 3 : 1,
                CornerRadius = ExtremeMetrics ? 48 : 28,
                CardPadding = ExtremeMetrics ? 36 : 24
            };
        }
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
            const double wattsPerHorsepower = 745.69987158227022;
            var ordinal = OrbitReference ? 3_781 : 1_335;
            var speedMph = ExtremeMetrics ? 987.6 : OrbitReference ? 92.1 : 123;
            var speedMeters = speedMph / SpeedModel.MetersPerSecondToMilesPerHour;
            var angularVelocity = (float)(speedMeters / 0.34);
            var maximumRpm = Electric ? 0 : ExtremeMetrics ? 18_000 : OrbitReference ? 9_200 : 8_000;
            var exactRedlineRpm = ExtremeMetrics ? 17_500 : OrbitReference ? 9_200 : 7_500;
            var state = new VehicleState
            {
                IsRaceOn = true,
                GameTimestampMilliseconds = 12_000,
                ReceivedAtUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
                CarOrdinal = ordinal,
                Drivetrain = DrivetrainType.AllWheelDrive,
                NumCylinders = Electric ? 0 : 8,
                PowerWatts = ExtremeMetrics ? (float)(9999 * wattsPerHorsepower) : OrbitReference ? (float)(427 * wattsPerHorsepower) : 84_500,
                TorqueNm = ExtremeMetrics ? 9999 : OrbitReference ? 474 : 310.25f,
                GroundSpeedMetersPerSecond = (float)(ExtremeMetrics ? 432.1 / SpeedModel.MetersPerSecondToMilesPerHour : OrbitReference ? 32.6 / SpeedModel.MetersPerSecondToMilesPerHour : speedMeters),
                WheelRotationRadiansPerSecond = new(angularVelocity, angularVelocity, angularVelocity, angularVelocity),
                TireSlipRatio = new(0.01f, 0.01f, 0.02f, 0.02f),
                TireSlipAngle = default,
                NormalizedSuspensionTravel = new(0.5f, 0.5f, 0.5f, 0.5f),
                LateralAccelerationMetersPerSecondSquared = ExtremeMetrics ? (float)(-9.87 * 9.80665) : OrbitReference ? (float)(-0.06 * 9.80665) : 3.2f,
                LongitudinalAccelerationMetersPerSecondSquared = ExtremeMetrics ? (float)(9.87 * 9.80665) : OrbitReference ? (float)(1.04 * 9.80665) : 2.1f,
                EngineRpm = Electric ? 0 : ExtremeMetrics ? 17_650 : OrbitReference ? 6_410 : 4_500,
                EngineMaximumRpm = maximumRpm,
                Gear = Electric ? TransmissionGear.First : ExtremeMetrics ? TransmissionGear.Tenth : OrbitReference ? TransmissionGear.Third : TransmissionGear.Fourth,
                Steering = ExtremeMetrics ? (sbyte)-127 : (sbyte)8,
                Accelerator = ExtremeMetrics ? (byte)255 : (byte)160,
                Brake = ExtremeMetrics ? (byte)255 : (byte)0
            };
            var assists = NativeAssistStateCalculator.Calculate(
                new NativeAssistRawState(true, !OrbitReference, !OrbitReference, true, 0, 0.2f, 0, 0,
                    [0, 0, 0, 0], 0, 0, OrbitReference ? 0u : 2u, 0), 0.1f, 1, ordinal);
            var native = new NativeHudSnapshot(
                !Electric,
                1,
                ordinal,
                NativeAssistProviderStatus.Ready,
                Electric ? ExactRedlineResult.Unavailable() : ExactRedlineResult.Exact(exactRedlineRpm * 2 * Math.PI / 60),
                maximumRpm,
                assists,
                NativeRegenFillAmount: Electric ? 0.19 : double.NaN,
                NativePowerFillAmount: Electric ? 0.77 : double.NaN,
                NativeRegenPowerRatio: Electric ? 0.42 : double.NaN,
                NativeElectricMaximumSpeed: Electric ? 310 : double.NaN,
                NativeGaugeObservedTimestamp: Electric ? System.Diagnostics.Stopwatch.GetTimestamp() : 0,
                ElectricGearState: Electric
                    ? new NativeElectricGearState(true, 1, 2, 0, -1, true)
                    : NativeElectricGearState.Unavailable);
            if (OrbitReference)
            {
                var peakSpeedMph = ExtremeMetrics ? 999.9 : 118.1;
                var peakSpeedMeters = peakSpeedMph / SpeedModel.MetersPerSecondToMilesPerHour;
                var peakRotation = (float)(peakSpeedMeters / 0.34);
                // An earlier sample supplies retained peaks; the current sample resumes after a gap.
                var peakState = state with
                {
                    GameTimestampMilliseconds = 9_000,
                    ReceivedAtUtc = state.ReceivedAtUtc.AddSeconds(-3),
                    PowerWatts = (float)((ExtremeMetrics ? 12345 : 611) * wattsPerHorsepower),
                    TorqueNm = ExtremeMetrics ? 12345 : 690,
                    GroundSpeedMetersPerSecond = (float)peakSpeedMeters,
                    WheelRotationRadiansPerSecond = new(peakRotation, peakRotation, peakRotation, peakRotation)
                };
                viewModel.Update(peakState,
                    new IndicatedSpeed(peakSpeedMeters, peakSpeedMph, true, false, "All four wheels"),
                    new CalibrationResult(0.34, null, 1, 120, true, string.Empty, true, RollingRadii.Uniform(0.34)),
                    native, statistics, TimeSpan.Zero, SpeedUnit.MilesPerHour,
                    60, refreshDiagnostics: true, updateGForce: false);
            }
            viewModel.Update(state,
                new IndicatedSpeed(speedMeters, speedMph, true, false, "All four wheels"),
                new CalibrationResult(0.34, null, 1, 120, true, string.Empty, true, RollingRadii.Uniform(0.34)),
                native, statistics, OrbitReference ? TimeSpan.Zero : TimeSpan.FromMilliseconds(8), SpeedUnit.MilesPerHour,
                60, refreshDiagnostics: true, updateGForce: true);
            viewModel.UpdateNativeGameplayVisibility(NativeGameplayVisibility.Visible, fresh: true);
        }

        viewModel.UpdateNativeCompatibility("Synthetic UI fixture; no game validation",
            "Offline visual review; no live services started.", false, false);
    }
}
