using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Wisp.App;

public abstract partial class ControlPanelWindow
{
    private SupportReminderPopup? _supportReminder;
    private bool _supportReminderRequested;
    private bool _supportReminderDailyEnabled;
    private bool _supportReminderDeferred;
    private bool _supportReminderQueued;
    private bool _supportReminderClosed;
    private bool _supportReminderWasMinimized;
    private bool _supportReminderAutomaticOpening;
    private int _supportReminderOpeningVersion;
    private DateTimeOffset? _supportReminderLastDismissedUtc;
    private DispatcherTimer? _supportReminderTimer;
    private Popup? _supportReminderDeferredConnection;
    private IInputElement? _focusBeforeSupportReminder;
    internal bool IsSupportReminderOpen => _supportReminder?.Visibility == Visibility.Visible;

    private bool SupportReminderBlocked => FeatureTour.IsOpen ||
        HudProfileDialog.Visibility == Visibility.Visible || ApplicationUpdateConfirmation.Visibility == Visibility.Visible ||
        IsTuneDialogOpen || _controller.ShortcutCaptureActive ||
        this is MainWindow { IsDashboardDisplayMode: true };

    private void InitializeSupportReminder()
    {
        if (Content is not Border { Child: Grid shell }) return;
        _supportReminder = new SupportReminderPopup { Name = "SupportReminderPopup", Visibility = Visibility.Collapsed };
        RegisterName(_supportReminder.Name, _supportReminder);
        Grid.SetRow(_supportReminder, 1);
        Grid.SetRowSpan(_supportReminder, Math.Max(1, shell.RowDefinitions.Count - 1));
        Panel.SetZIndex(_supportReminder, 95);
        shell.Children.Add(_supportReminder);
        _supportReminder.DismissButton.Click += (_, _) => CloseSupportReminder();
        _supportReminder.RepositoryButton.Click += StarWispOnGitHub_Click;
        _supportReminder.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None) return;
            args.Handled = true;
        };
        Loaded += (_, _) => QueueSupportReminder();
        Activated += (_, _) => QueueSupportReminder();
        Deactivated += (_, _) => _supportReminderTimer?.Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) QueueSupportReminder();
            else _supportReminderTimer?.Stop();
        };
        StateChanged += (_, _) =>
        {
            var minimized = WindowState == WindowState.Minimized;
            var restored = _supportReminderWasMinimized && !minimized;
            _supportReminderWasMinimized = minimized;
            if (restored && !_supportReminderAutomaticOpening) RequestSupportReminder(true);
            else QueueSupportReminder();
        };
        Closed += (_, _) =>
        {
            _supportReminderClosed = true;
            _supportReminderRequested = false;
            _supportReminderDailyEnabled = false;
            _supportReminderTimer?.Stop();
            UnsubscribeSupportReminderConnection();
            CloseSupportReminder(restoreFocus: false);
        };
        HudProfileDialog.IsVisibleChanged += (_, _) => SupportReminderDialogChanged();
        ApplicationUpdateConfirmation.IsVisibleChanged += (_, _) => SupportReminderDialogChanged();
        if (_featureTourOverlay is { } tour)
            tour.IsVisibleChanged += (_, _) => SupportReminderDialogChanged();
        ControlBody.IsEnabledChanged += (_, _) => RetryDeferredSupportReminder();
        TitleBar.IsEnabledChanged += (_, _) => RetryDeferredSupportReminder();
        TitleBar.IsVisibleChanged += (_, _) => RetryDeferredSupportReminder();
        AddHandler(Keyboard.KeyUpEvent, new KeyEventHandler((_, _) => RetryDeferredSupportReminder()), true);
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => RetryDeferredSupportReminder()), true);
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) => RetryDeferredSupportReminder()), true);
    }

    private void RequestSupportReminder(bool allowed)
    {
        _supportReminderRequested = allowed && !IsSupportReminderOpen;
        _supportReminderDailyEnabled = allowed;
        _supportReminderDeferred = false;
        _supportReminderTimer?.Stop();
        _supportReminderAutomaticOpening = !allowed;
        var version = ++_supportReminderOpeningVersion;
        if (allowed) QueueSupportReminder();
        else
        {
            // Ignore automatic startup's state changes, then allow a later
            // user restore from the taskbar to count as opening Wisp.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (version == _supportReminderOpeningVersion) _supportReminderAutomaticOpening = false;
            }));
        }
    }

    private void SupportReminderDialogChanged()
    {
        if (!SupportReminderBlocked) QueueSupportReminder();
    }

    private void RetryDeferredSupportReminder()
    {
        if (_supportReminderRequested || _supportReminderDeferred) QueueSupportReminder();
    }

    private void UnsubscribeSupportReminderConnection()
    {
        if (_supportReminderDeferredConnection is not { } popup) return;
        popup.Closed -= SupportReminderConnectionClosed;
        _supportReminderDeferredConnection = null;
    }

    private void SupportReminderConnectionClosed(object? sender, EventArgs e)
    {
        UnsubscribeSupportReminderConnection();
        RetryDeferredSupportReminder();
    }

    private void ScheduleDailySupportReminder(TimeSpan delay)
    {
        if (_supportReminderTimer is null)
        {
            _supportReminderTimer = new DispatcherTimer(DispatcherPriority.ContextIdle, Dispatcher);
            _supportReminderTimer.Tick += (_, _) =>
            {
                _supportReminderTimer.Stop();
                QueueSupportReminder();
            };
        }
        _supportReminderTimer.Interval = delay;
        _supportReminderTimer.Start();
    }

    private void QueueSupportReminder()
    {
        _supportReminderTimer?.Stop();
        if ((!_supportReminderRequested && !_supportReminderDailyEnabled) || _supportReminderQueued ||
            _supportReminderClosed || IsSupportReminderOpen || _supportReminder is null) return;
        _supportReminderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _supportReminderQueued = false;
            if (_supportReminderClosed || (!_supportReminderRequested && !_supportReminderDailyEnabled) ||
                IsSupportReminderOpen || !IsLoaded || !IsVisible || !IsActive || WindowState == WindowState.Minimized) return;
            var now = DateTimeOffset.UtcNow;
            var manualOpening = _supportReminderRequested;
            var delay = SupportReminderPolicy.DelayUntilDue(_controller.Settings.LastSupportReminderShownUtc, now,
                _supportReminderLastDismissedUtc);
            if (!manualOpening && delay > TimeSpan.Zero)
            {
                _supportReminderDeferred = false;
                ScheduleDailySupportReminder(delay);
                return;
            }
            if (SupportReminderBlocked || !ControlBody.IsEnabled || !TitleBar.IsEnabled ||
                _connectionPopup is { IsOpen: true } || Mouse.Captured is not null)
            {
                _supportReminderDeferred = true;
                if (_connectionPopup is { IsOpen: true } popup && !ReferenceEquals(popup, _supportReminderDeferredConnection))
                {
                    UnsubscribeSupportReminderConnection();
                    _supportReminderDeferredConnection = popup;
                    popup.Closed += SupportReminderConnectionClosed;
                }
                return;
            }
            _supportReminderRequested = false;
            _supportReminderDeferred = false;
            UnsubscribeSupportReminderConnection();
            if (!_controller.TryRecordSupportReminderShown(now, manualOpening)) return;
            _focusBeforeSupportReminder = Keyboard.FocusedElement;
            ControlBody.IsEnabled = false;
            _supportReminder.Visibility = Visibility.Visible;
            _supportReminder.DismissButton.Focus();
        }));
    }

    internal void CloseSupportReminder(bool restoreFocus = true)
    {
        _supportReminderRequested = false;
        _supportReminderDeferred = false;
        if (!IsSupportReminderOpen) return;
        _supportReminderLastDismissedUtc = DateTimeOffset.UtcNow;
        _supportReminder!.Visibility = Visibility.Collapsed;
        if (HudProfileDialog.Visibility != Visibility.Visible && ApplicationUpdateConfirmation.Visibility != Visibility.Visible)
            ControlBody.IsEnabled = true;
        var previous = _focusBeforeSupportReminder;
        _focusBeforeSupportReminder = null;
        if (restoreFocus && IsActive && !SupportReminderBlocked && previous is UIElement { IsVisible: true, IsEnabled: true } element)
            element.Focus();
        QueueSupportReminder();
    }
}
