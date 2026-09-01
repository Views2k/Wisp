using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Wisp.Update;

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
    private bool _runtimeActive;
    private bool _applicationUpdateHandoffActive;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _activationCancellation = new CancellationTokenSource();

        var settingsService = new SettingsService();
        var settings = settingsService.Load();
        _controller = new AppController(settings, settingsService);
        if (ApplicationUpdateLauncher.TryConsumeResult(out var updateResult))
        {
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
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _setupWindow = new SetupWindow(_controller);
            MainWindow = _setupWindow;
            var completed = _setupWindow.ShowDialog() == true;
            _setupWindow = null;
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

        var mainWindow = new MainWindow(_controller);
        _controller.ControlPanel = mainWindow;
        MainWindow = mainWindow;
        mainWindow.Closing += OnControlPanelClosing;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _forzaStartupTimer?.Stop();
        _startupTray?.Dispose();
        _startupTray = null;
        try
        {
            if (_activationCancellation is not null)
            {
                _activationCancellation.Cancel();
                _activationEvent?.Set();
                _activationListener?.GetAwaiter().GetResult();
            }

            _controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            _activationCancellation?.Dispose();
            _activationEvent?.Dispose();
            _instanceMutex?.Dispose();
            base.OnExit(e);
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
                await _controller.StartAsync();
                _runtimeActive = true;
                _startupTray?.SetWaiting(false);
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or System.Net.Sockets.SocketException)
            {
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
        if (_applicationUpdateHandoffActive)
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

        _exiting = true;
        _forzaStartupTimer?.Stop();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
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
        if (_applicationUpdateHandoffActive)
        {
            return;
        }

        _exiting = true;
        _forzaStartupTimer?.Stop();
        Shutdown();
    }

    internal async Task<(bool Started, string Error)> TryBeginApplicationUpdateAsync(
        VerifiedInstaller installer)
    {
        if (_exiting || _controller is null || _applicationUpdateHandoffActive)
        {
            return (false, "Wisp is already closing or preparing another update.");
        }

        _applicationUpdateHandoffActive = true;
        _controller.MarkApplicationUpdatePreparing(installer);
        ApplicationUpdateHandoff handoff;
        try
        {
            handoff = ApplicationUpdateLauncher.Launch(installer);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or ArgumentException or Win32Exception or
                                           System.Security.SecurityException)
        {
            var error = "Wisp could not start the verified update. No files were installed.";
            _applicationUpdateHandoffActive = false;
            _controller.ViewModel.UpdateApplicationUpdateStatus(error, "Try again", canCheck: true);
            return (false, error);
        }

        using (handoff)
        {
            ApplicationUpdateHandoffState handoffState;
            try
            {
                handoffState = await handoff.WaitUntilReadyAsync(TimeSpan.FromSeconds(20));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidOperationException or ObjectDisposedException)
            {
                handoff.StopIfRunning();
                _applicationUpdateHandoffActive = false;
                const string waitError =
                    "The update helper could not complete its safety checks. Wisp stayed open and no files were installed.";
                _controller.ViewModel.UpdateApplicationUpdateStatus(waitError, "Try again", canCheck: true);
                return (false, waitError);
            }

            if (handoffState != ApplicationUpdateHandoffState.Ready)
            {
                handoff.StopIfRunning();
                _applicationUpdateHandoffActive = false;
                var error = handoffState == ApplicationUpdateHandoffState.TimedOut
                    ? "The update helper did not become ready in time. Wisp stayed open and no files were installed."
                    : "The update helper could not verify this installation. Wisp stayed open and no files were installed.";
                _controller.ViewModel.UpdateApplicationUpdateStatus(error, "Try again", canCheck: true);
                return (false, error);
            }
        }

        _controller.MarkApplicationUpdateStarting(installer);
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
