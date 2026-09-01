using System.Security;
using System.Text.Json;
using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ForzaStartupTests
{
    [Fact]
    public void NewOptionsAreOptInAndMissingJsonKeepsAnimationEnabled()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;

        Assert.False(settings.StartWithForza);
        Assert.False(settings.StartMinimizedWithForza);
        Assert.True(settings.AnimatedBackground);
        Assert.True(settings.StartWithWindows);
        Assert.True(settings.RequiresSetup);
    }

    [Fact]
    public void OptionsRoundTripInMemoryWithoutChangingExistingPreferences()
    {
        var settings = new AppSettings
        {
            StartWithWindows = false,
            StartWithForza = true,
            StartMinimizedWithForza = true,
            AnimatedBackground = false,
            AutoMinimizeOnTelemetry = false
        };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        Assert.True(restored.StartWithForza);
        Assert.True(restored.StartMinimizedWithForza);
        Assert.False(restored.StartWithWindows);
        Assert.False(restored.AnimatedBackground);
        Assert.False(restored.AutoMinimizeOnTelemetry);
    }

    [Fact]
    public void ViewModelLoadsAndNotifiesTheThreeNewBindings()
    {
        var settings = new AppSettings
        {
            StartWithForza = true,
            StartMinimizedWithForza = true,
            AnimatedBackground = false
        };
        var model = new DiagnosticsViewModel(settings);
        var changes = new List<string?>();
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.True(model.StartWithForza);
        Assert.False(model.CanSetWindowsStartup);
        Assert.True(model.StartMinimizedWithForza);
        Assert.False(model.AnimatedBackground);

        model.StartWithForza = false;
        model.StartMinimizedWithForza = false;
        model.AnimatedBackground = true;
        model.AnimatedBackground = true;

        Assert.True(model.CanSetWindowsStartup);
        Assert.Equal(
            new[] { "StartWithForza", "CanSetWindowsStartup", "StartMinimizedWithForza", "AnimatedBackground" }, changes);
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(false, true, "\"C:\\Program Files\\Wisp\\Wisp.exe\" --wait-for-forza")]
    [InlineData(true, false, "\"C:\\Program Files\\Wisp\\Wisp.exe\" --background")]
    [InlineData(true, true, "\"C:\\Program Files\\Wisp\\Wisp.exe\" --wait-for-forza")]
    public void OneQuotedLoginCommandReconcilesBothStartupOptions(
        bool windows, bool forza, string? expected)
    {
        Assert.Equal(expected, StartupRegistrationService.BuildCommand(
            @"C:\Program Files\Wisp\Wisp.exe", windows, forza));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Wisp.exe")]
    [InlineData("C:\\Apps\\Wisp\".exe")]
    [InlineData("C:\\Apps\\Wisp\r.exe")]
    [InlineData("C:\\Apps\\Wisp\n.exe")]
    [InlineData("C:\\Apps\\Wisp\0.exe")]
    public void UnsafeStartupExecutableCannotBecomeACommand(string? path)
    {
        Assert.Throws<InvalidOperationException>(() =>
            StartupRegistrationService.BuildCommand(path, false, true));
        Assert.Null(StartupRegistrationService.BuildCommand(path, false, false));
    }

    [Theory]
    [InlineData(false, false, false, false, "Interactive")]
    [InlineData(false, false, false, true, "Interactive")]
    [InlineData(false, false, true, false, "Interactive")]
    [InlineData(false, false, true, true, "Interactive")]
    [InlineData(true, false, false, false, "DisabledCompanion")]
    [InlineData(true, true, false, false, "DisabledCompanion")]
    [InlineData(true, false, true, false, "Background")]
    [InlineData(true, false, false, true, "WaitForForza")]
    [InlineData(true, false, true, true, "WaitForForza")]
    [InlineData(false, true, false, true, "WaitForForza")]
    [InlineData(false, true, true, true, "WaitForForza")]
    [InlineData(false, true, true, false, "Background")]
    [InlineData(false, true, false, false, "DisabledCompanion")]
    public void ForzaModeWinsOnlyForAutomaticLaunchesAndPreservesWindowsFallback(
        bool background, bool companion, bool windows, bool forza, string expected)
    {
        Assert.Equal(expected, StartupLaunchPolicy.Evaluate(
            background, companion, false, windows, forza).ToString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RequiredSetupAlwaysWinsOverAutomaticStartup(bool background, bool companion)
    {
        Assert.Equal(StartupLaunchMode.Interactive, StartupLaunchPolicy.Evaluate(
            background, companion, true, true, true));
        Assert.Equal(StartupLaunchMode.Interactive, StartupLaunchPolicy.Evaluate(
            background, companion, true, false, false));
    }

    [Theory]
    [InlineData("--background", true)]
    [InlineData("--BACKGROUND", true)]
    [InlineData("--wait-for-forza", true)]
    [InlineData("--WAIT-FOR-FORZA", true)]
    [InlineData("--background-extra", false)]
    [InlineData("", false)]
    public void AutomaticSecondInstancesDoNotActivateAnExistingDashboard(string argument, bool expected)
    {
        Assert.Equal(expected, StartupLaunchPolicy.IsAutomaticInvocation([argument]));
        Assert.False(StartupLaunchPolicy.IsAutomaticInvocation([]));
    }

    [Fact]
    public void WindowAppearanceStartsOnceAndNeverRestartsAnAlreadyRunningWisp()
    {
        var latch = new ForzaStartupLatch();

        Assert.False(latch.Observe(false, runtimeActive: false));
        Assert.True(latch.Observe(true, runtimeActive: false));
        Assert.False(latch.Observe(true, runtimeActive: true));
        Assert.False(latch.Observe(true, runtimeActive: false));
        Assert.False(latch.Observe(false, runtimeActive: false));
        Assert.False(latch.Observe(false, runtimeActive: false));
        Assert.False(latch.Observe(true, runtimeActive: true));
        Assert.False(latch.Observe(true, runtimeActive: false));
    }

    [Fact]
    public void ClosingDashboardDoesNotImmediatelyReopenItForTheSameGame()
    {
        var latch = new ForzaStartupLatch();
        latch.SuppressCurrentGame();

        Assert.False(latch.Observe(true, runtimeActive: false));
        Assert.False(latch.Observe(null, runtimeActive: false));
        Assert.False(latch.Observe(false, runtimeActive: false));
        Assert.False(latch.Observe(true, runtimeActive: false));
        Assert.False(latch.Observe(false, runtimeActive: false));
        Assert.False(latch.Observe(false, runtimeActive: false));
        Assert.True(latch.Observe(true, runtimeActive: false));
    }

    [Fact]
    public void IncompleteWindowChecksNeverCountAsAConfirmedGameExit()
    {
        var latch = new ForzaStartupLatch();
        Assert.True(latch.Observe(true, runtimeActive: false));

        for (var i = 0; i < 100; i++)
        {
            Assert.False(latch.Observe(null, runtimeActive: false));
        }
        Assert.False(latch.Observe(true, runtimeActive: false));
        latch.Reset();
        Assert.True(latch.Observe(true, runtimeActive: false));
    }

    [Fact]
    public void ClosingWithoutAGameDoesNotSuppressItsNextLaunch()
    {
        var latch = new ForzaStartupLatch();
        latch.SuppressCurrentGame(gameWindowPresent: false);
        Assert.True(latch.Observe(true, runtimeActive: false));
    }

    [Theory]
    [InlineData("Forza Horizon 6", true)]
    [InlineData(" forza horizon 6 ", true)]
    [InlineData("Forza Horizon 6 - Browser", false)]
    [InlineData("Forza Horizon 5", false)]
    [InlineData("Wisp", false)]
    [InlineData(null, false)]
    public void OnlyTheExactGameCaptionIsAnAutoStartHint(string? caption, bool expected)
    {
        Assert.Equal(expected, ForzaStartupWindowObserver.MatchesCaption(caption));
        Assert.Equal(TimeSpan.FromSeconds(2), ForzaStartupWindowObserver.PollInterval);
    }

    [Fact]
    public async Task ControllerPersistsOptionsUsingFakeRegistrationAndAnInMemorySave()
    {
        var settings = VerifiedSettings();
        var registration = new FakeRegistration();
        AppSettings? saved = null;
        await using (var controller = new AppController(
            settings,
            value => saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(value)),
            registration))
        {
            var notifications = 0;
            controller.StartupOptionsChanged += (_, _) => notifications++;
            controller.ViewModel.StartWithForza = true;
            controller.ViewModel.StartMinimizedWithForza = true;
            controller.ViewModel.AnimatedBackground = false;
            controller.ApplyViewOptions();
            controller.ApplyViewOptions();

            Assert.Equal(new[] { (false, true) }, registration.Calls);
            Assert.Equal(1, notifications);
            Assert.True(settings.StartWithForza);
            Assert.True(settings.StartMinimizedWithForza);
            Assert.False(settings.AnimatedBackground);
            Assert.True(settings.AutoMinimizeOnTelemetry);
        }

        Assert.NotNull(saved);
        Assert.True(saved.StartWithForza);
        Assert.True(saved.StartMinimizedWithForza);
        Assert.False(saved.AnimatedBackground);
    }

    [Fact]
    public async Task DisablingForzaPreservesAnEnabledWindowsLoginEntry()
    {
        var settings = VerifiedSettings();
        settings.StartWithWindows = true;
        settings.StartWithForza = true;
        settings.StartMinimizedWithForza = true;
        var registration = new FakeRegistration();
        await using var controller = new AppController(settings, _ => { }, registration);

        controller.ViewModel.StartWithForza = false;
        controller.ApplyViewOptions();

        Assert.Equal(new[] { (true, false) }, registration.Calls);
        Assert.True(settings.StartWithWindows);
        Assert.False(settings.StartWithForza);
        Assert.True(settings.StartMinimizedWithForza);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForzaTogglePreservesThePreviousWindowsPreference(bool windowsPreference)
    {
        var settings = VerifiedSettings();
        settings.StartWithWindows = windowsPreference;
        var registration = new FakeRegistration();
        await using var controller = new AppController(settings, _ => { }, registration);

        controller.ViewModel.StartWithForza = true;
        controller.ViewModel.StartMinimizedWithForza = true;
        controller.ApplyViewOptions();

        Assert.Equal(windowsPreference, settings.StartWithWindows);
        Assert.Equal(windowsPreference, controller.ViewModel.StartWithWindows);
        Assert.False(controller.ViewModel.CanSetWindowsStartup);
        Assert.EndsWith("--wait-for-forza", StartupRegistrationService.BuildCommand(
            @"C:\Apps\Wisp\Wisp.exe", settings.StartWithWindows, settings.StartWithForza)!);

        controller.ViewModel.StartWithForza = false;
        controller.ApplyViewOptions();

        Assert.Equal(windowsPreference, settings.StartWithWindows);
        Assert.True(controller.ViewModel.CanSetWindowsStartup);
        Assert.Equal(
            windowsPreference ? "\"C:\\Apps\\Wisp\\Wisp.exe\" --background" : null,
            StartupRegistrationService.BuildCommand(
                @"C:\Apps\Wisp\Wisp.exe", settings.StartWithWindows, settings.StartWithForza));
        Assert.Equal(new[] { (windowsPreference, true), (windowsPreference, false) }, registration.Calls);
    }

    [Fact]
    public async Task LastStartupOptOutRemovesTheSingleEntryAndMinimizedOnlyChangesDoNotWriteIt()
    {
        var settings = VerifiedSettings();
        settings.StartWithForza = true;
        var registration = new FakeRegistration();
        await using var controller = new AppController(settings, _ => { }, registration);

        controller.ViewModel.StartMinimizedWithForza = true;
        controller.ApplyViewOptions();
        Assert.Empty(registration.Calls);
        controller.ViewModel.StartWithForza = false;
        controller.ApplyViewOptions();

        Assert.Equal(new[] { (false, false) }, registration.Calls);
    }

    [Fact]
    public async Task RegistrationFailureRollsBackOnlyStartupOptions()
    {
        var settings = VerifiedSettings();
        var registration = new FakeRegistration { Failure = new SecurityException("Synthetic policy denial") };
        await using var controller = new AppController(settings, _ => { }, registration);
        var notifications = 0;
        controller.StartupOptionsChanged += (_, _) => notifications++;

        controller.ViewModel.StartWithForza = true;
        controller.ViewModel.StartMinimizedWithForza = true;
        controller.ViewModel.AnimatedBackground = false;
        controller.ApplyViewOptions();

        Assert.False(settings.StartWithForza);
        Assert.False(settings.StartMinimizedWithForza);
        Assert.False(controller.ViewModel.StartWithForza);
        Assert.False(controller.ViewModel.StartMinimizedWithForza);
        Assert.False(settings.AnimatedBackground);
        Assert.Equal(0, notifications);
        Assert.Contains("previous options were restored", controller.ViewModel.StatusDetail);
    }

    [Fact]
    public async Task StartupRegistrationRunsOnceAndNeverDuringIncompleteSetup()
    {
        var registration = new FakeRegistration();
        var incomplete = new AppSettings { StartWithWindows = true, StartWithForza = true };
        await using (var controller = new AppController(incomplete, _ => { }, registration))
        {
            Assert.False(controller.InitializeStartupRegistration());
            controller.ViewModel.StartWithForza = false;
            controller.ApplyViewOptions();
            Assert.Empty(registration.Calls);
            Assert.True(incomplete.StartWithForza);
        }

        var settings = VerifiedSettings();
        settings.StartWithForza = true;
        await using (var controller = new AppController(settings, _ => { }, registration))
        {
            Assert.True(controller.InitializeStartupRegistration());
            Assert.True(controller.InitializeStartupRegistration());
            Assert.Equal(new[] { (false, true) }, registration.Calls);
        }
    }

    [Fact]
    public async Task DisabledStartupStillReconcilesItsOwnedLoginEntryExactlyOnce()
    {
        var settings = VerifiedSettings();
        var registration = new FakeRegistration();
        var saves = 0;
        await using var controller = new AppController(settings, _ => saves++, registration);

        Assert.True(controller.InitializeStartupRegistration());
        Assert.True(controller.InitializeStartupRegistration());

        Assert.Equal(new[] { (false, false) }, registration.Calls);
        Assert.False(settings.StartWithWindows);
        Assert.False(settings.StartWithForza);
        Assert.Equal(0, saves);
        Assert.Null(controller.ControlPanel);
        Assert.Null(controller.Overlay);
    }

    [Fact]
    public async Task FailedLoginEntryRemovalStaysDisabledAndReportsRemovalNotEnableFailure()
    {
        var settings = VerifiedSettings();
        settings.StartMinimizedWithForza = true;
        var registration = new FakeRegistration
        {
            Failure = new UnauthorizedAccessException("Synthetic removal denial")
        };
        var saves = 0;
        var changes = 0;
        await using var controller = new AppController(settings, _ => saves++, registration);
        controller.StartupOptionsChanged += (_, _) => changes++;

        Assert.False(controller.InitializeStartupRegistration());
        Assert.False(controller.InitializeStartupRegistration());

        Assert.Equal(new[] { (false, false) }, registration.Calls);
        Assert.False(settings.StartWithWindows);
        Assert.False(settings.StartWithForza);
        Assert.False(controller.ViewModel.StartWithWindows);
        Assert.False(controller.ViewModel.StartWithForza);
        Assert.True(settings.StartMinimizedWithForza);
        Assert.Equal(0, saves);
        Assert.Equal(0, changes);
        Assert.Contains("could not remove Wisp's sign-in entry", controller.ViewModel.StatusDetail);
        Assert.Contains("automatic startup remains disabled", controller.ViewModel.StatusDetail);
        Assert.DoesNotContain("could not enable", controller.ViewModel.StatusDetail);
        Assert.Null(controller.ControlPanel);
        Assert.Null(controller.Overlay);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedAutomaticRegistrationCannotFallThroughToAnEnabledLaunchMode(
        bool windows, bool forza)
    {
        var settings = VerifiedSettings();
        settings.StartWithWindows = windows;
        settings.StartWithForza = forza;
        var registration = new FakeRegistration
        {
            Failure = new SecurityException("Synthetic registration denial")
        };
        var saves = 0;
        await using var controller = new AppController(settings, _ => saves++, registration);

        Assert.False(controller.InitializeStartupRegistration());
        Assert.Equal(new[] { (windows, forza) }, registration.Calls);
        Assert.Equal(1, saves);
        Assert.False(settings.StartWithWindows);
        Assert.False(settings.StartWithForza);
        Assert.Contains("could not enable automatic startup", controller.ViewModel.StatusDetail);
        Assert.Equal(StartupLaunchMode.DisabledCompanion, StartupLaunchPolicy.Evaluate(
            true, false, false, settings.StartWithWindows, settings.StartWithForza));
        Assert.Equal(StartupLaunchMode.DisabledCompanion, StartupLaunchPolicy.Evaluate(
            false, true, false, settings.StartWithWindows, settings.StartWithForza));
        Assert.Equal(StartupLaunchMode.Interactive, StartupLaunchPolicy.Evaluate(
            false, false, false, settings.StartWithWindows, settings.StartWithForza));
    }

    [Fact]
    public void DisabledAutomaticLaunchReconcilesAfterSetupBeforeExitingWithoutCreatingRuntime()
    {
        var app = Source("App.xaml.cs");
        var setup = app.IndexOf("if (!completed || settings.RequiresSetup)", StringComparison.Ordinal);
        var reconcile = app.IndexOf("var startupRegistrationReady = _controller.InitializeStartupRegistration();",
            StringComparison.Ordinal);
        var evaluate = app.IndexOf("var launchMode = StartupLaunchPolicy.Evaluate(", StringComparison.Ordinal);
        var disabled = app.IndexOf("if (launchMode == StartupLaunchMode.DisabledCompanion)", StringComparison.Ordinal);
        var continueStartup = app.IndexOf("_controller.StartupOptionsChanged +=", StringComparison.Ordinal);

        Assert.True(setup >= 0 && reconcile > setup && evaluate > reconcile);
        Assert.True(disabled > evaluate && continueStartup > disabled);
        var exit = app[disabled..continueStartup];
        Assert.Contains("if (!startupRegistrationReady)", exit);
        Assert.Contains("_controller.ViewModel.StatusDetail", exit);
        Assert.Contains("Shutdown();", exit);
        Assert.Contains("return;", exit);
        Assert.DoesNotContain("StartRuntimeAsync", exit);
        Assert.DoesNotContain("EnsureRuntimeWindows", exit);
        Assert.DoesNotContain("ConfigureStartupCompanion", exit);
    }

    [Fact]
    public async Task IdleCompanionCannotRestartUdpThroughTheDashboardPortAction()
    {
        var settings = VerifiedSettings();
        settings.StartWithForza = true;
        var registration = new FakeRegistration();
        await using var controller = new AppController(settings, _ => { }, registration);

        await controller.SuspendForForzaAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RestartListenerAsync(5500));
        Assert.Empty(registration.Calls);
        Assert.Null(controller.Overlay);
        Assert.Null(controller.GForceOverlay);
        Assert.False(controller.ViewModel.HasLiveTelemetry);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DrivingAutoMinimizeRemainsASeparateExplicitPreference(bool autoMinimize, bool expected)
    {
        var source = Source("App.xaml.cs");
        var timerStart = source.IndexOf("private void OnForzaStartupTimer(", StringComparison.Ordinal);
        var timerEnd = source.IndexOf("private async Task StartRuntimeAsync(", timerStart, StringComparison.Ordinal);
        var start = source[timerStart..timerEnd];
        Assert.Contains("minimize: settings.StartMinimizedWithForza", start, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.AutoMinimizeOnTelemetry", start, StringComparison.Ordinal);

        var transition = DrivingTransitionPolicy.Evaluate(false, DrivingTelemetrySignal.Driving, autoMinimize);
        Assert.Equal(expected, transition.ShouldMinimizeControlPanel);
        Assert.False(DrivingTransitionPolicy.Evaluate(
            true, DrivingTelemetrySignal.Driving, autoMinimize).ShouldMinimizeControlPanel);
    }

    [Fact]
    public void CompanionWaitAndCloseReleaseTelemetryAndKeepSingleInstanceActivation()
    {
        var app = Source("App.xaml.cs");
        var controller = Source("AppController.cs");
        Assert.Contains("StartupLaunchMode.WaitForForza && _startupTray is { IsAvailable: true }", app);
        Assert.Contains("await SuspendRuntimeAsync();\n            return;", app.Replace("\r\n", "\n"));
        Assert.Contains("_forzaStartupLatch.SuppressCurrentGame(_forzaWindowObserver.IsGameWindowPresent());", app);
        Assert.Contains("if (!StartupLaunchPolicy.IsAutomaticInvocation(e.Args))", app);
        Assert.Contains("_controller?.Settings.RequiresSetup == true && _setupWindow is null", app);
        var suspend = controller[
            controller.IndexOf("internal async Task SuspendForForzaAsync()", StringComparison.Ordinal)..controller.IndexOf("public void CompleteSetup(", StringComparison.Ordinal)];
        Assert.Contains("_runtimeSuspended = true;", suspend);
        Assert.Contains("_uiTimer.Stop();", suspend);
        Assert.Contains("_receiver.PacketAvailable -= OnPacketAvailable;", suspend);
        Assert.Contains("ResetControllerSession();", suspend);
        Assert.Contains("await _receiver.StopAsync()", suspend);
        Assert.Contains("if (_runtimeSuspended)", controller);
    }

    [Fact]
    public void RuntimeIsMarkedActiveOnlyAfterTheListenerStarts()
    {
        var source = Source("App.xaml.cs");
        var methodStart = source.IndexOf("private async Task StartRuntimeAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private void OnControlPanelClosing(", methodStart, StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];
        var listenerStart = method.IndexOf("await _controller.StartAsync();", StringComparison.Ordinal);
        var active = method.IndexOf("_runtimeActive = true;", StringComparison.Ordinal);
        var catchBlock = method.IndexOf("catch (Exception exception)", StringComparison.Ordinal);

        Assert.True(listenerStart >= 0 && active > listenerStart && catchBlock > active);
        Assert.Contains("_runtimeActive = false;", method[catchBlock..], StringComparison.Ordinal);
        Assert.Contains("_startupTray?.SetWaiting(true);", method[catchBlock..], StringComparison.Ordinal);
    }

    [Fact]
    public void WindowHintDoesNotUseGameHandlesProcessesOrSystemHooks()
    {
        var source = Source("ForzaStartupService.cs");
        Assert.Contains("EnumWindows(", source);
        Assert.Contains("GetWindowText(", source);
        foreach (var forbidden in new[]
        {
            "System.Diagnostics", "Process.Get", "OpenProcess", "ReadProcessMemory",
            "ManagementEventWatcher", "SetWindowsHookEx", "SetWinEventHook", "Process.Start"
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CompanionWindowPollingIsGatedByVerifiedSetup()
    {
        var app = Source("App.xaml.cs");
        var completionGate = app.IndexOf("if (!completed || settings.RequiresSetup)", StringComparison.Ordinal);
        Assert.True(completionGate >= 0);
        Assert.True(app.IndexOf("ConfigureStartupCompanion();", StringComparison.Ordinal) > completionGate);

        var configureStart = app.IndexOf("private void ConfigureStartupCompanion()", StringComparison.Ordinal);
        var pollStart = app.IndexOf("private void OnForzaStartupTimer(", configureStart, StringComparison.Ordinal);
        var activateStart = app.IndexOf("private async Task StartRuntimeAsync(", pollStart, StringComparison.Ordinal);
        var configure = app[configureStart..pollStart];
        var poll = app[pollStart..activateStart];
        const string verifiedGate = "RequiresSetup: false, StartWithForza: true";
        Assert.Contains(verifiedGate, configure);
        Assert.True(configure.IndexOf(verifiedGate, StringComparison.Ordinal) <
                    configure.IndexOf("_forzaStartupTimer.Start();", StringComparison.Ordinal));
        Assert.Contains(verifiedGate, poll);
        Assert.True(poll.IndexOf(verifiedGate, StringComparison.Ordinal) <
                    poll.IndexOf("_forzaWindowObserver.IsGameWindowPresent()", StringComparison.Ordinal));
    }

    private static AppSettings VerifiedSettings()
    {
        var settings = new AppSettings { StartWithWindows = false };
        var now = DateTimeOffset.UtcNow;
        SetupCompletion.Save(settings, SetupPreferences.FromSettings(settings) with
        {
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        }, new SetupTelemetryEvidence(settings.UdpPort, 12, 12, TimeSpan.FromMilliseconds(550), now),
            _ => { }, now);
        return settings;
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

    private sealed class FakeRegistration : IStartupRegistrationService
    {
        internal List<(bool Windows, bool Forza)> Calls { get; } = new();
        internal Exception? Failure { get; init; }

        public void Apply(bool startWithWindows, bool startWithForza)
        {
            Calls.Add((startWithWindows, startWithForza));
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }
}
