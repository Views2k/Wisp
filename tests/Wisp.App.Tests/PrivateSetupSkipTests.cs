using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;

namespace Wisp.App.Tests;

public sealed class PrivateSetupSkipTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.PrivateSetupSkipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        AppController? controller = null;
        SetupWindow? window = null;
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var marker = Path.Combine(directory, SettingsService.SetupRequiredMarkerFileName);
            const string markerContent = "private setup UI check";
            File.WriteAllText(marker, markerContent);
            var service = new SettingsService(settingsPath);
            var settings = service.Load();
            controller = new AppController(settings, service.Save, new NoStartupRegistration(), service.SaveCompletedSetup,
                runsDirectory: Path.Combine(directory, "Runs"),
                shiftCalibrationDirectory: Path.Combine(directory, "ShiftCalibrations"),
                clipLibraryDirectory: Path.Combine(directory, "Clips"),
                tuneLibraryDirectory: Path.Combine(directory, "Tunes"));
            window = new SetupWindow(controller) { ShowActivated = false, ShowInTaskbar = false };
            var button = Assert.IsType<Button>(window.FindName("PrivateSkipButton"));
            var privateBuild = PrivateSetupPolicy.IsAvailable;
            Assert.True(settings.RequiresSetup);
            Assert.Equal(privateBuild ? Visibility.Visible : Visibility.Collapsed, button.Visibility);

            if (privateBuild)
            {
                Assert.True(ClickSkipInDialog(window, button));
            }
            else
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            Assert.Equal(!privateBuild, settings.RequiresSetup);
            Assert.Equal(privateBuild, settings.PrivateSetupSkippedForSession);
            Assert.False(settings.HasCompletedSetup);
            Assert.Null(settings.SetupCompletion);
            Assert.Null(controller.SetupTelemetry.SuccessfulEvidence);
            Assert.False(controller.SetupTelemetry.IsRunning);
            Assert.True(settings.InstallerSetupRequired);
            Assert.False(File.Exists(settingsPath));
            Assert.Equal(markerContent, File.ReadAllText(marker));
        }
        finally
        {
            try
            {
                window?.Close();
                controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static bool? ClickSkipInDialog(SetupWindow window, Button button)
    {
        Exception? failure = null;
        var timeout = new DispatcherTimer(DispatcherPriority.Send, window.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        void Loaded(object sender, RoutedEventArgs args)
        {
            try
            {
                Assert.True(button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception exception)
            {
                failure = exception;
                window.Close();
            }
        }
        void TimedOut(object? sender, EventArgs args)
        {
            failure ??= new TimeoutException("The private setup skip dialog did not close.");
            timeout.Stop();
            window.Close();
        }
        window.Loaded += Loaded;
        timeout.Tick += TimedOut;
        try
        {
            timeout.Start();
            var result = window.ShowDialog();
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }
        finally
        {
            timeout.Stop();
            timeout.Tick -= TimedOut;
            window.Loaded -= Loaded;
        }
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("lap-review-3d-private-test", true)]
    public void OnlyExplicitDiagnosticIdentityAllowsASessionSkip(string? diagnosticBuildId, bool allowed)
    {
        Assert.Equal(allowed, PrivateSetupPolicy.CanSkip(diagnosticBuildId));
        Assert.Equal(!allowed, PrivateSetupPolicy.RequiresSetup(true, true, diagnosticBuildId));
        Assert.True(PrivateSetupPolicy.RequiresSetup(true, false, diagnosticBuildId));
        Assert.False(PrivateSetupPolicy.RequiresSetup(false, false, diagnosticBuildId));
    }

    [Fact]
    public void RuntimeSkipUsesAssemblyIdentityAndNeverCreatesCompletionEvidence()
    {
        var settings = new AppSettings();
        settings.MigrateSettings();
        var before = JsonSerializer.Serialize(settings);
        var allowed = !string.IsNullOrWhiteSpace(ApplicationVersionInfo.DiagnosticBuildId);

        Assert.Equal(allowed, settings.TrySkipSetupForPrivateSession());
        Assert.Equal(allowed, settings.PrivateSetupSkippedForSession);
        Assert.Equal(!allowed, settings.RequiresSetup);
        settings.MigrateSettings();

        Assert.False(settings.HasCompletedSetup);
        Assert.Null(settings.SetupCompletion);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        var restored = JsonSerializer.Deserialize<AppSettings>(before)!;
        restored.MigrateSettings();
        Assert.True(restored.RequiresSetup);
        Assert.False(restored.PrivateSetupSkippedForSession);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstallerMarkerAndSettingsRemainUnverifiedAfterASessionSkip(bool previouslyCompleted)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.PrivateSetupSkipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var service = new SettingsService(Path.Combine(directory, "settings.json"));
            var marker = Path.Combine(directory, SettingsService.SetupRequiredMarkerFileName);
            var completion = previouslyCompleted ? new SetupCompletionRecord
            {
                Version = SetupCompletionRecord.CurrentVersion,
                CompletedAtUtc = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
                ValidatedUdpPort = 5500,
                ValidatedPackets = 12,
                MovingPackets = 3,
                ValidatedElapsedMilliseconds = 500,
                DataOutConfirmed = true,
                DisplayModeConfirmed = true,
                StockHudConfirmed = true
            } : null;
            service.Save(new AppSettings { SetupCompletion = completion, HasCompletedSetup = previouslyCompleted });
            File.WriteAllText(marker, "setup required");
            var settings = service.Load();
            Assert.True(settings.InstallerSetupRequired);
            settings.TrySkipSetupForPrivateSession();
            settings.MigrateSettings();
            service.Save(settings);

            Assert.True(File.Exists(marker));
            Assert.True(settings.InstallerSetupRequired);
            Assert.False(settings.HasCompletedSetup);
            Assert.Equal(completion, settings.SetupCompletion);
            Assert.Throws<InvalidOperationException>(() => service.SaveCompletedSetup(settings));
            var saved = File.ReadAllText(Path.Combine(directory, "settings.json"));
            Assert.DoesNotContain(nameof(AppSettings.PrivateSetupSkippedForSession), saved, StringComparison.OrdinalIgnoreCase);
            var restored = service.Load();
            Assert.True(restored.RequiresSetup);
            Assert.True(restored.InstallerSetupRequired);
            Assert.False(restored.PrivateSetupSkippedForSession);
            Assert.Equal(completion, restored.SetupCompletion);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SerializedSkipFlagCannotUnlockSetup()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"PrivateSetupSkippedForSession":true,"HasCompletedSetup":true}""")!;
        settings.MigrateSettings();
        Assert.True(settings.RequiresSetup);
        Assert.False(settings.PrivateSetupSkippedForSession);
        Assert.False(settings.HasCompletedSetup);
        Assert.Null(settings.SetupCompletion);
    }

    [Fact]
    public void PrivateButtonIsHiddenByDefaultAndDoesNotUseTheCompletionOrSavePaths()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var button = Assert.Single(XDocument.Parse(Source("SetupWindow.xaml")).Descendants(),
            element => element.Attribute(x + "Name")?.Value == "PrivateSkipButton");
        Assert.Equal("Skip setup", button.Attribute("Content")?.Value);
        Assert.Equal("Collapsed", button.Attribute("Visibility")?.Value);
        Assert.Equal("PrivateSkip_Click", button.Attribute("Click")?.Value);
        var code = Source("SetupWindow.xaml.cs");
        Assert.Contains("PrivateSkipButton.Visibility = PrivateSetupPolicy.IsAvailable ? Visibility.Visible : Visibility.Collapsed;", code);
        Assert.Contains("PrivateSkipButton.IsEnabled = !_testing && !_closing;", code);
        var start = code.IndexOf("private void PrivateSkip_Click(", StringComparison.Ordinal);
        var end = code.IndexOf("private HudLayoutMode SelectedLayout", start, StringComparison.Ordinal);
        var skip = code[start..end];
        Assert.Contains("_controller.SetupTelemetry.IsRunning", skip);
        Assert.Contains("_controller.Settings.TrySkipSetupForPrivateSession()", skip);
        Assert.Contains("DialogResult = true;", skip);
        Assert.DoesNotContain("CompleteSetup", skip);
        Assert.DoesNotContain("Save", skip);
    }

    private static string Source(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wisp.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "Wisp.App", name));
    }
}
