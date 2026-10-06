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
        _supportReminder.RepositoryButton.Click += (sender, args) =>
        {
            CloseSupportReminder();
            StarWispOnGitHub_Click(sender, args);
        };
        _supportReminder.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None) return;
            CloseSupportReminder();
            args.Handled = true;
        };
        Loaded += (_, _) => QueueSupportReminder();
        Activated += (_, _) => QueueSupportReminder();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) QueueSupportReminder();
            else CloseSupportReminder(restoreFocus: false);
        };
        StateChanged += (_, _) =>
        {
            var minimized = WindowState == WindowState.Minimized;
            var restored = _supportReminderWasMinimized && !minimized;
            _supportReminderWasMinimized = minimized;
            if (minimized) CloseSupportReminder(restoreFocus: false);
            else if (restored && !_supportReminderAutomaticOpening) RequestSupportReminder(true);
            else QueueSupportReminder();
        };
        Deactivated += (_, _) => CloseSupportReminder(restoreFocus: false);
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
        _supportReminderRequested = allowed;
        _supportReminderAutomaticOpening = !allowed;
        var version = ++_supportReminderOpeningVersion;
        if (allowed) QueueSupportReminder();
        else
        {
            CloseSupportReminder(restoreFocus: false);
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
        if (SupportReminderBlocked) CloseSupportReminder(restoreFocus: false);
        else QueueSupportReminder();
    }

    private void QueueSupportReminder()
    {
        if (!_supportReminderRequested || _supportReminderQueued || _supportReminderClosed || _supportReminder is null) return;
        _supportReminderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _supportReminderQueued = false;
            if (_supportReminderClosed || !_supportReminderRequested || !IsLoaded || !IsVisible || !IsActive ||
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
