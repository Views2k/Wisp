using System.IO;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Wisp.App;
using Wisp.Core;
using Wisp.Update;
using Xunit;

namespace Wisp.App.Tests;

public sealed class MaintenancePersistenceTests
{
    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("security")]
    public async Task FailedProfileSaveRetriesLatestSettingsWithoutAddingAnotherProfile(string failure)
    {
        var settings = CompletedSettings();
        var failSave = true;
        var saveAttempts = 0;
        string? persisted = null;
        await using var controller = new AppController(settings, value =>
        {
            saveAttempts++;
            if (failSave)
            {
                throw SaveFailure(failure);
            }
            persisted = JsonSerializer.Serialize(value);
        }, new NoStartupRegistration());

        Assert.True(controller.TryCreateHudPreset("Night driving", out var created, out var error), error);
        Assert.NotNull(created);
        Assert.Equal(0, saveAttempts);
        Assert.False(controller.TrySavePendingSettings());
        Assert.Equal(1, saveAttempts);
        Assert.Null(persisted);
        Assert.Same(created, Assert.Single(settings.HudPresets));

        settings.OverlayOpacity = 0.62;
        settings.SpeedUnit = SpeedUnit.KilometersPerHour;
        failSave = false;

        Assert.True(controller.TrySavePendingSettings());
        Assert.Equal(2, saveAttempts);
        var restored = JsonSerializer.Deserialize<AppSettings>(Assert.IsType<string>(persisted))!;
        Assert.Equal(created.Id, Assert.Single(restored.HudPresets).Id);
        Assert.Equal("Night driving", restored.HudPresets[0].Name);
        Assert.Equal(0.62, restored.OverlayOpacity);
        Assert.Equal(SpeedUnit.KilometersPerHour, restored.SpeedUnit);
        Assert.Same(created, Assert.Single(settings.HudPresets));
    }

    [Fact]
    public async Task IncompleteSetupNeverReportsSettingsSavedOrInvokesPersistence()
    {
        var settings = new AppSettings
        {
            StartWithWindows = false,
            AutomaticApplicationUpdateChecks = false
        };
        var saveAttempts = 0;
        await using var controller = new AppController(
            settings, _ => saveAttempts++, new NoStartupRegistration());

        Assert.True(settings.RequiresSetup);
        Assert.False(controller.TrySavePendingSettings());
        Assert.Equal(0, saveAttempts);

        await controller.DisposeAsync();

        Assert.False(controller.TrySavePendingSettings());
        Assert.Equal(0, saveAttempts);
    }

    [Fact]
    public async Task ShutdownRetriesFailedSaveAndDisposedControllerRejectsFurtherAttempts()
    {
        var saveAttempts = 0;
        await using var controller = new AppController(CompletedSettings(), _ =>
        {
            saveAttempts++;
            throw new IOException("Synthetic settings failure");
        }, new NoStartupRegistration());

        Assert.True(controller.TryCreateHudPreset("Night driving", out _, out var error), error);
        Assert.False(controller.TrySavePendingSettings());
        Assert.Equal(1, saveAttempts);

        await controller.DisposeAsync();

        Assert.Equal(2, saveAttempts);
        Assert.False(controller.TrySavePendingSettings());
        Assert.Equal(2, saveAttempts);
    }

    [Theory]
    [InlineData("1.0.13", "Maintenance fixes retained after staging.")]
    [InlineData("1.0.14", "")]
    [InlineData(null, "")]
    public async Task StagedInstallerShowsOnlyItsMatchingReleaseSummary(
        string? retainedVersion,
        string expectedSummary)
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(), "Wisp.App.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var stagedPath = Path.Combine(temporaryDirectory, "staged-installer.fixture");
            File.WriteAllBytes(stagedPath, [0]);
            var pending = (VerifiedInstaller)Activator.CreateInstance(
                typeof(VerifiedInstaller),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [stagedPath, new SemanticVersion(1, 0, 13), 1L, new string('A', 64)],
                culture: null)!;
            await using var controller = new AppController(
                CompletedSettings(), _ => { }, new NoStartupRegistration());
            SetPendingField(controller, "_pendingInstaller", pending);
            SetPendingField(
                controller,
                "_pendingApplicationUpdateDetails",
                retainedVersion is null
                    ? null
                    : new ApplicationUpdateDetails(
                        retainedVersion, "Maintenance fixes retained after staging."));

            var details = await controller.GetAvailableApplicationUpdateDetailsAsync();

            Assert.NotNull(details);
            Assert.Equal("1.0.13", details.Version);
            Assert.Equal(expectedSummary, details.ReleaseSummary);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    internal static void AssertProfileSaveRetryOnCurrentDispatcher()
    {
        var settings = CompletedSettings();
        var failSave = true;
        var saveAttempts = 0;
        string? persisted = null;
        var controller = new AppController(settings, value =>
        {
            saveAttempts++;
            if (failSave)
            {
                throw new IOException("Synthetic settings failure");
            }
            persisted = JsonSerializer.Serialize(value);
        }, new NoStartupRegistration());
        MainWindow? window = null;
        try
        {
            window = new MainWindow(controller);
            var modeType = typeof(MainWindow).GetNestedType(
                "HudProfileDialogMode", BindingFlags.NonPublic);
            var showDialog = typeof(MainWindow).GetMethod(
                "ShowHudProfileDialog", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(modeType);
            Assert.NotNull(showDialog);
            showDialog.Invoke(window, [Enum.Parse(modeType, "Create"), null]);
            var nameInput = Assert.IsType<TextBox>(window.FindName("HudProfileNameInput"));
            var confirm = Assert.IsType<Button>(window.FindName("ConfirmHudProfileButton"));
            var dialog = Assert.IsType<Grid>(window.FindName("HudProfileDialog"));
            var error = Assert.IsType<TextBlock>(window.FindName("HudProfileDialogError"));
            var status = Assert.IsType<TextBlock>(window.FindName("HudProfileStatusText"));
            nameInput.Text = "Night driving";

            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var created = Assert.Single(settings.HudPresets);
            Assert.Equal(1, saveAttempts);
            Assert.Null(persisted);
            Assert.Equal(Visibility.Visible, dialog.Visibility);
            Assert.Equal(Visibility.Visible, error.Visibility);
            Assert.Contains("could not write", error.Text, StringComparison.Ordinal);
            Assert.Equal("Try again", confirm.Content);
            Assert.Equal("The last profile save attempt failed.", status.Text);
            Assert.False(nameInput.IsEnabled);

            failSave = false;
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(2, saveAttempts);
            Assert.Same(created, Assert.Single(settings.HudPresets));
            var restored = JsonSerializer.Deserialize<AppSettings>(Assert.IsType<string>(persisted))!;
            Assert.Equal(created.Id, Assert.Single(restored.HudPresets).Id);
            Assert.Equal(Visibility.Collapsed, dialog.Visibility);
            Assert.Empty(error.Text);
            Assert.Equal("Night driving saved.", status.Text);
        }
        finally
        {
            window?.Close();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static AppSettings CompletedSettings()
    {
        var settings = new AppSettings
        {
            StartWithWindows = false,
            AutomaticApplicationUpdateChecks = false
        };
        var now = DateTimeOffset.UtcNow;
        var preferences = SetupPreferences.FromSettings(settings) with
        {
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        };
        SetupCompletion.Save(
            settings,
            preferences,
            new SetupTelemetryEvidence(
                settings.UdpPort, 12, 12, TimeSpan.FromMilliseconds(550), now),
            _ => { },
            now);
        return settings;
    }

    private static Exception SaveFailure(string failure) => failure switch
    {
        "io" => new IOException("Synthetic settings failure"),
        "access" => new UnauthorizedAccessException("Synthetic settings failure"),
        "security" => new SecurityException("Synthetic settings failure"),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    private static void SetPendingField(AppController controller, string name, object? value)
    {
        var field = typeof(AppController).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(controller, value);
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza)
        {
        }
    }
}
