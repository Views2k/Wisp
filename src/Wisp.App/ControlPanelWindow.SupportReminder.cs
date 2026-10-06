using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Wisp.App;

public abstract partial class ControlPanelWindow
{
    private SupportReminderPopup? _supportReminder;
    private bool _supportReminderRequested;
    private bool _supportReminderQueued;
    private bool _supportReminderClosed;
    private bool _supportReminderWasMinimized;
    private bool _supportReminderAutomaticOpening;
    private int _supportReminderOpeningVersion;
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
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) QueueSupportReminder();
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
            CloseSupportReminder(restoreFocus: false);
        };
        HudProfileDialog.IsVisibleChanged += (_, _) => SupportReminderDialogChanged();
        ApplicationUpdateConfirmation.IsVisibleChanged += (_, _) => SupportReminderDialogChanged();
        if (_featureTourOverlay is { } tour)
            tour.IsVisibleChanged += (_, _) => SupportReminderDialogChanged();
    }

    private void RequestSupportReminder(bool allowed)
    {
        _supportReminderRequested = allowed && !IsSupportReminderOpen;
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

    private void QueueSupportReminder()
    {
        if (!_supportReminderRequested || _supportReminderQueued || _supportReminderClosed || IsSupportReminderOpen || _supportReminder is null) return;
        _supportReminderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _supportReminderQueued = false;
            if (_supportReminderClosed || !_supportReminderRequested || IsSupportReminderOpen || !IsLoaded || !IsVisible || !IsActive ||
                WindowState == WindowState.Minimized || SupportReminderBlocked || !ControlBody.IsEnabled ||
                !TitleBar.IsEnabled || _connectionPopup is { IsOpen: true } || Mouse.Captured is not null) return;
            // A manual opening is the trigger; ordinary later activations do not
            // start another offer after the cooldown expires.
            _supportReminderRequested = false;
            if (!_controller.TryRecordSupportReminderShown(DateTimeOffset.UtcNow)) return;
            _focusBeforeSupportReminder = Keyboard.FocusedElement;
            ControlBody.IsEnabled = false;
            _supportReminder.Visibility = Visibility.Visible;
            _supportReminder.DismissButton.Focus();
        }));
    }

    internal void CloseSupportReminder(bool restoreFocus = true)
    {
        _supportReminderRequested = false;
        if (!IsSupportReminderOpen) return;
        _supportReminder!.Visibility = Visibility.Collapsed;
        if (HudProfileDialog.Visibility != Visibility.Visible && ApplicationUpdateConfirmation.Visibility != Visibility.Visible)
            ControlBody.IsEnabled = true;
        var previous = _focusBeforeSupportReminder;
        _focusBeforeSupportReminder = null;
        if (restoreFocus && IsActive && !SupportReminderBlocked && previous is UIElement { IsVisible: true, IsEnabled: true } element)
            element.Focus();
    }
}
