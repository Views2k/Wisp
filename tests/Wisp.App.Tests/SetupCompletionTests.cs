using System.Text.Json;
using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SetupCompletionTests
{
    private static readonly DateTimeOffset CompletedAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewAndLegacyAutoCompletedSettingsRequireExplicitSetup(bool oldCompletedFlag)
    {
        var settings = ExistingSettings();
        settings.HasCompletedSetup = oldCompletedFlag;
        var calibration = Assert.Single(settings.Calibrations);
        var placement = settings.Placements["synthetic-display"];

        settings.MigrateSettings();

        Assert.True(settings.RequiresSetup);
        Assert.False(settings.HasCompletedSetup);
        Assert.Equal(6500, settings.UdpPort);
        Assert.Equal(HudLayoutMode.Native, settings.LayoutMode);
        Assert.Equal(NativeGaugeMode.Analogue, settings.NativeGaugeMode);
        Assert.Equal(0.72, settings.OverlayOpacity);
        Assert.Equal(1.2, settings.OverlayWidthScale);
        Assert.Equal(calibration, Assert.Single(settings.Calibrations));
        Assert.Same(placement, settings.Placements["synthetic-display"]);
    }

    [Fact]
    public void ExplicitCompletionPersistsAndAllowsLaterOfflineStartup()
    {
        var settings = ExistingSettings();
        string? saved = null;

        SetupCompletion.Save(settings, Choices(), Evidence(), value => saved = JsonSerializer.Serialize(value), CompletedAt);
        var restored = JsonSerializer.Deserialize<AppSettings>(Assert.IsType<string>(saved))!;
        restored.MigrateSettings();

        Assert.False(settings.RequiresSetup);
        Assert.False(restored.RequiresSetup);
        Assert.True(restored.HasCompletedSetup);
        Assert.Equal(SetupCompletionRecord.CurrentVersion, restored.SetupCompletion!.Version);
        Assert.Equal(5500, restored.SetupCompletion.ValidatedUdpPort);
        Assert.Equal(CompletedAt, restored.SetupCompletion.CompletedAtUtc);
        Assert.Equal(HudLayoutMode.Combined, restored.LayoutMode);
        Assert.Equal(SpeedUnit.KilometersPerHour, restored.SpeedUnit);
        Assert.Equal(GearDisplayMode.Manual, restored.GearDisplayMode);
    }

    [Fact]
    public void CompletionIsHistoricalAndDoesNotExpireWhenTheGameIsOffline()
    {
        var settings = ExistingSettings();
        SetupCompletion.Save(
            settings, Choices(), Evidence() with { VerifiedAtUtc = CompletedAt.AddYears(-2).AddSeconds(-5) },
            _ => { }, CompletedAt.AddYears(-2));
        settings.UdpPort = 6501;
        settings.HasCompletedSetup = false;

        settings.MigrateSettings();

        Assert.False(settings.RequiresSetup);
        Assert.True(settings.HasCompletedSetup);
        Assert.Equal(6501, settings.UdpPort);
    }

    [Fact]
    public void SetupChangesOnlyChosenPreferencesAndCompletionMetadata()
    {
        var settings = ExistingSettings();
        var calibration = settings.Calibrations;
        var placements = settings.Placements;
        var gForcePlacements = settings.GForcePlacements;
        var saves = 0;

        SetupCompletion.Save(settings, Choices(), Evidence(), _ => saves++, CompletedAt);

        Assert.Equal(1, saves);
        Assert.Same(calibration, settings.Calibrations);
        Assert.Same(placements, settings.Placements);
        Assert.Same(gForcePlacements, settings.GForcePlacements);
        Assert.Equal("synthetic-display", settings.LastOverlayPlacementKey);
        Assert.Equal(1.2, settings.OverlayWidthScale);
        Assert.Equal(0.8, settings.OverlayHeightScale);
        Assert.Equal(0.72, settings.OverlayOpacity);
        Assert.Equal(0.2, settings.Smoothing);
        Assert.False(settings.StartWithWindows);
        Assert.False(settings.GForceEnabled);
        Assert.False(settings.GameAwareVisibility);
        Assert.False(settings.OverlayLocked);
        Assert.False(settings.InvertLateralG);
        Assert.True(settings.InvertLongitudinalG);
    }

    [Fact]
    public void FailedSaveRollsBackPreferencesAndKeepsTheStartupGateClosed()
    {
        var settings = ExistingSettings();
        settings.HasCompletedSetup = true;
        var before = JsonSerializer.Serialize(settings);

        Assert.Throws<IOException>(() => SetupCompletion.Save(
            settings, Choices(), Evidence(), _ => throw new IOException("Synthetic save failure"), CompletedAt));

        Assert.True(settings.RequiresSetup);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        Assert.Null(settings.SetupCompletion);
    }

    [Fact]
    public void FailedSaveCanBeRetriedWithoutRepeatingSuccessfulTelemetryTest()
    {
        var settings = ExistingSettings();
        var evidence = Evidence();
        Assert.Throws<UnauthorizedAccessException>(() => SetupCompletion.Save(
            settings, Choices(), evidence, _ => throw new UnauthorizedAccessException(), CompletedAt));

        SetupCompletion.Save(settings, Choices(), evidence, _ => { }, CompletedAt);

        Assert.False(settings.RequiresSetup);
    }

    [Theory]
    [InlineData("missing-test")]
    [InlineData("wrong-port")]
    [InlineData("data-out")]
    [InlineData("display")]
    [InlineData("stock-hud")]
    [InlineData("few-packets")]
    [InlineData("no-motion")]
    [InlineData("short-span")]
    [InlineData("bad-style")]
    public void MissingEvidenceOrConfirmationNeverSavesOrUnlocks(string failure)
    {
        var settings = ExistingSettings();
        var before = JsonSerializer.Serialize(settings);
        SetupTelemetryEvidence? evidence = failure switch
        {
            "missing-test" => null,
            "wrong-port" => Evidence() with { Port = 5501 },
            "few-packets" => Evidence() with { Packets = 1, MovingPackets = 1 },
            "no-motion" => Evidence() with { MovingPackets = 0 },
            "short-span" => Evidence() with { Elapsed = TimeSpan.FromMilliseconds(10) },
            _ => Evidence()
        };
        var preferences = failure switch
        {
            "data-out" => Choices() with { DataOutConfirmed = false },
            "display" => Choices() with { DisplayModeConfirmed = false },
            "stock-hud" => Choices() with { StockHudConfirmed = false },
            "bad-style" => Choices() with { LayoutMode = (HudLayoutMode)99 },
            _ => Choices()
        };
        var saved = false;

        Assert.Throws<InvalidOperationException>(() => SetupCompletion.Save(
            settings, preferences, evidence, _ => saved = true, CompletedAt));

        Assert.False(saved);
        Assert.True(settings.RequiresSetup);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("date")]
    [InlineData("port")]
    [InlineData("reserved")]
    [InlineData("packets")]
    [InlineData("motion")]
    [InlineData("elapsed")]
    [InlineData("confirmation")]
    public void MalformedOrDifferentVersionCompletionRecordsFailClosed(string field)
    {
        var settings = ExistingSettings();
        SetupCompletion.Save(settings, Choices(), Evidence(), _ => { }, CompletedAt);
        var record = settings.SetupCompletion!;
        settings.SetupCompletion = field switch
        {
            "version" => record with { Version = SetupCompletionRecord.CurrentVersion + 1 },
            "date" => record with { CompletedAtUtc = default },
            "port" => record with { ValidatedUdpPort = 0 },
            "reserved" => record with { ValidatedUdpPort = 5250 },
            "packets" => record with { ValidatedPackets = 0 },
            "motion" => record with { MovingPackets = record.ValidatedPackets + 1 },
            "elapsed" => record with { ValidatedElapsedMilliseconds = double.NaN },
            _ => record with { StockHudConfirmed = false }
        };

        settings.MigrateSettings();

        Assert.True(settings.RequiresSetup);
        Assert.False(settings.HasCompletedSetup);
    }

    private static SetupPreferences Choices() => new(
        5500, SpeedUnit.KilometersPerHour, SpeedSourceMode.Fh6VehicleSpeed,
        HudLayoutMode.Combined, NativeGaugeMode.Digital, GearDisplayMode.Manual, true, true, true);

    private static SetupTelemetryEvidence Evidence() =>
        new(5500, 12, 12, TimeSpan.FromMilliseconds(550), CompletedAt.AddSeconds(-5));

    private static AppSettings ExistingSettings() => new()
    {
        SettingsRevision = 7,
        UdpPort = 6500,
        LayoutMode = HudLayoutMode.Native,
        NativeGaugeMode = NativeGaugeMode.Analogue,
        SpeedUnit = SpeedUnit.MilesPerHour,
        SpeedSource = SpeedSourceMode.WheelIndicated,
        GearDisplayMode = GearDisplayMode.Automatic,
        OverlayWidthScale = 1.2,
        OverlayHeightScale = 0.8,
        OverlayOpacity = 0.72,
        Smoothing = 0.2,
        GForceEnabled = false,
        OverlayLocked = false,
        GameAwareVisibility = false,
        StartWithWindows = false,
        InvertLateralG = false,
        InvertLongitudinalG = true,
        LastOverlayPlacementKey = "synthetic-display",
        Placements = new Dictionary<string, OverlayPlacement>
        {
            ["synthetic-display"] = new(120, 80, 1.2, 0.8)
        },
        Calibrations =
        [
            new(1, 0.36, 121, DrivetrainType.RearWheelDrive, RollingRadiusEstimator.CurrentCalibrationRevision)
        ]
    };
}
