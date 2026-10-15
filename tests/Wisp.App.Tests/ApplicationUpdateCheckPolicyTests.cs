using System.Collections.Immutable;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wisp.Update;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ApplicationUpdateCheckPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmergencyPauseBlocksStagedUpdatesAndPreservesTheAutomaticPreference(bool automatic)
    {
        var settings = CompletedSettings();
        settings.AutomaticApplicationUpdateChecks = automatic;
        using var staged = new StagedInstallerFixture();
        var checks = 0;
        await using var controller = Controller(settings, (_, _) => { checks++; return Task.FromResult<UpdateRelease?>(null); });
        staged.Attach(controller);
        var permission = controller.ApplicationUpdateFeatureToken;
        Assert.True(controller.CanBeginApplicationUpdate(permission));
        await PauseUpdatesAsync(controller);
        controller.BeginStartupApplicationUpdateCheck();
        FireDailyTimer(controller);
        Assert.Equal(0, checks);
        Assert.False(Timer(controller).IsEnabled);
        Assert.False(controller.ViewModel.CanCheckApplicationUpdate);
        Assert.False(controller.ViewModel.IsApplicationUpdateAvailable);
        Assert.Equal("Update maintenance.", controller.ViewModel.ApplicationUpdateStatus);
        Assert.Null(await controller.GetAvailableApplicationUpdateDetailsAsync());
        Assert.Null(await controller.PrepareApplicationUpdateAsync("1.2.2"));
        Assert.True(permission.IsCancellationRequested);
        Assert.False(controller.CanBeginApplicationUpdate(permission));
        Assert.Equal(automatic, settings.AutomaticApplicationUpdateChecks);
        Assert.Equal(new byte[] { 0 }, File.ReadAllBytes(staged.Installer.StagedPath));

        await RestoreUpdatesAsync(controller);
        Assert.False(controller.CanBeginApplicationUpdate(permission));
        Assert.True(controller.CanBeginApplicationUpdate(controller.ApplicationUpdateFeatureToken));
        Assert.Equal(1, checks);
        Assert.True(Timer(controller).IsEnabled);
        Assert.Equal(automatic, settings.AutomaticApplicationUpdateChecks);
        Assert.Same(staged.Installer, await controller.PrepareApplicationUpdateAsync("1.2.2"));
        Assert.False(controller.SetupTelemetry.IsRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedCheckCancelsItsRequestAndCannotAcceptALateResponse(bool failure)
    {
        var response = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        var settings = CompletedSettings();
        settings.AutomaticApplicationUpdateChecks = false;
        var requests = 0;
        await using var controller = Controller(settings, (_, token) => { requestToken = token; return ++requests == 1 ? response.Task : Task.FromResult<UpdateRelease?>(null); });
        var check = controller.GetAvailableApplicationUpdateDetailsAsync();
        await PauseUpdatesAsync(controller);
        Assert.True(requestToken.IsCancellationRequested);
        if (failure) response.SetException(new HttpRequestException());
        else response.SetResult(Release());
        Assert.Null(await check);
        Assert.False(CheckActive(controller));
        Assert.Equal("Update maintenance.", controller.ViewModel.ApplicationUpdateStatus);
        await RestoreUpdatesAsync(controller);
        Assert.False(controller.ViewModel.IsApplicationUpdateAvailable);
        Assert.True(controller.ViewModel.CanCheckApplicationUpdate);
        Assert.True(Timer(controller).IsEnabled);
    }

    [Fact]
    public async Task RestoringDuringAnOldCheckRunsOneFreshCheckAfterTheOldResponseIsDiscarded()
    {
        var response = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        await using var controller = Controller(CompletedSettings(), (_, _) => ++checks == 1
            ? response.Task : Task.FromResult<UpdateRelease?>(null));
        controller.BeginStartupApplicationUpdateCheck();
        await PauseUpdatesAsync(controller);
        await RestoreUpdatesAsync(controller);
        Assert.Equal(1, checks);
        response.SetResult(Release());
        await WaitForCheckAsync(controller);
        Assert.Equal(2, checks);
        Assert.False(controller.ViewModel.IsApplicationUpdateAvailable);
        Assert.True(Timer(controller).IsEnabled);
    }

    private static Task PauseUpdatesAsync(AppController controller) => controller.ApplyFeatureControlsAsync(
        new(1, ImmutableDictionary<string, string>.Empty.Add("updates", "Update maintenance.")));
    private static Task RestoreUpdatesAsync(AppController controller) => controller.ApplyFeatureControlsAsync(
        new(2, ImmutableDictionary<string, string>.Empty));

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(24)]
    public async Task EveryOpenChecksDespiteRecentOrFuturePersistedTimestamp(int previousCheckHours)
    {
        var settings = CompletedSettings();
        settings.LastApplicationUpdateCheckUtc = DateTimeOffset.UtcNow.AddHours(previousCheckHours);
        var checks = 0;
        await using var controller = Controller(settings, (_, _) => { checks++; return Task.FromResult<UpdateRelease?>(null); });
        controller.BeginStartupApplicationUpdateCheck();
        Assert.Equal(1, checks);
        controller.BeginStartupApplicationUpdateCheck();
        Assert.Equal(2, checks);
        Assert.True(Timer(controller).IsEnabled);
        Assert.Equal(TimeSpan.FromMinutes(15), Timer(controller).Interval);
    }

    [Fact]
    public async Task DailyTimerSurvivesCompanionSuspensionWithoutStartingTelemetry()
    {
        var checks = 0;
        await using var controller = Controller(CompletedSettings(), (_, _) => { checks++; return Task.FromResult<UpdateRelease?>(null); });
        controller.BeginStartupApplicationUpdateCheck();
        await controller.SuspendForForzaAsync();
        Assert.True(Timer(controller).IsEnabled);
        FireDailyTimer(controller);
        Assert.Equal(2, checks);
        Assert.True(Timer(controller).IsEnabled);
        Assert.False(controller.SetupTelemetry.IsRunning);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LegacyOptOutAndIncompleteSetupStillCheckForRequiredUpdates(bool enabled, bool setupComplete)
    {
        var settings = setupComplete ? CompletedSettings() : new AppSettings();
        settings.AutomaticApplicationUpdateChecks = enabled;
        var checks = 0;
        await using var controller = Controller(settings, (_, _) => { checks++; return Task.FromResult<UpdateRelease?>(null); });
        controller.BeginStartupApplicationUpdateCheck();
        FireDailyTimer(controller);
        Assert.Equal(2, checks);
        Assert.True(Timer(controller).IsEnabled);
    }

    [Fact]
    public async Task LegacyPreferenceCannotStopChecksButDisposalDoes()
    {
        var checks = 0;
        await using var controller = Controller(CompletedSettings(), (_, _) => { checks++; return Task.FromResult<UpdateRelease?>(null); });
        controller.BeginStartupApplicationUpdateCheck();
        controller.ViewModel.AutomaticApplicationUpdateChecks = false;
        controller.ApplyViewOptions();
        FireDailyTimer(controller);
        Assert.Equal(2, checks);
        Assert.True(Timer(controller).IsEnabled);
        await controller.DisposeAsync();
        controller.BeginStartupApplicationUpdateCheck();
        FireDailyTimer(controller);
        Assert.Equal(2, checks);
        Assert.False(Timer(controller).IsEnabled);
    }

    [Fact]
    public async Task OverlappingOpensAndTimerRequestsDoNotDuplicateTheCheck()
    {
        var response = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        await using var controller = Controller(CompletedSettings(), (_, _) => { checks++; return response.Task; });
        controller.BeginStartupApplicationUpdateCheck();
        controller.BeginStartupApplicationUpdateCheck();
        FireDailyTimer(controller);
        Assert.Equal(1, checks);
        Assert.False(controller.ViewModel.CanCheckApplicationUpdate);
        response.SetResult(Release());
        await WaitForCheckAsync(controller);
        Assert.True(controller.ViewModel.IsApplicationUpdateAvailable);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("verification")]
    public async Task AutomaticRefreshFailureKeepsTheKnownRelease(string failure)
    {
        var checks = 0;
        await using var controller = Controller(CompletedSettings(), (_, _) => ++checks == 1
            ? Task.FromResult<UpdateRelease?>(Release()) : Task.FromException<UpdateRelease?>(Failure(failure)));
        controller.BeginStartupApplicationUpdateCheck();
        FireDailyTimer(controller);
        Assert.Equal(2, checks);
        Assert.True(controller.ViewModel.IsApplicationUpdateAvailable);
        Assert.True(controller.ViewModel.CanCheckApplicationUpdate);
        Assert.Equal("Update", controller.ViewModel.ApplicationUpdateAction);
        Assert.Equal("1.2.3", (await controller.GetAvailableApplicationUpdateDetailsAsync())!.Version);
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task AutomaticRefreshKeepsBannerWhilePendingAndClearsOnlyAfterVerifiedNoUpdate()
    {
        var response = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        await using var controller = Controller(CompletedSettings(), (_, _) => ++checks == 1
            ? Task.FromResult<UpdateRelease?>(Release()) : response.Task);
        controller.BeginStartupApplicationUpdateCheck();
        FireDailyTimer(controller);
        Assert.True(controller.ViewModel.IsApplicationUpdateAvailable);
        Assert.False(controller.ViewModel.CanCheckApplicationUpdate);
        response.SetResult(null);
        await WaitForCheckAsync(controller);
        Assert.False(controller.ViewModel.IsApplicationUpdateAvailable);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("verification")]
    [InlineData("newer")]
    [InlineData("current")]
    public async Task AutomaticRefreshPreservesStagedInstallerVersionActionAndConfirmation(string outcome)
    {
        using var staged = new StagedInstallerFixture();
        var response = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        await using var controller = Controller(CompletedSettings(), (_, _) => { checks++; return response.Task; });
        staged.Attach(controller);
        controller.BeginStartupApplicationUpdateCheck();
        Assert.True(controller.ViewModel.IsApplicationUpdateAvailable);
        Assert.Contains("1.2.2", controller.ViewModel.ApplicationUpdateStatus);
        Assert.Equal("Install update", controller.ViewModel.ApplicationUpdateAction);
        Assert.False(controller.ViewModel.CanCheckApplicationUpdate);
        if (outcome == "newer") response.SetResult(Release());
        else if (outcome == "current") response.SetResult(null);
        else response.SetException(Failure(outcome));
        await WaitForCheckAsync(controller);
        Assert.Equal(1, checks);
        Assert.True(controller.ViewModel.IsApplicationUpdateAvailable);
        if (outcome == "newer")
        {
            Assert.Equal("1.2.3", (await controller.GetAvailableApplicationUpdateDetailsAsync())!.Version);
            Assert.Null(await controller.PrepareApplicationUpdateAsync(confirmedVersion: "1.2.2"));
            return;
        }
        Assert.Contains("1.2.2", controller.ViewModel.ApplicationUpdateStatus);
        Assert.DoesNotContain("1.2.3", controller.ViewModel.ApplicationUpdateStatus);
        Assert.Equal("Install update", controller.ViewModel.ApplicationUpdateAction);
        var details = await controller.GetAvailableApplicationUpdateDetailsAsync();
        Assert.Equal("1.2.2", details!.Version);
        Assert.Equal("Staged release details.", details.ReleaseSummary);
        Assert.Same(staged.Installer, await controller.PrepareApplicationUpdateAsync());
        Assert.Equal(new byte[] { 0 }, File.ReadAllBytes(staged.Installer.StagedPath));
    }

    [Fact]
    public async Task MissingStagedInstallerDoesNotHideANewlyAvailableRelease()
    {
        using var staged = new StagedInstallerFixture();
        await using var controller = Controller(CompletedSettings(), (_, _) => Task.FromResult<UpdateRelease?>(Release()));
        staged.Attach(controller);
        File.Delete(staged.Installer.StagedPath);
        controller.BeginStartupApplicationUpdateCheck();
        Assert.Equal("Update", controller.ViewModel.ApplicationUpdateAction);
        Assert.Contains("1.2.3", controller.ViewModel.ApplicationUpdateStatus);
        Assert.Equal("1.2.3", (await controller.GetAvailableApplicationUpdateDetailsAsync())!.Version);
    }

    [Fact]
    public async Task ConfirmingAnUpdateNeverInstallsANewerReleaseFoundMeanwhile()
    {
        var found = Release();
        await using var controller = Controller(CompletedSettings(), (_, _) => Task.FromResult<UpdateRelease?>(found));
        var shown = await controller.GetAvailableApplicationUpdateDetailsAsync();
        Assert.Equal("1.2.3", shown!.Version);
        found = Release(new SemanticVersion(1, 2, 4));
        FireDailyTimer(controller);
        await WaitForCheckAsync(controller);
        Assert.Null(await controller.PrepareApplicationUpdateAsync(shown.Version));
        Assert.Contains("1.2.4", controller.ViewModel.ApplicationUpdateStatus);
        Assert.Equal("1.2.4", (await controller.GetAvailableApplicationUpdateDetailsAsync())!.Version);
    }

    [Fact]
    public async Task ManualChecksAlsoScheduleRequiredChecks()
    {
        var settings = CompletedSettings();
        settings.AutomaticApplicationUpdateChecks = false;
        await using var controller = Controller(settings, (_, _) => Task.FromResult<UpdateRelease?>(Release()));
        Assert.Equal("1.2.3", (await controller.GetAvailableApplicationUpdateDetailsAsync())!.Version);
        Assert.True(Timer(controller).IsEnabled);
    }

    internal static void AssertBannerOnCurrentDispatcher()
    {
        var checks = 0;
        var controller = Controller(CompletedSettings(), (_, _) => ++checks == 1
            ? Task.FromResult<UpdateRelease?>(Release()) : Task.FromException<UpdateRelease?>(new HttpRequestException()));
        var application = Application.Current;
        var shutdownMode = application.ShutdownMode;
        MainWindow? window = null;
        try
        {
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window = new MainWindow(controller);
            var surface = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            surface.Measure(new Size(720, 440));
            surface.Arrange(new Rect(0, 0, 720, 440));
            surface.UpdateLayout();
            var banner = Assert.IsAssignableFrom<Border>(window.FindName("DashboardUpdateBanner"));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Same(controller.ViewModel, banner.DataContext);
            Assert.Equal(Visibility.Collapsed, banner.Visibility);
            controller.BeginStartupApplicationUpdateCheck();
            Assert.Equal(1, checks);
            Assert.True(controller.ViewModel.IsApplicationUpdateAvailable);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Equal(Visibility.Visible, banner.Visibility);
            FireDailyTimer(controller);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Equal(Visibility.Visible, banner.Visibility);
            var action = Assert.Single(Assert.IsType<Grid>(banner.Child).Children.OfType<Button>(),
                button => button.Visibility == Visibility.Visible);
            Assert.Same(window.FindName("DashboardUpdateAction"), action);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Button>(window.FindName("DashboardBannerClose")).Visibility);
            Assert.Equal("Update", action.Content);
            Assert.True(action.IsEnabled);
            using var staged = new StagedInstallerFixture();
            staged.Attach(controller);
            FireDailyTimer(controller);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Equal(Visibility.Visible, banner.Visibility);
            Assert.Equal("Install update", action.Content);
            Assert.True(action.IsEnabled);
            Assert.Contains("1.2.2", controller.ViewModel.ApplicationUpdateStatus);
        }
        finally
        {
            window?.Close();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            application.ShutdownMode = shutdownMode;
        }
    }

    private static AppController Controller(AppSettings settings, Func<Version, CancellationToken, Task<UpdateRelease?>> check) =>
        new(settings, _ => { }, new NoStartupRegistration(), checkForApplicationUpdate: check);
    private static DispatcherTimer Timer(AppController controller) => (DispatcherTimer)typeof(AppController)
        .GetField("_applicationUpdateTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
    private static void FireDailyTimer(AppController controller) => typeof(AppController)
        .GetMethod("OnApplicationUpdateTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, [null, EventArgs.Empty]);
    private static async Task WaitForCheckAsync(AppController controller)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((!controller.ViewModel.CanCheckApplicationUpdate || CheckActive(controller)) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(controller.ViewModel.CanCheckApplicationUpdate);
        Assert.False(CheckActive(controller));
    }
    private static bool CheckActive(AppController controller) => (int)typeof(AppController)
        .GetField("_applicationUpdateOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)! != 0;
    private static UpdateRelease Release(SemanticVersion? version = null)
    {
        version ??= new SemanticVersion(1, 2, 3);
        return (UpdateRelease)Activator.CreateInstance(typeof(UpdateRelease),
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: [version, $"v{version}", $"Wisp-Setup-{version}.exe", 1L, new string('A', 64),
                new Uri($"https://github.com/Views2k/Wisp/releases/download/v{version}/Wisp-Setup-{version}.exe"), "Reviewed maintenance update."],
            culture: null)!;
    }
    private static Exception Failure(string kind) => kind switch
    {
        "http" => new HttpRequestException(),
        "timeout" => new OperationCanceledException(),
        _ => new UpdateSecurityException("Unverified fixture response.")
    };
    private static AppSettings CompletedSettings()
    {
        var settings = new AppSettings { StartWithWindows = false, StartWithForza = false };
        var now = DateTimeOffset.UtcNow;
        SetupCompletion.Save(settings, SetupPreferences.FromSettings(settings) with
        {
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        }, new SetupTelemetryEvidence(settings.UdpPort, 12, 12, TimeSpan.FromMilliseconds(550), now), _ => { }, now);
        return settings;
    }
    [Fact]
    public async Task RunsLayoutStatusReportsTheOutcomeOfTheSettingsWrite()
    {
        var failing = true;
        await using var controller = new AppController(CompletedSettings(),
            _ => { if (failing) throw new IOException("The settings file is locked."); },
            new NoStartupRegistration(), checkForApplicationUpdate: (_, _) => Task.FromResult<UpdateRelease?>(null));
        controller.Runs.SaveWorkspaceCommand.Execute(null);
        Assert.Equal("Saving layout…", controller.Runs.WorkspaceStatus);
        Assert.False(controller.TrySavePendingSettings());
        Assert.StartsWith("Layout not saved yet.", controller.Runs.WorkspaceStatus);
        failing = false;
        Assert.True(controller.TrySavePendingSettings());
        Assert.Equal("Layout saved.", controller.Runs.WorkspaceStatus);
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    private sealed class StagedInstallerFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", Guid.NewGuid().ToString("N"));
        public StagedInstallerFixture()
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "staged-installer.fixture");
            File.WriteAllBytes(path, [0]);
            Installer = (VerifiedInstaller)Activator.CreateInstance(typeof(VerifiedInstaller),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args: [path, new SemanticVersion(1, 2, 2), 1L, new string('A', 64)], culture: null)!;
        }
        public VerifiedInstaller Installer { get; }
        public void Attach(AppController controller)
        {
            typeof(AppController).GetField("_pendingInstaller", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, Installer);
            typeof(AppController).GetField("_pendingApplicationUpdateDetails", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(controller, new ApplicationUpdateDetails("1.2.2", "Staged release details."));
            typeof(AppController).GetField("_availableApplicationRelease", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, null);
            controller.MarkApplicationUpdateDeferred(Installer);
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
