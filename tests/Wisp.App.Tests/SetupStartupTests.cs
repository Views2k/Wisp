using System.Xml.Linq;
using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SetupStartupTests
{
    [Fact]
    public async Task LegacyFlagCannotStartNormalListenerOrCreateHudWindows()
    {
        var settings = new AppSettings { HasCompletedSetup = true, StartWithWindows = false };
        await using var controller = new AppController(settings, new SettingsService());

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RestartListenerAsync(5500));

        Assert.True(settings.RequiresSetup);
        Assert.Null(controller.Overlay);
        Assert.Null(controller.GForceOverlay);
        Assert.Null(controller.ControlPanel);
        Assert.False(controller.SetupTelemetry.IsRunning);
        controller.ViewModel.LayoutSelectionIndex = (int)HudLayoutMode.Native;
        controller.ApplyViewOptions();
        controller.SetOverlayLocked(false);
        Assert.Equal(HudLayoutMode.Minimal, settings.LayoutMode);
        Assert.True(settings.OverlayLocked);
    }

    [Fact]
    public async Task ClosingIncompleteSetupDoesNotRewriteCorruptExistingSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Setup.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        const string original = "{invalid-settings-for-test";
        try
        {
            File.WriteAllText(path, original);
            var service = new SettingsService(path);
            var settings = service.Load();
            Assert.True(settings.RequiresSetup);
            await using (var controller = new AppController(settings, service))
            {
                Assert.Null(controller.SetupTelemetry.SuccessfulEvidence);
            }

            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallerMarkerForcesWizardAndCancellationPreservesPriorLocalState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Setup.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        var markerPath = Path.Combine(directory, SettingsService.SetupRequiredMarkerFileName);
        try
        {
            var service = new SettingsService(settingsPath);
            service.Save(CompletedSettings());
            var persistedSettings = File.ReadAllBytes(settingsPath);
            File.WriteAllText(markerPath, "setup-required");

            var settings = service.Load();

            Assert.True(settings.InstallerSetupRequired);
            Assert.True(settings.RequiresSetup);
            Assert.False(settings.HasCompletedSetup);
            Assert.True(settings.SetupCompletion!.IsValid);
            Assert.Equal(6500, settings.UdpPort);
            Assert.Equal(HudLayoutMode.Native, settings.LayoutMode);
            var calibration = Assert.Single(settings.Calibrations);
            Assert.Equal(712, calibration.CarOrdinal);
            Assert.Equal(0.36, calibration.RadiusMeters);

            await using (var controller = new AppController(settings, service))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync());
            }

            Assert.True(File.Exists(markerPath));
            Assert.True(persistedSettings.SequenceEqual(File.ReadAllBytes(settingsPath)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InstallerMarkerSurvivesFailedPersistenceAndClearsAfterSuccessfulSetupSave()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Setup.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        var markerPath = Path.Combine(directory, SettingsService.SetupRequiredMarkerFileName);
        try
        {
            var service = new SettingsService(settingsPath);
            service.Save(CompletedSettings());
            File.WriteAllText(markerPath, "setup-required");
            var settings = service.Load();
            service.Save(settings);
            Assert.True(File.Exists(markerPath));
            var beforeFailedSave = File.ReadAllBytes(settingsPath);

            Exception? failure;
            using (new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                failure = Record.Exception(() => SetupCompletion.Save(
                    settings,
                    SetupChoices(),
                    SetupEvidence(),
                    service.SaveCompletedSetup,
                    CompletedAt));
            }

            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            Assert.True(File.Exists(markerPath));
            Assert.True(settings.RequiresSetup);
            Assert.True(beforeFailedSave.SequenceEqual(File.ReadAllBytes(settingsPath)));

            SetupCompletion.Save(
                settings,
                SetupChoices(),
                SetupEvidence(),
                service.SaveCompletedSetup,
                CompletedAt);

            Assert.False(File.Exists(markerPath));
            Assert.False(settings.InstallerSetupRequired);
            Assert.False(settings.RequiresSetup);
            var restored = service.Load();
            Assert.False(restored.RequiresSetup);
            Assert.Equal(5500, restored.UdpPort);
            Assert.Equal(HudLayoutMode.Combined, restored.LayoutMode);
            Assert.Equal(712, Assert.Single(restored.Calibrations).CarOrdinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BackgroundStartupMustFinishWizardBeforeAnyMainOrOverlayConstruction()
    {
        var source = Source("App.xaml.cs");
        var gate = source.IndexOf("if (setupRequired)", StringComparison.Ordinal);
        var wizard = source.IndexOf("_setupWindow.ShowDialog()", StringComparison.Ordinal);
        var completionCheck = source.IndexOf("if (!completed || settings.RequiresSetup)", StringComparison.Ordinal);
        Assert.True(gate >= 0 && wizard > gate && completionCheck > wizard);
        foreach (var construction in new[] { "new OverlayWindow(", "new GForceWindow(", "new MainWindow(" })
        {
            Assert.True(source.IndexOf(construction, StringComparison.Ordinal) > completionCheck);
        }

        Assert.Contains("var setupRequired = settings.RequiresSetup;", source, StringComparison.Ordinal);
        Assert.Contains("ShutdownMode = ShutdownMode.OnExplicitShutdown;", source, StringComparison.Ordinal);
        Assert.Contains("StringComparison.OrdinalIgnoreCase)) && !setupRequired", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WizardCompletionUsesTheMarkerAwarePersistencePath()
    {
        var source = Source("AppController.cs");
        Assert.Contains("settingsService.SaveCompletedSetup", source, StringComparison.Ordinal);
        var start = source.IndexOf("public void CompleteSetup(", StringComparison.Ordinal);
        var end = source.IndexOf("public async Task CheckNativeCompatibilityUpdatesAsync", start, StringComparison.Ordinal);
        var completion = source[start..end];

        Assert.Contains("_saveCompletedSetup", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("_saveSettings", completion, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondInstanceRestoresActiveWizardInsteadOfBypassingIt()
    {
        var source = Source("App.xaml.cs");
        Assert.True(
            source.IndexOf("_activationListener = ListenForActivationAsync", StringComparison.Ordinal) <
            source.IndexOf("_setupWindow.ShowDialog()", StringComparison.Ordinal));
        var activation = source[source.IndexOf("private void RestoreControlPanel()", StringComparison.Ordinal)..];

        Assert.Contains("_controller?.Settings.RequiresSetup == true && _setupWindow is null", activation, StringComparison.Ordinal);
        Assert.Contains("(_setupWindow ?? MainWindow)", activation, StringComparison.Ordinal);
        Assert.DoesNotContain("new MainWindow", activation, StringComparison.Ordinal);
    }

    [Fact]
    public void TelemetryCannotAutoCompleteAndSetupSharesOnlyTheControllerReceiver()
    {
        var controller = Source("AppController.cs");
        Assert.DoesNotContain("Settings.HasCompletedSetup = true", controller, StringComparison.Ordinal);
        Assert.Contains("new SetupTelemetrySource(_receiver)", controller, StringComparison.Ordinal);
        var uiUpdate = controller[
            controller.IndexOf("private void ProcessUiUpdate(", StringComparison.Ordinal)..];
        Assert.True(uiUpdate.IndexOf("if (Settings.RequiresSetup)", StringComparison.Ordinal) <
                    uiUpdate.IndexOf("_receiver.Latest", StringComparison.Ordinal));
        Assert.DoesNotContain("new TelemetryUdpReceiver", Source("SetupTelemetryTest.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("NativeHudProcess", Source("SetupTelemetryTest.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void WizardUsesExistingChromeAndScrollableStepsWithExplicitConfirmations()
    {
        var document = XDocument.Parse(Source("SetupWindow.xaml"));
        var elements = document.Descendants().ToArray();
        Assert.Contains(elements, element => element.Name.LocalName == "WindowChrome");
        Assert.Equal(3, elements.Count(element =>
            element.Name.LocalName == "Button" &&
            element.Attribute("Style")?.Value == "{StaticResource WindowButtonStyle}"));
        var scroll = Assert.Single(elements, element => element.Name.LocalName == "ScrollViewer");
        Assert.Equal("Auto", scroll.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Equal("Disabled", scroll.Attribute("HorizontalScrollBarVisibility")?.Value);
        var confirmations = elements.Where(element => element.Name.LocalName == "CheckBox").ToArray();
        Assert.Equal(3, confirmations.Length);
        Assert.All(confirmations, checkbox => Assert.Null(checkbox.Attribute("IsChecked")));
        Assert.Contains("your confirmations", Source("SetupWindow.xaml"), StringComparison.Ordinal);
        Assert.Contains("Sample preview", Source("SetupWindow.xaml"), StringComparison.Ordinal);
        Assert.Contains("not live FH6 data", Source("SetupWindow.xaml"), StringComparison.Ordinal);
        Assert.Contains("ControlWindowGeometry.FitToPhysicalWorkArea", Source("SetupWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("MonitorFromWindow", Source("SetupWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("LocationChanged +=", Source("SetupWindow.xaml.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitNativeResetRestoresReferenceSizeBeforeResettingPosition()
    {
        var source = Source("AppController.cs");
        var start = source.IndexOf("public void ResetOverlayPosition()", StringComparison.Ordinal);
        var end = source.IndexOf("private void ResetGForcePosition()", start, StringComparison.Ordinal);
        var reset = source[start..end];

        Assert.Contains("Settings.LayoutMode == HudLayoutMode.Native", reset, StringComparison.Ordinal);
        Assert.Contains("Settings.OverlayWidthScale = scale;", reset, StringComparison.Ordinal);
        Assert.Contains("Settings.OverlayHeightScale = scale;", reset, StringComparison.Ordinal);
        Assert.Contains("ViewModel.OverlayWidthScale = scale;", reset, StringComparison.Ordinal);
        Assert.Contains("ViewModel.OverlayHeightScale = scale;", reset, StringComparison.Ordinal);
        Assert.True(reset.IndexOf("Overlay.CurrentNativeReferenceScale()", StringComparison.Ordinal) <
                    reset.IndexOf("Overlay?.ResetPosition()", StringComparison.Ordinal));
        Assert.True(reset.IndexOf("Overlay.ApplyLayout(", StringComparison.Ordinal) <
                    reset.IndexOf("Overlay?.ResetPosition()", StringComparison.Ordinal));
    }

    private static string Source(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "Wisp.App", fileName));
    }

    private static readonly DateTimeOffset CompletedAt =
        new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static AppSettings CompletedSettings() => new()
    {
        SettingsRevision = 7,
        UdpPort = 6500,
        LayoutMode = HudLayoutMode.Native,
        NativeGaugeMode = NativeGaugeMode.Analogue,
        OverlayOpacity = 0.72,
        HasCompletedSetup = true,
        SetupCompletion = new SetupCompletionRecord
        {
            Version = SetupCompletionRecord.CurrentVersion,
            CompletedAtUtc = CompletedAt.AddDays(-1),
            ValidatedUdpPort = 6500,
            ValidatedPackets = SetupCompletionRecord.MinimumPackets,
            MovingPackets = SetupCompletionRecord.MinimumMovingPackets,
            ValidatedElapsedMilliseconds = SetupCompletionRecord.MinimumElapsedMilliseconds,
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        },
        Calibrations =
        [
            new CalibrationSnapshot(
                712,
                0.36,
                CalibrationOptions.DefaultMinimumSamples,
                DrivetrainType.RearWheelDrive,
                RollingRadiusEstimator.CurrentCalibrationRevision)
        ]
    };

    private static SetupPreferences SetupChoices() => new(
        5500,
        SpeedUnit.KilometersPerHour,
        SpeedSourceMode.Fh6VehicleSpeed,
        HudLayoutMode.Combined,
        NativeGaugeMode.Digital,
        GearDisplayMode.Manual,
        true,
        true,
        true);

    private static SetupTelemetryEvidence SetupEvidence() => new(
        5500,
        SetupCompletionRecord.MinimumPackets,
        SetupCompletionRecord.MinimumMovingPackets,
        TimeSpan.FromMilliseconds(SetupCompletionRecord.MinimumElapsedMilliseconds),
        CompletedAt.AddSeconds(-5));
}
