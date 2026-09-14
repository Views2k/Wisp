using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class PowerTorqueRangeFlowTests
{
    private const double WattsPerBhp = 745.69987158227022;

    [Fact]
    public void ReapplyingUnchangedDisplayOptionsDoesNotJumpOrResetTheNeedles()
    {
        var model = new DiagnosticsViewModel(new AppSettings { PowerGaugeEnabled = true });
        Update(model, 1, 1_000, 400, 500);
        Update(model, 1, 1_016, 1_400, 1_600);
        var displayed = model.PowerTorqueDisplay;
        model.RefreshPowerTorqueDisplayOptions();
        Assert.Equal(displayed, model.PowerTorqueDisplay);
        model.PowerTorqueShowNegative = true;
        model.RefreshPowerTorqueDisplayOptions();
        var afterChange = model.PowerTorqueDisplay;
        model.AdvancePowerTorqueNeedles(System.Diagnostics.Stopwatch.GetTimestamp());
        Assert.Equal(afterChange, model.PowerTorqueDisplay);
    }

    [Fact]
    public void LivePowerNeverChangesFixedRangesUntilTheUserExplicitlyFitsThem()
    {
        var settings = new AppSettings { PowerGaugeMaximum = 1_000, TorqueGaugeMaximumNm = 1_200 };
        var model = new DiagnosticsViewModel(settings);
        Update(model, 1, 1_000, 400, 500);
        Update(model, 1, 1_010, 1_400, 1_600);
        Assert.Equal(1_000, model.PowerGaugeMaximum);
        Assert.Equal(1_200, model.TorqueGaugeMaximumNm);
        Assert.Empty(settings.PowerTorqueGaugeRanges);

        var recorded = model.PowerTorqueDisplay;
        Assert.True(recorded.PowerBhp < recorded.PeakPowerBhp);
        Assert.True(model.CanSetPowerTorqueScales);
        Assert.True(model.SetPowerTorqueScalesFromCurrentRun());
        Assert.Equal(recorded.PeakPowerBhp * 1.1, model.PowerGaugeMaximum, 8);
        Assert.Equal(recorded.PeakTorqueNm * 1.1, model.TorqueGaugeMaximumNm, 8);
        var fittedPower = model.PowerGaugeMaximum;
        var fittedTorque = model.TorqueGaugeMaximumNm;
        Assert.Equal(fittedPower, settings.PowerTorqueGaugeRanges[1].PowerMaximum);
        Assert.Equal(fittedTorque, settings.PowerTorqueGaugeRanges[1].TorqueMaximumNm);

        Update(model, 1, 1_020, 2_000, 2_500);
        Assert.True(model.PowerTorqueDisplay.PeakPowerBhp > fittedPower);
        Assert.True(model.PowerTorqueDisplay.PeakTorqueNm > fittedTorque);
        Assert.Equal(fittedPower, model.PowerGaugeMaximum);
        Assert.Equal(fittedTorque, model.TorqueGaugeMaximumNm);
        Update(model, 1, 1_030, -100, -120);
        Assert.Equal(fittedPower, model.PowerGaugeMaximum);
        Assert.Equal(fittedTorque, model.TorqueGaugeMaximumNm);
    }

    [Fact]
    public void SavedRangesBelongToTheirCarAndNewCarsUseTheGlobalDefaults()
    {
        var settings = new AppSettings { PowerGaugeMaximum = 1_000, TorqueGaugeMaximumNm = 1_200 };
        var model = new DiagnosticsViewModel(settings);
        Update(model, 11, 1_000, 700, 800);
        model.PowerGaugeMaximum = 1_350;
        model.TorqueGaugeMaximumNm = 1_650;
        model.SavePowerTorqueRange();

        Update(model, 22, 1_010, 300, 400);
        Assert.Equal(1_000, model.PowerGaugeMaximum);
        Assert.Equal(1_200, model.TorqueGaugeMaximumNm);
        model.PowerGaugeMaximum = 750;
        model.TorqueGaugeMaximumNm = 900;
        model.SavePowerTorqueRange();
        Update(model, 11, 1_020, 100, 120);
        Assert.Equal(1_350, model.PowerGaugeMaximum);
        Assert.Equal(1_650, model.TorqueGaugeMaximumNm);
        Update(model, 22, 1_030, 100, 120);
        Assert.Equal(750, model.PowerGaugeMaximum);
        Assert.Equal(900, model.TorqueGaugeMaximumNm);
        Update(model, 33, 1_040, 100, 120);
        Assert.Equal(1_000, model.PowerGaugeMaximum);
        Assert.Equal(1_200, model.TorqueGaugeMaximumNm);
        Assert.Equal(1_000, settings.PowerGaugeMaximum);
        Assert.Equal(1_200, settings.TorqueGaugeMaximumNm);

        var reopened = new DiagnosticsViewModel(settings);
        Update(reopened, 11, 1_050, 100, 120);
        Assert.Equal(1_350, reopened.PowerGaugeMaximum);
        Assert.Equal(1_650, reopened.TorqueGaugeMaximumNm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TelemetryLossDisablesFitWhilePreservingTheChosenRanges(bool preserveHudVisuals)
    {
        var settings = new AppSettings();
        var model = new DiagnosticsViewModel(settings);
        Update(model, 1, 1_000, 600, 700);
        model.PowerGaugeMaximum = 850;
        model.TorqueGaugeMaximumNm = 950;
        model.SavePowerTorqueRange();
        Assert.True(model.CanSetPowerTorqueScales);
        var changes = new List<string?>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        model.UpdateWaiting(default, TelemetryConnectionState.Lost, TimeSpan.FromSeconds(1), 0,
            preserveHudVisuals: preserveHudVisuals);
        Assert.False(model.HasLiveTelemetry);
        Assert.False(model.CanSetPowerTorqueScales);
        Assert.False(model.SetPowerTorqueScalesFromCurrentRun());
        Assert.Equal(850, model.PowerGaugeMaximum);
        Assert.Equal(950, model.TorqueGaugeMaximumNm);
        Assert.Contains(nameof(model.CanSetPowerTorqueScales), changes);

        changes.Clear();
        Update(model, 1, 1_010, 650, 750);
        Assert.True(model.CanSetPowerTorqueScales);
        Assert.Equal(850, model.PowerGaugeMaximum);
        Assert.Equal(950, model.TorqueGaugeMaximumNm);
        Assert.Contains(nameof(model.CanSetPowerTorqueScales), changes);
    }

    [Fact]
    public void TorqueUnitChangesDoNotChangeTheStoredPhysicalRangeOrFitMath()
    {
        var settings = new AppSettings();
        var model = new DiagnosticsViewModel(settings);
        Update(model, 1, 1_000, 800, 1_000);
        model.TorqueUnitSelectionIndex = 1;
        Assert.True(model.SetPowerTorqueScalesFromCurrentRun());
        Assert.Equal(1_100, model.TorqueGaugeMaximumNm, 8);
        Assert.Equal(811.3183642049922, model.TorqueGaugeMaximum, 8);
        Assert.Equal(1_100, settings.PowerTorqueGaugeRanges[1].TorqueMaximumNm, 8);

        model.TorqueUnitSelectionIndex = 0;
        Assert.Equal(1_100, model.TorqueGaugeMaximum, 8);
        model.TorqueGaugeMaximumNm = 2_000;
        model.SavePowerTorqueRange();
        model.TorqueUnitSelectionIndex = 1;
        Assert.Equal(1475.1242985545312, model.TorqueGaugeMaximum, 8);
        Update(model, 2, 1_010, 400, 500);
        Update(model, 1, 1_020, 400, 500);
        Assert.Equal(2_000, model.TorqueGaugeMaximumNm);
        Assert.Equal(1475.1242985545312, model.TorqueGaugeMaximum, 8);
    }

    [Fact]
    public void PreviewDataCannotBeUsedToFitRangesBeforeLiveTelemetryArrives()
    {
        var settings = new AppSettings { PowerGaugeMaximum = 1_250, TorqueGaugeMaximumNm = 1_450 };
        var model = new DiagnosticsViewModel(settings);
        Assert.True(model.PreviewPowerTorqueDisplay.Available);
        Assert.False(model.HasLiveTelemetry);
        Assert.False(model.CanSetPowerTorqueScales);
        Assert.False(model.SetPowerTorqueScalesFromCurrentRun());
        Assert.Equal(1_250, model.PowerGaugeMaximum);
        Assert.Equal(1_450, model.TorqueGaugeMaximumNm);
        Assert.Empty(settings.PowerTorqueGaugeRanges);
        Update(model, 1, 1_000, 0, 0);
        Assert.True(model.HasLiveTelemetry);
        Assert.False(model.CanSetPowerTorqueScales);
        Assert.False(model.SetPowerTorqueScalesFromCurrentRun());
        Assert.Empty(settings.PowerTorqueGaugeRanges);
    }

    [Fact]
    public void ProfileCapturesAndRestoresTheEffectiveCarRangeAndCaptionIdentifiesItsOwner()
    {
        var settings = new AppSettings { PowerGaugeMaximum = 1_000, TorqueGaugeMaximumNm = 1_200 };
        var model = new DiagnosticsViewModel(settings);
        Assert.Equal("Default range · no car detected yet", model.PowerTorqueRangeCaption);
        Update(model, 1, 1_000, 200, 250);
        Assert.Equal("Range for the current car", model.PowerTorqueRangeCaption);
        model.PowerGaugeMaximum = 250;
        model.TorqueGaugeMaximumNm = 325;
        model.SavePowerTorqueRange();
        Assert.Equal(1_000, settings.PowerGaugeMaximum);
        Assert.Equal(1_200, settings.TorqueGaugeMaximumNm);

        var preset = HudPreset.Capture(settings, "Current car range");
        model.CapturePowerTorquePresetRange(preset);
        Assert.Equal(250, preset.PowerGaugeMaximum);
        Assert.Equal(325, preset.TorqueGaugeMaximumNm);
        model.PowerGaugeMaximum = 3_500;
        model.TorqueGaugeMaximumNm = 4_500;
        model.SavePowerTorqueRange();
        Assert.Equal(3_500, settings.PowerTorqueGaugeRanges[1].PowerMaximum);

        preset.ApplyTo(settings);
        model.ApplyPowerTorquePresetRange(preset);
        model.InitializePowerTorqueSettings(settings);
        Assert.Equal(250, model.PowerGaugeMaximum);
        Assert.Equal(325, model.TorqueGaugeMaximumNm);
        Assert.Equal(250, settings.PowerTorqueGaugeRanges[1].PowerMaximum);
        Assert.Equal(325, settings.PowerTorqueGaugeRanges[1].TorqueMaximumNm);

        model.UpdateWaiting(default, TelemetryConnectionState.Lost, TimeSpan.FromSeconds(1), 0);
        Assert.Equal("Range for the last detected car", model.PowerTorqueRangeCaption);
        Update(model, 2, 1_010, 100, 120);
        Assert.Equal("Range for the current car", model.PowerTorqueRangeCaption);
        Update(model, 1, 1_020, 200, 250);
        Assert.Equal(250, model.PowerGaugeMaximum);
        Assert.Equal(325, model.TorqueGaugeMaximumNm);
    }

    private static void Update(DiagnosticsViewModel model, int carOrdinal, uint timestamp,
        double horsepower, float torqueNm)
    {
        var state = new VehicleState
        {
            IsRaceOn = true,
            GameTimestampMilliseconds = timestamp,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            CarOrdinal = carOrdinal,
            Drivetrain = DrivetrainType.RearWheelDrive,
            GroundSpeedMetersPerSecond = 30,
            WheelRotationRadiansPerSecond = new WheelValues(100, 100, 100, 100),
            TireSlipRatio = default,
            TireSlipAngle = default,
            NormalizedSuspensionTravel = new WheelValues(.5f, .5f, .5f, .5f),
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = 0,
            EngineRpm = 4_000,
            EngineMaximumRpm = 8_000,
            Gear = TransmissionGear.Second,
            Steering = 0,
            Accelerator = 255,
            Brake = 0,
            PowerWatts = (float)(horsepower * WattsPerBhp),
            TorqueNm = torqueNm
        };
        model.Update(state, new IndicatedSpeed(0, 30, true, false, "Rear"),
            new CalibrationResult(null, .3, .2, 0, true, string.Empty, false),
            default, TimeSpan.Zero, SpeedUnit.MilesPerHour, 60,
            refreshDiagnostics: false, updateGForce: false);
    }
}
