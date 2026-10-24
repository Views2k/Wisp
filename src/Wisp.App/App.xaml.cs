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
    private SessionEndSentinel? _sessionEndSentinel;
    private DebugLogging.UiStallWatchdog? _uiStallWatchdog;
    private DisplayChangeWatcher? _displayChanges;

    // Receives an exception an operation on the dashboard thread caught and recovered from.
    internal static Action<Exception, string?>? RecoveredExceptionSink { get; private set; }
    // The dashboard draws in software after a render thread failure ended a previous run.
    internal static bool SoftwareRenderingActive { get; private set; }

    public App()
    {
        _crashReports = CrashReportStore.Current;
        _crashMonitor = new CrashExceptionMonitor(_crashReports, runId: () => _runId);
        DispatcherUnhandledException += OnUnhandledUiException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledBackgroundException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _previousCrashReport = _crashReports.LatestPending();
        RecoveredExceptionSink = _crashMonitor.RecoveredUiException;
        SoftwareRenderingActive = DashboardRenderingFallback.IsRequested(_crashReports.StoreDirectory, DateTimeOffset.UtcNow);
        if (SoftwareRenderingActive)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }

    private void OnUnhandledUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A clip player whose video surface could not be created has already failed itself and closes;
        // the exception WPF raises for the missing child window is reported, not fatal.
        if (e.Exception is Clips.LosslessVideoSurfaceException surface)
        {
            _crashMonitor.RecoveredUiException(surface, "lossless-surface");
            e.Handled = true;
            return;
        }
        Interlocked.Exchange(ref _fatalExit, 1);
        if (DashboardRenderingFallback.IsRenderThreadFailure(e.Exception))
            DashboardRenderingFallback.TryRequest(_crashReports.StoreDirectory, DateTimeOffset.UtcNow);
        _crashMonitor.UiException(e.Exception);
    }
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
        // Windows delivers its shutdown notice to this window on its own thread, so the close request
        // is recorded even while the dashboard thread is busy.
        _sessionEndSentinel = SessionEndSentinel.TryStart(() =>
        {
            if (Volatile.Read(ref _fatalExit) == 0) _runExitMarker?.MarkShutdownStarted(ShutdownIntent.OperatingSystem);
        }, () => _runExitMarker?.ClearShutdownIntent(ShutdownIntent.OperatingSystem));
        // The native handler records the faulting address and call stack of a hardware fault
        // for this run; the next run reports it with the crash.
        NativeCrashCapture.TryInstall(_crashReports.StoreDirectory, _runId);
        _crashReports.ActiveNativeRecord = Path.GetFileName(NativeCrashCapture.PathFor(_crashReports.StoreDirectory, _runId));
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _activationCancellation = new CancellationTokenSource();

        var settingsService = new SettingsService();
        var settings = settingsService.Load();
        _controller = new AppController(settings, settingsService);
        if (SoftwareRenderingActive) _controller.LocalNotices.Raise(LocalDashboardNotices.SoftwareRendering);
        _uiStallWatchdog = DebugLogging.UiStallWatchdog.Start(Dispatcher);
        _displayChanges = DisplayChangeWatcher.Start(Dispatcher, _controller.KeepOverlaysOnScreen);
        try
        {
            await StartFeatureControlsAsync();
        }
        catch (Exception exception)
        {
            // Feature controls are optional: every feature stays on without them, and the failure reaches diagnostics.
            _crashMonitor.RecoveredUiException(exception, "feature-controls");
            RecordSupplementarySignal("startup", "app", "failure", "launch", incident:
                DebugLogging.SupplementaryOperationFailure.Capture("app", "launch", exception, DateTimeOffset.UtcNow));
        }
        StartSupplementaryReporting();
        RecordSupplementarySignal("settings", "settings", settingsService.LoadOutcome, "load",
            incident: settingsService.LoadFailure is { } loadFailure ? Supplementary.SupplementaryIncidentSchema.Create(
                "settings", loadFailure.Reason, "settings-load", loadFailure.Code) : null);
        if (settingsService.RestoredFromBackup)
        {
            RecordSupplementarySignal("settings", "settings", "success", "recovery");
            DebugLogging.HealthContextRecorder.Current.RecordBreadcrumb(DebugLogging.HealthEventCode.SettingsRestored);
            _controller.LocalNotices.Raise(LocalDashboardNotices.SettingsRestored);
            // The restored copy replaces the unreadable file now, so the next launch does not restore again.
            _controller.TrySavePendingSettings();
        }
        _controller.ViewModel.InitializeCrashReport(_previousCrashReport, _crashReports.AcknowledgeThrough);
        if (_runExitMarker?.Previous is { } previous)
        {
            _previousRunCancellation = new CancellationTokenSource();
            Observe(RecoverPreviousRunAsync(previous, _runExitMarker.StartedAtUtc, _previousRunCancellation.Token), "previous-run-recovery");
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
            if (updateResult.Outcome == "success") ApplicationUpdateStaging.TryDeleteCompleted(updateResult.TargetVersion);
            _controller.ViewModel.UpdateApplicationUpdateStatus(
                updateResult.Status,
                updateResult.Action,
                canCheck: true);
        }
        _activationListener = ListenForActivationAsync(_activationCancellation.Token);

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

        // Update checks and automatic installs start only once setup is complete, so an available
        // release cannot close the wizard while the player is still in it.
        _controller.EnableAutomaticApplicationUpdates(async installer =>
            (await TryBeginApplicationUpdateAsync(installer)).Outcome);
        _controller.BeginStartupApplicationUpdateCheck();

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
        try
        {
            CreateHudWindows(_controller);
        }
        catch (Exception exception)
        {
            // A HUD window that cannot be created leaves the dashboard available and the failure in diagnostics.
            _crashMonitor.RecoveredUiException(exception, "hud-windows");
            RecordSupplementarySignal("startup", "hud", "failure", "hud-ready", incident:
                DebugLogging.SupplementaryOperationFailure.Capture("hud", "ready", exception, DateTimeOffset.UtcNow));
            _controller.ViewModel.ReportControlError("Wisp could not create its HUD windows; restart Wisp to try again");
        }

        ControlPanelWindow mainWindow = settings.UseLegacyInterface
            ? new LegacyMainWindow(_controller)
            : new MainWindow(_controller);
        _controller.ControlPanel = mainWindow;
        MainWindow = mainWindow;
        mainWindow.Closing += OnControlPanelClosing;
    }

    private void CreateHudWindows(AppController controller)
    {
        var settings = controller.Settings;
        var overlay = new OverlayWindow(controller);
        controller.Overlay = overlay;
        controller.RestoreOverlayPlacement();
        overlay.SetEditMode(!settings.OverlayLocked);

        var gForceOverlay = new GForceWindow(controller);
        controller.GForceOverlay = gForceOverlay;
        controller.RestoreGForcePlacement();
        gForceOverlay.SetEnabled(controller.IsStandaloneGForceWindowEnabled);
        gForceOverlay.SetEditMode(!settings.OverlayLocked);

        var boostGaugeOverlay = new BoostGaugeWindow(controller);
        controller.BoostGaugeOverlay = boostGaugeOverlay;
        boostGaugeOverlay.ApplyAppearance(settings.BoostGaugeScale, settings.OverlayOpacity);
        boostGaugeOverlay.SetEnabled(controller.IsDetachedBoostGaugeEnabled);
        controller.RestoreBoostGaugePlacement();
        boostGaugeOverlay.SetEditMode(!settings.OverlayLocked);

        var tireTemperatureGaugeOverlay = new TireTemperatureGaugeWindow(controller);
        controller.TireTemperatureGaugeOverlay = tireTemperatureGaugeOverlay;
        tireTemperatureGaugeOverlay.ApplyGaugeMode(settings.NativeGaugeMode);
        tireTemperatureGaugeOverlay.ApplyAppearance(
            settings.TireTemperatureGaugeScale,
            settings.OverlayOpacity);
        tireTemperatureGaugeOverlay.SetEnabled(controller.IsDetachedTireTemperatureGaugeEnabled);
        controller.RestoreTireTemperatureGaugePlacement();
        Observe(OverlayHookNotice.WatchAsync(controller.LocalNotices, _activationCancellation?.Token ?? CancellationToken.None), "overlay-hook-notice");
        tireTemperatureGaugeOverlay.SetEditMode(!settings.OverlayLocked);

        controller.InitializeDriftGaugeWindow();
        controller.InitializeLapDeltaWindow();
        controller.InitializePowerTorqueGaugeWindows();
    }

    // A task nobody awaits reports its failure when it faults, not when the finalizer finds it.
    private void Observe(Task task, string note) => task.ContinueWith(
        faulted => _crashMonitor.UnobservedTaskException(faulted.Exception!, note),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private async Task RecoverPreviousRunAsync(RunExitMarker previous, DateTimeOffset startedAtUtc, CancellationToken cancellationToken)
    {
        var model = _controller!.ViewModel;
        var generation = model.CrashReportNoticeGeneration;
        try
        {
            var report = await new PreviousRunRecovery(_crashReports, new WindowsFaultEventReader(), new WindowsStopEventReader())
                .RecoverAsync(previous, startedAtUtc, cancellationToken).ConfigureAwait(true);
            if (report is not null && !cancellationToken.IsCancellationRequested && !_exiting && !Dispatcher.HasShutdownStarted)
                model.TryInitializeRecoveredCrashReport(report, generation, _crashReports.AcknowledgeThrough);
        }
        catch (Exception) { } // Diagnostics and shutdown races must not interrupt startup.
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        // The sentinel records the notice and withdraws it when Windows cancels the shutdown. Without a
        // sentinel no cancel notice can follow, so the intent is left to the stop-event correlation.
        if (!e.Cancel && Volatile.Read(ref _fatalExit) == 0 && _sessionEndSentinel is not null)
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
            try { shutdownCompleted = disposal is null || disposal.Wait(TimeSpan.FromSeconds(10)); }
            catch (AggregateException error)
            {
                // The process still ends below; the report says why instead of an unexplained incomplete shutdown.
                _crashMonitor.RecoveredUiException(error.GetBaseException(), "shutdown");
                shutdownCompleted = false;
            }
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
                _displayChanges?.Dispose();
                _displayChanges = null;
                _uiStallWatchdog?.Dispose();
                _uiStallWatchdog = null;
                _sessionEndSentinel?.Dispose();
                _sessionEndSentinel = null;
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
                Observe(StartRuntimeAsync(showControlPanel: true, minimize: false), "runtime-start");
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
            Observe(StartRuntimeAsync(showControlPanel: false, minimize: false), "runtime-start");
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
            Observe(StartRuntimeAsync(
                showControlPanel: true, minimize: settings.StartMinimizedWithForza, fromForza: true), "runtime-start");
        }
    }

    // The Connection panel calls this after it applied a port. A runtime that never started because
    // the saved port could not be bound starts now; a running one returns at once.
    internal Task ResumeRuntimeAsync() => StartRuntimeAsync(showControlPanel: false, minimize: false);

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
            catch (Exception exception)
            {
                // Any other start failure is reported and leaves the dashboard open; the next start request retries.
                _crashMonitor.RecoveredUiException(exception, "runtime-start");
                RecordSupplementarySignal("startup", "app", "failure", "runtime-ready", incident:
                    DebugLogging.SupplementaryOperationFailure.Capture("app", "runtime-ready", exception, DateTimeOffset.UtcNow));
                _runtimeActive = false;
                _startupTray?.SetWaiting(true);
                _controller.ViewModel.ReportControlError("Wisp could not finish starting; restart Wisp to try again");
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
            Observe(SuspendRuntimeAsync(), "runtime-suspend");
            return;
        }

        // Keep the dispatcher alive while pending run details reach durable storage.
        e.Cancel = true;
        if (sender is Window closingWindow) Observe(CloseApplicationAsync(closingWindow), "close");
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

        Observe(CloseApplicationAsync(null), "close");
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
        var started = false;
        var metadataPrepared = false;
        try
        {
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
                var waiting = ApplicationVersionInfo.AutomaticInstallation
                    ? "Update ready. Wisp will update and restart after you close Forza."
                    : "Update ready. Close Forza, then use Update to install it.";
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
            DebugLogging.HealthContextRecorder.Current.RecordBreadcrumb(DebugLogging.HealthEventCode.UpdateHandoffStarted);
            if (!await PrepareRunMetadataForExitAsync())
            {
                _applicationUpdateHandoffActive = false;
                const string saveError = "Save the pending run details before updating. Wisp has stayed open.";
                _controller.ViewModel.UpdateApplicationUpdateStatus(saveError, "Try again", canCheck: true);
                return (false, saveError);
            }
            metadataPrepared = true;
            if (!_controller.CanBeginApplicationUpdate(permission))
            {
                _applicationUpdateHandoffActive = false;
                _controller.Runs.CancelMetadataClosePreparation();
                return (false, "Application updates were paused. Review the update again when available.");
            }
            ApplicationUpdateHandoff handoff;
            var launchedAt = DateTimeOffset.UtcNow;
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
                    var waiting = ApplicationVersionInfo.AutomaticInstallation
                        ? "Update ready. Wisp will restart after Forza closes."
                        : "Update ready. Close Forza, then use Update to install it.";
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
                    var waiting = ApplicationVersionInfo.AutomaticInstallation
                        ? "Update ready. Wisp will restart after your current work is saved."
                        : "Update ready. Finish your current work, then use Update to install it.";
                    ApplicationUpdateTelemetry.Record("waiting-for-work", installer.Version.ToString());
                    _controller.ViewModel.UpdateApplicationUpdateStatus(waiting, "Check again", true, true);
                    return (false, waiting);
                }
                if (handoffState != ApplicationUpdateHandoffState.Ready)
                {
                    string error;
                    var helperRejected = false;
                    if (handoffState == ApplicationUpdateHandoffState.HelperExited)
                    {
                        // The helper records which check failed before it exits. Report that code now,
                        // and stop automatic attempts when the same check fails again in this session.
                        var exit = ApplicationUpdateLauncher.DescribeHelperExit(handoff.ExitCode, launchedAt);
                        RecordSupplementarySignal("update", "update", "failure", "install-handoff", incident: exit.Incident);
                        error = exit.Status;
                        helperRejected = exit.Permanent;
                    }
                    else
                    {
                        RecordSupplementarySignal("update", "update", handoffState == ApplicationUpdateHandoffState.TimedOut ? "timeout" : "failure", "install-handoff",
                            incident: Supplementary.SupplementaryIncidentSchema.Create("update",
                                handoffState == ApplicationUpdateHandoffState.TimedOut ? "timeout" : "update-verification-failed", "update-install-handoff"));
                        error = handoffState == ApplicationUpdateHandoffState.TimedOut
                            ? "The update helper did not become ready in time. Wisp stayed open and no files were installed."
                            : "The update helper could not verify this installation. Wisp stayed open and no files were installed.";
                    }
                    handoff.StopIfRunning();
                    _applicationUpdateHandoffActive = false;
                    _controller.Runs.CancelMetadataClosePreparation();
                    _controller.ViewModel.UpdateApplicationUpdateStatus(error, "Try again", canCheck: true);
                    return new ApplicationUpdateStartResult(false, error, HelperRejected: helperRejected);
                }
            }

            _runExitMarker?.MarkShutdownStarted(ShutdownIntent.Update);
            started = true;
            _applicationUpdateHandoffActive = false;
            _exiting = true;
            _forzaStartupTimer?.Stop();
            Shutdown();
            return (true, string.Empty);
        }
        finally
        {
            // A failure the branches above did not expect must not leave Wisp unable to close or update.
            if (!started && _applicationUpdateHandoffActive)
            {
                _applicationUpdateHandoffActive = false;
                if (metadataPrepared) _controller?.Runs.CancelMetadataClosePreparation();
            }
        }
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
