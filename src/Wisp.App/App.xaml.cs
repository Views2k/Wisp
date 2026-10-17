using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Wisp.App.CrashDiagnostics;
using Wisp.Update;
using Wisp.App.Supplementary;

namespace Wisp.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\Wisp.SingleInstance";
    private const string ActivationEventName = @"Local\Wisp.ActivateControlPanel";

    private AppController? _controller;
    private SetupWindow? _setupWindow;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;
    private Task? _activationListener;
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly IForzaStartupWindowObserver _forzaWindowObserver = new ForzaStartupWindowObserver();
    private readonly ForzaStartupLatch _forzaStartupLatch = new();
    private DispatcherTimer? _forzaStartupTimer;
    private StartupTrayIcon? _startupTray;
    private OverlayHotkeyService? _overlayHotkey;
    private OverlayHotkeyService? _recordingHotkey;
    private OverlayHotkeyService? _markerHotkey;
    private OverlayHotkeyService? _clipToggleHotkey;
    private OverlayHotkeyService? _clipSaveHotkey;
    private bool _runtimeActive;
    private bool _applicationUpdateHandoffActive;
    private bool _closePreparationActive;
    private bool _exiting;
    private readonly CrashReportStore _crashReports;
    private readonly CrashExceptionMonitor _crashMonitor;
    private readonly CrashReport? _previousCrashReport;
    private readonly Guid _runId = Guid.NewGuid();
    private RunExitMarkerStore? _runExitMarker;
    private CancellationTokenSource? _previousRunCancellation;
    private int _fatalExit;

    public App()
    {
        _crashReports = CrashReportStore.Current;
        _crashMonitor = new CrashExceptionMonitor(_crashReports, runId: () => _runId);
        DispatcherUnhandledException += OnUnhandledUiException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledBackgroundException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _previousCrashReport = _crashReports.LatestPending();
    }

    private void OnUnhandledUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    { Interlocked.Exchange(ref _fatalExit, 1); _crashMonitor.UiException(e.Exception); }
    private void OnUnhandledBackgroundException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.IsTerminating) Interlocked.Exchange(ref _fatalExit, 1);
        _crashMonitor.BackgroundException(e.ExceptionObject, e.IsTerminating);
    }
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        _crashMonitor.UnobservedTaskException(e);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        FocusCues.Install();
        CursorRecovery.Install();

        _instanceMutex = new Mutex(initiallyOwned: false, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (!StartupLaunchPolicy.IsAutomaticInvocation(e.Args))
            {
                SignalExistingInstance();
            }
            Shutdown();
            return;
        }

        _runExitMarker = RunExitMarkerStore.BeginCurrent(_runId);
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _activationCancellation = new CancellationTokenSource();

        var settingsService = new SettingsService();
        var settings = settingsService.Load();
        _controller = new AppController(settings, settingsService);
        await StartFeatureControlsAsync();
        StartSupplementaryReporting();
        RecordSupplementarySignal("settings", "settings", settingsService.LoadOutcome, "load",
            incident: settingsService.LoadFailure is { } loadFailure ? Supplementary.SupplementaryIncidentSchema.Create(
                "settings", loadFailure.Reason, "settings-load", loadFailure.Code) : null);
        _controller.ViewModel.InitializeCrashReport(_previousCrashReport, _crashReports.AcknowledgeThrough);
        if (_runExitMarker?.Previous is { } previous)
        {
            _previousRunCancellation = new CancellationTokenSource();
            _ = RecoverPreviousRunAsync(previous, _runExitMarker.StartedAtUtc, _previousRunCancellation.Token);
        }
        _overlayHotkey = new OverlayHotkeyService();
        _overlayHotkey.Pressed += (_, _) =>
        {
            if (_controller is { ShortcutCaptureActive: false }) _controller.ToggleManualOverlayHidden();
        };
        _controller.SetOverlayHotkeyRegistration(_overlayHotkey.Apply);
        _recordingHotkey = new OverlayHotkeyService();
        _recordingHotkey.Pressed += async (_, _) =>
        {
            if (_controller is not null && _runtimeActive && !_controller.ShortcutCaptureActive)
            {
                await _controller.Runs.ToggleRecordingAsync();
            }
        };
        _controller.SetRecordingHotkeyRegistration(_recordingHotkey.Apply);
        _markerHotkey = new OverlayHotkeyService();
        _markerHotkey.Pressed += (_, _) =>
        {
            if (_controller is not null && _runtimeActive && !_controller.ShortcutCaptureActive)
            {
                _controller.Runs.MarkMoment();
            }
        };
        _controller.SetMarkerHotkeyRegistration(_markerHotkey.Apply);
        _clipToggleHotkey = new OverlayHotkeyService();
        _clipToggleHotkey.Pressed += async (_, _) =>
        {
            if (_controller is not null && _runtimeActive && !_controller.ShortcutCaptureActive)
                await _controller.Clips.ToggleFromShortcutAsync();
        };
        _clipSaveHotkey = new OverlayHotkeyService();
        _clipSaveHotkey.Pressed += async (_, _) =>
        {
            if (_controller is not null && _runtimeActive && !_controller.ShortcutCaptureActive)
                await _controller.Clips.SaveClipFromShortcutAsync();
        };
        _controller.SetClipHotkeyRegistrations(_clipToggleHotkey.Apply, _clipSaveHotkey.Apply);
        if (ApplicationUpdateLauncher.TryConsumeResult(out var updateResult))
        {
            ApplicationUpdateTelemetry.Record(updateResult.Outcome == "success" ? "started" : "failed", updateResult.TargetVersion,
                reason: updateResult.Outcome == "success" ? null : updateResult.Outcome == "unknown" ? "version-mismatch" : "installer", source: updateResult.SourceVersion);
            RecordSupplementarySignal("update", "update", updateResult.Outcome, "installed", incident: updateResult.Incident);
            _controller.ViewModel.UpdateApplicationUpdateStatus(
                updateResult.Status,
                updateResult.Action,
                canCheck: true);
        }
        _activationListener = ListenForActivationAsync(_activationCancellation.Token);

        _controller.EnableAutomaticApplicationUpdates(async installer =>
            (await TryBeginApplicationUpdateAsync(installer)).Outcome);
        _controller.BeginStartupApplicationUpdateCheck();

        // A first run or legacy auto-completed install always opens the wizard,
        // including --background launches. No dashboard or HUD exists yet.
        var setupRequired = settings.RequiresSetup;
        if (setupRequired)
        {
            RecordSupplementarySignal("setup", "setup", "attempt", "setup-start");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _setupWindow = new SetupWindow(_controller);
            MainWindow = _setupWindow;
            var completed = _setupWindow.ShowDialog() == true;
            RecordSupplementarySignal("setup", "setup", completed && !settings.RequiresSetup ? "success" : "cancelled", "setup-complete");
            _setupWindow = null;
            if (_exiting) return;
            if (!completed || settings.RequiresSetup)
            {
                Shutdown();
                return;
            }
        }

        var startInBackground = e.Args.Any(
            argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase)) && !setupRequired;
        var companionRequested = e.Args.Any(
            argument => argument.Equals(StartupLaunchPolicy.ForzaArgument, StringComparison.OrdinalIgnoreCase));
        var startupRegistrationReady = _controller.InitializeStartupRegistration();
        var launchMode = StartupLaunchPolicy.Evaluate(
            startInBackground, companionRequested, setupRequired, settings.StartWithWindows, settings.StartWithForza);
        if (launchMode == StartupLaunchMode.DisabledCompanion)
        {
            if (!startupRegistrationReady)
            {
                MessageBox.Show(
                    _controller.ViewModel.StatusDetail,
                    "Automatic startup unavailable",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }
        _controller.StartupOptionsChanged += OnStartupOptionsChanged;
        ConfigureStartupCompanion();
        if (launchMode == StartupLaunchMode.WaitForForza && _startupTray is { IsAvailable: true })
        {
            await SuspendRuntimeAsync();
            return;
        }

        // A failed tray creation must not leave an inaccessible hidden app.
        await StartRuntimeAsync(
            showControlPanel: launchMode != StartupLaunchMode.Background,
            minimize: false);
    }

    private void EnsureRuntimeWindows()
    {
        if (_controller is null || _controller.Settings.RequiresSetup || _controller.ControlPanel is not null)
        {
            return;
        }

        var settings = _controller.Settings;
        var overlay = new OverlayWindow(_controller);
        _controller.Overlay = overlay;
        _controller.RestoreOverlayPlacement();
        overlay.SetEditMode(!settings.OverlayLocked);

        var gForceOverlay = new GForceWindow(_controller);
        _controller.GForceOverlay = gForceOverlay;
        _controller.RestoreGForcePlacement();
        gForceOverlay.SetEnabled(_controller.IsStandaloneGForceWindowEnabled);
        gForceOverlay.SetEditMode(!settings.OverlayLocked);

        var boostGaugeOverlay = new BoostGaugeWindow(_controller);
        _controller.BoostGaugeOverlay = boostGaugeOverlay;
        boostGaugeOverlay.ApplyAppearance(settings.BoostGaugeScale, settings.OverlayOpacity);
        boostGaugeOverlay.SetEnabled(_controller.IsDetachedBoostGaugeEnabled);
        _controller.RestoreBoostGaugePlacement();
        boostGaugeOverlay.SetEditMode(!settings.OverlayLocked);

        var tireTemperatureGaugeOverlay = new TireTemperatureGaugeWindow(_controller);
        _controller.TireTemperatureGaugeOverlay = tireTemperatureGaugeOverlay;
        tireTemperatureGaugeOverlay.ApplyGaugeMode(settings.NativeGaugeMode);
        tireTemperatureGaugeOverlay.ApplyAppearance(
            settings.TireTemperatureGaugeScale,
            settings.OverlayOpacity);
        tireTemperatureGaugeOverlay.SetEnabled(_controller.IsDetachedTireTemperatureGaugeEnabled);
        _controller.RestoreTireTemperatureGaugePlacement();
        tireTemperatureGaugeOverlay.SetEditMode(!settings.OverlayLocked);

        _controller.InitializeDriftGaugeWindow();
        _controller.InitializeLapDeltaWindow();
        _controller.InitializePowerTorqueGaugeWindows();

        ControlPanelWindow mainWindow = settings.UseLegacyInterface
            ? new LegacyMainWindow(_controller)
            : new MainWindow(_controller);
        _controller.ControlPanel = mainWindow;
        MainWindow = mainWindow;
        mainWindow.Closing += OnControlPanelClosing;
    }

    private async Task RecoverPreviousRunAsync(RunExitMarker previous, DateTimeOffset startedAtUtc, CancellationToken cancellationToken)
    {
        var model = _controller!.ViewModel;
        var generation = model.CrashReportNoticeGeneration;
        try
        {
            var report = await new PreviousRunRecovery(_crashReports, new WindowsFaultEventReader())
                .RecoverAsync(previous, startedAtUtc, cancellationToken).ConfigureAwait(true);
            if (report is not null && !cancellationToken.IsCancellationRequested && !_exiting && !Dispatcher.HasShutdownStarted)
                model.TryInitializeRecoveredCrashReport(report, generation, _crashReports.AcknowledgeThrough);
        }
        catch (Exception) { } // Diagnostics and shutdown races must not interrupt startup.
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel && Volatile.Read(ref _fatalExit) == 0)
            _runExitMarker?.MarkShutdownStarted(ShutdownIntent.OperatingSystem);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        if (Volatile.Read(ref _fatalExit) == 0) _runExitMarker?.MarkShutdownStarted(ShutdownIntent.Requested);
        StopSupplementaryReporting();
        _featureControlClient?.Dispose();
        _previousRunCancellation?.Cancel();
        var shutdownCompleted = false;
        var nativeCleanupCompleted = true;
        _forzaStartupTimer?.Stop();
        _startupTray?.Dispose();
        _startupTray = null;
        try
        {
            if (_activationCancellation is not null)
            {
                _activationCancellation.Cancel();
                _activationEvent?.Set();
                _activationListener?.Wait(TimeSpan.FromSeconds(2));
            }

            // Bounded: a worker stuck in a driver or file call must not keep the process
            // alive after the user closed Wisp. An expired wait leaves the exit marker
            // incomplete, which is what happened.
            var disposal = _controller?.DisposeAsync().AsTask();
            shutdownCompleted = disposal is null || disposal.Wait(TimeSpan.FromSeconds(10));
        }
        finally
        {
            try
            {
                nativeCleanupCompleted = Clips.LosslessVlcRuntime.ShutdownAsync().GetAwaiter().GetResult();
                nativeCleanupCompleted &= Clips.LosslessMpvRuntime.ShutdownAsync().GetAwaiter().GetResult();
                _overlayHotkey?.Dispose();
                _overlayHotkey = null;
                _recordingHotkey?.Dispose();
                _recordingHotkey = null;
                _markerHotkey?.Dispose();
                _markerHotkey = null;
                _clipToggleHotkey?.Dispose();
                _clipToggleHotkey = null;
                _clipSaveHotkey?.Dispose();
                _clipSaveHotkey = null;
                _activationCancellation?.Dispose();
                _activationEvent?.Dispose();
                base.OnExit(e);
                DispatcherUnhandledException -= OnUnhandledUiException;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledBackgroundException;
                TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
                if (shutdownCompleted && nativeCleanupCompleted && Volatile.Read(ref _fatalExit) == 0) _runExitMarker?.MarkClean();
            }
            finally
            {
                _runExitMarker?.Dispose();
                _previousRunCancellation?.Dispose();
                _instanceMutex?.Dispose();
                // A worker that ignored its bounded wait is still inside native code. Finishing
                // the managed shutdown around it faulted (Windows recorded access violations on
                // 2.6.12), so the process ends here instead; the exit marker already says the
                // shutdown was incomplete and the next run reports it as such, not as a crash.
                if (!shutdownCompleted || !nativeCleanupCompleted) System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
        }
    }

    private async Task ListenForActivationAsync(CancellationToken cancellationToken)
    {
        if (_activationEvent is null)
        {
            return;
        }

        await Task.Run(() =>
        {
            var handles = new[] { _activationEvent, cancellationToken.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0 && !cancellationToken.IsCancellationRequested)
            {
                Dispatcher.BeginInvoke(RestoreControlPanel);
            }
        }).ConfigureAwait(false);
    }

    private void RestoreControlPanel()
    {
        if (_exiting)
        {
            return;
        }

        // Second-instance activation must restore the active wizard, never
        // manufacture a control panel while setup is incomplete.
        if (_controller?.Settings.RequiresSetup == true && _setupWindow is null)
        {
            return;
        }

        if ((_setupWindow ?? MainWindow) is not { } window)
        {
            if (_controller?.Settings.RequiresSetup == false)
            {
                _ = StartRuntimeAsync(showControlPanel: true, minimize: false);
            }
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.ShowActivated = true;
        if (window is ControlPanelWindow controlPanel) controlPanel.SetFeatureTourDiscoveryAllowed(true);
        window.Show();
        window.Activate();
        if (_setupWindow is null && _controller?.Settings.RequiresSetup == false)
        {
            _ = StartRuntimeAsync(showControlPanel: false, minimize: false);
        }
    }

    private void OnStartupOptionsChanged(object? sender, EventArgs e) => ConfigureStartupCompanion();

    private void ConfigureStartupCompanion()
    {
        if (_controller?.Settings is not { RequiresSetup: false, StartWithForza: true } || _exiting)
        {
            _forzaStartupTimer?.Stop();
            _forzaStartupLatch.Reset();
            _startupTray?.Dispose();
            _startupTray = null;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            return;
        }

        if (_startupTray is null)
        {
            try
            {
                _startupTray = new StartupTrayIcon();
                _startupTray.OpenRequested += (_, _) => RestoreControlPanel();
                _startupTray.ExitRequested += (_, _) => ExitFromTray();
                _forzaStartupLatch.Reset();
            }
            catch (Win32Exception)
            {
                _controller.ViewModel.ReportControlError(
                    "The Forza companion could not create its tray icon; keep Wisp open or restart it");
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                return;
            }
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _startupTray.SetWaiting(!_runtimeActive);
        if (_forzaStartupTimer is null)
        {
            _forzaStartupTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = ForzaStartupWindowObserver.PollInterval
            };
            _forzaStartupTimer.Tick += OnForzaStartupTimer;
        }
        _forzaStartupTimer.Start();
    }

    private void OnForzaStartupTimer(object? sender, EventArgs e)
    {
        if (_exiting || _controller?.Settings is not { RequiresSetup: false, StartWithForza: true } settings)
        {
            return;
        }

        if (_forzaStartupLatch.Observe(_forzaWindowObserver.IsGameWindowPresent(), _runtimeActive))
        {
            // This preference affects only a game-triggered start. The separate
            // AutoMinimizeOnTelemetry option still applies when driving begins.
            _ = StartRuntimeAsync(
                showControlPanel: true, minimize: settings.StartMinimizedWithForza, fromForza: true);
        }
    }

    private async Task StartRuntimeAsync(bool showControlPanel, bool minimize, bool fromForza = false)
    {
        await _runtimeGate.WaitAsync();
        try
        {
            if (_exiting || _controller is null || _controller.Settings.RequiresSetup ||
                fromForza && !_controller.Settings.StartWithForza)
            {
                return;
            }

            EnsureRuntimeWindows();
            if (showControlPanel && _controller.ControlPanel is { } window)
            {
                window.SetFeatureTourDiscoveryAllowed(!fromForza && !minimize);
                window.ShowActivated = !minimize;
                window.WindowState = minimize ? WindowState.Minimized : WindowState.Normal;
                window.Show();
                if (!minimize)
                {
                    window.Activate();
                }
            }
            if (_runtimeActive)
            {
                return;
            }

            try
            {
                RecordSupplementarySignal("connection", "telemetry", "attempt", "binding");
                await _controller.StartAsync();
                _runtimeActive = true;
                RecordSupplementaryRuntimeReady();
                _startupTray?.SetWaiting(false);
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or System.Net.Sockets.SocketException)
            {
                RecordSupplementarySignal("connection", "telemetry", "failure", "binding", incident:
                    DebugLogging.SupplementaryOperationFailure.Capture("telemetry", "binding", exception, DateTimeOffset.UtcNow));
                _runtimeActive = false;
                _startupTray?.SetWaiting(true);
                _controller.ViewModel.ReportControlError(exception.Message);
                if (showControlPanel && !minimize && !_exiting)
                {
                    MessageBox.Show(
                        _controller.ControlPanel,
                        exception.Message,
                        "Unable to start telemetry listener",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private void OnControlPanelClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        if (_applicationUpdateHandoffActive || _closePreparationActive)
        {
            e.Cancel = true;
            return;
        }

        if (!_exiting && _controller?.Settings.StartWithForza == true &&
            _startupTray is { IsAvailable: true } && sender is Window window)
        {
            e.Cancel = true;
            window.Hide();
            _forzaStartupLatch.SuppressCurrentGame(_forzaWindowObserver.IsGameWindowPresent());
            _ = SuspendRuntimeAsync();
            return;
        }

        // Keep the dispatcher alive while pending run details reach durable storage.
        e.Cancel = true;
        if (sender is Window closingWindow) _ = CloseApplicationAsync(closingWindow);
    }

    private async Task SuspendRuntimeAsync()
    {
        await _runtimeGate.WaitAsync();
        try
        {
            if (_exiting || _controller is null)
            {
                return;
            }
            _runtimeActive = false;
            _startupTray?.SetWaiting(true);
            await _controller.SuspendForForzaAsync();
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private void ExitFromTray()
    {
        if (_exiting || _applicationUpdateHandoffActive || _closePreparationActive)
        {
            return;
        }

        _ = CloseApplicationAsync(null);
    }

    private async Task CloseApplicationAsync(Window? closingWindow)
    {
        _closePreparationActive = true;
        try
        {
            if (!await PrepareRunMetadataForExitAsync()) return;
            _exiting = true;
            _forzaStartupTimer?.Stop();
            if (closingWindow is not null)
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                closingWindow.Close();
            }
            else Shutdown();
        }
        finally { _closePreparationActive = false; }
    }

    private async Task<bool> PrepareRunMetadataForExitAsync()
    {
        if (_controller is null) return true;
        if (await _controller.Runs.PrepareToCloseMetadataAsync()) return true;
        RestoreControlPanel();
        _controller.ControlPanel?.ShowRunSaveProblem();
        return false;
    }

    internal async Task<ApplicationUpdateStartResult> TryBeginApplicationUpdateAsync(
        VerifiedInstaller installer)
    {
        if (_exiting || _controller is null || _applicationUpdateHandoffActive || _closePreparationActive)
        {
            return (false, "Wisp is already closing or preparing another update.");
        }

        _applicationUpdateHandoffActive = true;
        bool gameOpen;
        try { gameOpen = await Task.Run(ForzaFocusService.IsOpenForApplicationUpdate); }
        catch { _applicationUpdateHandoffActive = false; throw; }
        if (_exiting || _controller is null)
        {
            _applicationUpdateHandoffActive = false;
            return (false, "Wisp is already closing.");
        }
        if (gameOpen)
        {
            _applicationUpdateHandoffActive = false;
            const string waiting = "Update ready. Wisp will update and restart after you close Forza.";
            _controller.ViewModel.UpdateApplicationUpdateStatus(waiting, "Check again", true, true);
            return (false, waiting);
        }

        var permission = _controller.ApplicationUpdateFeatureToken;
        if (!_controller.CanBeginApplicationUpdate(permission))
        {
            _applicationUpdateHandoffActive = false;
            return (false, _controller.ViewModel.ApplicationUpdateStatus);
        }
        _controller.MarkApplicationUpdatePreparing(installer);
        if (!await PrepareRunMetadataForExitAsync())
        {
            _applicationUpdateHandoffActive = false;
            const string saveError = "Save the pending run details before updating. Wisp has stayed open.";
            _controller.ViewModel.UpdateApplicationUpdateStatus(saveError, "Try again", canCheck: true);
            return (false, saveError);
        }
        if (!_controller.CanBeginApplicationUpdate(permission))
        {
            _applicationUpdateHandoffActive = false;
            _controller.Runs.CancelMetadataClosePreparation();
            return (false, "Application updates were paused. Review the update again when available.");
        }
        ApplicationUpdateHandoff handoff;
        try
        {
            handoff = ApplicationUpdateLauncher.Launch(installer);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or ArgumentException or Win32Exception or
                                           System.Security.SecurityException)
        {
            var error = ApplicationUpdateLauncher.DescribeStartFailure(exception);
            RecordSupplementarySignal("update", "update", "failure", "install-handoff", incident:
                DebugLogging.SupplementaryOperationFailure.Capture("update", "install-handoff", exception, DateTimeOffset.UtcNow));
            _applicationUpdateHandoffActive = false;
            _controller.Runs.CancelMetadataClosePreparation();
            _controller.ViewModel.UpdateApplicationUpdateStatus(error, "Try again", canCheck: true);
            // A missing helper cannot come back during this session, so the automatic coordinator
            // stops after this report instead of retrying every RetryInterval.
            return new ApplicationUpdateStartResult(false, error,
                HelperUnavailable: ApplicationUpdateLauncher.IsHelperUnavailable(exception));
        }

        using (handoff)
        {
            ApplicationUpdateHandoffState handoffState;
            try
            {
                handoffState = await handoff.WaitUntilReadyAsync(TimeSpan.FromSeconds(20), permission);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidOperationException or ObjectDisposedException)
            {
                handoff.StopIfRunning();
                _applicationUpdateHandoffActive = false;
                _controller.Runs.CancelMetadataClosePreparation();
                const string waitError =
                    "The update helper could not complete its safety checks. Wisp stayed open and no files were installed.";
                RecordSupplementarySignal("update", "update", "failure", "install-handoff", incident:
                DebugLogging.SupplementaryOperationFailure.Capture("update", "install-handoff", exception, DateTimeOffset.UtcNow));
                _controller.ViewModel.UpdateApplicationUpdateStatus(waitError, "Try again", canCheck: true);
                return (false, waitError);
            }

            if (handoffState == ApplicationUpdateHandoffState.Ready)
            {
                _controller.MarkApplicationUpdateStarting(installer);
                await FlushSupplementaryForUpdateAsync();
            }
            // Recheck every exit guard after the bounded reporting wait.
            if (!_controller.CanBeginApplicationUpdate(permission))
            {
                handoff.StopIfRunning();
                _applicationUpdateHandoffActive = false;
                _controller.Runs.CancelMetadataClosePreparation();
                ApplicationUpdateTelemetry.Record("paused", installer.Version.ToString(), reason: "cancelled");
                return (false, "Application updates were paused. Review the update again when available.");
            }
            if (await Task.Run(ForzaFocusService.IsOpenForApplicationUpdate))
            {
                handoff.StopIfRunning();
                _applicationUpdateHandoffActive = false;
                _controller.Runs.CancelMetadataClosePreparation();
                const string waiting = "Update ready. Wisp will restart after Forza closes.";
                ApplicationUpdateTelemetry.Record("waiting-for-game", installer.Version.ToString());
                _controller.ViewModel.UpdateApplicationUpdateStatus(waiting, "Check again", true, true);
                return (false, waiting);
            }
            if (!_controller.CanBeginApplicationUpdate(permission))
            {
                handoff.StopIfRunning();
                _applicationUpdateHandoffActive = false;
                _controller.Runs.CancelMetadataClosePreparation();
                ApplicationUpdateTelemetry.Record("paused", installer.Version.ToString(), reason: "cancelled");
                return (false, "Application updates are paused.");
            }
            if (_controller.HasApplicationUpdateWorkInProgress)
            {
                handoff.StopIfRunning();
                _applicationUpdateHandoffActive = false;
                _controller.Runs.CancelMetadataClosePreparation();
                const string waiting = "Update ready. Wisp will restart after your current work is saved.";
                ApplicationUpdateTelemetry.Record("waiting-for-work", installer.Version.ToString());
                _controller.ViewModel.UpdateApplicationUpdateStatus(waiting, "Check again", true, true);
                return (false, waiting);
            }
            if (handoffState != ApplicationUpdateHandoffState.Ready)
            {
                RecordSupplementarySignal("update", "update", handoffState == ApplicationUpdateHandoffState.TimedOut ? "timeout" : "failure", "install-handoff",
                    incident: Supplementary.SupplementaryIncidentSchema.Create("update",
                        handoffState == ApplicationUpdateHandoffState.TimedOut ? "timeout" : "update-verification-failed", "update-install-handoff"));
                handoff.StopIfRunning();
                _applicationUpdateHandoffActive = false;
                _controller.Runs.CancelMetadataClosePreparation();
                var error = handoffState == ApplicationUpdateHandoffState.TimedOut
                    ? "The update helper did not become ready in time. Wisp stayed open and no files were installed."
                    : "The update helper could not verify this installation. Wisp stayed open and no files were installed.";
                _controller.ViewModel.UpdateApplicationUpdateStatus(error, "Try again", canCheck: true);
                return (false, error);
            }
        }

        _runExitMarker?.MarkShutdownStarted(ShutdownIntent.Update);
        _applicationUpdateHandoffActive = false;
        _exiting = true;
        _forzaStartupTimer?.Stop();
        Shutdown();
        return (true, string.Empty);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance is still starting and will own the telemetry listener.
        }
    }

}
