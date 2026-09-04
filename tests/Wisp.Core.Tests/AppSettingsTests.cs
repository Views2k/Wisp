using System.Text.Json;
using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void NewSettingsDefaultToAquaWithAnExpandedSidebar()
    {
        var settings = new AppSettings();

        Assert.Equal("Aqua", settings.ColorTheme);
        Assert.Equal("Aqua", settings.HudBorderTheme);
        Assert.False(settings.SidebarCollapsed);
        Assert.True(settings.GForceAttached);
    }

    [Fact]
    public void MissingPalettePreferencesPreserveExistingHudAndStartupValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, """
                {
                  "SettingsRevision": 7,
                  "UdpPort": 5601,
                  "LayoutMode": "Native",
                  "OverlayOpacity": 0.62,
                  "StartWithForza": true,
                  "StartMinimizedWithForza": true
                }
                """);

            var settings = new SettingsService(path).Load();

            Assert.Equal("Aqua", settings.ColorTheme);
            Assert.Equal("Aqua", settings.HudBorderTheme);
            Assert.False(settings.SidebarCollapsed);
            Assert.Equal(5601, settings.UdpPort);
            Assert.Equal(HudLayoutMode.Native, settings.LayoutMode);
            Assert.Equal(0.62, settings.OverlayOpacity);
            Assert.True(settings.StartWithForza);
            Assert.True(settings.StartMinimizedWithForza);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryPaletteAndSidebarStateRoundTripsWithoutChangingOtherSettings(bool collapsed)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var service = new SettingsService(path);
            foreach (var theme in AppColorThemes.All)
            {
                var settings = CreateCurrentUiSettings();
                settings.ColorTheme = theme.Name;
                settings.HudBorderTheme = theme.Name;
                settings.SidebarCollapsed = collapsed;

                service.Save(settings);
                var loaded = service.Load();

                Assert.Equal(theme.Name, loaded.ColorTheme);
                Assert.Equal(theme.Name, loaded.HudBorderTheme);
                Assert.Equal(collapsed, loaded.SidebarCollapsed);
                Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(loaded));
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(null, "Aqua")]
    [InlineData("", "Aqua")]
    [InlineData("   ", "Aqua")]
    [InlineData("unknown", "Aqua")]
    [InlineData("blue", "Blue")]
    [InlineData(" PLuM ", "Plum")]
    public void PersistedThemeNamesNormalizeWithoutResettingOtherSettings(string? name, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = CreateCurrentUiSettings();
            settings.ColorTheme = name!;
            settings.SidebarCollapsed = true;
            File.WriteAllText(path, JsonSerializer.Serialize(settings));

            var loaded = new SettingsService(path).Load();
            settings.ColorTheme = expected;

            Assert.Equal(expected, loaded.ColorTheme);
            Assert.True(loaded.SidebarCollapsed);
            Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(loaded));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void NullCalibrationEntriesAreDiscardedDuringLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, """
                {
                  "SettingsRevision": 7,
                  "UdpPort": 5601,
                  "Calibrations": [null]
                }
                """);

            var settings = new SettingsService(path).Load();

            Assert.Equal(5601, settings.UdpPort);
            Assert.Empty(settings.Calibrations);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FailedAtomicReplacementPreservesThePreviousSettingsAndCleansStagingFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var service = new SettingsService(path);
            service.Save(new AppSettings { UdpPort = 5601 });

            Exception? failure;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                failure = Record.Exception(() => service.Save(new AppSettings { UdpPort = 5602 }));
            }

            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            Assert.Equal(5601, service.Load().UdpPort);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static AppSettings CreateCurrentUiSettings()
    {
        var settings = new AppSettings
        {
            SettingsRevision = 7,
            UdpPort = 5601,
            SpeedUnit = SpeedUnit.KilometersPerHour,
            SpeedSource = SpeedSourceMode.Fh6VehicleSpeed,
            AggregationMode = WheelAggregationMode.Robust,
            LayoutMode = HudLayoutMode.Native,
            NativeGaugeMode = NativeGaugeMode.Analogue,
            GearDisplayMode = GearDisplayMode.Automatic,
            OverlayWidthScale = 1.25,
            OverlayHeightScale = 0.9,
            OverlayOpacity = 0.62,
            OverlayLocked = false,
            GForceEnabled = false,
            GForceAttached = false,
            BoostGaugeEnabled = true,
            BoostGaugeAttached = false,
            BoostGaugeColorNumber = true,
            DigitalBoostGaugeColorNumber = true,
            DigitalBoostGaugeStockColors = true,
            BoostPressureUnit = BoostPressureUnit.Bar,
            BoostGaugeScale = 1.3,
            BoostGaugeTheme = "Stock",
            TireTemperatureGaugeEnabled = true,
            TireTemperatureGaugeAttached = false,
            TireTemperatureReactiveColors = false,
            TireTemperatureUnit = TireTemperatureUnit.Celsius,
            TireTemperatureGaugeScale = 1.15,
            GForceWidthScale = 0.8,
            GForceHeightScale = 1.1,
            Smoothing = 0.27,
            StartWithWindows = false,
            StartWithForza = false,
            StartMinimizedWithForza = true,
            AnimatedBackground = false,
            GameAwareVisibility = false,
            AutoMinimizeOnTelemetry = false,
            TractionCueEnabled = false,
            Placements = new Dictionary<string, OverlayPlacement>
            {
                ["display"] = new OverlayPlacement(90, 180, 1.25, 0.9)
            },
            LastOverlayPlacementKey = "display",
            Calibrations = new List<CalibrationSnapshot>
            {
                new(100, 0.36, 121, DrivetrainType.RearWheelDrive, RollingRadiusEstimator.CurrentCalibrationRevision)
            }
        };
        settings.MigrateSettings();
        return settings;
    }

    [Fact]
    public void EveryBoostGaugeStyleRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var service = new SettingsService(path);
            foreach (var theme in BoostGaugeThemes.All)
            {
                var settings = CreateCurrentUiSettings();
                settings.BoostGaugeTheme = theme.Name;

                service.Save(settings);
                var loaded = service.Load();

                Assert.Equal(theme.Name, loaded.BoostGaugeTheme);
                Assert.True(loaded.BoostGaugeColorNumber);
                Assert.True(loaded.DigitalBoostGaugeColorNumber);
                Assert.True(loaded.DigitalBoostGaugeStockColors);
                Assert.Equal(BoostPressureUnit.Bar, loaded.BoostPressureUnit);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ExistingSettingsMigrateToLiteralDrivenWheelAggregation()
    {
        var settings = new AppSettings
        {
            SettingsRevision = 0,
            AggregationMode = WheelAggregationMode.RawDrivenWheels
        };

        settings.MigrateSettings();

        Assert.Equal(WheelAggregationMode.RawDrivenWheels, settings.AggregationMode);
        Assert.Equal(9, settings.SettingsRevision);
    }

    [Fact]
    public void CurrentLiteralModeRemainsSelectedAfterMigration()
    {
        var settings = new AppSettings
        {
            SettingsRevision = 2,
            AggregationMode = WheelAggregationMode.RawDrivenWheels
        };

        settings.MigrateSettings();

        Assert.Equal(WheelAggregationMode.RawDrivenWheels, settings.AggregationMode);
        Assert.Equal(9, settings.SettingsRevision);
    }

    [Fact]
    public void FormerDefaultSmoothingMigratesToZeroLatency()
    {
        var settings = new AppSettings
        {
            SettingsRevision = 2,
            Smoothing = 0.08
        };

        settings.MigrateSettings();

        Assert.Equal(0, settings.Smoothing);
        Assert.Equal(9, settings.SettingsRevision);
    }

    [Fact]
    public void InvalidPersistedValuesAreNormalizedSafely()
    {
        var settings = new AppSettings
        {
            SettingsRevision = 2,
            UdpPort = 5250,
            SpeedUnit = (SpeedUnit)99,
            AggregationMode = (WheelAggregationMode)99,
            LayoutMode = (HudLayoutMode)99,
            NativeGaugeMode = (NativeGaugeMode)99,
            GearDisplayMode = (GearDisplayMode)99,
            OverlayWidthScale = double.NaN,
            OverlayHeightScale = 8,
            OverlayOpacity = -1,
            Smoothing = double.PositiveInfinity,
            TireTemperatureUnit = (TireTemperatureUnit)99,
            BoostPressureUnit = (BoostPressureUnit)99,
            TireTemperatureGaugeScale = double.PositiveInfinity
        };

        settings.MigrateSettings();

        Assert.Equal(5500, settings.UdpPort);
        Assert.Equal(SpeedUnit.MilesPerHour, settings.SpeedUnit);
        Assert.Equal(SpeedSourceMode.WheelIndicated, settings.SpeedSource);
        Assert.Equal(WheelAggregationMode.RawDrivenWheels, settings.AggregationMode);
        Assert.Equal(HudLayoutMode.Minimal, settings.LayoutMode);
        Assert.Equal(NativeGaugeMode.Digital, settings.NativeGaugeMode);
        Assert.Equal(GearDisplayMode.Manual, settings.GearDisplayMode);
        Assert.Equal(1, settings.OverlayWidthScale);
        Assert.Equal(2, settings.OverlayHeightScale);
        Assert.Equal(0.35, settings.OverlayOpacity);
        Assert.Equal(0, settings.Smoothing);
        Assert.Equal(TireTemperatureUnit.Fahrenheit, settings.TireTemperatureUnit);
        Assert.Equal(BoostPressureUnit.Psi, settings.BoostPressureUnit);
        Assert.Equal(1, settings.TireTemperatureGaugeScale);
    }

    [Fact]
    public void Fh6SpeedSourcePersistsAndInvalidValuesFailToWheelIndicated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var service = new SettingsService(path);
            service.Save(new AppSettings
            {
                SettingsRevision = 6,
                SpeedSource = SpeedSourceMode.Fh6VehicleSpeed
            });

            Assert.Equal(SpeedSourceMode.Fh6VehicleSpeed, service.Load().SpeedSource);

            File.WriteAllText(path, """
                {
                  "SettingsRevision": 6,
                  "SpeedSource": 99
                }
                """);
            Assert.Equal(SpeedSourceMode.WheelIndicated, service.Load().SpeedSource);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void NativeDefaultsUseDigitalGaugeAndRequestedDriftAxes()
    {
        var settings = new AppSettings();

        settings.MigrateSettings();

        Assert.Equal(NativeGaugeMode.Digital, settings.NativeGaugeMode);
        Assert.True(settings.InvertLateralG);
        Assert.False(settings.InvertLongitudinalG);
        Assert.Equal(1.0, settings.OverlayOpacity);
    }

    [Fact]
    public void NativeOpacityIsResetOnceWhenMigratingToCorrectedAssetAlpha()
    {
        var upgraded = new AppSettings
        {
            SettingsRevision = 6,
            LayoutMode = HudLayoutMode.Native,
            OverlayOpacity = 0.62
        };
        upgraded.MigrateSettings();

        Assert.Equal(9, upgraded.SettingsRevision);
        Assert.Equal(1.0, upgraded.OverlayOpacity);

        var current = new AppSettings
        {
            SettingsRevision = 7,
            LayoutMode = HudLayoutMode.Native,
            OverlayOpacity = 0.62
        };
        current.MigrateSettings();

        Assert.Equal(0.62, current.OverlayOpacity);
    }

    [Fact]
    public void CorruptSettingsFileFallsBackToSafeDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "{not valid json");

            var settings = new SettingsService(path).Load();

            Assert.Equal(5500, settings.UdpPort);
            Assert.Equal(WheelAggregationMode.RawDrivenWheels, settings.AggregationMode);
            Assert.Equal(9, settings.SettingsRevision);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RevisionThreeCalibrationProfilesAreDiscardedForSafeRelearning()
    {
        var settings = new AppSettings
        {
            SettingsRevision = 3,
            Smoothing = 0.52,
            Calibrations = new List<CalibrationSnapshot>
            {
                new(100, 0.36, 121, DrivetrainType.RearWheelDrive)
            }
        };

        settings.MigrateSettings();

        Assert.Equal(9, settings.SettingsRevision);
        Assert.Equal(0, settings.Smoothing);
        Assert.Empty(settings.Calibrations);
    }

    [Fact]
    public void CurrentCalibrationProfileSurvivesSettingsNormalization()
    {
        var snapshot = new CalibrationSnapshot(
            100,
            0.36,
            121,
            DrivetrainType.RearWheelDrive,
            RollingRadiusEstimator.CurrentCalibrationRevision);
        var settings = new AppSettings
        {
            SettingsRevision = 4,
            Calibrations = new List<CalibrationSnapshot> { snapshot }
        };

        settings.MigrateSettings();

        Assert.Equal(snapshot, Assert.Single(settings.Calibrations));
    }

    [Fact]
    public void LegacyScalarAwdProfileIsDiscardedBecauseItCannotRepresentStaggeredTires()
    {
        var settings = new AppSettings
        {
            SettingsRevision = 4,
            Calibrations = new List<CalibrationSnapshot>
            {
                new(
                    100,
                    0.34,
                    121,
                    DrivetrainType.AllWheelDrive,
                    RollingRadiusEstimator.CurrentCalibrationRevision)
            }
        };

        settings.MigrateSettings();

        Assert.Empty(settings.Calibrations);
    }

    [Fact]
    public void AxleSpecificAwdProfileSurvivesSettingsNormalization()
    {
        var snapshot = new CalibrationSnapshot(
            100,
            0.3375,
            121,
            DrivetrainType.AllWheelDrive,
            RollingRadiusEstimator.CurrentCalibrationRevision,
            0.3,
            0.375);
        var settings = new AppSettings
        {
            SettingsRevision = 4,
            Calibrations = new List<CalibrationSnapshot> { snapshot }
        };

        settings.MigrateSettings();

        Assert.Equal(snapshot, Assert.Single(settings.Calibrations));
    }

    [Fact]
    public void PreviousCalibrationRevisionIsDiscardedDuringSettingsNormalization()
    {
        var settings = new AppSettings
        {
            SettingsRevision = 4,
            Calibrations = new List<CalibrationSnapshot>
            {
                new(
                    100,
                    0.36,
                    121,
                    DrivetrainType.RearWheelDrive,
                    RollingRadiusEstimator.CurrentCalibrationRevision - 1)
            }
        };

        settings.MigrateSettings();

        Assert.Empty(settings.Calibrations);
    }

    [Fact]
    public void MissingCalibrationRevisionInPersistedJsonFailsClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "SettingsRevision": 3,
                  "Calibrations": [
                    {
                      "CarOrdinal": 100,
                      "RadiusMeters": 0.36,
                      "SampleCount": 121,
                      "Drivetrain": "RearWheelDrive"
                    }
                  ]
                }
                """);

            var settings = new SettingsService(path).Load();

            Assert.Equal(9, settings.SettingsRevision);
            Assert.Empty(settings.Calibrations);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LegacyExternalRedlineSettingsAreIgnoredAndNotWrittenBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "SettingsRevision": 5,
                  "ExactNativeRedlineEnabled": true,
                  "FctExternalServiceConsent": true,
                  "FctExecutablePath": "C:\\legacy\\provider.exe",
                  "Fh6ProfilePath": "C:\\legacy\\profile.bin",
                  "UdpPort": 5601
                }
                """);

            var service = new SettingsService(path);
            var settings = service.Load();
            service.Save(settings);
            var saved = File.ReadAllText(path);

            Assert.Equal(9, settings.SettingsRevision);
            Assert.Equal(5601, settings.UdpPort);
            Assert.DoesNotContain("ExactNativeRedlineEnabled", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("FctExternalServiceConsent", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("FctExecutablePath", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("Fh6ProfilePath", saved, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PhysicallyExtremeCurrentCalibrationIsDiscarded()
    {
        var settings = new AppSettings
        {
            SettingsRevision = 4,
            Calibrations = new List<CalibrationSnapshot>
            {
                new(
                    100,
                    1.2,
                    121,
                    DrivetrainType.RearWheelDrive,
                    RollingRadiusEstimator.CurrentCalibrationRevision)
            }
        };

        settings.MigrateSettings();

        Assert.Empty(settings.Calibrations);
    }

    [Fact]
    public void UndersampledCurrentCalibrationIsDiscarded()
    {
        var settings = new AppSettings
        {
            SettingsRevision = 4,
            Calibrations = new List<CalibrationSnapshot>
            {
                new(
                    100,
                    0.36,
                    CalibrationOptions.DefaultMinimumSamples - 1,
                    DrivetrainType.RearWheelDrive,
                    RollingRadiusEstimator.CurrentCalibrationRevision)
            }
        };

        settings.MigrateSettings();

        Assert.Empty(settings.Calibrations);
    }

    [Fact]
    public void NewlyTrustedProfileRoundTripsAndResumesWithoutCalibrationFrames()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var learned = new RollingRadiusEstimator();
            for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
            {
                learned.Observe(TestVehicleState.Create());
            }

            var service = new SettingsService(path);
            service.Save(new AppSettings
            {
                SettingsRevision = 4,
                Calibrations = learned.ExportSnapshots().ToList()
            });

            var loaded = service.Load();
            var restored = new RollingRadiusEstimator();
            restored.ImportSnapshots(loaded.Calibrations);
            var wheelspin = restored.Observe(TestVehicleState.Create(
                wheelSpeed: new WheelValues(200, 200, 200, 200),
                slipRatio: new WheelValues(1, 1, 1, 1)));

            Assert.True(wheelspin.IsTrusted);
            Assert.Equal(0.3, wheelspin.RadiusMeters!.Value, 6);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
